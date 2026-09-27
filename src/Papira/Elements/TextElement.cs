using System.Text;
using Papira.Fonts;
using Papira.Infrastructure;
using Papira.Rendering;
using Papira.Text;

namespace Papira.Elements;

/// <summary>A text style resolved to a concrete font face and point-based metrics.</summary>
internal sealed class ResolvedTextStyle
{
    public ResolvedTextStyle(TextStyle style)
        : this(style, FontManager.Resolve(style.Family, style.Weight ?? FontWeight.Normal, style.IsItalic ?? false))
    {
    }

    /// <summary>The style rendered with a specific font face, e.g. a fallback font.</summary>
    public ResolvedTextStyle(TextStyle style, ResolvedFont resolvedFont)
    {
        Font = resolvedFont;
        Size = style.Size ?? 12;
        Color = style.TextColor ?? Colors.Black;
        LetterSpacing = style.Spacing ?? 0;
        Underline = style.IsUnderline ?? false;
        Strikethrough = style.IsStrikethrough ?? false;

        var font = Font.Font;
        Scale = Size / font.UnitsPerEm;
        Ascent = font.Ascender * Scale;
        Descent = -font.Descender * Scale;
        LineHeight = style.LineHeightFactor is { } factor
            ? Size * factor
            : (font.Ascender - font.Descender + font.LineGap) * Scale;
    }

    public ResolvedFont Font { get; }
    public float Size { get; }
    public Color Color { get; }
    public float LetterSpacing { get; }
    public bool Underline { get; }
    public bool Strikethrough { get; }
    public float Scale { get; }
    public float Ascent { get; }
    public float Descent { get; }
    public float LineHeight { get; }
}

internal sealed class TextSpan
{
    public string? Text;
    public Func<LayoutContext, string>? DynamicText;
    public TextStyle Style = TextStyle.Default;

    /// <summary>The address this piece of the paragraph opens, if it is a link.</summary>
    public string? Uri;

    /// <summary>The named section this piece of the paragraph jumps to, if it is an internal link.</summary>
    public string? Section;

    public bool IsLink => Uri != null || Section != null;
}

internal enum TextAlignment : byte { Left, Center, Right, Justify }

/// <summary>
/// A paragraph of styled spans. Text is shaped once (codepoint → glyph → advance) and line-broken
/// per available width; results are cached, so repeated measuring during layout is cheap.
/// </summary>
internal sealed class TextElement : Element
{
    private struct Line
    {
        public int Start;       // first glyph
        public int End;         // end of visible glyphs (trailing spaces excluded)
        public int Next;        // first glyph of the following line
        public float Width;
        public float Height;
        public float Ascent;
        public float Descent;
        public bool EndsParagraph;
    }

    public readonly List<TextSpan> Spans = [];

    /// <summary>Unset means left for a left to right paragraph and right for a right to left one.</summary>
    public TextAlignment? Alignment;
    public TextStyle ParagraphStyle = TextStyle.Default;
    public TextDirection Direction = TextDirection.Auto;

    /// <summary>What this text is in a tagged document: a paragraph, or a heading of some level.</summary>
    public string Role = "P";

    // The paragraph in logical order, one entry per character.
    private int[] _text = [];
    private int[] _spanStyleOf = [];  // the style of the span the character came from
    private int[] _charStyle = [];    // the style that draws it, which may use a fallback font
    private ushort[] _charGlyph = []; // the glyph that style has for it, before any shaping
    private byte[] _charLevels = [];  // embedding level from the bidirectional algorithm
    private int _textLength;

