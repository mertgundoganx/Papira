using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

/// <summary>Makes the area of its content clickable, opening a URI or jumping to a named section.</summary>
internal sealed class LinkElement : ContainerElement
{
    private readonly string? _uri;
    private readonly string? _section;

    private LinkElement(string? uri, string? section)
    {
        _uri = uri;
        _section = section;
    }

    public static LinkElement ToUri(string uri) => new(uri, null);

    public static LinkElement ToSection(string section) => new(null, section);

    internal override void Draw(Size available, LayoutContext context)
    {
        // The link covers the allocated width and the height of the part drawn on this page.
        var plan = Child.Measure(available, context);
        if (plan.HasContent)
        {
            var (left, top) = context.Canvas.ToPdf(0, 0);
            var (right, bottom) = context.Canvas.ToPdf(available.Width, Math.Min(plan.Height, available.Height));
            context.PageLinks.Add(new LinkArea(left, bottom, right, top, _uri, _section));
        }

        Child.Draw(available, context);
    }
}

/// <summary>Marks the position where its content starts, as a target for internal links and bookmarks.</summary>
internal abstract class LocationElement : ContainerElement
{
    private bool _recorded;

    protected abstract void Record(Destination destination, LayoutContext context);

    internal override void Draw(Size available, LayoutContext context)
    {
        if (!_recorded && Child.Measure(available, context).HasContent)
        {
            _recorded = true;
            var (x, y) = context.Canvas.ToPdf(0, 0);
            Record(new Destination(context.PageNumber - 1, x, y), context);
        }

        Child.Draw(available, context);
    }

    internal override void Reset()
    {
        _recorded = false;
        base.Reset();
    }
}

internal sealed class SectionElement(string name) : LocationElement
{
    protected override void Record(Destination destination, LayoutContext context) =>
        context.Sections.TryAdd(name, destination);
}

internal sealed class BookmarkElement(string title, int level) : LocationElement
{
    protected override void Record(Destination destination, LayoutContext context) =>
        context.Bookmarks.Add(new Bookmark(title, level, destination));
}
