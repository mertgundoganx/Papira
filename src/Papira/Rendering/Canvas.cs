using Papira.Elements;
using Papira.Fonts;
using Papira.Infrastructure;
using Papira.Pdf;

namespace Papira.Rendering;

/// <summary>
/// Writes PDF content-stream operators for one page at a time. Layout code uses a top-left origin;
/// the canvas keeps a matrix that maps layout coordinates to PDF page coordinates, so rotation and
/// scaling work everywhere. Redundant graphics-state changes are skipped.
/// </summary>
internal sealed class Canvas(DocumentResources resources)
{
    private readonly Stack<Matrix> _matrices = new();
    private ByteBuffer _out = null!;
    private Matrix _matrix = Matrix.Identity;

    private bool _inText;
    private Color? _fill;
    private Color? _stroke;
    private float _lineWidth;
    private FontUsage? _font;
    private float _fontSize;
    private float _charSpacing;
    private int _renderMode;
    private int _lineCap;
    private int _lineJoin;
    private bool? _dashed;
    private bool _shadingMask;
    private bool _tagging;
    private bool _inArtifact;
    private bool _inTagged;
    private float _pageWidth;
    private float _pageHeight;

    public DocumentResources Resources => resources;

    /// <summary>Transform from the current layout coordinates to PDF page coordinates.</summary>
    public Matrix Transform => _matrix;

    /// <summary>Converts a point of the current layout coordinates to PDF page coordinates.</summary>
    public (float X, float Y) ToPdf(float x, float y) => _matrix.Apply(x, y);

    /// <summary>
    /// Whether the document is tagged. Everything drawn then belongs either to a structure element or,
    /// failing that, to an artifact: page furniture a reader for the blind skips over.
    /// </summary>
    public bool Tagging
    {
        get => _tagging;
        set => _tagging = value;
    }

    public void BeginPage(ByteBuffer output, float pageWidth, float pageHeight) =>

        // Layout y grows downwards from the top of the page; PDF y grows upwards from the bottom.
        BeginContent(output, pageWidth, pageHeight, new Matrix(1, 0, 0, -1, 0, pageHeight));

    /// <summary>
    /// Starts a stream of its own — the inside of a mask or of one tile of a pattern — which is drawn
    /// with the transform it is given rather than the one of a page.
    /// </summary>
    public void BeginContent(ByteBuffer output, float pageWidth, float pageHeight, Matrix transform)
    {
        _out = output;
        _matrices.Clear();
        _pageWidth = pageWidth;
        _pageHeight = pageHeight;
        _shadingMask = false;
        _matrix = transform;
        ResetGraphicsState();
    }

    /// <summary>
    /// Says what a piece of drawing stands for in words. The scripts of India draw a syllable in an
    /// order of their own, so the glyphs alone would extract as the letters shuffled; this hands the
    /// reader the text as it was written.
    /// </summary>
    public void BeginActualText(ReadOnlySpan<int> codepoints)
    {
        EndText();
        _out.Ascii("/Span<</ActualText<FEFF");
        foreach (var codepoint in codepoints)
        {
            if (codepoint <= 0xFFFF)
            {
                _out.Hex16((ushort)codepoint);
            }
            else
            {
                var value = codepoint - 0x10000;
                _out.Hex16((ushort)(0xD800 + (value >> 10)));
                _out.Hex16((ushort)(0xDC00 + (value & 0x3FF)));
            }
        }

        _out.Ascii(">>>BDC\n");
    }

    public void EndActualText()
    {
        EndText();
        _out.Ascii("EMC\n");
    }

    /// <summary>Applies a graphics state the document already holds, such as a mask.</summary>
    public void BeginState(string name)
    {
        EndText();
        _out.Ascii("q /").Ascii(name).Ascii(" gs\n");
        ResetGraphicsState();
    }

    public void EndPage()
    {
        EndText();
        EndMark();
    }

    /// <summary>Opens a piece of content that belongs to a structure element.</summary>
    public void BeginTagged(string role, int mcid)
    {
        if (!_tagging)
            return;

        EndText();
        EndMark();
        _out.Byte((byte)'/').Ascii(role).Ascii("<</MCID ").Int(mcid).Ascii(">> BDC\n");
        _inTagged = true;
    }

    public void EndTagged()
    {
        if (!_tagging || !_inTagged)
            return;

        EndText();
        _out.Ascii("EMC\n");
        _inTagged = false;
    }

    /// <summary>
    /// Called before anything is drawn: content that belongs to no structure element is marked as an
    /// artifact, which is what a reader for the blind is told to skip.
    /// </summary>
    private void MarkContent()
    {
        if (!_tagging || _inTagged || _inArtifact)
            return;

        _out.Ascii("/Artifact BMC\n");
        _inArtifact = true;
    }

