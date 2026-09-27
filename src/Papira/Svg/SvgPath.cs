using Papira.Infrastructure;

namespace Papira.Svg;

/// <summary>
/// One segment of a path in the user space of the element that owns it. Every curve is cubic:
/// quadratic segments and elliptical arcs are converted while the path is read.
/// </summary>
internal readonly record struct PathCommand(char Op, float X1, float Y1, float X2, float Y2, float X, float Y)
{
    public static PathCommand Move(float x, float y) => new('m', 0, 0, 0, 0, x, y);

    public static PathCommand Line(float x, float y) => new('l', 0, 0, 0, 0, x, y);

    public static PathCommand Curve(float x1, float y1, float x2, float y2, float x, float y) => new('c', x1, y1, x2, y2, x, y);

    public static readonly PathCommand Close = new('h', 0, 0, 0, 0, 0, 0);
}

/// <summary>A shape as a list of cubic segments, built by the path parser or by the shape elements.</summary>
internal sealed class SvgPath
{
    // A quarter circle as a cubic Bézier: the control points sit this far along the tangents.
    private const float Kappa = 0.5522847f;

    private readonly List<PathCommand> _commands = [];

    public IReadOnlyList<PathCommand> Commands => _commands;

    public int Count => _commands.Count;

    public float CurrentX { get; private set; }

    public float CurrentY { get; private set; }

    public float StartX { get; private set; }

    public float StartY { get; private set; }

    /// <summary>The last control point of a curve, which S and T mirror.</summary>
    public float ControlX { get; private set; }

    public float ControlY { get; private set; }

    public char LastOp { get; private set; }

    /// <summary>S and T record the command the parser saw, not the cubic it was turned into.</summary>
    public void SetLastOp(char op) => LastOp = op;

    public void MoveTo(float x, float y)
    {
        _commands.Add(PathCommand.Move(x, y));
        (CurrentX, CurrentY) = (StartX, StartY) = (x, y);
        (ControlX, ControlY) = (x, y);
        LastOp = 'M';
    }

    public void LineTo(float x, float y)
    {
        _commands.Add(PathCommand.Line(x, y));
        (CurrentX, CurrentY) = (x, y);
        (ControlX, ControlY) = (x, y);
        LastOp = 'L';
    }

    public void CurveTo(float x1, float y1, float x2, float y2, float x, float y)
    {
        _commands.Add(PathCommand.Curve(x1, y1, x2, y2, x, y));
        (CurrentX, CurrentY) = (x, y);
        (ControlX, ControlY) = (x2, y2);
        LastOp = 'C';
    }

    /// <summary>A quadratic segment; a cubic with both control points on the quadratic's one.</summary>
    public void QuadraticTo(float x1, float y1, float x, float y)
    {
        var c1x = CurrentX + 2f / 3 * (x1 - CurrentX);
        var c1y = CurrentY + 2f / 3 * (y1 - CurrentY);
        var c2x = x + 2f / 3 * (x1 - x);
        var c2y = y + 2f / 3 * (y1 - y);
        CurveTo(c1x, c1y, c2x, c2y, x, y);
        (ControlX, ControlY) = (x1, y1);
        LastOp = 'Q';
    }

    public void ClosePath()
    {
        if (_commands.Count == 0)
            return;

        _commands.Add(PathCommand.Close);
        (CurrentX, CurrentY) = (StartX, StartY);
        (ControlX, ControlY) = (StartX, StartY);
        LastOp = 'Z';
    }

