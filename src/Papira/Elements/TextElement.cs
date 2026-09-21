using System.Text;
using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

/// <summary>A text style resolved to a concrete font face and point-based metrics.</summary>
internal sealed class ResolvedTextStyle
{
    public ResolvedTextStyle(TextStyle style)
    {
        Font = FontManager.Resolve(style.Family, style.Weight ?? FontWeight.Normal, style.IsItalic ?? false);
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
    public TextAlignment Alignment = TextAlignment.Left;
    public TextStyle ParagraphStyle = TextStyle.Default;

    // Shaping results, one entry per codepoint.
    private ushort[] _glyphs = [];
    private int[] _codepoints = [];
    private float[] _advances = [];
    private int[] _spanOf = [];
    private int _length;
    private ResolvedTextStyle[] _styles = [];
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
        _styles = new ResolvedTextStyle[Spans.Count];
        _hasDynamic = false;

        var texts = new string[Spans.Count];
        var capacity = 0;
        for (var s = 0; s < Spans.Count; s++)
        {
            var span = Spans[s];
            if (span.DynamicText != null)
                _hasDynamic = true;
            texts[s] = span.DynamicText?.Invoke(context) ?? span.Text ?? string.Empty;
            capacity += texts[s].Length;
            _styles[s] = new ResolvedTextStyle(span.Style.InheritFrom(paragraphStyle));
        }

        if (_glyphs.Length < capacity)
        {
            _glyphs = new ushort[capacity];
            _codepoints = new int[capacity];
            _advances = new float[capacity];
            _spanOf = new int[capacity];
        }

        var n = 0;
        for (var s = 0; s < Spans.Count; s++)
        {
            var style = _styles[s];
            var font = style.Font.Font;

            foreach (var rune in texts[s].EnumerateRunes())
            {
                var cp = rune.Value;
                switch (cp)
                {
                    case '\r' or 0xAD or 0x200B:
                        continue; // CR, soft hyphen and zero-width space are not rendered
                    case '\t':
                        cp = ' ';
                        break;
                }

                ushort glyph = 0;
                float advance = 0;
                if (cp != '\n')
                {
                    glyph = font.GetGlyph(cp);
                    advance = font.GetAdvance(glyph) * style.Scale + style.LetterSpacing;
                }

                _glyphs[n] = glyph;
                _codepoints[n] = cp;
                _advances[n] = advance;
                _spanOf[n] = s;
                n++;
            }
        }

        _length = n;
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

        if (start == end)
        {
            // Empty line: size it with the style of the character at that position (or the last span).
            var style = start < _length ? _styles[_spanOf[start]] : _styles[^1];
            (ascent, descent, height) = (style.Ascent, style.Descent, style.LineHeight);
        }
        else
        {
            var previous = -1;
            for (var k = start; k < end; k++)
            {
                var s = _spanOf[k];
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

        var extra = availableWidth - line.Width;
        float x = Alignment switch
        {
            TextAlignment.Center => extra / 2,
            TextAlignment.Right => extra,
            _ => 0,
        };

        float spaceStretch = 0;
        if (Alignment == TextAlignment.Justify && !line.EndsParagraph && extra > 0)
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
        var runStart = line.Start;
        var runX = x;

        for (var k = line.Start; k <= line.End; k++)
        {
            var boundary = k == line.End
                || _spanOf[k] != _spanOf[runStart]
                || (spaceStretch > 0 && _codepoints[k] == ' ');

            if (boundary)
            {
                if (k > runStart)
                {
                    var style = _styles[_spanOf[runStart]];
                    var runWidth = DrawRun(style, runStart, k, runX, baseline, canvas);
                    runX += runWidth;
                }

                runStart = k;

                // In justified text every space becomes its own positioned gap.
                if (spaceStretch > 0 && k < line.End && _codepoints[k] == ' ')
                {
                    var style = _styles[_spanOf[k]];
                    DrawDecorations(style, runX, baseline, _advances[k] + spaceStretch, canvas);
                    runX += _advances[k] + spaceStretch;
                    runStart = k + 1;
                }
            }
        }
    }

    private float DrawRun(ResolvedTextStyle style, int start, int end, float x, float baseline, Canvas canvas)
    {
        float width = 0;
        for (var k = start; k < end; k++)
            width += _advances[k];

        canvas.DrawGlyphs(style, x, baseline, _glyphs.AsSpan(start, end - start), _codepoints.AsSpan(start, end - start));
        DrawDecorations(style, x, baseline, width, canvas);
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