    private void EndMark()
    {
        if (_inArtifact)
        {
            _out.Ascii("EMC\n");
            _inArtifact = false;
        }

        if (_inTagged)
        {
            _out.Ascii("EMC\n");
            _inTagged = false;
        }
    }

    /// <summary>
    /// Forgets the cached graphics state. A "Q" restores whatever was in effect before the matching "q",
    /// which the canvas no longer knows, so every value is marked unknown and written again when it is next used.
    /// </summary>
    private void ResetGraphicsState()
    {
        _inText = false;
        _fill = _stroke = null;
        _lineWidth = -1;
        _font = null;
        _fontSize = -1;
        _charSpacing = float.NaN;
        _renderMode = -1;
        _lineCap = -1;
        _lineJoin = -1;
        _dashed = null;
    }

    // ---- Transforms ------------------------------------------------------------------------------

    public void Translate(float x, float y) => Concat(Matrix.Translation(x, y));

    public void Rotate(float degrees) => Concat(Matrix.Rotation(degrees));

    public void Scale(float x, float y) => Concat(Matrix.Scaling(x, y));

    /// <summary>Applies a transform of its own inside the one already in force.</summary>
    public void Concat(Matrix local) => _matrix = Matrix.Multiply(local, _matrix);

    public void PushTransform() => _matrices.Push(_matrix);

    public void PopTransform() => _matrix = _matrices.Pop();

    // ---- Graphics state groups -------------------------------------------------------------------

    /// <summary>Starts a group with the given opacity (0–1) applied to everything drawn until <see cref="EndGroup"/>.</summary>
    public void BeginOpacityGroup(float opacity)
    {
        EndText();
        var name = resources.GetOpacity(opacity);
        _out.Ascii("q /").Ascii(name).Ascii(" gs\n");
        ResetGraphicsState();
    }

    /// <summary>Starts a group clipped to a rectangle with rounded corners.</summary>
    public void BeginClipGroup(float x, float y, float width, float height, float radius)
    {
        EndText();
        _out.Ascii("q ");
        WriteRoundedRectanglePath(x, y, width, height, radius);
        _out.Ascii(" W n\n");
        ResetGraphicsState();
    }

    public void EndGroup()
    {
        EndText();
        _out.Ascii("Q\n");
        ResetGraphicsState();
    }

    /// <summary>
    /// Lays a colour over an area through a grey picture: the colour shows where the picture is light
    /// and not at all where it is dark. The soft edge of a shadow is drawn this way, as a browser draws
    /// one — the picture holds nothing but the shape of the softness, and the colour stays a colour.
    /// </summary>
    public void DrawShadow(Image mask, float x, float y, float width, float height, Color color)
    {
        if (width <= 0 || height <= 0)
            return;

        MarkContent();
        EndText();

        // The picture is drawn into a stream of its own, in the place on the page the colour goes, and
        // handed to the page as a graphics state.
        using var content = new ByteBuffer(256);
        var inner = new Canvas(resources);
        inner.BeginContent(content, 0, 0, _matrix);
        inner.DrawImage(mask, x, y, width, height);
        inner.EndPage();

        var corners = new[] { ToPdf(x, y), ToPdf(x + width, y), ToPdf(x, y + height), ToPdf(x + width, y + height) };
        var name = resources.GetMaskForm(
            content.ToArray(),
            corners.Min(corner => corner.X),
            corners.Min(corner => corner.Y),
            corners.Max(corner => corner.X),
            corners.Max(corner => corner.Y));

        _out.Ascii("q /").Ascii(name).Ascii(" gs\n");
        ResetGraphicsState();
        FillRectangle(x, y, width, height, color);
        EndGroup();
    }

    // ---- Shapes ----------------------------------------------------------------------------------

    public void FillRectangle(float x, float y, float width, float height, Color color)
    {
        MarkContent();
        if (width <= 0 || height <= 0)
            return;

        EndText();
        SetFill(color);
        WriteRectanglePath(x, y, width, height);
        _out.Ascii(" f\n");
    }

    public void FillRoundedRectangle(float x, float y, float width, float height, float radius, Color color)
    {
        MarkContent();
        if (width <= 0 || height <= 0)
            return;

        if (radius <= 0)
        {
            FillRectangle(x, y, width, height, color);
            return;
        }

        EndText();
        SetFill(color);
        WriteRoundedRectanglePath(x, y, width, height, radius);
        _out.Ascii(" f\n");
    }