    // Shaping results, one entry per glyph. Substitution can merge characters into one glyph or expand
    // one into several, so these do not line up with the characters above; _cluster says where each came from.
    private ushort[] _glyphs = [];
    private int[] _codepoints = [];
    private float[] _advances = [];
    private short[] _kerning = []; // adjustment after each glyph, in font units (already included in _advances)
    private int[] _styleOf = [];   // index into _styles; differs from the span's style where a fallback font is used
    private byte[] _levelOf = [];
    private int[] _cluster = [];
    private ushort[] _clusterLength = [];
    private int[] _visual = [];    // scratch: the order the glyphs of one line are drawn in
    private int _length;
    private bool _bidi;
    private bool _hasLigatures;
    private bool _hasComplexScript;
    private bool _plainDirection;
    private byte _paragraphLevel;
    private ShapingBuffer? _buffer;
    private ushort[] _reverseGlyphs = [];
    private int[] _reverseCodepoints = [];
    private short[] _reverseKerning = [];
    private readonly List<ResolvedTextStyle> _styles = [];
    private readonly List<TextStyle?> _spanStyles = [];   // parallel to _styles
    private readonly List<List<int>?> _fallbacks = [];    // parallel to _styles
    private readonly List<int> _spanOfStyle = [];         // parallel to _styles: the span a style came from
    private bool _tagSuspended;                           // a link interrupted the paragraph's marked content
    private bool _anyLink;                                // any span of the paragraph is a link
    private int _lastSpanStyle;
    private TextStyle? _shapedForStyle;
    private (int Page, int Total) _shapedForPage = (-1, -1);
    private bool _hasDynamic;

    // Line breaking results for the text starting at _linesStart.
    private readonly List<Line> _lines = [];
    private float _linesWidth = -1;
    private int _linesStart = -1;

    // Pagination state. The position is a glyph offset, not a line index: when the available width changes
    // between pages (e.g. in a Row), the remaining text is re-broken from exactly where the previous page ended.
    private int _resumeAt;
    private bool _done;

    private void EnsureShaped(LayoutContext context)
    {
        var page = (context.PageNumber, context.TotalPages);
        if (ReferenceEquals(_shapedForStyle, context.DefaultStyle) && (!_hasDynamic || _shapedForPage == page))
            return;

        _shapedForStyle = context.DefaultStyle;
        _shapedForPage = page;
        _linesWidth = -1;

        var paragraphStyle = ParagraphStyle.InheritFrom(context.DefaultStyle);
        _styles.Clear();
        _spanStyles.Clear();
        _fallbacks.Clear();
        _spanOfStyle.Clear();
        _anyLink = Spans.Exists(span => span.IsLink);
        _hasDynamic = false;

        CollectCharacters(context, paragraphStyle);

        var text = _text.AsSpan(0, _textLength);
        _bidi = !_plainDirection || Direction == TextDirection.RightToLeft;
        _length = 0;
        _paragraphLevel = 0;
        _hasLigatures = false;

        // Text that neither changes direction nor joins its letters is drawn as it is written.
        if (!_bidi && !_hasComplexScript)
        {
            AppendPlainText();
            return;
        }

        if (_bidi)
            _paragraphLevel = Bidi.Resolve(text, Direction, _charLevels.AsSpan(0, _textLength));
        else
            Array.Clear(_charLevels, 0, _textLength);

        ResolveFonts();

        for (var start = 0; start < _textLength;)
        {
            var end = RunEnd(start);
            AppendRun(start, end);
            start = end;
        }
    }

    /// <summary>
    /// The common case: one glyph per character, in the order they were written. Fonts are resolved as the
    /// glyphs are produced, and kerning is applied to every stretch that shares a font.
    /// </summary>
    private void AppendPlainText()
    {
        var runStart = 0;
        var runStyle = -1;
        var spanStyle = -1;
        ResolvedTextStyle? style = null;
        TrueTypeFont? font = null;

        for (var i = 0; i < _textLength; i++)
        {
            var codepoint = _text[i];
            if (IsJoiner(codepoint))
                continue;

            var styleIndex = _spanStyleOf[i];
            if (styleIndex != spanStyle)
            {
                spanStyle = styleIndex;
                style = _styles[styleIndex];
                font = style.Font.Font;
            }

            ushort glyph = 0;
            float advance = 0;

            if (codepoint != '\n')
            {
                glyph = font!.GetGlyph(codepoint);
                if (glyph == 0)
                {
                    foreach (var candidate in Fallbacks(styleIndex))
                    {
                        var fallback = _styles[candidate].Font.Font.GetGlyph(codepoint);
                        if (fallback != 0)
                        {
                            styleIndex = candidate;
                            glyph = fallback;
                            break;
                        }
                    }

                    advance = Advance(_styles[styleIndex], glyph);
                }
                else
                {
                    advance = Advance(style!, glyph);
                }
            }

            if (styleIndex != runStyle)
            {
                ApplyKerning(runStart, _length);
                runStart = _length;
                runStyle = styleIndex;
            }

            _glyphs[_length] = glyph;
            _codepoints[_length] = codepoint;
            _advances[_length] = advance;
            _kerning[_length] = 0;
            _styleOf[_length] = styleIndex;
            _length++;
        }

        ApplyKerning(runStart, _length);
    }

