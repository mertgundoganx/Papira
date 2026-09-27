using Papira.Elements;
using Papira.Fonts;
using Papira.Infrastructure;
using Papira.Pdf;
using Papira.Rendering;
using Papira.Text;

namespace Papira.Svg;

/// <summary>
/// Draws a parsed SVG tree with the PDF path operators. Coordinates are transformed here rather than with
/// the canvas transform stack, which keeps the emitted content stream flat and short.
/// </summary>
internal sealed class SvgRenderer(Canvas canvas)
{
    /// <summary>Draws the document into a box of <paramref name="width"/> by <paramref name="height"/> points.</summary>
    public void Draw(SvgDocument document, float width, float height)
    {
        if (width <= 0 || height <= 0 || document.ViewBoxWidth <= 0 || document.ViewBoxHeight <= 0)
            return;

        var scaleX = width / document.ViewBoxWidth;
        var scaleY = height / document.ViewBoxHeight;
        if (document.PreserveAspectRatio)
            scaleX = scaleY = MathF.Min(scaleX, scaleY);

        // The element is sized from the aspect ratio, so the drawing is centred only when it was overridden.
        var offsetX = (width - document.ViewBoxWidth * scaleX) / 2;
        var offsetY = (height - document.ViewBoxHeight * scaleY) / 2;

        var matrix = Matrix.Multiply(
            Matrix.Multiply(Matrix.Translation(-document.ViewBoxX, -document.ViewBoxY), Matrix.Scaling(scaleX, scaleY)),
            Matrix.Translation(offsetX, offsetY));

        Draw(document.Root, matrix);
    }

    /// <summary>Draws a tree with a transform of its own, which a glyph from a font needs.</summary>
    public void DrawNode(SvgNode node, Matrix matrix) => Draw(node, matrix);

    private void Draw(SvgNode node, Matrix parent)
    {
        var matrix = Matrix.Multiply(node.Transform, parent);
        var groups = 0;

        if (node.Mask is { } mask && Mask(node, mask, matrix) is { } state)
        {
            canvas.BeginState(state);
            groups++;
        }

        if (node.Opacity < 1)
        {
            canvas.BeginOpacityGroup(node.Opacity);
            groups++;
        }

        if (node.Clip is { } clip)
        {
            canvas.BeginGroup();
            WritePath(clip, matrix);
            canvas.ClipPath(node.ClipEvenOdd);
            groups++;
        }

        switch (node)
        {
            case SvgGroupNode group:
                foreach (var child in group.Children)
                    Draw(child, matrix);

                break;

            case SvgShapeNode shape:
                DrawShape(shape, matrix);
                break;

            case SvgTextNode text:
                DrawText(text, matrix);
                break;
        }

        while (groups-- > 0)
            canvas.EndGroup();
    }

    private void DrawShape(SvgShapeNode shape, Matrix matrix)
    {
        var style = shape.Style;
        var scale = matrix.Scale;
        var strokeWidth = style.StrokeWidth * scale;
        var strokes = style.Stroke.Paints && strokeWidth > 0;
        var fills = style.Fill.Paints;

        // One painting operator when the fill is a plain color and both parts share the same opacity.
        if (fills && strokes && style.Fill.Kind == SvgPaintKind.Solid && MathF.Abs(style.FillOpacity - style.StrokeOpacity) < 1e-3f)
        {
            BeginOpacity(style.FillOpacity);
            SetLineStyle(style, scale);
            canvas.BeginPath(style.Fill.Color, style.Stroke.Color, strokeWidth);
            WritePath(shape.Path, matrix);
            canvas.EndPath(true, true, style.EvenOdd);
            EndOpacity(style.FillOpacity);
            return;
        }

        if (fills)
        {
            BeginOpacity(style.FillOpacity);

            // A gradient that cannot be placed (a shape with no area) falls back to its first stop.
            var gradient = style.Fill.Gradient is { } declared ? Shading(declared, shape.Path, matrix) : null;
            var pattern = style.Fill.Pattern is { } tiling ? Tiling(tiling, shape.Path, matrix) : null;
            if (gradient is { } placed)
                canvas.BeginShadingPath(placed.Shading, placed.Local);
            else if (pattern is { } name)
                canvas.BeginPatternPath(name);
            else
                canvas.BeginPath(style.Fill.Color, null, 0);

            WritePath(shape.Path, matrix);
            canvas.EndPath(true, false, style.EvenOdd);
            EndOpacity(style.FillOpacity);
        }

        if (strokes)
        {
            BeginOpacity(style.StrokeOpacity);
            SetLineStyle(style, scale);

            // A gradient stroke falls back to the first stop of the gradient.
            canvas.BeginPath(null, style.Stroke.Color, strokeWidth);
            WritePath(shape.Path, matrix);
            canvas.EndPath(false, true);
            EndOpacity(style.StrokeOpacity);
        }
    }

