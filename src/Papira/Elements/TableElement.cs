using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

internal readonly record struct TableColumn(bool IsConstant, float Value);

internal sealed class TableCell : ContainerElement, ITableCellContainer
{
    public int ColumnSpan = 1;
    public int RowSpan = 1;
}

/// <summary>
/// Grid of cells flowing left to right into rows; cells may span columns and rows. Rows connected by row spans
/// form a group that is laid out as a unit and moves to the next page as a whole; a group of a single row may
/// split across pages. Header rows are repeated on every page.
/// </summary>
internal sealed class TableElement : Element
{
    // A value type kept in arrays: tables are measured many times during layout, so cells should be contiguous in memory.
    private readonly record struct PlacedCell(TableCell Cell, int Row, int Column, int ColumnSpan, int RowSpan);

    /// <summary>Consecutive rows connected by row spans. Cell rows are relative to the group.</summary>
    private sealed class RowGroup(PlacedCell[] cells, int rowCount)
    {
        public PlacedCell[] Cells { get; } = cells;
        public int RowCount { get; } = rowCount;
    }

    // Large enough for any page; used to measure the natural height of cells in multi-row groups.
    private const float Unbounded = 1_000_000;

    public readonly List<TableColumn> Columns = [];
    public readonly List<TableCell> HeaderCells = [];
    public readonly List<TableCell> Cells = [];

    private List<RowGroup>? _headerGroups;
    private List<RowGroup>? _groups;
    private float[] _columnX = [];
    private float _widthsFor = -1;
    private int _currentGroup;
    private bool _headerOnlyDrawn;

    /// <summary>Places cells on the grid, skipping positions taken by row spans above, and groups connected rows.</summary>
    private List<RowGroup> BuildGroups(List<TableCell> cells)
    {
        if (Columns.Count == 0)
            throw new DocumentComposeException("Table has no columns. Define them with table.ColumnsDefinition(...).");

        // Row (exclusive) up to which each column is taken by a cell placed earlier. Cells are placed in row order,
        // so a column is free at a row once that row reaches this value.
        var occupiedUntil = new int[Columns.Count];
        var placed = new List<PlacedCell>(cells.Count);
        int row = 0, column = 0;

        foreach (var cell in cells)
        {
            var columnSpan = Math.Clamp(cell.ColumnSpan, 1, Columns.Count);
            var rowSpan = Math.Max(1, cell.RowSpan);

            while (true)
            {
                if (column + columnSpan > Columns.Count)
                {
                    row++;
                    column = 0;
                    continue;
                }

                var free = true;
                for (var c = column; c < column + columnSpan && free; c++)
                    free = occupiedUntil[c] <= row;

                if (free)
                    break;
                column++;
            }

            placed.Add(new PlacedCell(cell, row, column, columnSpan, rowSpan));
            for (var c = column; c < column + columnSpan; c++)
                occupiedUntil[c] = row + rowSpan;

            column += columnSpan;
        }

        // Split into groups in one pass: a group ends at a row that no row span crosses.
        var groups = new List<RowGroup>();
        var first = 0;
        int groupStart = 0, groupEnd = 0;

        for (var i = 0; i <= placed.Count; i++)
        {
            if (i < placed.Count && (i == first || placed[i].Row < groupEnd))
            {
                if (i == first)
                    groupStart = placed[i].Row;
                groupEnd = Math.Max(groupEnd, placed[i].Row + placed[i].RowSpan);
                continue;
            }

            if (i > first)
            {
                var groupCells = new PlacedCell[i - first];
                for (var k = first; k < i; k++)
                    groupCells[k - first] = placed[k] with { Row = placed[k].Row - groupStart };
                groups.Add(new RowGroup(groupCells, groupEnd - groupStart));
            }

            if (i < placed.Count)
            {
                first = i;
                groupStart = placed[i].Row;
                groupEnd = placed[i].Row + placed[i].RowSpan;
            }
        }

        return groups;
    }