    /// <summary>Reads the text of every span into one paragraph, in logical order.</summary>
    private void CollectCharacters(LayoutContext context, TextStyle paragraphStyle)
    {
        _hasComplexScript = false;
        _plainDirection = true;
        var texts = new string[Spans.Count];
        var capacity = 0;
        for (var s = 0; s < Spans.Count; s++)
        {
            var span = Spans[s];
            if (span.DynamicText != null)
                _hasDynamic = true;

            texts[s] = span.DynamicText?.Invoke(context) ?? span.Text ?? string.Empty;
            capacity += texts[s].Length;
        }

        EnsureCapacity(capacity);

        var n = 0;
        for (var s = 0; s < Spans.Count; s++)
        {
            var textStyle = Spans[s].Style.InheritFrom(paragraphStyle);
            var primary = _styles.Count;
            _styles.Add(new ResolvedTextStyle(textStyle));
            _spanStyles.Add(textStyle);
            _fallbacks.Add(null);
            _spanOfStyle.Add(s);
            _lastSpanStyle = primary;

            foreach (var rune in texts[s].EnumerateRunes())
            {
                var codepoint = rune.Value;
                if (IsIgnorable(codepoint))
                    continue;

                _hasComplexScript |= TextShaper.ScriptOf(codepoint) != 0 || TextShaper.IsEmoji(codepoint);
                _plainDirection &= Bidi.IsPlainLeftToRight(codepoint);
                _text[n] = codepoint == '\t' ? ' ' : codepoint;
                _spanStyleOf[n] = primary;
                n++;
            }
        }

        _textLength = n;
    }