    private void SetLineStyle(SvgStyle style, float scale)
    {
        float[]? dashes = null;
        if (style.Dashes is { Length: > 0 } pattern)
        {
            dashes = new float[pattern.Length];
            for (var i = 0; i < pattern.Length; i++)
                dashes[i] = pattern[i] * scale;
        }

        canvas.SetLineStyle(style.LineCap, style.LineJoin, dashes, style.DashOffset * scale);
    }

    /// <summary>
    /// Places a gradient: in user space units its coordinates only need the element's transform, while in
    /// object bounding box units they are fractions of the shape's own box.
    /// </summary>
    private static (Shading Shading, Matrix Local)? Shading(SvgGradient gradient, SvgPath path, Matrix matrix)
    {
        var local = matrix;
        if (!gradient.UserSpace)
        {
            var (minX, minY, maxX, maxY) = path.Bounds();
            float width = maxX - minX, height = maxY - minY;
            if (width <= 0 || height <= 0)
                return null; // a degenerate box has no gradient to map onto

            local = Matrix.Multiply(Matrix.Multiply(Matrix.Scaling(width, height), Matrix.Translation(minX, minY)), matrix);
        }

        var shading = new Shading(
            gradient.Radial,
            gradient.X0,
            gradient.Y0,
            gradient.Radius0,
            gradient.X1,
            gradient.Y1,
            gradient.Radius1,
            gradient.Stops);

        return (shading, Matrix.Multiply(gradient.Transform, local));
    }

    // ---- Text -----------------------------------------------------------------------------------

    /// <summary>
    /// Draws the runs of a piece of text. The pen walks from run to run, unless a run says where it
    /// starts; a chunk that begins with such a run is placed by what it is anchored at.
    /// </summary>
    private void DrawText(SvgTextNode node, Matrix matrix)
    {
        var runs = node.Runs;
        var shaped = new ShapedRun[runs.Length];
        for (var i = 0; i < runs.Length; i++)
            shaped[i] = Measure(runs[i]);

        float penX = 0, penY = 0;
        for (var i = 0; i < runs.Length; i++)
        {
            if (runs[i].X.Length > 0)
            {
                // A new chunk: what it is anchored at decides where the whole of it goes.
                penX = runs[i].X[0] - Anchor(runs, shaped, i);
            }

            if (runs[i].Y.Length > 0)
                penY = runs[i].Y[0];

            DrawRun(runs[i], shaped[i], matrix, ref penX, ref penY);
        }
    }

    /// <summary>How far a chunk is moved so that it sits where it is anchored.</summary>
    private static float Anchor(SvgTextRun[] runs, ShapedRun[] shaped, int start)
    {
        if (runs[start].Anchor == SvgTextAnchor.Start)
            return 0;

        // The chunk reaches to the next run that says where it starts.
        float width = 0;
        for (var i = start; i < runs.Length && (i == start || runs[i].X.Length == 0); i++)
            width += shaped[i].Width;

        return runs[start].Anchor == SvgTextAnchor.Middle ? width / 2 : width;
    }

