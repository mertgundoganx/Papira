using System.Globalization;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using Papira.Elements;
using Papira.Fonts;
using Papira.Infrastructure;
using Papira.Pdf;

namespace Papira.Rendering;

/// <summary>
/// Turns a composed document into PDF bytes:
/// 1. layout (sequential by nature: each page depends on the previous one) writes page content streams;
/// 2. CPU-heavy work — content compression, font subsetting, image encoding — runs in parallel;
/// 3. objects are written sequentially to the output stream.
/// </summary>
internal static class DocumentRenderer
{
    private sealed class PageOutput(PageSize size, ByteBuffer content, LinkArea[] links) : IDisposable
    {
        public PageSize Size { get; } = size;
        public ByteBuffer Content { get; } = content;
        public LinkArea[] Links { get; } = links;
        public byte[]? Compressed { get; set; }

        public void Dispose() => Content.Dispose();
    }

    private sealed class FontOutput(FontUsage usage)
    {
        public FontUsage Usage { get; } = usage;
        public ushort[] Glyphs { get; set; } = [];
        public byte[] Program { get; set; } = [];
        public int ProgramLength { get; set; }
        public byte[] ToUnicode { get; set; } = [];
        public string BaseFont { get; set; } = "";
    }

    public static void Render(DocumentDescriptor document, DocumentMetadata metadata, DocumentSettings settings, Stream output)
    {
        var pages = new List<PageOutput>();
        try
        {
            var context = Layout(document, settings, pages, totalPages: 0);

            if (context.TotalPagesRequested)
            {
                // Second pass, now that the page count is known.
                var total = pages.Count;
                foreach (var page in pages)
                    page.Dispose();
                pages.Clear();
                ResetAll(document);
                context = Layout(document, settings, pages, total);
            }

            Emit(pages, context, metadata, settings, output);
        }
        finally
        {
            foreach (var page in pages)
                page.Dispose();
        }
    }

    private static void ResetAll(DocumentDescriptor document)
    {
        foreach (var page in document.Pages)
        {
            page.BackgroundSlot.Reset();
            page.ForegroundSlot.Reset();
            page.HeaderSlot.Reset();
            page.ContentSlot.Reset();
            page.FooterSlot.Reset();
        }
    }