    /// <summary>An elliptical arc in endpoint form (ISO 32000 has no arcs, so it becomes up to four cubics).</summary>
    public void ArcTo(float rx, float ry, float rotationDegrees, bool largeArc, bool sweep, float x, float y)
    {
        float x1 = CurrentX, y1 = CurrentY;
        rx = MathF.Abs(rx);
        ry = MathF.Abs(ry);

        // Degenerate arcs are straight lines, as the specification prescribes.
        if (rx < 1e-6f || ry < 1e-6f || (MathF.Abs(x - x1) < 1e-9f && MathF.Abs(y - y1) < 1e-9f))
        {
            LineTo(x, y);
            return;
        }

        var phi = rotationDegrees * MathF.PI / 180;
        float cosPhi = MathF.Cos(phi), sinPhi = MathF.Sin(phi);

        // Endpoint to center parameterization (SVG 1.1, appendix F.6.5).
        float halfDx = (x1 - x) / 2, halfDy = (y1 - y) / 2;
        var xp = cosPhi * halfDx + sinPhi * halfDy;
        var yp = -sinPhi * halfDx + cosPhi * halfDy;

        // Radii too small to span the two endpoints are scaled up until they just fit.
        var lambda = xp * xp / (rx * rx) + yp * yp / (ry * ry);
        if (lambda > 1)
        {
            var scale = MathF.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        var denominator = rx * rx * yp * yp + ry * ry * xp * xp;
        var numerator = rx * rx * ry * ry - denominator;
        var factor = denominator <= 0 ? 0 : MathF.Sqrt(MathF.Max(numerator / denominator, 0));
        if (largeArc == sweep)
            factor = -factor;

        var cxp = factor * rx * yp / ry;
        var cyp = -factor * ry * xp / rx;
        var cx = cosPhi * cxp - sinPhi * cyp + (x1 + x) / 2;
        var cy = sinPhi * cxp + cosPhi * cyp + (y1 + y) / 2;

        var start = MathF.Atan2((yp - cyp) / ry, (xp - cxp) / rx);
        var end = MathF.Atan2((-yp - cyp) / ry, (-xp - cxp) / rx);
        var sweepAngle = end - start;
        if (!sweep && sweepAngle > 0)
            sweepAngle -= 2 * MathF.PI;
        else if (sweep && sweepAngle < 0)
            sweepAngle += 2 * MathF.PI;

        // A cubic tracks a circular arc well up to a quarter turn.
        var segments = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweepAngle) / (MathF.PI / 2)));
        var step = sweepAngle / segments;
        var alpha = 4f / 3 * MathF.Tan(step / 4);

        for (var segment = 0; segment < segments; segment++)
        {
            var from = start + segment * step;
            var to = from + step;
            var (fromX, fromY) = Point(from);
            var (toX, toY) = Point(to);
            var (fromDx, fromDy) = Tangent(from);
            var (toDx, toDy) = Tangent(to);

            CurveTo(
                fromX + alpha * fromDx, fromY + alpha * fromDy,
                toX - alpha * toDx, toY - alpha * toDy,
                segment == segments - 1 ? x : toX,
                segment == segments - 1 ? y : toY);
        }

        LastOp = 'A';

        (float X, float Y) Point(float angle) => (
            cx + rx * MathF.Cos(angle) * cosPhi - ry * MathF.Sin(angle) * sinPhi,
            cy + rx * MathF.Cos(angle) * sinPhi + ry * MathF.Sin(angle) * cosPhi);

        (float X, float Y) Tangent(float angle) => (
            -rx * MathF.Sin(angle) * cosPhi - ry * MathF.Cos(angle) * sinPhi,
            -rx * MathF.Sin(angle) * sinPhi + ry * MathF.Cos(angle) * cosPhi);
    }

    public void AddRectangle(float x, float y, float width, float height, float rx, float ry)
    {
        rx = Math.Min(rx, width / 2);
        ry = Math.Min(ry, height / 2);
        if (rx <= 0 || ry <= 0)
        {
            MoveTo(x, y);
            LineTo(x + width, y);
            LineTo(x + width, y + height);
            LineTo(x, y + height);
            ClosePath();
            return;
        }

        float cx = rx * Kappa, cy = ry * Kappa;
        float right = x + width, bottom = y + height;

        MoveTo(x + rx, y);
        LineTo(right - rx, y);
        CurveTo(right - rx + cx, y, right, y + ry - cy, right, y + ry);
        LineTo(right, bottom - ry);
        CurveTo(right, bottom - ry + cy, right - rx + cx, bottom, right - rx, bottom);
        LineTo(x + rx, bottom);
        CurveTo(x + rx - cx, bottom, x, bottom - ry + cy, x, bottom - ry);
        LineTo(x, y + ry);
        CurveTo(x, y + ry - cy, x + rx - cx, y, x + rx, y);
        ClosePath();
    }

    public void AddEllipse(float centerX, float centerY, float rx, float ry)
    {
        float cx = rx * Kappa, cy = ry * Kappa;
        MoveTo(centerX, centerY - ry);
        CurveTo(centerX + cx, centerY - ry, centerX + rx, centerY - cy, centerX + rx, centerY);
        CurveTo(centerX + rx, centerY + cy, centerX + cx, centerY + ry, centerX, centerY + ry);
        CurveTo(centerX - cx, centerY + ry, centerX - rx, centerY + cy, centerX - rx, centerY);
        CurveTo(centerX - rx, centerY - cy, centerX - cx, centerY - ry, centerX, centerY - ry);
        ClosePath();
    }

    /// <summary>Appends another path with a transform applied, which flattens a clip-path into one shape.</summary>
    public void AppendTransformed(SvgPath source, Matrix matrix)
    {
        foreach (var command in source.Commands)
        {
            var (x, y) = matrix.Apply(command.X, command.Y);
            switch (command.Op)
            {
                case 'm':
                    MoveTo(x, y);
                    break;
                case 'l':
                    LineTo(x, y);
                    break;
                case 'c':
                    var (x1, y1) = matrix.Apply(command.X1, command.Y1);
                    var (x2, y2) = matrix.Apply(command.X2, command.Y2);
                    CurveTo(x1, y1, x2, y2, x, y);
                    break;
                default:
                    ClosePath();
                    break;
            }
        }
    }

    /// <summary>
    /// The tight bounding box of the path in its own user space, which gradients in object bounding box
    /// units are mapped onto. Curves are solved exactly rather than bounded by their control points.
    /// </summary>
    public (float MinX, float MinY, float MaxX, float MaxY) Bounds()
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        float x = 0, y = 0, startX = 0, startY = 0;

        foreach (var command in _commands)
        {
            switch (command.Op)
            {
                case 'm':
                    Include(command.X, command.Y);
                    (x, y) = (startX, startY) = (command.X, command.Y);
                    break;
                case 'l':
                    Include(command.X, command.Y);
                    (x, y) = (command.X, command.Y);
                    break;
                case 'c':
                    Include(command.X, command.Y);
                    IncludeCurve(x, command.X1, command.X2, command.X, true);
                    IncludeCurve(y, command.Y1, command.Y2, command.Y, false);
                    (x, y) = (command.X, command.Y);
                    break;
                default:
                    (x, y) = (startX, startY);
                    break;
            }
        }

        return minX > maxX ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);

        void Include(float pointX, float pointY)
        {
            minX = MathF.Min(minX, pointX);
            minY = MathF.Min(minY, pointY);
            maxX = MathF.Max(maxX, pointX);
            maxY = MathF.Max(maxY, pointY);
        }

        // The extremes of a cubic are at the roots of its derivative, a quadratic.
        void IncludeCurve(float p0, float p1, float p2, float p3, bool horizontal)
        {
            // The endpoints are already included; only the interior extremes are missing.
            var a = 3 * (-p0 + 3 * p1 - 3 * p2 + p3);
            var b = 6 * (p0 - 2 * p1 + p2);
            var c = 3 * (p1 - p0);

            foreach (var t in Roots(a, b, c))
            {
                if (t is <= 0 or >= 1)
                    continue;

                var u = 1 - t;
                var value = u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
                if (horizontal)
                {
                    minX = MathF.Min(minX, value);
                    maxX = MathF.Max(maxX, value);
                }
                else
                {
                    minY = MathF.Min(minY, value);
                    maxY = MathF.Max(maxY, value);
                }
            }
        }

        static float[] Roots(float a, float b, float c)
        {
            if (MathF.Abs(a) < 1e-9f)
                return MathF.Abs(b) < 1e-9f ? [] : [-c / b];

            var discriminant = b * b - 4 * a * c;
            if (discriminant < 0)
                return [];

            var root = MathF.Sqrt(discriminant);
            return [(-b + root) / (2 * a), (-b - root) / (2 * a)];
        }
    }
}