    private void DrawRun(SvgTextRun run, ShapedRun shaped, Matrix matrix, ref float penX, ref float penY)
    {
        if (shaped.Glyphs.Length == 0)
            return;

        if (run.Style.Fill.Kind == SvgPaintKind.None)
        {
            penX += shaped.Width;
            return;
        }

        canvas.PushTransform();
        canvas.Concat(matrix);
        BeginOpacity(run.Style.FillOpacity);

        // A glyph that stands for several characters has to extract as all of them.
        if (shaped.HasLigatures)
        {
            var usage = canvas.Resources.GetFont(shaped.Style.Font.Font);
            for (var i = 0; i < shaped.Glyphs.Length; i++)
            {
                var glyph = shaped.Glyphs[i];
                if (glyph.Length > 1 && glyph.Cluster + glyph.Length <= shaped.Text.Length)
                    usage.UseSequence(glyph.Glyph, shaped.Text.AsSpan(glyph.Cluster, glyph.Length));
            }
        }

        // Glyphs that follow one another are drawn in one go; one that the drawing places itself, or
        // that the font moved off the line, is drawn on its own.
        var batch = -1;
        var batchX = penX;
        var x = penX;
        var y = penY;

        for (var i = 0; i < shaped.Glyphs.Length; i++)
        {
            var glyph = shaped.Glyphs[i];
            var cluster = glyph.Cluster;
            var placed = (cluster > 0 && (cluster < run.X.Length || cluster < run.Y.Length)) ||
                cluster < run.Dx.Length || cluster < run.Dy.Length ||
                glyph.XOffset != 0 || glyph.YOffset != 0;

            if (!placed)
            {
                if (batch < 0)
                {
                    batch = i;
                    batchX = x;
                }

                x += Advance(run, shaped, i);
                continue;
            }

            Flush(i);

            if (cluster > 0 && cluster < run.X.Length)
                x = run.X[cluster];
            if (cluster > 0 && cluster < run.Y.Length)
                y = run.Y[cluster];
            if (cluster < run.Dx.Length)
                x += run.Dx[cluster];
            if (cluster < run.Dy.Length)
                y += run.Dy[cluster];

            canvas.DrawGlyphs(
                shaped.Mark,
                x + (glyph.XOffset * shaped.Style.Scale),
                y - (glyph.YOffset * shaped.Style.Scale),
                shaped.Ids.AsSpan(i, 1),
                shaped.Codepoints.AsSpan(i, 1),
                []);

            x += Advance(run, shaped, i);
        }

        Flush(shaped.Glyphs.Length);

        penX = x;
        penY = y;
        EndOpacity(run.Style.FillOpacity);
        canvas.PopTransform();

        void Flush(int end)
        {
            if (batch < 0 || end <= batch)
            {
                batch = -1;
                return;
            }

            canvas.DrawGlyphs(
                shaped.Style,
                batchX,
                y,
                shaped.Ids.AsSpan(batch, end - batch),
                shaped.Codepoints.AsSpan(batch, end - batch),
                shaped.Kerning.AsSpan(batch, end - batch));

            batch = -1;
        }
    }

    /// <summary>How far the pen moves on after a glyph: its advance, and the spacing between letters.</summary>
    private static float Advance(SvgTextRun run, ShapedRun shaped, int index)
    {
        var glyph = shaped.Glyphs[index];
        return ((glyph.BaseAdvance + glyph.XAdvance) * shaped.Style.Scale) + (glyph.Mark ? 0 : run.LetterSpacing);
    }

    /// <summary>A run of text with the glyphs a font draws it as.</summary>
    private readonly record struct ShapedRun(
        ShapedGlyph[] Glyphs,
        ushort[] Ids,
        int[] Codepoints,
        short[] Kerning,
        int[] Text,
        bool HasLigatures,
        float Width,
        ResolvedTextStyle Style,
        ResolvedTextStyle Mark);

