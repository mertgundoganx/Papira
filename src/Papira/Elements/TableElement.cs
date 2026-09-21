using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

internal readonly record struct TableColumn(bool IsConstant, float Value);

internal sealed class TableCell : ContainerElement, ITableCellContainer
{
    public int ColumnSpan = 1;
}

/// <summary>
/// Grid of cells flowing left to right into rows. Rows are laid out as units (cells of a row share its height)
/// and may split across pages; header rows are repeated on every page.
/// </summary>
internal sealed class TableElement : Element
{
    private sealed class TableRow
    {
        public readonly List<(TableCell Cell, int Column)> Cells = [];
    }

    public readonly List<TableColumn> Columns = [];
    public readonly List<TableCell> HeaderCells = [];
    public readonly List<TableCell> Cells = [];

    private List<TableRow>? _headerRows;
    private List<TableRow>? _rows;
    private float[] _columnX = [];
    private float _widthsFor = -1;
    private int _currentRow;
    private bool _headerOnlyDrawn;

    private List<TableRow> BuildRows(List<TableCell> cells)
    {
        if (Columns.Count == 0)
            throw new DocumentComposeException("Table has no columns. Define them with table.ColumnsDefinition(...).");

        var rows = new List<TableRow>();
        TableRow? row = null;
        var column = 0;

        foreach (var cell in cells)
        {
            var span = Math.Clamp(cell.ColumnSpan, 1, Columns.Count);
            if (row == null || column + span > Columns.Count)
            {
                row = new TableRow();
                rows.Add(row);
                column = 0;
            }

            row.Cells.Add((cell, column));
            cell.ColumnSpan = span;
            column += span;
        }

        return rows;
    }

    private void EnsureStructure(float width)
    {
        _headerRows ??= BuildRows(HeaderCells);
        _rows ??= BuildRows(Cells);

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

    private float CellWidth(TableCell cell, int column) => _columnX[column + cell.ColumnSpan] - _columnX[column];

    private SpacePlan MeasureRow(TableRow row, float availableHeight, LayoutContext context)
    {
        float height = 0;
        bool anyContent = false, anyPartial = false;

        foreach (var (cell, column) in row.Cells)
        {
            var plan = cell.Measure(new Size(CellWidth(cell, column), availableHeight), context);
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

    private void DrawRow(TableRow row, float height, LayoutContext context)
    {
        foreach (var (cell, column) in row.Cells)
        {
            var size = new Size(CellWidth(cell, column), height);
            var finished = !cell.Measure(size, context).HasContent;

            // Cells whose content is finished still draw their background and borders, so a row that
            // continues on the next page (or has empty cells) keeps its grid.
            var x = _columnX[column];
            var previous = context.DrawEmptyDecorations;
            context.Canvas.Translate(x, 0);
            context.DrawEmptyDecorations = previous || finished;
            cell.Draw(size, context);
            context.DrawEmptyDecorations = previous;
            context.Canvas.Translate(-x, 0);
        }
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

        foreach (var row in _headerRows!)
        {
            var plan = MeasureRow(row, available.Height - height, context);
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
        if (_rows!.Count == 0)
        {
            if (_headerOnlyDrawn || _headerRows!.Count == 0)
                return SpacePlan.Empty;

            var headerHeight = MeasureHeader(available, context, out var fits);
            return fits ? SpacePlan.Full(_columnX[^1], headerHeight) : SpacePlan.Wrap;
        }

        if (_currentRow >= _rows.Count)
            return SpacePlan.Empty;

        var y = MeasureHeader(available, context, out var headerFits);
        if (!headerFits)
            return SpacePlan.Wrap;

        var placed = 0;
        var complete = true;
        for (var r = _currentRow; r < _rows.Count; r++)
        {
            var plan = MeasureRow(_rows[r], available.Height - y, context);
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

        if (_rows!.Count == 0)
        {
            if (_headerOnlyDrawn)
                return;
            _headerOnlyDrawn = true;
        }
        else if (_currentRow >= _rows.Count)
        {
            return;
        }

        ResetHeader();
        float y = 0;
        foreach (var row in _headerRows!)
        {
            var plan = MeasureRow(row, available.Height - y, context);
            if (!plan.HasContent)
                continue;

            canvas.Translate(0, y);
            DrawRow(row, plan.Height, context);
            canvas.Translate(0, -y);
            y += plan.Height;
        }

        while (_currentRow < _rows.Count)
        {
            var row = _rows[_currentRow];
            var plan = MeasureRow(row, available.Height - y, context);
            if (plan.IsEmpty)
            {
                _currentRow++;
                continue;
            }

            if (plan.IsWrap)
                return;

            canvas.Translate(0, y);
            DrawRow(row, plan.Height, context);
            canvas.Translate(0, -y);
            y += plan.Height;

            if (plan.Kind == SpacePlanKind.Partial)
                return;

            _currentRow++;
        }
    }

    internal override void Reset()
    {
        _currentRow = 0;
        _headerOnlyDrawn = false;
        foreach (var cell in HeaderCells)
            cell.Reset();
        foreach (var cell in Cells)
            cell.Reset();
    }
}
