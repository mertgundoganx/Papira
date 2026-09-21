using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

/// <summary>
/// Base of the layout tree. Elements are stateful to support pagination:
/// <see cref="Measure"/> is side-effect free and reports how much of the remaining content fits;
/// <see cref="Draw"/> renders that part and advances the element's state to the rest.
/// </summary>
internal abstract class Element
{
    internal abstract SpacePlan Measure(Size available, LayoutContext context);

    internal abstract void Draw(Size available, LayoutContext context);

    /// <summary>Restores the initial state so the element can be laid out again (repeated headers, second pass).</summary>
    internal virtual void Reset()
    {
    }
}

internal sealed class EmptyElement : Element
{
    public static readonly EmptyElement Instance = new();

    internal override SpacePlan Measure(Size available, LayoutContext context) => SpacePlan.Empty;

    internal override void Draw(Size available, LayoutContext context)
    {
    }
}

/// <summary>An element with a single child slot that user code fills through <see cref="IContainer"/>.</summary>
internal abstract class ContainerElement : Element, IContainer
{
    private Element _child = EmptyElement.Instance;

    internal Element Child
    {
        get => _child;
        set
        {
            if (!ReferenceEquals(_child, EmptyElement.Instance))
                throw new DocumentComposeException("This container already has content. Each container accepts exactly one child; use Column or Row to combine several elements.");
            _child = value;
        }
    }

    internal override SpacePlan Measure(Size available, LayoutContext context) => Child.Measure(available, context);

    internal override void Draw(Size available, LayoutContext context) => Child.Draw(available, context);

    internal override void Reset() => Child.Reset();

    /// <summary>Draws the child translated by the given offset.</summary>
    protected void DrawChildAt(float x, float y, Size size, LayoutContext context)
    {
        context.Canvas.Translate(x, y);
        Child.Draw(size, context);
        context.Canvas.Translate(-x, -y);
    }
}

/// <summary>Pass-through slot, e.g. a column item or page section.</summary>
internal sealed class Slot : ContainerElement;
