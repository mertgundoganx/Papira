using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

internal enum RowItemKind : byte { Constant, Relative, Auto }

internal sealed class RowItem(RowItemKind kind, float value) : ContainerElement
{
    public RowItemKind Kind => kind;
    public float Value => value;
}

/// <summary>Places items side by side. Items are laid out together, so the row spans pages as a unit.</summary>
internal sealed class RowElement : Element
{
    public readonly List<RowItem> Items = [];
    public float Spacing;

    private float[] _widths = [];

    private float[] ComputeWidths(Size available, LayoutContext context)
    {
        if (_widths.Length != Items.Count)
            _widths = new float[Items.Count];

        var fixedWidth = Spacing * Math.Max(0, Items.Count - 1);
        float relativeTotal = 0;

        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            switch (item.Kind)
            {
                case RowItemKind.Constant:
                    _widths[i] = item.Value;
                    fixedWidth += item.Value;
                    break;
                case RowItemKind.Relative:
                    relativeTotal += item.Value;
                    break;
            }
        }

        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].Kind != RowItemKind.Auto)
                continue;

            var plan = Items[i].Measure(new Size(Math.Max(0, available.Width - fixedWidth), available.Height), context);
            _widths[i] = plan.HasContent ? plan.Width : 0;
            fixedWidth += _widths[i];
        }

        var perUnit = relativeTotal > 0 ? Math.Max(0, available.Width - fixedWidth) / relativeTotal : 0;
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].Kind == RowItemKind.Relative)
                _widths[i] = Items[i].Value * perUnit;
        }

        return _widths;
    }

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        var widths = ComputeWidths(available, context);
        float totalWidth = Spacing * Math.Max(0, Items.Count - 1), height = 0;
        bool anyContent = false, anyPartial = false;

        for (var i = 0; i < Items.Count; i++)
        {
            totalWidth += widths[i];
            var plan = Items[i].Measure(new Size(widths[i], available.Height), context);
            if (plan.IsWrap)
                return SpacePlan.Wrap;
            if (plan.IsEmpty)
                continue;

            anyContent = true;
            anyPartial |= plan.Kind == SpacePlanKind.Partial;
            height = Math.Max(height, plan.Height);
        }

        if (!anyContent)
            return SpacePlan.Empty;

        if (totalWidth > available.Width + Size.Epsilon)
            return SpacePlan.Wrap;

        return anyPartial ? SpacePlan.Partial(totalWidth, height) : SpacePlan.Full(totalWidth, height);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        var plan = Measure(available, context);
        if (!plan.HasContent)
            return;

        var widths = ComputeWidths(available, context);
        var canvas = context.Canvas;
        float x = 0;

        for (var i = 0; i < Items.Count; i++)
        {
            var size = new Size(widths[i], plan.Height);
            var item = Items[i];
            var finished = !item.Measure(size, context).HasContent;

            var previous = context.DrawEmptyDecorations;
            canvas.Translate(x, 0);
            context.DrawEmptyDecorations = previous || finished;
            item.Draw(size, context);
            context.DrawEmptyDecorations = previous;
            canvas.Translate(-x, 0);

            x += widths[i] + Spacing;
        }
    }

    internal override void Reset()
    {
        foreach (var item in Items)
            item.Reset();
    }
}
