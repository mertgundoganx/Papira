using System.Collections.Concurrent;
using Papira.Fonts;

namespace Papira;

/// <summary>
/// Process-wide registry of fonts available to documents. Papira ships with the Lato family (SIL OFL) as default font,
/// so documents render identically on every machine, including minimal Docker images without fonts.
/// All members are thread-safe.
/// </summary>
public static class FontManager
{
    /// <summary>Family used when a text style does not specify one.</summary>
    public const string DefaultFontFamily = "Lato";

    private static readonly object RegistrationLock = new();
    private static readonly object SystemScanLock = new();
    private static readonly ConcurrentDictionary<(string Family, int Weight, bool Italic), ResolvedFont> ResolveCache = new();
    private static readonly ConcurrentDictionary<(string Family, int Weight, bool Italic), ResolvedFont?> ExactCache = new();

    // Replaced as a whole on every change; readers never see a collection being modified.
    private static Dictionary<string, FontSource[]> _registered = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, FontSource[]>? _system;
    private static int _version;
    private static volatile bool _useSystemFonts = true;
    private static string[] _fallbackFamilies = [];

    static FontManager()
    {
        var assembly = typeof(FontManager).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("Papira.Fonts.", StringComparison.Ordinal))
                continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            RegisterCore(ReadAll(stream), null);
        }
    }

    /// <summary>
    /// When a requested family is not registered, look it up among the fonts installed on the machine.
    /// Defaults to <c>true</c>.
    /// </summary>
    public static bool UseSystemFonts
    {
        get => _useSystemFonts;
        set
        {
            _useSystemFonts = value;
            Invalidate();
        }
    }

    /// <summary>
    /// Families used, in order, for characters missing from a text's own font and its fallbacks
    /// (for example <c>["Noto Sans Arabic", "Noto Sans SC"]</c>). Registered fonts are tried after these.
    /// Families must be registered or installed on the machine.
    /// </summary>
    public static IReadOnlyList<string> FallbackFontFamilies
    {
        get => Volatile.Read(ref _fallbackFamilies);
        set => Volatile.Write(ref _fallbackFamilies, value?.ToArray() ?? []);
    }

    /// <summary>Registers every face of a .ttf/.ttc file.</summary>
    /// <exception cref="InvalidDataException">The file is not a valid font.</exception>
    /// <exception cref="NotSupportedException">The font has no TrueType outlines (e.g. CFF-based .otf).</exception>
    public static void RegisterFont(string path) => RegisterCore(File.ReadAllBytes(path), null);

    /// <summary>Registers every face of a .ttf/.ttc font read from <paramref name="stream"/>.</summary>
    public static void RegisterFont(Stream stream) => RegisterCore(ReadAll(stream), null);

    /// <summary>Registers every face of a .ttf/.ttc font. The data is copied.</summary>
    public static void RegisterFont(byte[] data) => RegisterCore(Copy(data), null);

    /// <summary>Registers a font file under a custom family name, regardless of the name stored in the file.</summary>
    public static void RegisterFontWithCustomName(string familyName, string path) =>
        RegisterCore(File.ReadAllBytes(path), familyName);

    /// <summary>Registers a font under a custom family name, regardless of the name stored in the file.</summary>
    public static void RegisterFontWithCustomName(string familyName, Stream stream) =>
        RegisterCore(ReadAll(stream), familyName);

    /// <summary>Registers a font under a custom family name. The data is copied.</summary>
    public static void RegisterFontWithCustomName(string familyName, byte[] data) =>
        RegisterCore(Copy(data), familyName);

    /// <summary>
    /// Registers all .ttf and .ttc fonts in a directory (recursively). Files that are not valid or supported fonts are skipped.
    /// Returns the number of faces registered.
    /// </summary>
    public static int RegisterFontsFromDirectory(string directory)
    {
        var count = 0;
        foreach (var file in EnumerateFontFiles(directory))
        {
            try
            {
                count += RegisterCore(File.ReadAllBytes(file), null);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                // Skip broken, unsupported or unreadable files.
            }
        }

        return count;
    }

    /// <summary>Names of explicitly registered families (including the built-in ones).</summary>
    public static IReadOnlyCollection<string> RegisteredFamilies => Volatile.Read(ref _registered).Keys.ToArray();

    private static int RegisterCore(byte[] data, string? customFamily)
    {
        if (customFamily != null && string.IsNullOrWhiteSpace(customFamily))
            throw new ArgumentException("Family name must not be empty.", nameof(customFamily));

        var faces = TrueTypeFont.LoadAll(data);
        if (faces.Count == 0)
            throw new NotSupportedException("The font file contains no TrueType-outline faces. CFF-based .otf fonts are not supported yet.");

        lock (RegistrationLock)
        {
            var next = new Dictionary<string, FontSource[]>(_registered, StringComparer.OrdinalIgnoreCase);
            foreach (var face in faces)
            {
                var family = customFamily ?? face.Info.Family;
                var info = face.Info with { Family = family };
                var existing = next.TryGetValue(family, out var list) ? list : [];

                next[family] = existing
                    .Where(source => source.Info.Weight != info.Weight || source.Info.Italic != info.Italic)
                    .Append(new FontSource(info, () => face))
                    .ToArray();
            }

            Volatile.Write(ref _registered, next);
        }

        Invalidate();
        return faces.Count;
    }

    private static void Invalidate()
    {
        Interlocked.Increment(ref _version);
        ResolveCache.Clear();
        ExactCache.Clear();
    }

    /// <summary>
    /// Fallback families for a style, in priority order: the style's own fallbacks, the global fallbacks,
    /// then every other registered family. The style's primary family is excluded.
    /// </summary>
    internal static IEnumerable<string> FallbackCandidates(TextStyle style)
    {
        var primary = style.Family ?? DefaultFontFamily;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary };
        var ordered = (style.FallbackFamilies ?? []).Concat(FallbackFontFamilies).Concat(Volatile.Read(ref _registered).Keys);

        foreach (var family in ordered)
        {
            if (seen.Add(family))
                yield return family;
        }
    }

    /// <summary>Resolves a family that must exist (registered or installed); unlike <see cref="Resolve"/>, never substitutes the default font.</summary>
    internal static bool TryResolveFamily(string family, FontWeight weight, bool italic, out ResolvedFont resolved)
    {
        var key = (family, (int)weight, italic);
        if (!ExactCache.TryGetValue(key, out var cached))
        {
            var version = Volatile.Read(ref _version);
            cached = ResolveExact(family, (int)weight, italic);
            if (Volatile.Read(ref _version) == version)
            {
                ExactCache[key] = cached;
                if (Volatile.Read(ref _version) != version)
                    ExactCache.TryRemove(key, out _);
            }
        }

        resolved = cached ?? default;
        return cached != null;
    }

    private static ResolvedFont? ResolveExact(string family, int weight, bool italic)
    {
        Volatile.Read(ref _registered).TryGetValue(family, out var candidates);
        if (candidates == null && UseSystemFonts)
            GetSystemFonts().TryGetValue(family, out candidates);

        return candidates != null && TryPick(candidates, weight, italic, out var resolved) ? resolved : null;
    }

    internal static ResolvedFont Resolve(string? family, FontWeight weight, bool italic)
    {
        var key = (family ?? DefaultFontFamily, (int)weight, italic);
        if (ResolveCache.TryGetValue(key, out var cached))
            return cached;

        var version = Volatile.Read(ref _version);
        var resolved = ResolveCore(key.Item1, key.Item2, italic);

        // Don't cache a result computed while fonts were being registered.
        if (Volatile.Read(ref _version) == version)
        {
            ResolveCache[key] = resolved;

            // Registration may have cleared the cache between the check and the store.
            if (Volatile.Read(ref _version) != version)
                ResolveCache.TryRemove(key, out _);
        }

        return resolved;
    }

    private static ResolvedFont ResolveCore(string family, int weight, bool italic)
    {
        var registered = Volatile.Read(ref _registered);
        registered.TryGetValue(family, out var candidates);

        if (candidates == null && UseSystemFonts)
            GetSystemFonts().TryGetValue(family, out candidates);

        if (candidates != null && TryPick(candidates, weight, italic, out var resolved))
            return resolved;

        if (!TryPick(registered[DefaultFontFamily], weight, italic, out resolved))
            throw new InvalidOperationException("The built-in default font could not be loaded.");

        return resolved;
    }

    /// <summary>Picks the closest face; faces that fail to load (e.g. corrupt files) are skipped.</summary>
    private static bool TryPick(FontSource[] candidates, int weight, bool italic, out ResolvedFont resolved)
    {
        var ordered = candidates
            .Where(c => c.Info.HasTrueTypeOutlines)
            .OrderBy(c => c.Info.Italic == italic ? 0 : 1)
            .ThenBy(c => Math.Abs(c.Info.Weight - weight))
            .ThenBy(c => weight > 400 ? -c.Info.Weight : c.Info.Weight);

        foreach (var candidate in ordered)
        {
            if (!candidate.TryGetFont(out var font))
                continue;

            var fakeBold = weight >= 600 && candidate.Info.Weight < 550;
            var fakeItalic = italic && !candidate.Info.Italic;
            resolved = new ResolvedFont(font, fakeBold, fakeItalic);
            return true;
        }

        resolved = default;
        return false;
    }

    private static Dictionary<string, FontSource[]> GetSystemFonts()
    {
        var system = Volatile.Read(ref _system);
        if (system != null)
            return system;

        // A separate lock: scanning takes a while and must not block registration or lookups of registered fonts.
        lock (SystemScanLock)
        {
            system = _system;
            if (system != null)
                return system;

            var result = new Dictionary<string, List<FontSource>>(StringComparer.OrdinalIgnoreCase);
            var files = SystemFontDirectories()
                .Where(Directory.Exists)
                .SelectMany(EnumerateFontFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Parallel.ForEach(files, file =>
            {
                List<FontFaceInfo> infos;
                try
                {
                    using var handle = File.OpenHandle(file);
                    var length = RandomAccess.GetLength(handle);
                    infos = TrueTypeFont.ReadFaceInfos((offset, count) =>
                    {
                        count = (int)Math.Clamp(length - offset, 0, count);
                        var buffer = new byte[count];
                        var read = RandomAccess.Read(handle, buffer, offset);
                        return read == count ? buffer : buffer[..read];
                    });
                }
                catch (Exception)
                {
                    return; // unreadable or malformed font file
                }

                for (var faceIndex = 0; faceIndex < infos.Count; faceIndex++)
                {
                    var info = infos[faceIndex];
                    if (!info.HasTrueTypeOutlines)
                        continue;

                    var index = faceIndex;
                    var source = new FontSource(info, () => TrueTypeFont.Load(File.ReadAllBytes(file), index));
                    lock (result)
                    {
                        if (!result.TryGetValue(info.Family, out var list))
                            result[info.Family] = list = [];
                        list.Add(source);
                    }
                }
            });

            system = result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            Volatile.Write(ref _system, system);
            return system;
        }
    }

    private static IEnumerable<string> SystemFontDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(home, "Library", "Fonts");
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Combine(home, ".fonts");
            yield return Path.Combine(home, ".local", "share", "fonts");
        }
    }

    private static IEnumerable<string> EnumerateFontFiles(string directory)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        return Directory.EnumerateFiles(directory, "*.tt?", options)
            .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] Copy(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.AsSpan().ToArray();
    }

    private static byte[] ReadAll(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

internal sealed class FontSource(FontFaceInfo info, Func<TrueTypeFont> loader)
{
    private readonly Lazy<TrueTypeFont?> _font = new(() =>
    {
        try
        {
            return loader();
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public FontFaceInfo Info { get; } = info;

    public bool TryGetFont(out TrueTypeFont font)
    {
        font = _font.Value!;
        return font != null;
    }
}

internal readonly record struct ResolvedFont(TrueTypeFont Font, bool FakeBold, bool FakeItalic);
