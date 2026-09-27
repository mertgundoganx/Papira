namespace Papira.Infrastructure;

/// <summary>The element a selector is matched against: its tag name, its id and its classes.</summary>
internal readonly record struct StyleTarget(string Tag, string? Id, string[] Classes);

/// <summary>
/// The rules of a &lt;style&gt; element or a style sheet passed alongside a document. The selectors
/// that documents and drawings use in practice are matched: a tag name, a class, an id, the universal
/// selector, and those combined with the descendant (space) and child (&gt;) combinators. Anything
/// else — attribute selectors, pseudo classes, sibling combinators — is skipped rather than guessed at,
/// so a rule either applies as CSS says or not at all.
/// </summary>
internal sealed class StyleSheet
{
    private const int MaxRules = 4096;
    private const int MaxParts = 8;

    private readonly List<Rule> _rules = [];

    public bool IsEmpty => _rules.Count == 0;

    public void Add(string css)
    {
        var text = RemoveComments(css);
        var index = 0;
        while (index < text.Length && _rules.Count < MaxRules)
        {
            var open = text.IndexOf('{', index);
            if (open < 0)
                break;

            var close = text.IndexOf('}', open);
            if (close < 0)
                break;

            var selectors = text[index..open];

            // An at-rule (@media, @font-face) brings its own block, which is skipped whole.
            if (selectors.Contains('@', StringComparison.Ordinal))
            {
                index = close + 1;
                continue;
            }

            var declarations = ParseDeclarations(text[(open + 1)..close]);
            if (declarations.Count > 0)
            {
                foreach (var selector in selectors.Split(','))
                {
                    if (TryParseSelector(selector.Trim(), out var rule))
                        _rules.Add(rule with { Declarations = declarations, Order = _rules.Count });
                }
            }

            index = close + 1;
        }
    }

    /// <summary>Collects the declarations that apply to a single element, weakest rule first.</summary>
    public void Apply(StyleTarget target, Dictionary<string, string> into) => Apply([target], into);

    /// <summary>
    /// Collects the declarations that apply to the last element of <paramref name="path"/>, which runs
    /// from the root of the document to the element itself so that combinators can be matched.
    /// </summary>
    public void Apply(IReadOnlyList<StyleTarget> path, Dictionary<string, string> into)
    {
        if (_rules.Count == 0 || path.Count == 0)
            return;

        foreach (var rule in _rules.Where(r => r.Matches(path)).OrderBy(r => r.Specificity).ThenBy(r => r.Order))
        {
            foreach (var declaration in rule.Declarations)
                into[declaration.Key] = declaration.Value;
        }
    }

    public static Dictionary<string, string> ParseDeclarations(string text)
    {
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in Split(text))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
                continue;

            var name = part[..colon].Trim().ToLowerInvariant();
            var value = part[(colon + 1)..].Trim();

            // "!important" does not change which rule wins here, so it is only stripped.
            if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
                value = value[..^"!important".Length].TrimEnd();

            if (name.Length > 0 && value.Length > 0)
                declarations[name] = value;
        }

        return declarations;
    }

    /// <summary>Splits declarations at semicolons that are not inside a function such as rgb(...).</summary>
    private static List<string> Split(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case ';' when depth == 0:
                    parts.Add(text[start..i]);
                    start = i + 1;
                    break;
                default:
                    break;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    private static string RemoveComments(string css)
    {
        var start = css.IndexOf("/*", StringComparison.Ordinal);
        if (start < 0)
            return css;

        var builder = new System.Text.StringBuilder(css.Length);
        var index = 0;
        while (start >= 0)
        {
            builder.Append(css, index, start - index);
            var end = css.IndexOf("*/", start + 2, StringComparison.Ordinal);
            if (end < 0)
                return builder.ToString();

            index = end + 2;
            start = css.IndexOf("/*", index, StringComparison.Ordinal);
        }

        builder.Append(css, index, css.Length - index);
        return builder.ToString();
    }

    private static bool TryParseSelector(string selector, out Rule rule)
    {
        rule = default;
        if (selector.Length == 0 || selector.Any(c => c is '+' or '~' or '[' or ':'))
            return false; // sibling combinators, attributes and pseudo classes are out of scope

        var parts = new List<Compound>();
        var child = new List<bool>();
        var directChild = false;

        foreach (var token in selector.Replace(">", " > ", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == ">")
            {
                if (parts.Count == 0)
                    return false;

                directChild = true;
                continue;
            }

            if (!TryParseCompound(token, out var compound))
                return false;

            parts.Add(compound);
            child.Add(directChild);
            directChild = false;
        }

        if (parts.Count == 0 || parts.Count > MaxParts || directChild)
            return false;

        rule = new Rule([.. parts], [.. child], [], 0);
        return true;
    }

    private static bool TryParseCompound(string selector, out Compound compound)
    {
        compound = default;
        string? tag = null, id = null;
        var classes = new List<string>(2);
        var index = 0;
        while (index < selector.Length)
        {
            var next = selector.IndexOfAny(['.', '#'], index + 1);
            var part = next < 0 ? selector[index..] : selector[index..next];
            switch (part[0])
            {
                case '.' when part.Length > 1:
                    classes.Add(part[1..]);
                    break;
                case '#' when part.Length > 1:
                    id = part[1..];
                    break;
                case '*':
                    break;
                default:
                    if (!char.IsAsciiLetter(part[0]))
                        return false;

                    tag = part.ToLowerInvariant();
                    break;
            }

            if (next < 0)
                break;

            index = next;
        }

        compound = new Compound(tag, id, [.. classes]);
        return true;
    }

    private readonly record struct Compound(string? Tag, string? Id, string[] Classes)
    {
        public int Specificity => (Id == null ? 0 : 100) + (Classes.Length * 10) + (Tag == null ? 0 : 1);

        public bool Matches(in StyleTarget target)
        {
            if (Tag != null && !string.Equals(Tag, target.Tag, StringComparison.Ordinal))
                return false;

            if (Id != null && !string.Equals(Id, target.Id, StringComparison.Ordinal))
                return false;

            foreach (var required in Classes)
            {
                if (Array.IndexOf(target.Classes, required) < 0)
                    return false;
            }

            return true;
        }
    }

    private readonly record struct Rule(Compound[] Parts, bool[] DirectChild, Dictionary<string, string> Declarations, int Order)
    {
        /// <summary>CSS specificity, counting ids, then classes, then tag names.</summary>
        public int Specificity => Parts.Sum(part => part.Specificity);

        /// <summary>
        /// Matches the last element of the path, then walks the selector and the ancestors leftwards.
        /// A descendant combinator may skip ancestors, so it tries each one in turn.
        /// </summary>
        public bool Matches(IReadOnlyList<StyleTarget> path) => MatchFrom(Parts.Length - 1, path.Count - 1, path);

        private bool MatchFrom(int part, int element, IReadOnlyList<StyleTarget> path)
        {
            if (element < 0 || !Parts[part].Matches(path[element]))
                return false;

            if (part == 0)
                return true;

            if (DirectChild[part])
                return MatchFrom(part - 1, element - 1, path);

            for (var ancestor = element - 1; ancestor >= 0; ancestor--)
            {
                if (MatchFrom(part - 1, ancestor, path))
                    return true;
            }

            return false;
        }
    }
}