    public void StrokeRoundedRectangle(float x, float y, float width, float height, float radius, Color color, float lineWidth)
    {
        MarkContent();
        if (width <= 0 || height <= 0)
            return;

        EndText();
        SetStroke(color);
        SetLineWidth(lineWidth * _matrix.Scale);
        WriteRoundedRectanglePath(x, y, width, height, radius);
        _out.Ascii(" S\n");
    }

    /// <summary>Fills a rectangle with a shading pattern (see <see cref="DocumentResources.GetShading"/>).</summary>
    public void FillRectangleWithShading(float x, float y, float width, float height, Shading shading)
    {
        MarkContent();
        if (width <= 0 || height <= 0)
            return;

        EndText();

        // The pattern is placed over this rectangle, in page coordinates.
        var name = resources.GetShading(shading with { Matrix = Matrix.Multiply(Matrix.Translation(x, y), _matrix) });
        _out.Ascii("/Pattern cs /").Ascii(name).Ascii(" scn\n");
        _fill = null;
        WriteRectanglePath(x, y, width, height);
        _out.Ascii(" f\n");
    }

    private void WriteRectanglePath(float x, float y, float width, float height)
    {
        if (_matrix.IsAxisAligned)
        {
            var (left, bottom) = _matrix.Apply(x, y + height);
            _out.Space().Real(left).Space().Real(bottom).Space()
                .Real(width * _matrix.A).Space().Real(height * -_matrix.D).Ascii(" re");
            return;
        }

        MoveTo(x, y);
        LineTo(x + width, y);
        LineTo(x + width, y + height);
        LineTo(x, y + height);
        _out.Ascii(" h");
    }

    private void WriteRoundedRectanglePath(float x, float y, float width, float height, float radius)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        if (radius <= 0)
        {
            WriteRectanglePath(x, y, width, height);
            return;
        }

        // Circular arcs approximated with cubic Béziers.
        const float k = 0.5523f;
        var c = radius * k;
        float right = x + width, bottom = y + height;

