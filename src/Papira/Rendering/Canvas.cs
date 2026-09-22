using Papira.Elements;
using Papira.Pdf;

namespace Papira.Rendering;

/// <summary>
/// Writes PDF content-stream operators for one page at a time. Layout code uses a top-left origin;
/// conversion to PDF's bottom-left origin happens here. Redundant graphics-state changes are skipped.
/// </summary>
internal sealed class Canvas(DocumentResources resources)
{
    private ByteBuffer _out = null!;
    private float _pageHeight;

    private bool _inText;
    private Color? _fill;
    private Color? _stroke;
    private float _lineWidth;
    private FontUsage? _font;
    private float _fontSize;
    private float _charSpacing;
    private int _renderMode;

    public float OffsetX { get; private set; }
    public float OffsetY { get; private set; }

    public DocumentResources Resources => resources;

    /// <summary>Converts a point of the current (translated) layout coordinates to PDF page coordinates.</summary>
    public (float X, float Y) ToPdf(float x, float y) => (PdfX(x), PdfY(y));

    public void BeginPage(ByteBuffer output, float pageHeight)
    {
        _out = output;
        _pageHeight = pageHeight;
        OffsetX = OffsetY = 0;
        _inText = false;
        _fill = _stroke = null;
        _lineWidth = -1;
        _font = null;
        _fontSize = -1;
        _charSpacing = 0;
        _renderMode = 0;
    }

    public void EndPage() => EndText();

    public void Translate(float x, float y)
    {
        OffsetX += x;
        OffsetY += y;
    }

    private float PdfX(float x) => OffsetX + x;
    private float PdfY(float y) => _pageHeight - (OffsetY + y);

    public void FillRectangle(float x, float y, float width, float height, Color color)
    {
        if (width <= 0 || height <= 0)
            return;

        EndText();
        SetFill(color);
        _out.Real(PdfX(x)).Space().Real(PdfY(y + height)).Space().Real(width).Space().Real(height).Ascii(" re f\n");
    }

    public void DrawImage(Image image, float x, float y, float width, float height)
    {
        EndText();
        var usage = resources.GetImage(image);
        _out.Ascii("q ").Real(width).Ascii(" 0 0 ").Real(height).Space()
            .Real(PdfX(x)).Space().Real(PdfY(y + height)).Ascii(" cm /").Ascii(usage.Name).Ascii(" Do Q\n");
    }

    /// <summary>
    /// Draws glyphs of a single style starting at (<paramref name="x"/>, <paramref name="baseline"/>).
    /// <paramref name="kerning"/> holds the adjustment after each glyph in font units.
    /// </summary>
    public void DrawGlyphs(ResolvedTextStyle style, float x, float baseline, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<int> codepoints, ReadOnlySpan<short> kerning)
    {
        if (glyphs.IsEmpty)
            return;

        var font = resources.GetFont(style.Font.Font);
        for (var i = 0; i < glyphs.Length; i++)
            font.Use(glyphs[i], codepoints[i]);

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

        SetFill(style.Color);

        var renderMode = style.Font.FakeBold ? 2 : 0;
        if (renderMode == 2)
        {
            SetStroke(style.Color);
            SetLineWidth(style.Size * 0.03f);
        }

        if (_renderMode != renderMode)
        {
            _renderMode = renderMode;
            _out.Int(renderMode).Ascii(" Tr\n");
        }

        _out.Ascii(style.Font.FakeItalic ? "1 0 0.2 1 " : "1 0 0 1 ")
            .Real(PdfX(x)).Space().Real(PdfY(baseline)).Ascii(" Tm ");

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