    private static LayoutContext Layout(DocumentDescriptor document, DocumentSettings settings, List<PageOutput> pages, int totalPages)
    {
        var canvas = new Canvas(new DocumentResources());
        var context = new LayoutContext(canvas) { TotalPages = totalPages };
        var documentStyle = document.Style.InheritFrom(TextStyle.BuiltIn);

        foreach (var page in document.Pages)
        {
            var pageStyle = page.Style.InheritFrom(documentStyle);
            var (width, height) = (page.PageSize.Width, page.PageSize.Height);
            var contentWidth = width - page.LeftMargin - page.RightMargin;
            var contentHeight = height - page.TopMargin - page.BottomMargin;
            if (contentWidth <= 0 || contentHeight <= 0)
                throw new DocumentLayoutException("Page margins leave no room for content.");

            while (true)
            {
                if (pages.Count >= settings.MaxPages)
                    throw new DocumentLayoutException($"The document exceeded {settings.MaxPages} pages. This usually means content keeps being pushed to the next page; check ShowEntire/EnsureSpace usage or raise DocumentSettings.MaxPages.");

                context.PageNumber = pages.Count + 1;
                context.DefaultStyle = pageStyle;

                page.HeaderSlot.Reset();
                page.FooterSlot.Reset();
                page.BackgroundSlot.Reset();
                page.ForegroundSlot.Reset();

                var header = page.HeaderSlot.Measure(new Size(contentWidth, contentHeight), context);
                if (header.Kind is SpacePlanKind.Wrap or SpacePlanKind.Partial)
                    throw new DocumentLayoutException("The page header does not fit on the page.");

                var footer = page.FooterSlot.Measure(new Size(contentWidth, contentHeight - header.Height), context);
                if (footer.Kind is SpacePlanKind.Wrap or SpacePlanKind.Partial)
                    throw new DocumentLayoutException("The page footer does not fit on the page.");

                var body = new Size(contentWidth, contentHeight - header.Height - footer.Height);
                context.BodyHeight = body.Height;
                var content = page.ContentSlot.Measure(body, context);
                if (content.IsWrap)
                {
                    // Nothing fits on an empty page. Ignore keep-together rules before giving up.
                    context.RelaxKeepTogether = true;
                    content = page.ContentSlot.Measure(body, context);
                }

                if (content.IsWrap)
                    throw new DocumentLayoutException(string.Create(CultureInfo.InvariantCulture,
                        $"Content does not fit on an empty page (available area {body.Width:0.#} x {body.Height:0.#} pt). ") +
                        "An element that cannot be split (image, ShowEntire, fixed Height, table row with such content) is larger than the page.");

                var buffer = new ByteBuffer(16 * 1024);
                canvas.BeginPage(buffer, height);
                context.PageLinks.Clear();

                if (page.BackgroundColor is { } color)
                    canvas.FillRectangle(0, 0, width, height, color);

                DrawLayer(page.BackgroundSlot, 0, 0, new Size(width, height), context);
                DrawAt(page.HeaderSlot, page.LeftMargin, page.TopMargin, new Size(contentWidth, header.Height), context);
                DrawAt(page.ContentSlot, page.LeftMargin, page.TopMargin + header.Height, body, context);
                DrawAt(page.FooterSlot, page.LeftMargin, page.TopMargin + contentHeight - footer.Height, new Size(contentWidth, footer.Height), context);
                DrawLayer(page.ForegroundSlot, 0, 0, new Size(width, height), context);

                canvas.EndPage();
                context.RelaxKeepTogether = false;
                pages.Add(new PageOutput(page.PageSize, buffer, [.. context.PageLinks]));

                if (content.Kind != SpacePlanKind.Partial)
                    break;

                // Nothing left after a trailing page break: don't emit an empty page.
                if (page.ContentSlot.Measure(body, context).IsEmpty)
                    break;
            }
        }

        return context;
    }

    private static void DrawLayer(Slot slot, float x, float y, Size size, LayoutContext context)
    {
        if (slot.Measure(size, context).HasContent)
            DrawAt(slot, x, y, size, context);
    }

    private static void DrawAt(Element element, float x, float y, Size size, LayoutContext context)
    {
        context.Canvas.Translate(x, y);
        element.Draw(size, context);
        context.Canvas.Translate(-x, -y);
    }

