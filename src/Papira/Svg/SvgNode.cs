using Papira.Infrastructure;

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