/// <summary>Reads the "d" attribute of a path element.</summary>
internal static class SvgPathParser
{
    /// <summary>
    /// Appends the segments of <paramref name="data"/> to <paramref name="path"/>. Unknown commands end the
    /// path, as the specification requires; malformed numbers simply stop the scan.
    /// </summary>
    public static void Parse(string data, SvgPath path, int limit)
    {
        var scanner = new SvgScanner(data);
        var command = '\0';

        while (!scanner.AtEnd && path.Count < limit)
        {
            if (scanner.PeekLetter() is { } letter)
            {
                command = scanner.ReadLetter();
                if (command is 'Z' or 'z')
                {
                    path.ClosePath();
                    continue;
                }
            }
            else if (command == '\0')
            {
                return; // numbers before any command
            }

            var relative = char.IsLower(command);
            float x = path.CurrentX, y = path.CurrentY;

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    if (!Pair(ref scanner, relative, x, y, out var mx, out var my))
                        return;

                    path.MoveTo(mx, my);

                    // Further coordinate pairs after an M are implicit line segments.
                    command = relative ? 'l' : 'L';
                    break;

                case 'L':
                    if (!Pair(ref scanner, relative, x, y, out var lx, out var ly))
                        return;

                    path.LineTo(lx, ly);
                    break;

                case 'H':
                    if (!scanner.TryReadNumber(out var hx))
                        return;

                    path.LineTo(relative ? x + hx : hx, y);
                    break;

                case 'V':
                    if (!scanner.TryReadNumber(out var vy))
                        return;

                    path.LineTo(x, relative ? y + vy : vy);
                    break;

                case 'C':
                    if (!Pair(ref scanner, relative, x, y, out var c1x, out var c1y) ||
                        !Pair(ref scanner, relative, x, y, out var c2x, out var c2y) ||
                        !Pair(ref scanner, relative, x, y, out var cx, out var cy))
                        return;

                    path.CurveTo(c1x, c1y, c2x, c2y, cx, cy);
                    break;

                case 'S':
                    if (!Pair(ref scanner, relative, x, y, out var s2x, out var s2y) ||
                        !Pair(ref scanner, relative, x, y, out var sx, out var sy))
                        return;

                    // The first control point mirrors the previous one; without a previous curve it is the current point.
                    var (s1x, s1y) = path.LastOp is 'C' or 'S'
                        ? (2 * x - path.ControlX, 2 * y - path.ControlY)
                        : (x, y);
                    path.CurveTo(s1x, s1y, s2x, s2y, sx, sy);
                    path.SetLastOp('S');
                    break;

                case 'Q':
                    if (!Pair(ref scanner, relative, x, y, out var q1x, out var q1y) ||
                        !Pair(ref scanner, relative, x, y, out var qx, out var qy))
                        return;

                    path.QuadraticTo(q1x, q1y, qx, qy);
                    break;

                case 'T':
                    if (!Pair(ref scanner, relative, x, y, out var tx, out var ty))
                        return;

                    var (t1x, t1y) = path.LastOp is 'Q' or 'T'
                        ? (2 * x - path.ControlX, 2 * y - path.ControlY)
                        : (x, y);
                    path.QuadraticTo(t1x, t1y, tx, ty);
                    path.SetLastOp('T');
                    break;

                case 'A':
                    if (!scanner.TryReadNumber(out var rx) || !scanner.TryReadNumber(out var ry) ||
                        !scanner.TryReadNumber(out var rotation))
                        return;

                    var largeArc = scanner.ReadFlag();
                    var sweep = scanner.ReadFlag();
                    if (!Pair(ref scanner, relative, x, y, out var ax, out var ay))
                        return;

                    path.ArcTo(rx, ry, rotation, largeArc, sweep, ax, ay);
                    break;

                default:
                    return; // an unsupported command ends the path
            }
        }
    }

    private static bool Pair(ref SvgScanner scanner, bool relative, float x, float y, out float pointX, out float pointY)
    {
        pointX = pointY = 0;
        if (!scanner.TryReadNumber(out var first) || !scanner.TryReadNumber(out var second))
            return false;

        pointX = relative ? x + first : first;
        pointY = relative ? y + second : second;
        return true;
    }
}
