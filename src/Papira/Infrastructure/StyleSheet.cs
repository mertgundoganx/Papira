namespace Papira.Infrastructure;

/// <summary>
/// The element a selector is matched against: its tag name, its id, its classes, and where it stands
/// among the elements beside it, which is what the structural pseudo classes ask about. An index of zero
/// means that is not known, and such a rule then does not apply.
/// </summary>
internal readonly record struct StyleTarget(string Tag, string? Id, string[] Classes, int Index = 0, int Count = 0);

/// <summary>
/// The rules of a &lt;style&gt; element or a style sheet passed alongside a document. The selectors
/// that documents and drawings use in practice are matched: a tag name, a class, an id, the universal
/// selector, those combined with the descendant (space) and child (&gt;) combinators, the pseudo classes
/// that ask where an element stands among the others (<c>:first-child</c>, <c>:last-child</c>,
/// <c>:nth-child</c>, <c>:not</c>) and the <c>::before</c> and <c>::after</c> an element may be given.
/// Anything else — attribute selectors, sibling combinators — is skipped rather than guessed at, so a
/// rule either applies as CSS says or not at all.
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
                    if (!TryParseSelector(selector.Trim(), out var rule))
                        continue;

                    _rules.Add(rule with { Declarations = declarations, Order = _rules.Count });
                    HasPseudoElements |= rule.Pseudo != null;
                }
            }

            index = close + 1;
        }
    }

    /// <summary>Collects the declarations that apply to a single element, weakest rule first.</summary>
    public void Apply(StyleTarget target, Dictionary<string, string> into) => Apply([target], into);

    /// <summary>True where a rule gives the element something before or after what it holds itself.</summary>
    public bool HasPseudoElements { get; private set; }

    /// <summary>
    /// Collects the declarations that apply to the last element of <paramref name="path"/>, which runs
    /// from the root of the document to the element itself so that combinators can be matched.
    /// </summary>
    public void Apply(IReadOnlyList<StyleTarget> path, Dictionary<string, string> into, string? pseudo = null)
    {
        if (_rules.Count == 0 || path.Count == 0)
            return;

        foreach (var rule in _rules.Where(r => r.Pseudo == pseudo && r.Matches(path)).OrderBy(r => r.Specificity).ThenBy(r => r.Order))
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
        if (selector.Length == 0 || selector.Any(c => c is '+' or '~' or '['))
            return false; // sibling combinators and attribute selectors are out of scope

        // What an element is given before or after what it holds is kept apart from the element's own rules.
        string? pseudo = null;
        foreach (var name in (string[])["::before", "::after", ":before", ":after"])
        {
            if (!selector.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                continue;

            pseudo = name.TrimStart(':').ToLowerInvariant();
            selector = selector[..^name.Length];
            break;
        }

        if (selector.Length == 0)
            return false;

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

        rule = new Rule([.. parts], [.. child], [], 0) { Pseudo = pseudo };
        return true;
    }

    /// <summary>What a pseudo class asks about the element: where it stands, or what it is not.</summary>
    private enum PseudoKind : byte { First, Last, Nth, Odd, Even, Not }

    private readonly record struct Condition(PseudoKind Kind, int Number, string? NotTag, string? NotId, string? NotClass)
    {
        public bool Matches(in StyleTarget target)
        {
            if (Kind == PseudoKind.Not)
            {
                var hit = (NotTag == null || string.Equals(NotTag, target.Tag, StringComparison.Ordinal)) &&
                    (NotId == null || string.Equals(NotId, target.Id, StringComparison.Ordinal)) &&
                    (NotClass == null || Array.IndexOf(target.Classes, NotClass) >= 0);
                return !hit;
            }

            // Where an element stands among the others is only known of a document's own elements.
            if (target.Index <= 0)
                return false;

            return Kind switch
            {
                PseudoKind.First => target.Index == 1,
                PseudoKind.Last => target.Index == target.Count,
                PseudoKind.Odd => target.Index % 2 == 1,
                PseudoKind.Even => target.Index % 2 == 0,
                _ => target.Index == Number,
            };
        }
    }

    /// <summary>Reads the pseudo classes off the end of a compound selector, e.g. "tr:nth-child(odd)".</summary>
    private static bool TryParseConditions(ref string selector, out Condition[] conditions)
    {
        conditions = [];
        var colon = selector.IndexOf(':');
        if (colon < 0)
            return true;

        var found = new List<Condition>(2);
        var rest = selector[colon..];
        selector = selector[..colon];

        while (rest.Length > 0)
        {
            if (rest[0] != ':')
                return false;

            var end = 1;
            while (end < rest.Length && rest[end] != ':' && rest[end] != '(')
                end++;

            var name = rest[1..end].ToLowerInvariant();
            string? argument = null;
            if (end < rest.Length && rest[end] == '(')
            {
                var close = rest.IndexOf(')', end);
                if (close < 0)
                    return false;

                argument = rest[(end + 1)..close].Trim();
                end = close + 1;
            }

            rest = rest[end..];
            if (Condition(name, argument) is not { } condition)
                return false;

            found.Add(condition);
        }

        conditions = [.. found];
        return true;

        static Condition? Condition(string name, string? argument) => (name, argument) switch
        {
            ("first-child" or "first-of-type", null) => new Condition(PseudoKind.First, 0, null, null, null),
            ("last-child" or "last-of-type", null) => new Condition(PseudoKind.Last, 0, null, null, null),
            ("only-child", null) => new Condition(PseudoKind.Nth, 1, null, null, null),
            ("nth-child" or "nth-of-type", "odd") => new Condition(PseudoKind.Odd, 0, null, null, null),
            ("nth-child" or "nth-of-type", "even") => new Condition(PseudoKind.Even, 0, null, null, null),
            ("nth-child" or "nth-of-type", not null) => int.TryParse(argument, out var number) && number > 0
                ? new Condition(PseudoKind.Nth, number, null, null, null)
                : null,
            ("not", not null) => Negation(argument),
            _ => null,
        };

        static Condition? Negation(string argument) => argument.Length < 2 || argument.Any(c => c is ' ' or ',' or ':')
            ? null
            : argument[0] switch
            {
                '.' => new Condition(PseudoKind.Not, 0, null, null, argument[1..]),
                '#' => new Condition(PseudoKind.Not, 0, null, argument[1..], null),
                _ => char.IsAsciiLetter(argument[0]) ? new Condition(PseudoKind.Not, 0, argument.ToLowerInvariant(), null, null) : null,
            };
    }

    private static bool TryParseCompound(string selector, out Compound compound)
    {
        compound = default;
        if (!TryParseConditions(ref selector, out var conditions))
            return false;

        if (selector.Length == 0)
            selector = "*";

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

        compound = new Compound(tag, id, [.. classes], conditions);
        return true;
    }

    private readonly record struct Compound(string? Tag, string? Id, string[] Classes, Condition[] Conditions)
    {
        public int Specificity =>
            (Id == null ? 0 : 100) + (Classes.Length * 10) + (Conditions.Length * 10) + (Tag == null ? 0 : 1);

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

            foreach (var condition in Conditions)
            {
                if (!condition.Matches(target))
                    return false;
            }

            return true;
        }
    }

    private readonly record struct Rule(Compound[] Parts, bool[] DirectChild, Dictionary<string, string> Declarations, int Order)
    {
        /// <summary>What the rule gives the element around what it holds: "before", "after", or nothing.</summary>
        public string? Pseudo { get; init; }

        /// <summary>CSS specificity, counting ids, then classes, then tag names.</summary>
        public int Specificity => Parts.Sum(part => part.Specificity) + (Pseudo == null ? 0 : 1);

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
