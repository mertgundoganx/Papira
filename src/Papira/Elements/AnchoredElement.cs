using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

/// <summary>
/// A box that other elements are placed against. What it holds is laid out as usual and decides how much
/// room the box takes; the elements placed against it are drawn over that, each where its own offsets put
/// it, and take no room of their own. This is what a style sheet asks for with <c>position: relative</c>
/// around <c>position: absolute</c>.
/// </summary>
internal sealed class AnchoredElement : ContainerElement
{
    public readonly List<AnchoredChild> Children = [];

    internal override void Draw(Size available, LayoutContext context)
    {
        Child.Draw(available, context);

        foreach (var child in Children)
            child.DrawAgainst(available, context);
    }

    internal override void Reset()
    {
        base.Reset();
        foreach (var child in Children)
            child.Reset();
    }
}

/// <summary>
/// An element placed against the box around it. Each offset it states is measured from that edge of the
/// box; held against both edges of an axis it spans the distance between them, and held against one it is
/// as large as what it holds. The offsets may be negative, which puts it outside the box, as CSS allows.
/// </summary>
internal sealed class AnchoredChild : ContainerElement
{
    public CssLength? Left, Top, Right, Bottom;

    private bool _drawn;

    public void DrawAgainst(Size box, LayoutContext context)
    {
        // Such an element belongs to the box it is placed against, and is drawn with it once; where that
        // box carries on over a page it is not drawn again.
        if (_drawn)
            return;

        _drawn = true;

        var viewport = context.Viewport;
        var left = Left?.Resolve(box.Width, viewport);
        var right = Right?.Resolve(box.Width, viewport);
        var top = Top?.Resolve(box.Height, viewport);
        var bottom = Bottom?.Resolve(box.Height, viewport);

        var spanned = left != null && right != null;
        var stacked = top != null && bottom != null;
        var width = spanned ? Math.Max(0, box.Width - left!.Value - right!.Value) : box.Width;

        // Nothing holds the element to the bottom of the box, so it may be as tall as it needs to be.
        var height = stacked
            ? Math.Max(0, box.Height - top!.Value - bottom!.Value)
            : Math.Max(box.Height, context.BodyHeight);

        // Held to one side only, the element is as wide as what it holds; held to both, it spans the
        // distance between them. This is the shrink-to-fit of CSS.
        var previous = context.ShrinkToFit;
        context.ShrinkToFit = !spanned;
        var plan = Child.Measure(new Size(width, height), context);
        if (!plan.HasContent)
        {
            context.ShrinkToFit = previous;
            return;
        }

        var size = new Size(spanned ? width : plan.Width, stacked ? height : plan.Height);
        var x = left ?? (right is { } fromRight ? box.Width - fromRight - size.Width : 0);
        var y = top ?? (bottom is { } fromBottom ? box.Height - fromBottom - size.Height : 0);

        var canvas = context.Canvas;
        canvas.Translate(x, y);
        Child.Draw(size, context);
        canvas.Translate(-x, -y);
        context.ShrinkToFit = previous;
    }

    internal override SpacePlan Measure(Size available, LayoutContext context) => SpacePlan.Empty;

    internal override void Draw(Size available, LayoutContext context)
    {
    }

    internal override void Reset()
    {
        _drawn = false;
        base.Reset();
    }
}