    private static ShapedRun Measure(SvgTextRun run)
    {
        var style = new ResolvedTextStyle(Style(run, run.LetterSpacing));

        var codepoints = new List<int>(run.Text.Length);
        foreach (var rune in run.Text.EnumerateRunes())
            codepoints.Add(rune.Value);

        var script = 0u;
        foreach (var codepoint in codepoints)
        {
            script = TextShaper.ScriptOf(codepoint);
            if (script != 0)
                break;
        }

        var buffer = new ShapingBuffer();
        TextShaper.Shape(
            style.Font.Font,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(codepoints),
            script,
            rightToLeft: false,
            buffer,
            ligatures: run.LetterSpacing == 0);

        var glyphs = buffer.Glyphs.ToArray();
        var ids = new ushort[glyphs.Length];
        var characters = new int[glyphs.Length];
        var kerning = new short[glyphs.Length];
        var ligatures = false;
        float width = 0;
        for (var i = 0; i < glyphs.Length; i++)
        {
            ids[i] = glyphs[i].Glyph;
            characters[i] = codepoints[Math.Clamp(glyphs[i].Cluster, 0, codepoints.Count - 1)];

            // What positioning added to the advance moves the glyph after it, which is what kerning does.
            kerning[i] = glyphs[i].XAdvance;
            ligatures |= glyphs[i].Length > 1;
            width += ((glyphs[i].BaseAdvance + glyphs[i].XAdvance) * style.Scale) + (glyphs[i].Mark ? 0 : run.LetterSpacing);
        }

        // A mark is drawn with no spacing of its own: it belongs to the letter, not between two of them.
        var mark = run.LetterSpacing == 0 ? style : new ResolvedTextStyle(Style(run, 0), style.Font);
        return new ShapedRun(glyphs, ids, characters, kerning, [.. codepoints], ligatures, width, style, mark);
    }

    /// <summary>The text style a run is drawn with: the font it asks for, at the size it asks for.</summary>
    private static TextStyle Style(SvgTextRun run, float letterSpacing) =>
        TextStyle.Default
            .FontFamily(run.Family ?? FontManager.DefaultFontFamily)
            .FontSize(run.FontSize)
            .FontWeight(run.Weight)
            .Italic(run.Italic)
            .LetterSpacing(letterSpacing)
            .FontColor(run.Style.Fill.Color);

    // ---- Masks and patterns ----------------------------------------------------------------------

    /// <summary>
    /// Draws the inside of a mask into a stream of its own and hands it to the page as a graphics state:
    /// what follows shows through where the mask is light.
    /// </summary>
    private string? Mask(SvgNode node, SvgMask mask, Matrix matrix)
    {
        var (minX, minY, maxX, maxY) = Bounds(node, matrix);
        if (maxX <= minX || maxY <= minY)
            return null;

        // The area of the mask, either in the user space of the element or as fractions of its box.
        var area = mask.ObjectBoundingBox
            ? new Matrix(maxX - minX, 0, 0, maxY - minY, minX, minY)
            : matrix;

        // The corners of the mask, taken all the way to the page: that is the space the form is drawn in.
        var page = canvas.Transform;
        var start = area.Apply(mask.X, mask.Y);
        var end = area.Apply(mask.X + mask.Width, mask.Y + mask.Height);
        var (left, top) = page.Apply(start.X, start.Y);
        var (right, bottom) = page.Apply(end.X, end.Y);

        using var content = new ByteBuffer(512);
        var inner = new Canvas(canvas.Resources);
        inner.BeginContent(content, 0, 0, canvas.Transform);
        new SvgRenderer(inner).DrawNode(mask.Content, matrix);
        inner.EndPage();

        return canvas.Resources.GetMaskForm(
            content.ToArray(),
            Math.Min(left, right),
            Math.Min(top, bottom),
            Math.Max(left, right),
            Math.Max(top, bottom));
    }

