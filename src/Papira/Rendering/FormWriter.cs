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
        var buttons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            // The buttons of a radio group share the name of the group and differ in what they stand for.
            if (field.Kind == FormFieldKind.Radio)
            {
                if (!buttons.Add(field.Name + "\u0000" + field.Export))
                    throw new InvalidOperationException($"Two buttons of the radio group '{field.Name}' stand for '{field.Export}'. Each button of a group needs a value of its own.");

                if (names.Contains(field.Name))
                    throw new InvalidOperationException($"'{field.Name}' is the name of both a radio group and another field. Each field of a form needs a name of its own.");
            }
            else if (!names.Add(field.Name))
            {
                throw new InvalidOperationException($"Two form fields are named '{field.Name}'. Each field of a form needs a name of its own.");
            }

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
    /// as they may be when a field has a single place on a single page; a button of a radio group is
    /// only the annotation, and hangs on the object that holds the group.
    /// </summary>
    public static void Write(
        PdfWriter writer,
        int id,
        FormField field,
        DocumentResources resources,
        int resourcesId,
        int structParent,
        CompressionLevel? level,
        int parentId = 0,
        int signatureId = 0)
    {
        var style = field.Style!;
        var fontName = resources.GetFont(style.Font.Font).Name;
        var state = field.Kind == FormFieldKind.Radio ? Name(field.Export ?? "On") : "Yes";

        // The appearances have to be written before the annotation that points at them.
        var normalId = writer.ReserveObject();
        var offId = field.Kind is FormFieldKind.Checkbox or FormFieldKind.Radio ? writer.ReserveObject() : 0;
        WriteAppearance(writer, normalId, field, fontName, resourcesId, level, ticked: true);
        if (offId != 0)
            WriteAppearance(writer, offId, field, fontName, resourcesId, level, ticked: false);

        var o = writer.Out;
        writer.BeginObject(id);
        o.Ascii("<</Type/Annot/Subtype/Widget/F 4/Rect[")
            .Real(field.Left).Space().Real(field.Bottom).Space().Real(field.Right).Space().Real(field.Top).Ascii("]");

        if (parentId != 0)
        {
            // A button of a radio group: the group itself holds the name, the value and the flags.
            o.Ascii("/Parent ").Int(parentId).Ascii(" 0 R/TU");
            writer.TextString(field.Tooltip ?? field.Name);
            o.Ascii("/AS/").Ascii(field.Checked ? state : "Off");
        }
        else
        {
            o.Ascii(field.Kind switch
            {
                FormFieldKind.Checkbox => "/FT/Btn",
                FormFieldKind.Choice => "/FT/Ch",
                FormFieldKind.Signature => "/FT/Sig",
                _ => "/FT/Tx",
            });

            o.Ascii("/T");
            writer.TextString(field.Name);
            o.Ascii("/TU");
            writer.TextString(field.Tooltip ?? field.Name);
        }

        var flags = 0;
        if (field.ReadOnly)
            flags |= 1;
        if (field.Required)
            flags |= 2;
        if (field.Kind == FormFieldKind.Text && field.Multiline)
            flags |= 1 << 12;
        if (field.Kind == FormFieldKind.Choice)
            flags |= 1 << 17; // a dropdown rather than a list box
        if (flags != 0 && parentId == 0)
            o.Ascii("/Ff ").Int(flags);

        switch (field.Kind)
        {
            case FormFieldKind.Radio:
                break;

            case FormFieldKind.Signature:
                if (signatureId != 0)
                    o.Ascii("/V ").Int(signatureId).Ascii(" 0 R");

                break;

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
            o.Ascii("<</").Ascii(state).Space().Int(normalId).Ascii(" 0 R/Off ").Int(offId).Ascii(" 0 R>>>>");

        if (structParent >= 0)
            o.Ascii("/StructParent ").Int(structParent);

        o.Ascii(">>");
        writer.EndObject();
    }

    /// <summary>
    /// Writes the field a group of radio buttons belongs to. The buttons themselves are its widgets:
    /// the group carries the name and the value, which is the button the reader chose.
    /// </summary>
    public static void WriteRadioGroup(PdfWriter writer, int id, IReadOnlyList<FormField> buttons, IReadOnlyList<int> widgetIds)
    {
        var o = writer.Out;
        var first = buttons[0];
        var chosen = buttons.FirstOrDefault(button => button.Checked);

        writer.BeginObject(id);
        o.Ascii("<</FT/Btn/T");
        writer.TextString(first.Name);
        o.Ascii("/TU");
        writer.TextString(first.Tooltip ?? first.Name);

        // Radio (bit 16) and, with it, the rule that one of the buttons is always chosen (bit 15).
        var flags = (1 << 15) | (1 << 14);
        if (first.ReadOnly)
            flags |= 1;
        if (first.Required)
            flags |= 2;

        o.Ascii("/Ff ").Int(flags).Ascii("/V/").Ascii(chosen != null ? Name(chosen.Export ?? "On") : "Off")
            .Ascii("/DV/").Ascii(chosen != null ? Name(chosen.Export ?? "On") : "Off").Ascii("/Kids[");

        foreach (var widget in widgetIds)
            o.Int(widget).Ascii(" 0 R ");

        o.Ascii("]>>");
        writer.EndObject();
    }

    /// <summary>
    /// A name as PDF writes one: the characters that are not allowed in one are written as their code.
    /// </summary>
    private static string Name(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            if (b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'_' or (byte)'-')
                builder.Append((char)b);
            else
                builder.Append('#').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.Length > 0 ? builder.ToString() : "On";
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

        var button = field.Kind is FormFieldKind.Checkbox or FormFieldKind.Radio;
        var round = field.Kind == FormFieldKind.Radio;

        using var content = new ByteBuffer(256);
        var o = content;

        if (!button)
            o.Ascii("/Tx BMC\n");

        o.Ascii("q\n");

        if (field.BackgroundColor is { } background)
        {
            WriteColor(o, background);
            if (round)
                Circle(o, width / 2, height / 2, Math.Min(width, height) / 2).Ascii(" rg f\n");
            else
                o.Ascii(" rg 0 0 ").Real(width).Space().Real(height).Ascii(" re f\n");
        }

        // The frame sits inside the box, so that a one point line is not cut in half by its edge.
        WriteColor(o, field.BorderColor).Ascii(" RG 1 w ");
        if (round)
            Circle(o, width / 2, height / 2, (Math.Min(width, height) - 1) / 2).Ascii(" S\n");
        else
            o.Ascii("0.5 0.5 ").Real(width - 1).Space().Real(height - 1).Ascii(" re S\n");

        switch (field.Kind)
        {
            case FormFieldKind.Checkbox when ticked:
                WriteTick(o, style.Color, width, height);
                break;

            case FormFieldKind.Radio when ticked:
                WriteColor(o, style.Color);
                Circle(o, width / 2, height / 2, Math.Min(width, height) / 4).Ascii(" rg f\n");
                break;

            case FormFieldKind.Checkbox or FormFieldKind.Radio:
                break;

            case FormFieldKind.Signature:
                WriteSignatureLine(o, field, width, height);
                break;

            default:
                WriteValue(o, field, fontName, width, height);
                break;
        }

        o.Ascii("Q\n");
        if (!button)
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

    /// <summary>A circle drawn as four Bézier curves, which is as close to one as a PDF path gets.</summary>
    private static ByteBuffer Circle(ByteBuffer o, float x, float y, float radius)
    {
        const float k = 0.5522847f;
        var c = radius * k;
        o.Real(x + radius).Space().Real(y).Ascii(" m ");
        o.Real(x + radius).Space().Real(y + c).Space().Real(x + c).Space().Real(y + radius).Space().Real(x).Space().Real(y + radius).Ascii(" c ");
        o.Real(x - c).Space().Real(y + radius).Space().Real(x - radius).Space().Real(y + c).Space().Real(x - radius).Space().Real(y).Ascii(" c ");
        o.Real(x - radius).Space().Real(y - c).Space().Real(x - c).Space().Real(y - radius).Space().Real(x).Space().Real(y - radius).Ascii(" c ");
        o.Real(x + c).Space().Real(y - radius).Space().Real(x + radius).Space().Real(y - c).Space().Real(x + radius).Space().Real(y).Ascii(" c h");
        return o;
    }

    /// <summary>The line a signature is written on, with what it is for beneath it.</summary>
    private static void WriteSignatureLine(ByteBuffer o, FormField field, float width, float height)
    {
        var style = field.Style!;
        WriteColor(o, field.BorderColor).Ascii(" RG 0.75 w ")
            .Real(width * 0.08f).Space().Real(height * 0.32f).Ascii(" m ")
            .Real(width * 0.92f).Space().Real(height * 0.32f).Ascii(" l S\n");

        _ = style;
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