    /// <summary>Picks the font that draws each character: the span's own, or a fallback that has the glyph.</summary>
    private void ResolveFonts()
    {
        for (var i = 0; i < _textLength; i++)
        {
            var primary = _spanStyleOf[i];
            var codepoint = _text[i];
            _charStyle[i] = primary;
            _charGlyph[i] = 0;

            // A character that is never drawn keeps the font of its span: looking for another font for a
            // joiner or a presentation selector would only cut the text around it into separate runs.
            if (codepoint == '\n' || IsJoiner(codepoint))
                continue;

            var glyph = _styles[primary].Font.Font.GetGlyph(codepoint);
            if (glyph != 0)
            {
                _charGlyph[i] = glyph;
                continue;
            }

            foreach (var candidate in Fallbacks(primary))
            {
                glyph = _styles[candidate].Font.Font.GetGlyph(codepoint);
                if (glyph != 0)
                {
                    _charStyle[i] = candidate;
                    _charGlyph[i] = glyph;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The end of the run that starts at <paramref name="start"/>: a stretch of characters with one font,
    /// one embedding level and one script, which the shaper can treat as a unit.
    /// </summary>
    private int RunEnd(int start)
    {
        if (_text[start] == '\n')
            return start + 1;

        var style = _charStyle[start];
        var level = _charLevels[start];
        var script = TextShaper.ScriptOf(_text[start]);

        var end = start + 1;
        while (end < _textLength && _text[end] != '\n' && _charStyle[end] == style && _charLevels[end] == level)
        {
            var next = TextShaper.ScriptOf(_text[end]);

            // Characters that belong to no script in particular continue the run they are in.
            if (next != 0 && script != 0 && next != script)
                break;

            script = script == 0 ? next : script;
            end++;
        }

        return end;
    }

    private void AppendRun(int start, int end)
    {
        var styleIndex = _charStyle[start];
        var style = _styles[styleIndex];
        var font = style.Font.Font;
        var level = _charLevels[start];
        var first = _length;

        if (_text[start] == '\n')
        {
            Append(0, start, 1, styleIndex, level, 0);
            return;
        }

        var script = ScriptOfRun(start, end);
        var rightToLeft = (level & 1) == 1;

        // Text of no particular script, running left to right, is drawn as it is written: one glyph per
        // character, with the glyph the font manager already found. A font that draws pictures composes
        // them from several characters, so its text goes through the shaper as a cursive script does.
        if (script == 0 && !rightToLeft && font.Colors.IsEmpty && !RunHasEmoji(start, end))
        {
            for (var i = start; i < end; i++)
            {
                if (!IsJoiner(_text[i]))
                    Append(_charGlyph[i], i, 1, styleIndex, level, Advance(style, _charGlyph[i]));
            }
        }
        else
        {
            _buffer ??= new ShapingBuffer();
            TextShaper.Shape(font, _text.AsSpan(start, end - start), script, rightToLeft, _buffer);

            foreach (var shaped in _buffer.Glyphs)
            {
                _hasLigatures |= shaped.Length > 1;
                Append(shaped.Glyph, start + shaped.Cluster, shaped.Length, styleIndex, level, Advance(style, shaped.Glyph));
            }
        }

        ApplyKerning(first, _length);
    }

    private static float Advance(ResolvedTextStyle style, ushort glyph) =>
        style.Font.Font.GetAdvance(glyph) * style.Scale + style.LetterSpacing;

    private bool RunHasEmoji(int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (TextShaper.IsEmoji(_text[i]))
                return true;
        }

        return false;
    }

    private uint ScriptOfRun(int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var script = TextShaper.ScriptOf(_text[i]);
            if (script != 0)
                return script;
        }

        return 0;
    }

    private void Append(ushort glyph, int cluster, int clusterLength, int styleIndex, byte level, float advance)
    {
        if (_length == _glyphs.Length)
            GrowGlyphs(_length + 1);

        _glyphs[_length] = glyph;
        _codepoints[_length] = _text[cluster];
        _advances[_length] = advance;
        _kerning[_length] = 0;
        _styleOf[_length] = styleIndex;
        _levelOf[_length] = level;
        _cluster[_length] = cluster;
        _clusterLength[_length] = (ushort)clusterLength;
        _length++;
    }

    private void EnsureCapacity(int characters)
    {
        if (_text.Length < characters)
        {
            _text = new int[characters];
            _spanStyleOf = new int[characters];
            _charStyle = new int[characters];
            _charGlyph = new ushort[characters];
            _charLevels = new byte[characters];
        }

        // Nothing has been written yet, so the buffers are replaced rather than copied.
        if (_glyphs.Length < characters)
        {
            _glyphs = new ushort[characters];
            _codepoints = new int[characters];
            _advances = new float[characters];
            _kerning = new short[characters];
            _styleOf = new int[characters];
            _levelOf = new byte[characters];
            _cluster = new int[characters];
            _clusterLength = new ushort[characters];
            _visual = new int[characters];
        }
    }

    private void GrowGlyphs(int capacity)
    {
        // Substitution can add glyphs, so the buffer grows beyond the number of characters.
        capacity = Math.Max(capacity, _glyphs.Length * 2);
        Array.Resize(ref _glyphs, capacity);
        Array.Resize(ref _codepoints, capacity);
        Array.Resize(ref _advances, capacity);
        Array.Resize(ref _kerning, capacity);
        Array.Resize(ref _styleOf, capacity);
        Array.Resize(ref _levelOf, capacity);
        Array.Resize(ref _cluster, capacity);
        Array.Resize(ref _clusterLength, capacity);
        Array.Resize(ref _visual, capacity);
    }

    /// <summary>
    /// Characters that are not drawn: carriage return, soft hyphen, zero-width space, word joiner,
    /// variation selectors and the byte order mark. The zero width joiner and non-joiner are kept, and so
    /// is the emoji presentation selector, because they decide how the characters beside them are drawn.
    /// </summary>
    /// <summary>Characters that shape the ones beside them but are never drawn themselves.</summary>
    private static bool IsJoiner(int codepoint) => codepoint is 0x200C or 0x200D or 0xFE0E or 0xFE0F;

    private static bool IsIgnorable(int cp) =>
        cp is '\r' or 0xAD or 0x200B or (>= 0x2060 and <= 0x2064) or (>= 0xFE00 and <= 0xFE0D) or 0xFEFF;

    /// <summary>The fallback fonts of a span, resolved the first time one of its characters needs one.</summary>
    private List<int> Fallbacks(int primary)
    {
        if (_fallbacks[primary] is { } cached)
            return cached;

        var result = new List<int>();
        var textStyle = _spanStyles[primary]!;
        var weight = textStyle.Weight ?? FontWeight.Normal;
        var italic = textStyle.IsItalic ?? false;

        foreach (var family in FontManager.FallbackCandidates(textStyle))
        {
            if (!FontManager.TryResolveFamily(family, weight, italic, out var font) ||
                ReferenceEquals(font.Font, _styles[primary].Font.Font))
                continue;

            result.Add(_styles.Count);
            _styles.Add(new ResolvedTextStyle(textStyle, font));
            _spanStyles.Add(textStyle);
            _fallbacks.Add(null);
            _spanOfStyle.Add(_spanOfStyle[primary]);
        }

        _fallbacks[primary] = result;
        return result;
    }

    /// <summary>Applies pair kerning between consecutive glyphs of one run.</summary>
    private void ApplyKerning(int start, int end)
    {
        for (var i = start; i + 1 < end; i++)
        {
            if (_codepoints[i] == '\n' || _codepoints[i + 1] == '\n')
                continue;

            var style = _styles[_styleOf[i]];
            var kerning = style.Font.Font.Kerning;
            if (kerning.IsEmpty)
                continue;

            var value = kerning.Get(_glyphs[i], _glyphs[i + 1]);
            if (value == 0)
                continue;

            _kerning[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            _advances[i] += value * style.Scale;
        }
    }

    private void EnsureLines(float maxWidth)
    {
        if (_linesStart == _resumeAt && Math.Abs(_linesWidth - maxWidth) < Size.Epsilon)
            return;

        _linesWidth = maxWidth;
        _linesStart = _resumeAt;
        _lines.Clear();

        var i = Math.Min(_resumeAt, _length);
        do
        {
            var start = i;
            var lastBreak = -1;
            float width = 0;
            var hardBreak = false;

            while (i < _length)
            {
                var cp = _codepoints[i];
                if (cp == '\n')
                {
                    hardBreak = true;
                    break;
                }

                var advance = _advances[i];
                if (cp != ' ' && i > start && width + advance > maxWidth + Size.Epsilon)
                {
                    if (lastBreak > start)
                        i = lastBreak;
                    break;
                }

                width += advance;
                i++;

                if (cp is ' ' or '-' or '/' or 0x2013 or 0x2014)
                    lastBreak = i;
            }

            var end = i;
            while (end > start && _codepoints[end - 1] == ' ')
                end--;

            if (hardBreak)
            {
                i++; // consume '\n'
            }
            else
            {
                while (i < _length && _codepoints[i] == ' ')
                    i++; // spaces at a soft break are not carried to the next line
            }

            AddLine(start, end, i, endsParagraph: hardBreak || i >= _length);

            // A trailing newline produces a final empty line.
            if (hardBreak && i == _length)
                AddLine(i, i, i, endsParagraph: true);
        }
        while (i < _length);
    }

    private void AddLine(int start, int end, int next, bool endsParagraph)
    {
        float width = 0, ascent = 0, descent = 0, height = 0;

        for (var k = start; k < end; k++)
            width += _advances[k];

        // The last glyph's kerning pairs it with the first glyph of the next line; it doesn't belong to this line.
        if (end > start)
            width -= _kerning[end - 1] * _styles[_styleOf[end - 1]].Scale;

        if (start == end)
        {
            // Empty line: size it with the style of the character at that position (or the last span).
            var style = start < _length ? _styles[_styleOf[start]] : _styles[_lastSpanStyle];
            (ascent, descent, height) = (style.Ascent, style.Descent, style.LineHeight);
        }
        else
        {
            var previous = -1;
            for (var k = start; k < end; k++)
            {
                var s = _styleOf[k];
                if (s == previous)
                    continue;
                previous = s;
                var style = _styles[s];
                ascent = Math.Max(ascent, style.Ascent);
                descent = Math.Max(descent, style.Descent);
                height = Math.Max(height, style.LineHeight);
            }
        }

        _lines.Add(new Line
        {
            Start = start,
            End = end,
            Next = next,
            Width = width,
            Height = height,
            Ascent = ascent,
            Descent = descent,
            EndsParagraph = endsParagraph,
        });
    }

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        if (Spans.Count == 0 || _done)
            return SpacePlan.Empty;

        EnsureShaped(context);
        EnsureLines(available.Width);

        float height = 0, width = 0;
        var count = 0;
        for (; count < _lines.Count; count++)
        {
            var line = _lines[count];
            if (height + line.Height > available.Height + Size.Epsilon)
                break;
            height += line.Height;
            width = Math.Max(width, line.Width);
        }

        if (count == 0)
            return SpacePlan.Wrap;

        return count == _lines.Count ? SpacePlan.Full(width, height) : SpacePlan.Partial(width, height);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        if (Spans.Count == 0 || _done)
            return;

        EnsureShaped(context);
        EnsureLines(available.Width);

        _tagSuspended = false;
        using var tag = context.Tag(Role);

        float y = 0;
        var drawn = 0;
        for (; drawn < _lines.Count; drawn++)
        {
            var line = _lines[drawn];
            if (y + line.Height > available.Height + Size.Epsilon)
                break;

            var halfLeading = (line.Height - line.Ascent - line.Descent) / 2;
            DrawLine(line, y + halfLeading + line.Ascent, available.Width, context);
            y += line.Height;
        }

        if (drawn == _lines.Count)
        {
            _done = true;
            return;
        }

        // Continue from the first line that did not fit. The cached lines after it stay valid,
        // because breaking from that position at the same width yields the same lines.
        _resumeAt = _lines[drawn].Start;
        _lines.RemoveRange(0, drawn);
        _linesStart = _resumeAt;
    }

    private void DrawLine(in Line line, float baseline, float availableWidth, LayoutContext context)
    {
        if (line.Start == line.End)
            return;

        // A right to left paragraph is aligned to the right unless the caller said otherwise.
        var alignment = Alignment ?? ((_paragraphLevel & 1) == 1 ? TextAlignment.Right : TextAlignment.Left);

        var extra = availableWidth - line.Width;
        float x = alignment switch
        {
            TextAlignment.Center => extra / 2,
            TextAlignment.Right => extra,
            _ => 0,
        };

        float spaceStretch = 0;
        if (alignment == TextAlignment.Justify && !line.EndsParagraph && extra > 0)
        {
            var spaces = 0;
            for (var k = line.Start; k < line.End; k++)
            {
                if (_codepoints[k] == ' ')
                    spaces++;
            }

            if (spaces > 0)
                spaceStretch = extra / spaces;
        }

        var canvas = context.Canvas;
        var count = line.End - line.Start;
        var order = Order(line);

        var runStart = 0;
        var runX = x;

        for (var k = 0; k <= count; k++)
        {
            var position = k < count ? order[k] : -1;

            // A run is a stretch of glyphs with one style and one direction, drawn in one go. A link
            // also ends a run, so that the area a reader clicks covers exactly the linked words.
            var boundary = k == count
                || _styleOf[position] != _styleOf[order[runStart]]
                || (_bidi && _levelOf[position] != _levelOf[order[runStart]])
                || (_anyLink && SpanOf(position) != SpanOf(order[runStart]))
                || (spaceStretch > 0 && _codepoints[position] == ' ');

            if (!boundary)
                continue;

            if (k > runStart)
            {
                var style = _styles[_styleOf[order[runStart]]];
                var span = Spans[SpanOf(order[runStart])];
                var rightToLeft = _bidi && (_levelOf[order[runStart]] & 1) == 1;
                var from = Math.Min(order[runStart], order[k - 1]);
                var to = Math.Max(order[runStart], order[k - 1]);
                runX += span.IsLink
                    ? DrawLinkedRun(span, style, from, to, rightToLeft, runX, baseline, context)
                    : DrawRun(style, from, to, rightToLeft, runX, baseline, ResumeTag(context));
            }

            runStart = k;

            // In justified text every space becomes its own positioned gap.
            if (spaceStretch > 0 && k < count && _codepoints[position] == ' ')
            {
                var style = _styles[_styleOf[position]];
                DrawDecorations(style, runX, baseline, _advances[position] + spaceStretch, ResumeTag(context));
                runX += _advances[position] + spaceStretch;
                runStart = k + 1;
            }
        }
    }

    /// <summary>The span a character came from, through the style it was drawn with.</summary>
    private int SpanOf(int position) => _spanOfStyle[_styleOf[position]];

    /// <summary>
    /// Draws a run of a link and records the area that opens it. In a tagged document the run becomes a
    /// structure element of its own: a reader for the blind announces the link, not the paragraph
    /// around it, and PDF/UA asks for the annotation to sit inside that element.
    /// </summary>
    private float DrawLinkedRun(TextSpan span, ResolvedTextStyle style, int from, int to, bool rightToLeft, float x, float baseline, LayoutContext context)
    {
        var canvas = context.Canvas;
        var tree = context.Artifact ? null : context.Structure;
        StructureElement? element = null;

        if (tree != null)
        {
            // The paragraph's own marked content ends here and resumes after the link.
            element = tree.Push("Link");
            canvas.BeginTagged("Link", tree.NextMarkedContent(context.PageNumber - 1));
            _tagSuspended = true;
        }

        var width = DrawRun(style, from, to, rightToLeft, x, baseline, canvas);

        var (left, top) = canvas.ToPdf(x, baseline - style.Ascent);
        var (right, bottom) = canvas.ToPdf(x + width, baseline + style.Descent);
        context.PageLinks.Add(new LinkArea(
            Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom),
            span.Uri, span.Section)
        {
            Structure = element,
        });

        if (tree != null)
        {
            canvas.EndTagged();
            tree.Pop();
        }

        return width;
    }

    /// <summary>Reopens the marked content of the paragraph after a link ended it.</summary>
    private Canvas ResumeTag(LayoutContext context)
    {
        if (_tagSuspended && context.Structure is { } tree)
        {
            _tagSuspended = false;
            context.Canvas.BeginTagged(Role, tree.NextMarkedContent(context.PageNumber - 1));
        }

        return context.Canvas;
    }

    /// <summary>The order the glyphs of a line are drawn in; only bidirectional text differs from logical order.</summary>
    private ReadOnlySpan<int> Order(in Line line)
    {
        var count = line.End - line.Start;
        var order = _visual.AsSpan(0, count);
        for (var i = 0; i < count; i++)
            order[i] = line.Start + i;

        if (_bidi)
            Bidi.ReorderByLevel(_levelOf, order);

        return order;
    }

    /// <summary>Draws the glyphs from <paramref name="from"/> to <paramref name="to"/> and returns their width.</summary>
    private float DrawRun(ResolvedTextStyle style, int from, int to, bool rightToLeft, float x, float baseline, Canvas canvas)
    {
        float width = 0;
        for (var k = from; k <= to; k++)
            width += _advances[k];

        if (_hasLigatures)
        {
            // A glyph that replaced several characters has to extract as all of them.
            var usage = canvas.Resources.GetFont(style.Font.Font);
            for (var k = from; k <= to; k++)
            {
                if (_clusterLength[k] > 1)
                    usage.UseSequence(_glyphs[k], _text.AsSpan(_cluster[k], _clusterLength[k]));
            }
        }

        // An emoji font draws some of its glyphs in colour, one at a time, with plain text around them.
        if (style.Font.Font.Colors.IsEmpty)
            DrawGlyphRange(style, from, to, rightToLeft, x, baseline, canvas);
        else
            DrawColoredRun(style, from, to, rightToLeft, x, baseline, canvas);

        DrawDecorations(style, x, baseline, width, canvas);
        return width;
    }

    private void DrawColoredRun(ResolvedTextStyle style, int from, int to, bool rightToLeft, float x, float baseline, Canvas canvas)
    {
        var colors = style.Font.Font.Colors;
        var pen = x;
        var plainFrom = -1;
        var plainTo = -1;

        for (var i = 0; i <= to - from; i++)
        {
            var position = rightToLeft ? to - i : from + i;
            if (colors.KindOf(_glyphs[position]) == ColorGlyphKind.None)
            {
                plainFrom = plainFrom < 0 ? position : plainFrom;
                plainTo = position;
                continue;
            }

            pen += Flush();
            if (!canvas.DrawColorGlyph(style, pen, baseline, _glyphs[position], _codepoints[position]))
                DrawGlyphRange(style, position, position, rightToLeft, pen, baseline, canvas);

            pen += _advances[position];
        }

        Flush();

        float Flush()
        {
            if (plainFrom < 0)
                return 0;

            var width = DrawGlyphRange(
                style,
                Math.Min(plainFrom, plainTo),
                Math.Max(plainFrom, plainTo),
                rightToLeft,
                pen,
                baseline,
                canvas);

            plainFrom = plainTo = -1;
            return width;
        }
    }

    /// <summary>Draws a stretch of glyphs in one go and returns how wide it is.</summary>
    private float DrawGlyphRange(ResolvedTextStyle style, int from, int to, bool rightToLeft, float x, float baseline, Canvas canvas)
    {
        float width = 0;
        for (var k = from; k <= to; k++)
            width += _advances[k];

        var length = to - from + 1;
        if (!rightToLeft)
        {
            canvas.DrawGlyphs(style, x, baseline, _glyphs.AsSpan(from, length), _codepoints.AsSpan(from, length), _kerning.AsSpan(from, length));
            return width;
        }

        // The glyphs of a right to left run are drawn from the last one; the kerning of a pair follows it.
        if (_reverseGlyphs.Length < length)
        {
            _reverseGlyphs = new ushort[length];
            _reverseCodepoints = new int[length];
            _reverseKerning = new short[length];
        }

        for (var i = 0; i < length; i++)
        {
            var source = to - i;
            _reverseGlyphs[i] = _glyphs[source];
            _reverseCodepoints[i] = _codepoints[source];
            _reverseKerning[i] = source > from ? _kerning[source - 1] : (short)0;
        }

        canvas.DrawGlyphs(style, x, baseline, _reverseGlyphs.AsSpan(0, length), _reverseCodepoints.AsSpan(0, length), _reverseKerning.AsSpan(0, length));
        return width;
    }

    private static void DrawDecorations(ResolvedTextStyle style, float x, float baseline, float width, Canvas canvas)
    {
        if (!style.Underline && !style.Strikethrough)
            return;

        var font = style.Font.Font;
        if (style.Underline)
        {
            var thickness = Math.Max(0.5f, font.UnderlineThickness * style.Scale);
            canvas.FillRectangle(x, baseline - font.UnderlinePosition * style.Scale - thickness / 2, width, thickness, style.Color);
        }

        if (style.Strikethrough)
        {
            var thickness = Math.Max(0.5f, font.StrikeoutSize * style.Scale);
            canvas.FillRectangle(x, baseline - font.StrikeoutPosition * style.Scale - thickness / 2, width, thickness, style.Color);
        }
    }

    internal override void Reset()
    {
        _resumeAt = 0;
        _done = false;
    }

    /// <summary>Plain text of all static spans; used for diagnostics.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var span in Spans)
            builder.Append(span.Text ?? "{dynamic}");
        return builder.ToString();
    }
}
