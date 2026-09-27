using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Svg;

/// <summary>
/// A drawable node of a parsed SVG file. The tree is built once and drawn as often as the layout needs it,
/// so nodes are immutable after parsing.
/// </summary>
internal abstract class SvgNode
{
    /// <summary>The element's own transform, applied inside the transforms of its ancestors.</summary>
    public Matrix Transform { get; init; } = Matrix.Identity;

    /// <summary>The shape of the referenced clip-path, in the element's own user space.</summary>
    public SvgPath? Clip { get; init; }

    public bool ClipEvenOdd { get; init; }

    /// <summary>Group opacity, which applies to the node as a whole.</summary>
    public float Opacity { get; init; } = 1;

    /// <summary>What hides part of the node, where the drawing gives it a mask.</summary>
    public SvgMask? Mask { get; init; }
}

internal sealed class SvgGroupNode : SvgNode
{
    public List<SvgNode> Children { get; } = [];
}

internal sealed class SvgShapeNode : SvgNode
{
    public required SvgPath Path { get; init; }

    public required SvgStyle Style { get; init; }
}

/// <summary>Where a piece of text is anchored: at its start, its middle or its end.</summary>
internal enum SvgTextAnchor : byte { Start, Middle, End }

/// <summary>
/// One stretch of text of an SVG drawing, with the places it is written at. A drawing gives positions
/// character by character where it needs to, which is what <c>x="10 20 30"</c> means.
/// </summary>
internal sealed class SvgTextRun
{
    public required string Text { get; set; }

    /// <summary>Where the run starts, when it says so; a run without one continues the one before it.</summary>
    public float[] X { get; init; } = [];

    public float[] Y { get; init; } = [];

    /// <summary>How far the run is moved from where it would otherwise start.</summary>
    public float[] Dx { get; init; } = [];

    public float[] Dy { get; init; } = [];

    public string? Family { get; init; }

    public float FontSize { get; init; } = 16;

    public FontWeight Weight { get; init; } = FontWeight.Normal;

    public bool Italic { get; init; }

    public float LetterSpacing { get; init; }

    public SvgTextAnchor Anchor { get; init; }

    public SvgStyle Style { get; init; }
}

/// <summary>A piece of text of a drawing, as one or more runs that follow one another.</summary>
internal sealed class SvgTextNode : SvgNode
{
    public required SvgTextRun[] Runs { get; init; }
}

/// <summary>
/// A mask: the drawing behind it shows through where the mask is light and is hidden where it is dark.
/// </summary>
internal sealed class SvgMask
{
    public required SvgGroupNode Content { get; init; }

    /// <summary>The area the mask covers, in the user space of the element it is applied to.</summary>
    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Width { get; init; }

    public required float Height { get; init; }

    /// <summary>True when the area is given as fractions of the bounding box of what is masked.</summary>
    public required bool ObjectBoundingBox { get; init; }
}

/// <summary>A pattern: a drawing repeated over and over to fill a shape.</summary>
internal sealed class SvgPattern
{
    public required SvgGroupNode Content { get; init; }

    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Width { get; init; }

    public required float Height { get; init; }

    public required bool ObjectBoundingBox { get; init; }

    /// <summary>True when the content itself is measured in the bounding box of what is filled.</summary>
    public required bool ContentObjectBoundingBox { get; init; }

    public Matrix Transform { get; init; } = Matrix.Identity;

    /// <summary>The view box the content is drawn in, when the pattern gives one.</summary>
    public float[] ViewBox { get; init; } = [];
}

/// <summary>A parsed SVG file: the drawable tree plus the viewport it was authored for.</summary>
internal sealed class SvgDocument
{
    public required SvgGroupNode Root { get; init; }

    /// <summary>The intrinsic size in user units, used to give the element its aspect ratio.</summary>
    public required float Width { get; init; }

    public required float Height { get; init; }

    public required float ViewBoxX { get; init; }

    public required float ViewBoxY { get; init; }

    public required float ViewBoxWidth { get; init; }

    public required float ViewBoxHeight { get; init; }

    /// <summary>False for preserveAspectRatio="none", which stretches the drawing to fill its area.</summary>
    public required bool PreserveAspectRatio { get; init; }

    public int ShapeCount { get; init; }
}