        MoveTo(x + radius, y);
        LineTo(right - radius, y);
        CurveTo(right - radius + c, y, right, y + radius - c, right, y + radius);
        LineTo(right, bottom - radius);
        CurveTo(right, bottom - radius + c, right - radius + c, bottom, right - radius, bottom);
        LineTo(x + radius, bottom);
        CurveTo(x + radius - c, bottom, x, bottom - radius + c, x, bottom - radius);
        LineTo(x, y + radius);
        CurveTo(x, y + radius - c, x + radius - c, y, x + radius, y);
        _out.Ascii(" h");
    }

    // ---- Path primitives (layout coordinates) ----------------------------------------------------

    public void MoveTo(float x, float y) => Point(x, y).Ascii(" m");

    public void LineTo(float x, float y) => Point(x, y).Ascii(" l");

    public void CurveTo(float x1, float y1, float x2, float y2, float x3, float y3)
    {
        Point(x1, y1);
        Point(x2, y2);
        Point(x3, y3);
        _out.Ascii(" c");
    }

    public void ClosePath() => _out.Ascii(" h");

    /// <summary>
    /// Starts a path. Colors are set before the path is built, because a path may not be interrupted
    /// by other operators before it is painted.
    /// </summary>
    public void BeginPath(Color? fill, Color? stroke, float strokeWidth)
    {
        MarkContent();
        EndText();
        if (fill is { } fillColor)
            SetFill(fillColor);

        if (stroke is { } strokeColor)
        {
            SetStroke(strokeColor);
            SetLineWidth(strokeWidth * _matrix.Scale);
        }
    }

    public void EndPath(bool fill, bool stroke, bool evenOdd = false)
    {
        var suffix = evenOdd ? "*" : "";
        _out.Ascii(fill && stroke ? $" B{suffix}\n" : fill ? $" f{suffix}\n" : " S\n");

        if (!_shadingMask)
            return;

        // The mask applies to this fill alone.
        _shadingMask = false;
        _out.Ascii("Q\n");
        ResetGraphicsState();
    }

    /// <summary>
    /// Starts a path filled with a shading pattern. <paramref name="local"/> places the shading in the
    /// coordinate space its coordinates are given in.
    /// </summary>
    public void BeginShadingPath(Shading shading, Matrix local)
    {
        MarkContent();
        EndText();
        var placed = shading with { Matrix = Matrix.Multiply(local, _matrix) };

        // Where the gradient is partly transparent, a mask carries that transparency beside the colours.
        if (placed.HasAlpha)
        {
            _out.Ascii("q /").Ascii(resources.GetSoftMask(placed, _pageWidth, _pageHeight)).Ascii(" gs\n");
            _shadingMask = true;
            ResetGraphicsState();
        }

        var name = resources.GetShading(placed);
        _out.Ascii("/Pattern cs /").Ascii(name).Ascii(" scn\n");
        _fill = null;
    }

    /// <summary>Fills the path that follows with a pattern the document already holds.</summary>
    public void BeginPatternPath(string name)
    {
        MarkContent();
        EndText();
        _out.Ascii("/Pattern cs /").Ascii(name).Ascii(" scn\n");
        _fill = null;
    }

    /// <summary>Clips everything drawn until <see cref="EndGroup"/> to the path built so far.</summary>
    public void ClipPath(bool evenOdd)
    {
        _out.Ascii(evenOdd ? " W* n\n" : " W n\n");
        ResetGraphicsState();
    }

    /// <summary>Starts a group; pair it with <see cref="EndGroup"/>. Needed before clipping.</summary>
    public void BeginGroup()
    {
        EndText();
        _out.Ascii("q ");
        ResetGraphicsState();
    }

    /// <summary>Line caps and joins: 0 butt/miter, 1 round, 2 square/bevel.</summary>
    public void SetLineStyle(int cap, int join, float[]? dashes, float dashPhase)
    {
        if (cap != _lineCap)
        {
            _lineCap = cap;
            _out.Int(cap).Ascii(" J\n");
        }

        if (join != _lineJoin)
        {
            _lineJoin = join;
            _out.Int(join).Ascii(" j\n");
        }

        // A solid line needs no dash operator unless a dash pattern might still be in effect.
        if (dashes == null && _dashed == false)
            return;

        _dashed = dashes != null;
        _out.Byte((byte)'[');
        if (dashes != null)
        {
            for (var i = 0; i < dashes.Length; i++)
            {
                if (i > 0)
                    _out.Space();

                _out.Real(dashes[i] * _matrix.Scale);
            }
        }

        _out.Ascii("] ").Real(dashPhase * _matrix.Scale).Ascii(" d\n");
    }

    private ByteBuffer Point(float x, float y)
    {
        var (px, py) = _matrix.Apply(x, y);
        return _out.Space().Real(px).Space().Real(py);
    }

    // ---- Images and text -------------------------------------------------------------------------

    public void DrawImage(Image image, float x, float y, float width, float height)
    {
        MarkContent();
        EndText();
        var usage = resources.GetImage(image);

        // The unit square of an image maps to its box; its y axis points up, layout's points down.
        var placement = Matrix.Multiply(new Matrix(width, 0, 0, -height, x, y + height), _matrix);
        _out.Ascii("q ");
        WriteMatrix(placement);
        _out.Ascii(" cm /").Ascii(usage.Name).Ascii(" Do Q\n");
        ResetGraphicsState();
    }

    /// <summary>
    /// Draws glyphs of a single style starting at (<paramref name="x"/>, <paramref name="baseline"/>).
    /// <paramref name="kerning"/> holds the adjustment after each glyph in font units.
    /// </summary>
    public void DrawGlyphs(ResolvedTextStyle style, float x, float baseline, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<int> codepoints, ReadOnlySpan<short> kerning)
    {
        if (glyphs.IsEmpty)
            return;

        var usage = resources.GetFont(style.Font.Font);
        for (var i = 0; i < glyphs.Length; i++)
            usage.Use(glyphs[i], codepoints[i]);

        ShowGlyphs(style, style.Color, x, baseline, glyphs, kerning, style.Font.FakeBold ? 2 : 0);
    }

    /// <summary>
    /// Draws one glyph in colour when the font has a coloured version of it — layers of outlines, a
    /// picture or a drawing, which is how the emoji fonts of the three platforms store them. Returns
    /// false when it has none, so that the caller draws it as ordinary text.
    /// </summary>
    public bool DrawColorGlyph(ResolvedTextStyle style, float x, float baseline, ushort glyph, int codepoint)
    {
        var font = style.Font.Font;
        var colors = font.Colors;
        var kind = colors.KindOf(glyph);
        if (kind == ColorGlyphKind.None)
            return false;

        // The glyph itself is drawn invisibly, so the text can still be selected and searched.
        var usage = resources.GetFont(font);
        usage.Use(glyph, codepoint);

        switch (kind)
        {
            case ColorGlyphKind.Layers:
            {
                ShowGlyphs(style, style.Color, x, baseline, [glyph], [], 3);
                foreach (var (layer, color) in colors.Layers(glyph))
                {
                    usage.Use(layer, 0);
                    ShowGlyphs(style, color ?? style.Color, x, baseline, [layer], [], 0);
                }

                return true;
            }

            case ColorGlyphKind.Bitmap when colors.Bitmap(glyph) is { } bitmap:
            {
                ShowGlyphs(style, style.Color, x, baseline, [glyph], [], 3);
                DrawImage(
                    bitmap.Image,
                    x + bitmap.Left * style.Scale,
                    baseline + bitmap.Top * style.Scale,
                    bitmap.Width * style.Scale,
                    bitmap.Height * style.Scale);
                return true;
            }

            case ColorGlyphKind.Drawing when colors.Drawing(glyph) is { } drawing:
            {
                ShowGlyphs(style, style.Color, x, baseline, [glyph], [], 3);

                // The drawing is in font units, with the origin of the glyph at (x, baseline).
                new Svg.SvgRenderer(this).DrawNode(
                    drawing.Root,
                    Matrix.Multiply(Matrix.Scaling(style.Scale, style.Scale), Matrix.Translation(x, baseline)));
                return true;
            }

            default:
                return false;
        }
    }

    private void ShowGlyphs(
        ResolvedTextStyle style,
        Color color,
        float x,
        float baseline,
        ReadOnlySpan<ushort> glyphs,
        ReadOnlySpan<short> kerning,
        int renderMode)
    {
        MarkContent();
        var font = resources.GetFont(style.Font.Font);
        if (!_inText)
        {
            _out.Ascii("BT\n");
            _inText = true;
        }

        if (!ReferenceEquals(font, _font) || _fontSize != style.Size)
        {
            _font = font;
            _fontSize = style.Size;
            _out.Byte((byte)'/').Ascii(font.Name).Space().Real(style.Size).Ascii(" Tf\n");
        }

        if (_charSpacing != style.LetterSpacing)
        {
            _charSpacing = style.LetterSpacing;
            _out.Real(style.LetterSpacing).Ascii(" Tc\n");
        }

        SetFill(color);

        if (renderMode == 2)
        {
            SetStroke(color);
            SetLineWidth(style.Size * 0.03f * _matrix.Scale);
        }

        if (_renderMode != renderMode)
        {
            _renderMode = renderMode;
            _out.Int(renderMode).Ascii(" Tr\n");
        }

        // Text space has y pointing up; flip it into layout coordinates, place it, then map to the page.
        var placement = new Matrix(1, 0, 0, -1, x, baseline);
        if (style.Font.FakeItalic)
            placement = Matrix.Multiply(new Matrix(1, 0, -0.2f, 1, 0, 0), placement);

        WriteMatrix(Matrix.Multiply(placement, _matrix));
        _out.Ascii(" Tm ");

        // Adjustments after the last glyph don't matter for this run.
        var kerned = kerning.Length >= glyphs.Length && kerning[..^1].ContainsAnyExcept((short)0);
        if (!kerned)
        {
            _out.Byte((byte)'<');
            foreach (var glyph in glyphs)
                _out.Hex16(glyph);
            _out.Ascii("> Tj\n");
            return;
        }

        // TJ numbers are in thousandths of an em; positive values move the next glyph left.
        var toThousandths = -1000.0 / style.Font.Font.UnitsPerEm;
        _out.Ascii("[<");
        for (var i = 0; i < glyphs.Length; i++)
        {
            _out.Hex16(glyphs[i]);
            if (i < glyphs.Length - 1 && kerning[i] != 0)
                _out.Byte((byte)'>').Real(kerning[i] * toThousandths).Byte((byte)'<');
        }

        _out.Ascii(">] TJ\n");
    }

    private void WriteMatrix(Matrix m) =>
        _out.Real(m.A).Space().Real(m.B).Space().Real(m.C).Space().Real(m.D).Space().Real(m.E).Space().Real(m.F);

    private void EndText()
    {
        if (!_inText)
            return;
        _out.Ascii("ET\n");
        _inText = false;
    }

    private void SetFill(Color color)
    {
        if (_fill == color)
            return;
        _fill = color;
        WriteColor(color).Ascii(" rg\n");
    }

    private void SetStroke(Color color)
    {
        if (_stroke == color)
            return;
        _stroke = color;
        WriteColor(color).Ascii(" RG\n");
    }

    private void SetLineWidth(float width)
    {
        if (_lineWidth == width)
            return;
        _lineWidth = width;
        _out.Real(width).Ascii(" w\n");
    }

    private ByteBuffer WriteColor(Color color) =>
        _out.Real(color.R / 255.0).Space().Real(color.G / 255.0).Space().Real(color.B / 255.0);
}