    private void EnsureStructure(float width)
    {
        _headerGroups ??= BuildGroups(HeaderCells);
        _groups ??= BuildGroups(Cells);

        if (Math.Abs(_widthsFor - width) < Size.Epsilon)
            return;

        _widthsFor = width;
        float constant = 0, relative = 0;
        foreach (var column in Columns)
        {
            if (column.IsConstant) constant += column.Value;
            else relative += column.Value;
        }

        var perUnit = relative > 0 ? Math.Max(0, width - constant) / relative : 0;
        _columnX = new float[Columns.Count + 1];
        for (var i = 0; i < Columns.Count; i++)
            _columnX[i + 1] = _columnX[i] + (Columns[i].IsConstant ? Columns[i].Value : Columns[i].Value * perUnit);
    }

    private float CellWidth(in PlacedCell cell) => _columnX[cell.Column + cell.ColumnSpan] - _columnX[cell.Column];

    private SpacePlan MeasureGroup(RowGroup group, float availableHeight, LayoutContext context)
    {
        if (group.RowCount == 1)
            return MeasureSingleRow(group, availableHeight, context);

        var rowHeights = RowHeights(group, context, out var fits);
        if (!fits)
            return SpacePlan.Wrap;

        var height = rowHeights.Sum();
        return height > availableHeight + Size.Epsilon ? SpacePlan.Wrap : SpacePlan.Full(_columnX[^1], height);
    }

