namespace Papira.Rendering;

/// <summary>A piece of the page that belongs to a structure element: its number within that page.</summary>
internal readonly record struct MarkedContent(int Page, int Mcid);

/// <summary>
/// One element of the structure of a document: a paragraph, a heading, a table cell, a figure. What it
/// holds is either the content drawn for it or further elements.
/// </summary>
internal sealed class StructureElement(string role, StructureElement? parent)
{
    public string Role { get; } = role;

    public StructureElement? Parent { get; } = parent;

    /// <summary>Alternative text, which a reader announces in place of a figure.</summary>
    public string? Alt { get; set; }

    /// <summary>How many columns and rows a table cell covers; a reader needs both to follow the grid.</summary>
    public int ColumnSpan { get; set; } = 1;

    public int RowSpan { get; set; } = 1;

    /// <summary>The children, each one a <see cref="StructureElement"/> or a <see cref="MarkedContent"/>.</summary>
    public List<object> Children { get; } = [];

    /// <summary>The object number the element is written as; assigned when the file is written.</summary>
    public int Id { get; set; }

    /// <summary>How many annotations — links, form fields — belong to the element.</summary>
    public int Annotations { get; set; }

    public bool HasContent => Children.Count > 0 || Annotations > 0;
}

/// <summary>
/// The structure of a tagged document: what the content of each page means, in reading order. A reader
/// for the blind walks this tree rather than the page, which is why the order and the roles matter more
/// than where anything sits.
/// </summary>
internal sealed class StructureTree
{
    private readonly List<int> _mcidPerPage = [];
    private StructureElement _current;

    public StructureTree()
    {
        Root = new StructureElement("Document", null);
        _current = Root;
    }

    public StructureElement Root { get; }

    /// <summary>Opens an element; everything drawn until <see cref="Pop"/> belongs to it.</summary>
    public StructureElement Push(string role)
    {
        var element = new StructureElement(role, _current);
        _current.Children.Add(element);
        _current = element;
        return element;
    }

    public void Pop()
    {
        // An element that ended up with nothing in it would only confuse a reader.
        if (!_current.HasContent && _current.Parent is { } parent)
            parent.Children.Remove(_current);

        _current = _current.Parent ?? Root;
    }

    /// <summary>
    /// Records that an annotation belongs to the element that is open. A field of a form draws nothing
    /// itself, so without this the element would look empty and be dropped from the tree.
    /// </summary>
    public void AddAnnotation() => _current.Annotations++;

    /// <summary>The number the next piece of content on a page is marked with.</summary>
    public int NextMarkedContent(int page)
    {
        while (_mcidPerPage.Count <= page)
            _mcidPerPage.Add(0);

        var mcid = _mcidPerPage[page]++;
        _current.Children.Add(new MarkedContent(page, mcid));
        return mcid;
    }

    /// <summary>The number of pieces of content marked on a page, which its parent tree entry lists.</summary>
    public int MarkedContentCount(int page) => page < _mcidPerPage.Count ? _mcidPerPage[page] : 0;

    /// <summary>Walks the tree in reading order.</summary>
    public static IEnumerable<StructureElement> Flatten(StructureElement element)
    {
        yield return element;
        foreach (var child in element.Children)
        {
            if (child is not StructureElement structure)
                continue;

            foreach (var descendant in Flatten(structure))
                yield return descendant;
        }
    }
}
