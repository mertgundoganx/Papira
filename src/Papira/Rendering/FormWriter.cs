using System.IO.Compression;
using Papira.Elements;
using Papira.Fonts;
using Papira.Pdf;

namespace Papira.Rendering;

/// <summary>
/// Writes the interactive form of a document: one widget annotation per field, each with an appearance
/// stream of its own. Papira draws the appearances rather than asking the reader to (<c>/NeedAppearances</c>),
/// because a file whose fields only appear once a particular viewer has regenerated them prints blank
/// elsewhere — and because PDF/A does not allow it.
/// </summary>
internal static class FormWriter
{
    /// <summary>
    /// Makes sure the fonts hold every glyph the appearances need, before the fonts are subset, and
    /// that no two fields share a name.
    /// </summary>
    public static void Prepare(IReadOnlyList<FormField> fields, DocumentResources resources)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!names.Add(field.Name))
                throw new InvalidOperationException($"Two form fields are named '{field.Name}'. Each field of a form needs a name of its own.");

            var font = field.Style!.Font.Font;
            var usage = resources.GetFont(font);
            foreach (var text in Texts(field))
            {
                foreach (var (glyph, codepoint) in Glyphs(text, font))
                    usage.Use(glyph, codepoint);
            }
        }
    }

    /// <summary>Every piece of text an appearance of the field may have to show.</summary>
    private static IEnumerable<string> Texts(FormField field)
    {
        if (field.Value != null)
            yield return field.Value;

        foreach (var option in field.Options)
            yield return option;
    }

    private static IEnumerable<(ushort Glyph, int Codepoint)> Glyphs(string text, TrueTypeFont font)
    {
        foreach (var rune in text.EnumerateRunes())
            yield return (font.GetGlyph(rune.Value), rune.Value);
    }

    /// <summary>
    /// Writes a field and its appearance. The field dictionary and the widget annotation are one object,
    /// as they may be when a field has a single place on a single page.
    /// </summary>
    public static void Write(
        PdfWriter writer,
        int id,
        FormField field,
        DocumentResources resources,
        int resourcesId,
        int structParent,
        CompressionLevel? level)
    {
        var style = field.Style!;
        var fontName = resources.GetFont(style.Font.Font).Name;

        // The appearances have to be written before the annotation that points at them.
        var normalId = writer.ReserveObject();
        var offId = field.Kind == FormFieldKind.Checkbox ? writer.ReserveObject() : 0;
        WriteAppearance(writer, normalId, field, fontName, resourcesId, level, ticked: true);
        if (offId != 0)
            WriteAppearance(writer, offId, field, fontName, resourcesId, level, ticked: false);

        var o = writer.Out;
        writer.BeginObject(id);
        o.Ascii("<</Type/Annot/Subtype/Widget/F 4/Rect[")
            .Real(field.Left).Space().Real(field.Bottom).Space().Real(field.Right).Space().Real(field.Top).Ascii("]");

        o.Ascii(field.Kind switch
        {
            FormFieldKind.Checkbox => "/FT/Btn",
            FormFieldKind.Choice => "/FT/Ch",
            _ => "/FT/Tx",
        });

        o.Ascii("/T");
        writer.TextString(field.Name);
        o.Ascii("/TU");
        writer.TextString(field.Tooltip ?? field.Name);

        var flags = 0;
        if (field.ReadOnly)
            flags |= 1;
        if (field.Required)
            flags |= 2;
        if (field.Kind == FormFieldKind.Text && field.Multiline)
            flags |= 1 << 12;
        if (field.Kind == FormFieldKind.Choice)
            flags |= 1 << 17; // a dropdown rather than a list box
        if (flags != 0)
            o.Ascii("/Ff ").Int(flags);

        switch (field.Kind)
        {
            case FormFieldKind.Checkbox:
                o.Ascii(field.Checked ? "/V/Yes/DV/Yes/AS/Yes" : "/V/Off/DV/Off/AS/Off");
                break;

            case FormFieldKind.Choice:
                o.Ascii("/Opt[");
                foreach (var option in field.Options)
                {
                    writer.TextString(option);
                    o.Space();
                }

                o.Ascii("]/V");
                writer.TextString(field.Value ?? string.Empty);
                break;

            default:
                o.Ascii("/V");
                writer.TextString(field.Value ?? string.Empty);
                if (field.MaxLength > 0)
                    o.Ascii("/MaxLen ").Int(field.MaxLength);
                break;
        }

        // What a viewer needs to redraw the field itself, should a reader change its value.
        o.Ascii("/DA");
        writer.AsciiString(DefaultAppearance(fontName, field));
        o.Ascii("/MK<<");
        WriteColorArray(o, "/BC", field.BorderColor);
        if (field.BackgroundColor is { } background)
            WriteColorArray(o, "/BG", background);
        o.Ascii(">>");

        o.Ascii("/AP<</N");
        if (offId == 0)
            o.Space().Int(normalId).Ascii(" 0 R>>");
        else
            o.Ascii("<</Yes ").Int(normalId).Ascii(" 0 R/Off ").Int(offId).Ascii(" 0 R>>>>");

        if (structParent >= 0)
            o.Ascii("/StructParent ").Int(structParent);

        o.Ascii(">>");
        writer.EndObject();
    }

    /// <summary>The text state a viewer sets before it draws the value of a field.</summary>
    public static string DefaultAppearance(string fontName, FormField field)
    {
        var style = field.Style!;
        var color = style.Color;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"/{fontName} {style.Size:0.###} Tf {color.R / 255f:0.###} {color.G / 255f:0.###} {color.B / 255f:0.###} rg");
    }

    private static void WriteColorArray(ByteBuffer o, string key, Color color) =>
        o.Ascii(key).Ascii("[").Real(color.R / 255f).Space().Real(color.G / 255f).Space().Real(color.B / 255f).Ascii("]");

    /// <summary>
    /// Draws what the field looks like: its background and frame, and the value in it. A form XObject,
    /// in its own coordinates, with the origin at its bottom left corner.
    /// </summary>
    private static void WriteAppearance(
        PdfWriter writer,
        int id,
        FormField field,
        string fontName,
        int resourcesId,
        CompressionLevel? level,
        bool ticked)
    {
        var style = field.Style!;
        var width = field.Width;
        var height = field.Height;

        using var content = new ByteBuffer(256);
        var o = content;

        if (field.Kind != FormFieldKind.Checkbox)
            o.Ascii("/Tx BMC\n");

        o.Ascii("q\n");

        if (field.BackgroundColor is { } background)
        {
            WriteColor(o, background).Ascii(" rg 0 0 ").Real(width).Space().Real(height).Ascii(" re f\n");
        }

        // The frame sits inside the box, so that a one point line is not cut in half by its edge.
        WriteColor(o, field.BorderColor).Ascii(" RG 1 w 0.5 0.5 ")
            .Real(width - 1).Space().Real(height - 1).Ascii(" re S\n");

        switch (field.Kind)
        {
            case FormFieldKind.Checkbox when ticked:
                WriteTick(o, style.Color, width, height);
                break;

            case FormFieldKind.Checkbox:
                break;

            default:
                WriteValue(o, field, fontName, width, height);
                break;
        }

        o.Ascii("Q\n");
        if (field.Kind != FormFieldKind.Checkbox)
            o.Ascii("EMC\n");

        var data = content.Span;
        var compressed = level is { } compression ? PdfWriter.Deflate(data, compression) : null;
        writer.WriteStream(
            id,
            compressed ?? data.ToArray(),
            b => b.Ascii("/Type/XObject/Subtype/Form/Resources ").Int(resourcesId).Ascii(" 0 R/BBox[0 0 ")
                .Real(width).Space().Real(height).Ascii("]"),
            flateEncoded: compressed != null);
    }

    /// <summary>The mark of a ticked box: two strokes, as a pen would leave them.</summary>
    private static void WriteTick(ByteBuffer o, Color color, float width, float height)
    {
        var thickness = MathF.Max(1, MathF.Min(width, height) * 0.14f);
        WriteColor(o, color).Ascii(" RG ").Real(thickness).Ascii(" w 1 J 1 j\n")
            .Real(width * 0.22f).Space().Real(height * 0.55f).Ascii(" m ")
            .Real(width * 0.42f).Space().Real(height * 0.28f).Ascii(" l ")
            .Real(width * 0.78f).Space().Real(height * 0.72f).Ascii(" l S\n");
    }

    /// <summary>Draws the text of the field, clipped to the box and wrapped when the field holds lines.</summary>
    private static void WriteValue(ByteBuffer o, FormField field, string fontName, float width, float height)
    {
        var padding = FormFieldElement.Padding;

        // A dropdown keeps the right edge for the arrow a reader opens it with.
        var arrow = field.Kind == FormFieldKind.Choice ? MathF.Min(14, width / 4) : 0;
        if (arrow > 0)
            WriteArrow(o, field.Style!.Color, width - arrow, height);

        var text = field.Value;
        if (string.IsNullOrEmpty(text))
            return;

        var style = field.Style!;
        var available = width - 2 * padding - arrow;
        if (available <= 0)
            return;

        o.Ascii("q ").Real(padding).Space().Real(padding).Space().Real(width - 2 * padding - arrow).Space()
            .Real(height - 2 * padding).Ascii(" re W n\n");

        o.Ascii("BT ").Byte((byte)'/').Ascii(fontName).Space().Real(style.Size).Ascii(" Tf ");
        WriteColor(o, style.Color).Ascii(" rg\n");

        var lines = field.Multiline
            ? Wrap(text, style, available, Math.Max(1, (int)((height - 2 * padding) / style.LineHeight)))
            : [text.ReplaceLineEndings(" ")];

        var baseline = field.Multiline
            ? height - padding - style.Ascent
            : (height - style.Ascent - style.Descent) / 2 + style.Descent;

        foreach (var line in lines)
        {
            o.Ascii("1 0 0 1 ").Real(padding).Space().Real(baseline).Ascii(" Tm <");
            foreach (var (glyph, _) in Glyphs(line, style.Font.Font))
                o.Hex16(glyph);

            o.Ascii("> Tj\n");
            baseline -= style.LineHeight;
        }

        o.Ascii("ET Q\n");
    }

    /// <summary>The triangle that says a field opens a list.</summary>
    private static void WriteArrow(ByteBuffer o, Color color, float x, float height)
    {
        var middle = height / 2;
        WriteColor(o, color).Ascii(" rg ")
            .Real(x + 3).Space().Real(middle + 2).Ascii(" m ")
            .Real(x + 11).Space().Real(middle + 2).Ascii(" l ")
            .Real(x + 7).Space().Real(middle - 3).Ascii(" l f\n");
    }

    /// <summary>Breaks text into as many lines as fit, at spaces where it can.</summary>
    private static List<string> Wrap(string text, ResolvedTextStyle style, float width, int maxLines)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(candidate, style) > width)
                {
                    lines.Add(line);
                    if (lines.Count == maxLines)
                        return lines;
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }

            lines.Add(line);
            if (lines.Count == maxLines)
                return lines;
        }

        return lines;
    }

    private static float Measure(string text, ResolvedTextStyle style)
    {
        var font = style.Font.Font;
        var units = 0;
        foreach (var rune in text.EnumerateRunes())
            units += font.GetAdvance(font.GetGlyph(rune.Value));

        return units * style.Scale;
    }

    private static ByteBuffer WriteColor(ByteBuffer o, Color color) =>
        o.Real(color.R / 255f).Space().Real(color.G / 255f).Space().Real(color.B / 255f);
}