    /// <summary>A single row may be drawn partially, continuing on the next page.</summary>
    private SpacePlan MeasureSingleRow(RowGroup group, float availableHeight, LayoutContext context)
    {
        float height = 0;
        bool anyContent = false, anyPartial = false;

        foreach (var cell in group.Cells)
        {
            var plan = cell.Cell.Measure(new Size(CellWidth(cell), availableHeight), context);
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

        return anyPartial ? SpacePlan.Partial(_columnX[^1], height) : SpacePlan.Full(_columnX[^1], height);
    }

    /// <summary>
    /// Row heights of a multi-row group: each row fits its single-row cells; when a spanning cell needs more
    /// than its rows provide, the last spanned row grows.
    /// </summary>
    private float[] RowHeights(RowGroup group, LayoutContext context, out bool fits)
    {
        var heights = new float[group.RowCount];
        var natural = new float[group.Cells.Length];
        fits = true;

        for (var i = 0; i < group.Cells.Length; i++)
        {
            var cell = group.Cells[i];
            var plan = cell.Cell.Measure(new Size(CellWidth(cell), Unbounded), context);
            if (plan.IsWrap)
            {
                fits = false;
                return heights;
            }

            natural[i] = plan.HasContent ? plan.Height : 0;
            if (cell.RowSpan == 1)
                heights[cell.Row] = Math.Max(heights[cell.Row], natural[i]);
        }

        foreach (var i in Enumerable.Range(0, group.Cells.Length).OrderBy(i => group.Cells[i].RowSpan))
        {
            var cell = group.Cells[i];
            if (cell.RowSpan == 1)
                continue;

            var last = Math.Min(cell.Row + cell.RowSpan, group.RowCount) - 1;
            float spanned = 0;
            for (var r = cell.Row; r <= last; r++)
                spanned += heights[r];

            if (natural[i] > spanned)
                heights[last] += natural[i] - spanned;
        }

        return heights;
    }

    private void DrawGroup(RowGroup group, float height, LayoutContext context)
    {
        if (group.RowCount == 1)
        {
            foreach (var cell in group.Cells)
                DrawCell(cell, 0, height, context);
            return;
        }

        var rowHeights = RowHeights(group, context, out _);
        var rowY = new float[group.RowCount + 1];
        for (var r = 0; r < group.RowCount; r++)
            rowY[r + 1] = rowY[r] + rowHeights[r];

        foreach (var cell in group.Cells)
        {
            var last = Math.Min(cell.Row + cell.RowSpan, group.RowCount);
            DrawCell(cell, rowY[cell.Row], rowY[last] - rowY[cell.Row], context);
        }
    }

    private void DrawCell(in PlacedCell cell, float y, float height, LayoutContext context)
    {
        var size = new Size(CellWidth(cell), height);
        var finished = !cell.Cell.Measure(size, context).HasContent;

        // Cells whose content is finished still draw their background and borders, so a row that
        // continues on the next page (or has empty cells) keeps its grid.
        var x = _columnX[cell.Column];
        var previous = context.DrawEmptyDecorations;
        context.Canvas.Translate(x, y);
        context.DrawEmptyDecorations = previous || finished;
        cell.Cell.Draw(size, context);
        context.DrawEmptyDecorations = previous;
        context.Canvas.Translate(-x, -y);
    }

    private void ResetHeader()
    {
        foreach (var cell in HeaderCells)
            cell.Reset();
    }

    private float MeasureHeader(Size available, LayoutContext context, out bool fits)
    {
        ResetHeader();
        float height = 0;
        fits = true;

        foreach (var group in _headerGroups!)
        {
            var plan = MeasureGroup(group, available.Height - height, context);
            if (plan.Kind is SpacePlanKind.Wrap or SpacePlanKind.Partial)
            {
                fits = false;
                return height;
            }

            height += plan.Height;
        }

        return height;
    }

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        EnsureStructure(available.Width);

        // A table without body rows still shows its header once.
        if (_groups!.Count == 0)
        {
            if (_headerOnlyDrawn || _headerGroups!.Count == 0)
                return SpacePlan.Empty;

            var headerHeight = MeasureHeader(available, context, out var fits);
            return fits ? SpacePlan.Full(_columnX[^1], headerHeight) : SpacePlan.Wrap;
        }

        if (_currentGroup >= _groups.Count)
            return SpacePlan.Empty;

        var y = MeasureHeader(available, context, out var headerFits);
        if (!headerFits)
            return SpacePlan.Wrap;

        var placed = 0;
        var complete = true;
        for (var g = _currentGroup; g < _groups.Count; g++)
        {
            var plan = MeasureGroup(_groups[g], available.Height - y, context);
            if (plan.IsEmpty)
                continue;

            if (plan.IsWrap)
            {
                complete = false;
                break;
            }

            placed++;
            y += plan.Height;
            if (plan.Kind == SpacePlanKind.Partial)
            {
                complete = false;
                break;
            }
        }

        if (placed == 0)
            return complete ? SpacePlan.Empty : SpacePlan.Wrap;

        return complete ? SpacePlan.Full(_columnX[^1], y) : SpacePlan.Partial(_columnX[^1], y);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        EnsureStructure(available.Width);
        var canvas = context.Canvas;

        if (_groups!.Count == 0)
        {
            if (_headerOnlyDrawn)
                return;
            _headerOnlyDrawn = true;
        }
        else if (_currentGroup >= _groups.Count)
        {
            return;
        }

        ResetHeader();
        float y = 0;
        foreach (var group in _headerGroups!)
        {
            var plan = MeasureGroup(group, available.Height - y, context);
            if (!plan.HasContent)
                continue;

            canvas.Translate(0, y);
            DrawGroup(group, plan.Height, context);
            canvas.Translate(0, -y);
            y += plan.Height;
        }

        while (_currentGroup < _groups.Count)
        {
            var group = _groups[_currentGroup];
            var plan = MeasureGroup(group, available.Height - y, context);
            if (plan.IsEmpty)
            {
                _currentGroup++;
                continue;
            }

            if (plan.IsWrap)
                return;

            canvas.Translate(0, y);
            DrawGroup(group, plan.Height, context);
            canvas.Translate(0, -y);
            y += plan.Height;

            if (plan.Kind == SpacePlanKind.Partial)
                return;

            _currentGroup++;
        }
    }

    internal override void Reset()
    {
        _currentGroup = 0;
        _headerOnlyDrawn = false;
        foreach (var cell in HeaderCells)
            cell.Reset();
        foreach (var cell in Cells)
            cell.Reset();
    }
}
