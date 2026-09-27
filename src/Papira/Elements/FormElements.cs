using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

/// <summary>The kinds of field a reader can fill in.</summary>
internal enum FormFieldKind : byte { Text, Checkbox, Choice }

/// <summary>
/// A field of a form: what it is, what it holds, and — once the document has been laid out — where on
/// which page it ended up. The annotation itself is written when the file is written.
/// </summary>
internal sealed class FormField(FormFieldKind kind, string name)
{
    public FormFieldKind Kind { get; } = kind;

    public string Name { get; } = name;

    public string? Value { get; set; }

    public bool Checked { get; set; }

    public string[] Options { get; set; } = [];

    /// <summary>What the field is for; shown as a tooltip and announced by a reader for the blind.</summary>
    public string? Tooltip { get; set; }

    public bool ReadOnly { get; set; }

    public bool Required { get; set; }

    public bool Multiline { get; set; }

    public int MaxLength { get; set; }

    public float? RequestedHeight { get; set; }

    public Color BorderColor { get; set; } = Colors.Grey.Medium;

    public Color? BackgroundColor { get; set; }

    public int PageIndex { get; set; }

    public float Left { get; set; }

    public float Bottom { get; set; }

    public float Right { get; set; }

    public float Top { get; set; }

    public float Width => Right - Left;

    public float Height => Top - Bottom;

    /// <summary>The style the field inherited where it was placed; the value is drawn with it.</summary>
    public ResolvedTextStyle? Style { get; set; }

    /// <summary>The element of a tagged document the field belongs to.</summary>
    public StructureElement? Structure { get; set; }
}

/// <summary>Draws the box of a form field and records where it ended up, so a widget can be written for it.</summary>
internal sealed class FormFieldElement(FormField formField) : Element
{
    private ResolvedTextStyle? _style;

    public FormField Field => formField;

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        var (width, height) = Box(available, context);
        return height > available.Height + Size.Epsilon || width > available.Width + Size.Epsilon
            ? SpacePlan.Wrap
            : SpacePlan.Full(width, height);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        if (context.Artifact)
            throw new InvalidOperationException(
                $"The form field '{formField.Name}' is inside a header, footer, background or foreground. It would repeat on every page, which a form cannot express; place it in the content of the page.");

        var (width, height) = Box(available, context);
        if (height > available.Height + Size.Epsilon)
            return;

        var style = Style(context);
        using var tag = context.Tag("Form", content: false);
        context.Structure?.AddAnnotation();

        var canvas = context.Canvas;
        var (left, top) = canvas.ToPdf(0, 0);
        var (right, bottom) = canvas.ToPdf(width, height);

        formField.PageIndex = context.PageNumber - 1;
        formField.Left = Math.Min(left, right);
        formField.Right = Math.Max(left, right);
        formField.Bottom = Math.Min(top, bottom);
        formField.Top = Math.Max(top, bottom);
        formField.Style = style;
        formField.Structure = context.CurrentStructure;

        context.PageFormFields.Add(formField);
    }

    internal override void Reset() => _style = null;

    /// <summary>The size of the box, which for everything but a checkbox spans the available width.</summary>
    private (float Width, float Height) Box(Size available, LayoutContext context)
    {
        var style = Style(context);
        if (formField.Kind == FormFieldKind.Checkbox)
        {
            var side = formField.RequestedHeight ?? Math.Max(MathF.Round(style.Size), 10);
            return (side, side);
        }

        var lines = formField.Multiline ? 3 : 1;
        var height = formField.RequestedHeight ?? MathF.Max(style.LineHeight * lines + 2 * Padding, 18);
        return (available.Width, height);
    }

    private ResolvedTextStyle Style(LayoutContext context) => _style ??= new ResolvedTextStyle(context.DefaultStyle);

    /// <summary>The gap between the border of a formField and the text in it, in points.</summary>
    public const float Padding = 3;
}