    private static void Emit(List<PageOutput> pages, LayoutContext context, DocumentMetadata metadata, DocumentSettings settings, Stream output)
    {
        var resources = context.Canvas.Resources;
        CompressionLevel? level = settings.Compression switch
        {
            PdfCompression.None => null,
            PdfCompression.Fastest => CompressionLevel.Fastest,
            PdfCompression.Smallest => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        var fonts = resources.Fonts.Select(f => new FontOutput(f)).ToList();
        var images = resources.Images.ToList();

        // ---- Parallel stage ----
        var work = new List<Action>(pages.Count + fonts.Count + images.Count);
        foreach (var font in fonts)
            work.Add(() => PrepareFont(font, level));
        foreach (var image in images)
            work.Add(() => _ = image.Image.Encoded);
        if (level is { } compression)
        {
            foreach (var page in pages)
                work.Add(() => page.Compressed = PdfWriter.Deflate(page.Content.Span, compression));
        }

        if (settings.MaxDegreeOfParallelism > 1 && work.Count > 1)
        {
            try
            {
                Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = settings.MaxDegreeOfParallelism }, action => action());
            }
            catch (AggregateException e) when (e.InnerExceptions.Count > 0)
            {
                // Surface the same exception type as the sequential path would.
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
        }
        else
        {
            work.ForEach(action => action());
        }

        // ---- Sequential write ----
        using var writer = new PdfWriter(output);
        var catalogId = writer.ReserveObject();
        var pagesId = writer.ReserveObject();
        var resourcesId = writer.ReserveObject();
        var infoId = writer.ReserveObject();
        var o = writer.Out;

        // Page ids are reserved up front so links can point to later pages.
        var pageIds = new int[pages.Count];
        for (var i = 0; i < pages.Count; i++)
            pageIds[i] = writer.ReserveObject();

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var contentId = writer.ReserveObject();

            // Links to unknown sections are dropped.
            var links = page.Links.Where(link => link.Uri != null || context.Sections.ContainsKey(link.Section!)).ToArray();
            var linkIds = links.Select(_ => writer.ReserveObject()).ToArray();

            writer.BeginObject(pageIds[i]);
            o.Ascii("<</Type/Page/Parent ").Int(pagesId).Ascii(" 0 R/MediaBox[0 0 ")
                .Real(page.Size.Width).Space().Real(page.Size.Height)
                .Ascii("]/Resources ").Int(resourcesId).Ascii(" 0 R/Contents ").Int(contentId).Ascii(" 0 R");
            if (linkIds.Length > 0)
            {
                o.Ascii("/Annots[");
                foreach (var id in linkIds)
                    o.Int(id).Ascii(" 0 R ");
                o.Ascii("]");
            }

            o.Ascii(">>");
            writer.EndObject();

            if (page.Compressed != null)
                writer.WriteStream(contentId, page.Compressed, null, flateEncoded: true);
            else
                writer.WriteStream(contentId, page.Content.Span, null, flateEncoded: false);

            page.Compressed = null;

            for (var l = 0; l < links.Length; l++)
                WriteLink(writer, linkIds[l], links[l], context.Sections, pageIds);
        }

        var fontIds = new List<(string Name, int Id)>();
        foreach (var font in fonts)
            fontIds.Add((font.Usage.Name, WriteFont(writer, font, level != null)));

        var imageIds = new List<(string Name, int Id)>();
        foreach (var image in images)
            imageIds.Add((image.Name, WriteImage(writer, image.Image.Encoded)));

        writer.BeginObject(resourcesId);
        o.Ascii("<<");
        if (fontIds.Count > 0)
        {
            o.Ascii("/Font<<");
            foreach (var (name, id) in fontIds)
                o.Byte((byte)'/').Ascii(name).Space().Int(id).Ascii(" 0 R");
            o.Ascii(">>");
        }

        if (imageIds.Count > 0)
        {
            o.Ascii("/XObject<<");
            foreach (var (name, id) in imageIds)
                o.Byte((byte)'/').Ascii(name).Space().Int(id).Ascii(" 0 R");
            o.Ascii(">>");
        }

        o.Ascii(">>");
        writer.EndObject();

        writer.BeginObject(pagesId);
        o.Ascii("<</Type/Pages/Count ").Int(pages.Count).Ascii("/Kids[");
        foreach (var id in pageIds)
            o.Int(id).Ascii(" 0 R ");
        o.Ascii("]>>");
        writer.EndObject();

        var outlinesId = WriteOutline(writer, context.Bookmarks, pageIds);

        writer.BeginObject(catalogId);
        o.Ascii("<</Type/Catalog/Pages ").Int(pagesId).Ascii(" 0 R");
        if (outlinesId != 0)
            o.Ascii("/Outlines ").Int(outlinesId).Ascii(" 0 R/PageMode/UseOutlines");
        o.Ascii(">>");
        writer.EndObject();

        WriteInfo(writer, infoId, metadata);

