using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

/// <summary>Stacks items vertically; continues on the next page where an item no longer fits.</summary>
internal sealed class ColumnElement : Element
{
    public readonly List<Element> Items = [];
    public float Spacing;

    private int _current;

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        float height = 0, width = 0;
        var placed = 0;

        for (var i = _current; i < Items.Count; i++)
        {
            var spacing = placed > 0 ? Spacing : 0;
            var remaining = available.Height - height - spacing;
            if (remaining < -Size.Epsilon)
                return SpacePlan.Partial(width, height);

            var plan = Items[i].Measure(new Size(available.Width, Math.Max(0, remaining)), context);
            switch (plan.Kind)
            {
                case SpacePlanKind.Empty:
                    continue;
                case SpacePlanKind.Wrap:
                    return placed == 0 ? SpacePlan.Wrap : SpacePlan.Partial(width, height);
            }

            placed++;
            height += spacing + plan.Height;
            width = Math.Max(width, plan.Width);

            if (plan.Kind == SpacePlanKind.Partial)
                return SpacePlan.Partial(width, height);
        }

        return placed == 0 ? SpacePlan.Empty : SpacePlan.Full(width, height);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        var canvas = context.Canvas;
        float y = 0;
        var placed = 0;

        while (_current < Items.Count)
        {
            var item = Items[_current];
            var spacing = placed > 0 ? Spacing : 0;
            var remaining = available.Height - y - spacing;
            if (remaining < -Size.Epsilon)
                return;

            var plan = item.Measure(new Size(available.Width, Math.Max(0, remaining)), context);
            if (plan.Kind == SpacePlanKind.Empty)
            {
                _current++;
                continue;
            }

            if (plan.Kind == SpacePlanKind.Wrap)
                return;

            y += spacing;
            canvas.Translate(0, y);
            item.Draw(new Size(available.Width, plan.Height), context);
            canvas.Translate(0, -y);
            y += plan.Height;
            placed++;

            if (plan.Kind == SpacePlanKind.Partial)
                return;

            _current++;
        }
    }

    internal override void Reset()
    {
        _current = 0;
        foreach (var item in Items)
            item.Reset();
    }
}