    /// <summary>Draws one tile of a pattern and says how the copies of it are laid out.</summary>
    private string? Tiling(SvgPattern pattern, SvgPath path, Matrix matrix)
    {
        var (minX, minY, maxX, maxY) = path.Bounds();
        float boxWidth = maxX - minX, boxHeight = maxY - minY;
        if (pattern.ObjectBoundingBox && (boxWidth <= 0 || boxHeight <= 0))
            return null;

        // Where one tile goes and how large it is, in the user space of the shape.
        var x = pattern.ObjectBoundingBox ? minX + (pattern.X * boxWidth) : pattern.X;
        var y = pattern.ObjectBoundingBox ? minY + (pattern.Y * boxHeight) : pattern.Y;
        var width = pattern.ObjectBoundingBox ? pattern.Width * boxWidth : pattern.Width;
        var height = pattern.ObjectBoundingBox ? pattern.Height * boxHeight : pattern.Height;
        if (width <= 0 || height <= 0)
            return null;

        // The tile is drawn in its own space, which the pattern matrix then maps onto the page.
        var content = new ByteBuffer(512);
        try
        {
            var inner = new Canvas(canvas.Resources);
            inner.BeginContent(content, width, height, Matrix.Identity);

            // The tile is clipped to its own box here rather than left to the reader, so that what falls
            // outside it — half of a stroke on the edge, say — is gone wherever the file is opened.
            inner.BeginClipGroup(0, 0, width, height, 0);

            var local = Matrix.Identity;
            if (pattern.ViewBox.Length == 4 && pattern.ViewBox[2] > 0 && pattern.ViewBox[3] > 0)
            {
                local = Matrix.Multiply(
                    Matrix.Translation(-pattern.ViewBox[0], -pattern.ViewBox[1]),
                    Matrix.Scaling(width / pattern.ViewBox[2], height / pattern.ViewBox[3]));
            }
            else if (pattern.ContentObjectBoundingBox)
            {
                local = Matrix.Scaling(boxWidth, boxHeight);
            }

            new SvgRenderer(inner).DrawNode(pattern.Content, local);
            inner.EndGroup();
            inner.EndPage();

            // Tile space to the page: where the tile sits, then the element's transform, then the page.
            var placement = Matrix.Multiply(
                Matrix.Multiply(pattern.Transform, Matrix.Translation(x, y)),
                Matrix.Multiply(matrix, canvas.Transform));

            return canvas.Resources.GetTiling(content.ToArray(), 0, 0, width, height, width, height, placement);
        }
        finally
        {
            content.Dispose();
        }
    }

    /// <summary>The box a node covers, which the fractions of a mask are measured against.</summary>
    private static (float MinX, float MinY, float MaxX, float MaxY) Bounds(SvgNode node, Matrix matrix)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        Walk(node, matrix);
        return (minX, minY, maxX, maxY);

        void Walk(SvgNode current, Matrix parent)
        {
            var local = ReferenceEquals(current, node) ? parent : Matrix.Multiply(current.Transform, parent);
            switch (current)
            {
                case SvgShapeNode shape:
                    foreach (var command in shape.Path.Commands)
                    {
                        var (x, y) = local.Apply(command.X, command.Y);
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }

                    break;

                case SvgGroupNode group:
                    foreach (var child in group.Children)
                        Walk(child, local);

                    break;

                case SvgTextNode text:
                    // Text is measured by where it is written, which is enough for a mask to cover it.
                    foreach (var run in text.Runs)
                    {
                        var (x, y) = local.Apply(run.X.Length > 0 ? run.X[0] : 0, run.Y.Length > 0 ? run.Y[0] : 0);
                        minX = Math.Min(minX, x - run.FontSize);
                        minY = Math.Min(minY, y - run.FontSize);
                        maxX = Math.Max(maxX, x + (run.FontSize * run.Text.Length));
                        maxY = Math.Max(maxY, y + run.FontSize);
                    }

                    break;
            }
        }
    }

    private void WritePath(SvgPath path, Matrix matrix)
    {
        foreach (var command in path.Commands)
        {
            var (x, y) = matrix.Apply(command.X, command.Y);
            switch (command.Op)
            {
                case 'm':
                    canvas.MoveTo(x, y);
                    break;
                case 'l':
                    canvas.LineTo(x, y);
                    break;
                case 'c':
                    var (x1, y1) = matrix.Apply(command.X1, command.Y1);
                    var (x2, y2) = matrix.Apply(command.X2, command.Y2);
                    canvas.CurveTo(x1, y1, x2, y2, x, y);
                    break;
                default:
                    canvas.ClosePath();
                    break;
            }
        }
    }

    private void BeginOpacity(float opacity)
    {
        if (opacity < 1)
            canvas.BeginOpacityGroup(opacity);
    }

    private void EndOpacity(float opacity)
    {
        if (opacity < 1)
            canvas.EndGroup();
    }
}