        writer.WriteTrailer(catalogId, infoId);
    }

    private static void WriteDestination(ByteBuffer o, Destination destination, int[] pageIds) =>
        o.Ascii("[").Int(pageIds[destination.PageIndex]).Ascii(" 0 R/XYZ ")
            .Real(destination.X).Space().Real(destination.Y).Ascii(" null]");

    private static void WriteLink(PdfWriter writer, int id, LinkArea link, Dictionary<string, Destination> sections, int[] pageIds)
    {
        var o = writer.Out;
        writer.BeginObject(id);
        o.Ascii("<</Type/Annot/Subtype/Link/F 4/Border[0 0 0]/Rect[")
            .Real(link.Left).Space().Real(link.Bottom).Space().Real(link.Right).Space().Real(link.Top).Ascii("]");

        if (link.Uri != null)
        {
            o.Ascii("/A<</S/URI/URI").AsciiString(link.Uri).Ascii(">>");
        }
        else
        {
            o.Ascii("/Dest");
            WriteDestination(o, sections[link.Section!], pageIds);
        }

        o.Ascii(">>");
        writer.EndObject();
    }

    private sealed class OutlineNode(Bookmark? bookmark)
    {
        public Bookmark? Bookmark { get; } = bookmark;
        public List<OutlineNode> Children { get; } = [];
        public int Id { get; set; }
        public int Descendants => Children.Sum(child => 1 + child.Descendants);
    }

    /// <summary>Writes the document outline (bookmarks panel). Returns 0 when there are no bookmarks.</summary>
    private static int WriteOutline(PdfWriter writer, List<Bookmark> bookmarks, int[] pageIds)
    {
        if (bookmarks.Count == 0)
            return 0;

        // Build the tree. A level may be at most one deeper than the previous entry.
        var root = new OutlineNode(null);
        var path = new List<OutlineNode> { root };
        foreach (var bookmark in bookmarks)
        {
            var level = Math.Min(bookmark.Level, path.Count - 1);
            path.RemoveRange(level + 1, path.Count - level - 1);
            var node = new OutlineNode(bookmark);
            path[level].Children.Add(node);
            path.Add(node);
        }

        void AssignIds(OutlineNode node)
        {
            node.Id = writer.ReserveObject();
            foreach (var child in node.Children)
                AssignIds(child);
        }

        AssignIds(root);

        var o = writer.Out;
        void Write(OutlineNode node, OutlineNode? parent, OutlineNode? previous, OutlineNode? next)
        {
            writer.BeginObject(node.Id);
            o.Ascii("<<");
            if (parent == null)
            {
                o.Ascii("/Type/Outlines");
            }
            else
            {
                o.Ascii("/Title").TextString(node.Bookmark!.Value.Title).Ascii("/Parent ").Int(parent.Id).Ascii(" 0 R");
                if (previous != null) o.Ascii("/Prev ").Int(previous.Id).Ascii(" 0 R");
                if (next != null) o.Ascii("/Next ").Int(next.Id).Ascii(" 0 R");
                o.Ascii("/Dest");
                WriteDestination(o, node.Bookmark!.Value.Destination, pageIds);
            }

            if (node.Children.Count > 0)
            {
                // A positive count shows the entry expanded.
                o.Ascii("/First ").Int(node.Children[0].Id).Ascii(" 0 R/Last ").Int(node.Children[^1].Id)
                    .Ascii(" 0 R/Count ").Int(node.Descendants);
            }

            o.Ascii(">>");
            writer.EndObject();

            for (var i = 0; i < node.Children.Count; i++)
            {
                Write(node.Children[i], node,
                    i > 0 ? node.Children[i - 1] : null,
                    i + 1 < node.Children.Count ? node.Children[i + 1] : null);
            }
        }

        Write(root, null, null, null);
        return root.Id;
    }

    private static void PrepareFont(FontOutput output, CompressionLevel? level)
    {
        var usage = output.Usage;
        var map = usage.GlyphToUnicode;
        var glyphs = new List<ushort> { 0 };
        for (var g = 1; g < map.Length; g++)
        {
            if (map[g] != 0)
                glyphs.Add((ushort)g);
        }

        output.Glyphs = glyphs.ToArray();

        var program = FontSubsetter.Subset(usage.Font, glyphs);
        output.ProgramLength = program.Length;
        output.Program = level is { } l ? PdfWriter.Deflate(program, l) : program;

        var cmap = BuildToUnicode(output.Glyphs, map);
        output.ToUnicode = level is { } l2 ? PdfWriter.Deflate(cmap, l2) : cmap;

        // Subset prefix: six capitals derived from the font name and glyph set (stable across runs).
        var hash = 14695981039346656037UL; // FNV-1a
        foreach (var c in usage.Font.PostScriptName)
            hash = (hash ^ c) * 1099511628211UL;
        foreach (var g in output.Glyphs)
            hash = (hash ^ g) * 1099511628211UL;

        var tag = new char[6];
        for (var i = 0; i < 6; i++)
        {
            tag[i] = (char)('A' + (int)(hash % 26));
            hash /= 26;
        }

        output.BaseFont = new string(tag) + "+" + usage.Font.PostScriptName;
    }

    private static byte[] BuildToUnicode(ushort[] glyphs, int[] map)
    {
        using var b = new ByteBuffer(256 + glyphs.Length * 20);
        b.Ascii("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n")
            .Ascii("/CIDSystemInfo<</Registry(Adobe)/Ordering(UCS)/Supplement 0>>def\n")
            .Ascii("/CMapName/Adobe-Identity-UCS def\n/CMapType 2 def\n")
            .Ascii("1 begincodespacerange\n<0000><FFFF>\nendcodespacerange\n");

        var mapped = glyphs.Where(g => map[g] > 0).ToArray();
        Span<char> pair = stackalloc char[2];
        for (var start = 0; start < mapped.Length; start += 100)
        {
            var count = Math.Min(100, mapped.Length - start);
            b.Int(count).Ascii(" beginbfchar\n");
            for (var i = start; i < start + count; i++)
            {
                var glyph = mapped[i];
                b.Byte((byte)'<').Hex16(glyph).Ascii("><");
                var codepoint = map[glyph];
                if (codepoint > 0xFFFF)
                {
                    new System.Text.Rune(codepoint).EncodeToUtf16(pair);
                    b.Hex16(pair[0]).Hex16(pair[1]);
                }
                else
                {
                    b.Hex16((ushort)codepoint);
                }

                b.Ascii(">\n");
            }

            b.Ascii("endbfchar\n");
        }

        b.Ascii("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return b.ToArray();
    }

    private static int WriteFont(PdfWriter writer, FontOutput output, bool compressed)
    {
        var font = output.Usage.Font;
        var o = writer.Out;
        var type0Id = writer.ReserveObject();
        var cidFontId = writer.ReserveObject();
        var descriptorId = writer.ReserveObject();
        var programId = writer.ReserveObject();
        var toUnicodeId = writer.ReserveObject();
        double Scale(int value) => value * 1000.0 / font.UnitsPerEm;

        writer.BeginObject(type0Id);
        o.Ascii("<</Type/Font/Subtype/Type0/BaseFont/").Ascii(output.BaseFont)
            .Ascii("/Encoding/Identity-H/DescendantFonts[").Int(cidFontId).Ascii(" 0 R]/ToUnicode ").Int(toUnicodeId).Ascii(" 0 R>>");
        writer.EndObject();

        writer.BeginObject(cidFontId);
        o.Ascii("<</Type/Font/Subtype/CIDFontType2/BaseFont/").Ascii(output.BaseFont)
            .Ascii("/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/FontDescriptor ").Int(descriptorId)
            .Ascii(" 0 R/CIDToGIDMap/Identity/W[");

        var glyphs = output.Glyphs;
        for (var i = 0; i < glyphs.Length;)
        {
            var start = i;
            while (i + 1 < glyphs.Length && glyphs[i + 1] == glyphs[i] + 1)
                i++;
            o.Int(glyphs[start]).Byte((byte)'[');
            for (var k = start; k <= i; k++)
            {
                if (k > start) o.Space();
                o.Real(Scale(font.GetAdvance(glyphs[k])));
            }

            o.Ascii("]");
            i++;
        }

        o.Ascii("]>>");
        writer.EndObject();

        var flags = 4; // symbolic: glyphs are addressed by id, not by a standard encoding
        if (font.IsFixedPitch) flags |= 1;
        if (font.Info.Italic || font.ItalicAngle != 0) flags |= 64;

        writer.BeginObject(descriptorId);
        o.Ascii("<</Type/FontDescriptor/FontName/").Ascii(output.BaseFont)
            .Ascii("/Flags ").Int(flags)
            .Ascii("/FontBBox[").Real(Math.Round(Scale(font.XMin))).Space().Real(Math.Round(Scale(font.YMin))).Space()
            .Real(Math.Round(Scale(font.XMax))).Space().Real(Math.Round(Scale(font.YMax)))
            .Ascii("]/ItalicAngle ").Real(font.ItalicAngle)
            .Ascii("/Ascent ").Real(Math.Round(Scale(font.Ascender)))
            .Ascii("/Descent ").Real(Math.Round(Scale(font.Descender)))
            .Ascii("/CapHeight ").Real(Math.Round(Scale(font.CapHeight)))
            .Ascii("/StemV ").Int(font.Info.Weight >= 600 ? 120 : 80)
            .Ascii("/FontFile2 ").Int(programId).Ascii(" 0 R>>");
        writer.EndObject();

        var length = output.ProgramLength;
        writer.WriteStream(programId, output.Program, b => b.Ascii("/Length1 ").Int(length), compressed);
        writer.WriteStream(toUnicodeId, output.ToUnicode, null, compressed);

        return type0Id;
    }

    private static int WriteImage(PdfWriter writer, Images.EncodedImage image)
    {
        var id = writer.ReserveObject();
        var maskId = image.SoftMask != null ? writer.ReserveObject() : 0;

        writer.WriteStream(id, image.Data, b =>
        {
            b.Ascii("/Type/XObject/Subtype/Image/Width ").Int(image.Width).Ascii("/Height ").Int(image.Height)
                .Ascii("/BitsPerComponent ").Int(image.BitsPerComponent).Ascii("/ColorSpace");
            image.ColorSpace(b);
            b.Ascii("/Filter/").Ascii(image.Filter);
            image.ExtraEntries?.Invoke(b);
            if (maskId != 0)
                b.Ascii("/SMask ").Int(maskId).Ascii(" 0 R");
        }, flateEncoded: false);

        if (image.SoftMask != null)
        {
            writer.WriteStream(maskId, image.SoftMask, b => b
                .Ascii("/Type/XObject/Subtype/Image/Width ").Int(image.Width).Ascii("/Height ").Int(image.Height)
                .Ascii("/ColorSpace/DeviceGray/BitsPerComponent 8"), flateEncoded: true);
        }

        return id;
    }

    private static void WriteInfo(PdfWriter writer, int infoId, DocumentMetadata metadata)
    {
        var o = writer.Out;
        writer.BeginObject(infoId);
        o.Ascii("<<");

        void Entry(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                o.Byte((byte)'/').Ascii(key).TextString(value);
        }

        Entry("Title", metadata.Title);
        Entry("Author", metadata.Author);
        Entry("Subject", metadata.Subject);
        Entry("Keywords", metadata.Keywords);
        Entry("Creator", metadata.Creator);
        Entry("Producer", metadata.Producer);

        var date = metadata.CreationDate ?? DateTimeOffset.Now;
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var pdfDate = string.Create(CultureInfo.InvariantCulture,
            $"D:{date:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'");
        o.Ascii("/CreationDate(").Ascii(pdfDate).Ascii(")/ModDate(").Ascii(pdfDate).Ascii(")>>");
        writer.EndObject();
    }
}
