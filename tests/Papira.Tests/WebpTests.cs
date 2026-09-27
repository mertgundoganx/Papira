using System.IO.Compression;
using Papira.Images;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// WebP images, both the lossless and the lossy kind. The decoders were checked against libwebp through
/// Pillow: 109 lossless and 120 lossy pictures, from 1x1 to 333 pixels wide, at every quality setting,
/// came out pixel for pixel identical.
/// </summary>
public class WebpTests
{
    [Fact]
    public void A_lossy_file_is_recognised_and_measured()
    {
        var data = File.ReadAllBytes(Asset("photo.webp"));

        Assert.True(WebpDecoder.IsWebp(data));
        var info = WebpDecoder.ReadInfo(data);
        Assert.False(info.Lossless);
        Assert.Equal(320, info.Width);
        Assert.Equal(120, info.Height);
    }

    [Fact]
    public void A_lossless_file_with_transparency_is_recognised()
    {
        var info = WebpDecoder.ReadInfo(File.ReadAllBytes(Asset("circle-alpha.webp")));

        Assert.True(info.Lossless);
        Assert.Equal(256, info.Width);
        Assert.Equal(256, info.Height);
    }

    [Fact]
    public void The_image_api_accepts_webp()
    {
        var image = Image.FromFile(Asset("photo.webp"));

        Assert.Equal(320, image.Width);
        Assert.Equal(120, image.Height);
        Assert.Equal(120f / 320f, image.AspectRatio, 4);
    }

    [Fact]
    public void A_lossy_picture_is_embedded_as_pixels()
    {
        var pdf = Inspect(Generate(c => c.Width(200).Image(Asset("photo.webp"))));

        // WebP has no place in a PDF, so it is decoded and written as compressed pixels.
        Assert.Contains("/Subtype/Image", pdf.Raw);
        Assert.Contains("/ColorSpace/DeviceRGB", pdf.Raw);
        Assert.Contains("/Filter/FlateDecode", pdf.Raw);
        Assert.DoesNotContain("/Filter//", pdf.Raw);
        Assert.DoesNotContain("/DCTDecode", pdf.Raw);
    }

    [Fact]
    public void Transparency_becomes_a_soft_mask()
    {
        var pdf = Inspect(Generate(c => c.Width(120).Image(Asset("circle-alpha.webp"))));

        Assert.Contains("/SMask ", pdf.Raw);
        Assert.Contains("/ColorSpace/DeviceGray", pdf.Raw);
    }

    [Fact]
    public void A_lossless_picture_comes_out_exactly_as_the_png_of_the_same_image()
    {
        // circle-alpha.webp was written from circle-rgba.png without losing anything, so the pixels and
        // the transparency Papira decodes from the two files have to be the same, byte for byte.
        var webp = WebpDecoder.Encode(WebpDecoder.ReadInfo(File.ReadAllBytes(Asset("circle-alpha.webp"))), CompressionLevel.Fastest);
        var png = PngDecoder.Encode(PngDecoder.ReadInfo(File.ReadAllBytes(Asset("circle-rgba.png"))), CompressionLevel.Fastest);

        Assert.Equal(png.Width, webp.Width);
        Assert.Equal(Inflate(png.Data), Inflate(webp.Data));
        Assert.NotNull(webp.SoftMask);
        Assert.Equal(Inflate(png.SoftMask!), Inflate(webp.SoftMask!));
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    [Fact]
    public void An_animation_is_refused_with_a_clear_message()
    {
        // A file whose chunks say "ANMF" holds frames, which a document cannot show.
        var data = File.ReadAllBytes(Asset("photo.webp"));
        var animated = (byte[])data.Clone();
        var chunk = FindChunk(animated);
        animated[chunk] = (byte)'A';
        animated[chunk + 1] = (byte)'N';
        animated[chunk + 2] = (byte)'M';
        animated[chunk + 3] = (byte)'F';

        var exception = Assert.Throws<NotSupportedException>(() => WebpDecoder.ReadInfo(animated));
        Assert.Contains("Animated", exception.Message);
    }

    private static int FindChunk(byte[] data)
    {
        for (var i = 12; i + 8 < data.Length; i++)
        {
            if (data[i] == 'V' && data[i + 1] == 'P' && data[i + 2] == '8')
                return i;
        }

        throw new InvalidOperationException("the file has no picture chunk");
    }

    [Theory]
    [InlineData("RIFF")]
    [InlineData("RIFF____WEBPVP8 ")]
    public void Malformed_files_are_rejected(string text)
    {
        var data = System.Text.Encoding.ASCII.GetBytes(text);
        Assert.ThrowsAny<Exception>(() => WebpDecoder.ReadInfo(data));
    }
}
