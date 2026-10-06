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
    private sealed class PageOutput(PageSize size, ByteBuffer content, LinkArea[] links, FormField[] fields) : IDisposable
    {
        public PageSize Size { get; } = size;
        public ByteBuffer Content { get; } = content;
        public LinkArea[] Links { get; } = links;
        public FormField[] Fields { get; } = fields;
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

    public static void Render(DocumentDescriptor document, DocumentMetadata metadata, DocumentSettings settings, IReadOnlyList<DocumentAttachment> attachments, Stream output)
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

            if (settings.Signature is { } signature)
            {
                // The signature covers the whole file, so it is written into a buffer, signed and copied.
                using var file = new MemoryStream();
                var placeholder = Emit(pages, context, metadata, settings, attachments, file);
                SignatureWriter.Sign(file, placeholder!, signature, signature.Date ?? DateTimeOffset.Now);
                file.Position = 0;
                file.CopyTo(output);
            }
            else
            {
                Emit(pages, context, metadata, settings, attachments, output);
            }
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
        var canvas = new Canvas(new DocumentResources()) { Tagging = settings.Tagged };
        var context = new LayoutContext(canvas)
        {
            TotalPages = totalPages,
            Structure = settings.Tagged ? new StructureTree() : null,
        };
        var documentStyle = document.Style.InheritFrom(TextStyle.BuiltIn);

        foreach (var page in document.Pages)
        {
            var pageStyle = page.Style.InheritFrom(documentStyle);
            var (width, height) = (page.PageSize.Width, page.PageSize.Height);
            context.Viewport = new Size(width, height);
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
                canvas.BeginPage(buffer, width, height);
                context.PageLinks.Clear();
                context.PageFormFields.Clear();

                if (page.BackgroundColor is { } color)
                    canvas.FillRectangle(0, 0, width, height, color);

                // Everything but the content of the page is furniture: a reader for the blind skips it.
                context.Artifact = true;
                DrawLayer(page.BackgroundSlot, 0, 0, new Size(width, height), context);
                DrawAt(page.HeaderSlot, page.LeftMargin, page.TopMargin, new Size(contentWidth, header.Height), context);
                context.Artifact = false;

                DrawAt(page.ContentSlot, page.LeftMargin, page.TopMargin + header.Height, body, context);

                context.Artifact = true;
                DrawAt(page.FooterSlot, page.LeftMargin, page.TopMargin + contentHeight - footer.Height, new Size(contentWidth, footer.Height), context);
                DrawLayer(page.ForegroundSlot, 0, 0, new Size(width, height), context);
                context.Artifact = false;

                canvas.EndPage();
                context.RelaxKeepTogether = false;
                pages.Add(new PageOutput(page.PageSize, buffer, [.. context.PageLinks], [.. context.PageFormFields]));

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

    private static SignaturePlaceholder? Emit(List<PageOutput> pages, LayoutContext context, DocumentMetadata metadata, DocumentSettings settings, IReadOnlyList<DocumentAttachment> attachments, Stream output)
    {
        var resources = context.Canvas.Resources;
        var standard = settings.Standard;
        if (settings.Signature != null && settings.Encryption != null)
            throw new InvalidOperationException("A signed document cannot be encrypted: the signature is written into the file, and encrypting it would break it.");

        if (standard == PdfStandard.PdfA2b && attachments.Count > 0)
            throw new InvalidOperationException("PDF/A-2b does not allow attachments; use PdfStandard.PdfA3b instead.");
        if (standard != PdfStandard.None && settings.Encryption != null)
            throw new InvalidOperationException("PDF/A files cannot be encrypted; archive standards require readable content.");

        var encryptor = settings.Encryption is { } encryption
            ? new PdfEncryptor(encryption.UserPassword, encryption.OwnerPassword, PermissionBits(encryption.Permissions))
            : null;

        CompressionLevel? level = settings.Compression switch
        {
            PdfCompression.None => null,
            PdfCompression.Fastest => CompressionLevel.Fastest,
            PdfCompression.Smallest => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        // The appearances of the form are drawn with the same fonts as the pages, so the glyphs they need
        // have to be known before the fonts are subset.
        var pageFields = pages.Select(page => page.Fields.ToList()).ToList();
        var signatureField = SignatureField(settings, pageFields);
        var formFields = pageFields.SelectMany(fields => fields).ToList();
        FormWriter.Prepare(formFields, resources);

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
        using var writer = new PdfWriter(output, encryptor);
        var catalogId = writer.ReserveObject();
        var pagesId = writer.ReserveObject();
        var resourcesId = writer.ReserveObject();
        var infoId = writer.ReserveObject();
        var o = writer.Out;

        // The signature is reserved before the pages, so that the field which is signed can point at it.
        var signatureId = settings.Signature != null ? writer.ReserveObject() : 0;

        // Page ids are reserved up front so links can point to later pages.
        var annotations = new List<(StructureElement? Structure, int Id)>();
        var fieldIds = new List<int>();

        // The buttons of a radio group are the widgets of one field, which is written after the pages.
        var radioGroups = formFields
            .Where(field => field.Kind == FormFieldKind.Radio)
            .GroupBy(field => field.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (Id: writer.ReserveObject(), Buttons: group.ToList(), Widgets: new List<int>()), StringComparer.Ordinal);
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
            var widgetIds = pageFields[i].Select(_ => writer.ReserveObject()).ToArray();

            writer.BeginObject(pageIds[i]);
            o.Ascii("<</Type/Page/Parent ").Int(pagesId).Ascii(" 0 R/MediaBox[0 0 ")
                .Real(page.Size.Width).Space().Real(page.Size.Height)
                .Ascii("]/Resources ").Int(resourcesId).Ascii(" 0 R/Contents ").Int(contentId).Ascii(" 0 R");

            if (context.Structure != null)
                o.Ascii("/StructParents ").Int(i).Ascii("/Tabs/S");
            if (linkIds.Length > 0 || widgetIds.Length > 0)
            {
                o.Ascii("/Annots[");
                foreach (var id in linkIds.Concat(widgetIds))
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
            {
                annotations.Add((links[l].Structure, linkIds[l]));
                WriteLink(writer, linkIds[l], links[l], context.Sections, pageIds, standard != PdfStandard.None, context.Structure != null ? pages.Count + annotations.Count - 1 : -1);
            }

            for (var f = 0; f < pageFields[i].Count; f++)
            {
                var field = pageFields[i][f];
                annotations.Add((field.Structure, widgetIds[f]));

                var parent = 0;
                if (field.Kind == FormFieldKind.Radio && radioGroups.TryGetValue(field.Name, out var group))
                {
                    parent = group.Id;
                    group.Widgets.Add(widgetIds[f]);
                }
                else
                {
                    fieldIds.Add(widgetIds[f]);
                }

                FormWriter.Write(writer, widgetIds[f], field, resources, resourcesId,
                    context.Structure != null ? pages.Count + annotations.Count - 1 : -1, level, parent,
                    ReferenceEquals(field, signatureField) ? signatureId : 0);
            }
        }

        foreach (var (id, buttons, widgets) in radioGroups.Values)
        {
            FormWriter.WriteRadioGroup(writer, id, buttons, widgets);
            fieldIds.Add(id);
        }

        var fontIds = new List<(string Name, int Id)>();
        foreach (var font in fonts)
            fontIds.Add((font.Usage.Name, WriteFont(writer, font, level != null)));

        var imageIds = new List<(string Name, int Id)>();
        foreach (var image in images)
            imageIds.Add((image.Name, WriteImage(writer, image.Image.Encoded)));

        var shadingIds = new List<(string Name, int Id)>();
        foreach (var (name, shading) in resources.Shadings)
            shadingIds.Add((name, WriteShadingPattern(writer, shading)));

        var maskIds = new List<(string Name, int Id)>();
        foreach (var (name, shading, width, height) in resources.SoftMasks)
            maskIds.Add((name, WriteSoftMask(writer, shading, width, height, level != null)));

        foreach (var mask in resources.MaskForms)
            maskIds.Add((mask.Name, WriteMaskForm(writer, mask, resourcesId, level)));

        var tilingIds = new List<(string Name, int Id)>();
        foreach (var tiling in resources.Tilings)
            tilingIds.Add((tiling.Name, WriteTiling(writer, tiling, resourcesId, level)));

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

        if (resources.Opacities.Count > 0 || maskIds.Count > 0)
        {
            o.Ascii("/ExtGState<<");
            foreach (var (alpha, name) in resources.Opacities)
                o.Byte((byte)'/').Ascii(name).Ascii("<</ca ").Real(alpha).Ascii("/CA ").Real(alpha).Ascii(">>");

            foreach (var (name, id) in maskIds)
                o.Byte((byte)'/').Ascii(name).Space().Int(id).Ascii(" 0 R");

            o.Ascii(">>");
        }

        if (shadingIds.Count > 0 || tilingIds.Count > 0)
        {
            o.Ascii("/Pattern<<");
            foreach (var (name, id) in shadingIds.Concat(tilingIds))
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

        // Attachments, sorted by name as the embedded files name tree requires.
        var files = attachments
            .OrderBy(a => a.FileName, StringComparer.Ordinal)
            .Select(a => (a.FileName, Id: WriteAttachment(writer, a, level, metadata.CreationDate)))
            .ToList();

        var date = metadata.CreationDate ?? DateTimeOffset.Now;
        var metadataId = 0;
        var outputIntentProfileId = 0;
        if (standard != PdfStandard.None || settings.Tagged)
        {
            var part = standard switch { PdfStandard.PdfA2b => 2, PdfStandard.PdfA3b => 3, _ => 0 };
            metadataId = writer.ReserveObject();

            // The XMP packet stays uncompressed so that tools can read it without parsing the whole file.
            writer.WriteStream(metadataId, XmpMetadata.Create(metadata, date, part, settings.Tagged),
                b => b.Ascii("/Type/Metadata/Subtype/XML"), flateEncoded: false);
        }

        if (standard != PdfStandard.None)
        {
            var profile = IccProfile.CreateSrgb();
            outputIntentProfileId = writer.ReserveObject();
            writer.WriteStream(outputIntentProfileId, level is { } profileLevel ? PdfWriter.Deflate(profile, profileLevel) : profile,
                b => b.Ascii("/N 3"), level != null);
        }

        var signedAt = settings.Signature?.Date ?? DateTimeOffset.Now;
        var placeholder = settings.Signature is { } signatureSettings
            ? SignatureWriter.Write(writer, signatureId, signatureSettings, signedAt)
            : null;

        var structureId = context.Structure is { } structure
            ? WriteStructure(writer, structure, pageIds, annotations, metadata)
            : 0;

        writer.BeginObject(catalogId);
        o.Ascii("<</Type/Catalog/Pages ").Int(pagesId).Ascii(" 0 R");
        if (structureId != 0)
        {
            o.Ascii("/StructTreeRoot ").Int(structureId).Ascii(" 0 R/MarkInfo<</Marked true>>")
                .Ascii("/ViewerPreferences<</DisplayDocTitle true>>");
        }

        if (fieldIds.Count > 0)
        {
            o.Ascii("/AcroForm<</Fields[");
            foreach (var id in fieldIds)
                o.Int(id).Ascii(" 0 R ");

            // Papira draws every appearance itself, so a viewer has nothing left to regenerate.
            o.Ascii("]");
            if (formFields.Exists(field => field.Kind == FormFieldKind.Signature))
                o.Ascii("/SigFlags 3");

            o.Ascii("/NeedAppearances false/DR ").Int(resourcesId).Ascii(" 0 R/DA");
            writer.AsciiString(FormWriter.DefaultAppearance(
                resources.GetFont(formFields[0].Style!.Font.Font).Name, formFields[0]));
            o.Ascii(">>");
        }

        if (!string.IsNullOrEmpty(metadata.Language))
            o.Ascii("/Lang").AsciiString(metadata.Language);
        if (metadataId != 0)
            o.Ascii("/Metadata ").Int(metadataId).Ascii(" 0 R");

        if (outputIntentProfileId != 0)
        {
            o.Ascii("/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(sRGB)")
                .Ascii("/Info(sRGB IEC61966-2.1)/RegistryName(http://www.color.org)/DestOutputProfile ")
                .Int(outputIntentProfileId).Ascii(" 0 R>>]");
        }

        if (outlinesId != 0)
            o.Ascii("/Outlines ").Int(outlinesId).Ascii(" 0 R/PageMode/UseOutlines");

        if (files.Count > 0)
        {
            o.Ascii("/Names<</EmbeddedFiles<</Names[");
            foreach (var (name, id) in files)
                o.TextString(name).Space().Int(id).Ascii(" 0 R ");
            o.Ascii("]>>>>/AF[");
            foreach (var (_, id) in files)
                o.Int(id).Ascii(" 0 R ");
            o.Ascii("]");
        }

        o.Ascii(">>");
        writer.EndObject();

        WriteInfo(writer, infoId, metadata);

        var encryptId = 0;
        if (encryptor != null)
        {
            encryptId = writer.ReserveObject();
            writer.BeginObject(encryptId);
            o.Ascii("<</Filter/Standard/V 5/R 6/Length 256")
                .Ascii("/CF<</StdCF<</CFM/AESV3/AuthEvent/DocOpen/Length 32>>>>/StmF/StdCF/StrF/StdCF")
                .Ascii("/P ").Int(encryptor.Permissions).Ascii("/EncryptMetadata true");

            // The entries of the encryption dictionary itself are never encrypted.
            WriteRawHex(o, "/O", encryptor.O);
            WriteRawHex(o, "/U", encryptor.U);
            WriteRawHex(o, "/OE", encryptor.OwnerKey);
            WriteRawHex(o, "/UE", encryptor.UserKey);
            WriteRawHex(o, "/Perms", encryptor.Perms);
            o.Ascii(">>");
            writer.EndObject();
        }

        writer.WriteTrailer(catalogId, infoId, encryptId);
        return placeholder;
    }

    /// <summary>
    /// The field the signature belongs to. A document that has none is still signed: an empty field is
    /// added for it, which is what an invisible signature is.
    /// </summary>
    private static FormField? SignatureField(DocumentSettings settings, List<List<FormField>> pageFields)
    {
        if (settings.Signature is not { } signature)
            return null;

        var fields = pageFields.SelectMany(fields => fields).Where(field => field.Kind == FormFieldKind.Signature).ToList();
        if (signature.FieldName is { Length: > 0 } name)
        {
            return fields.Find(field => string.Equals(field.Name, name, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"The document has no signature field named '{name}'. Add one with SignatureField(\"{name}\"), or leave the name out to sign the document invisibly.");
        }

        if (fields.Count > 0)
            return fields[0];

        // Nothing was drawn for the signature, so it gets a field of no size on the first page.
        var invisible = new FormField(FormFieldKind.Signature, "Signature1")
        {
            Style = new Elements.ResolvedTextStyle(TextStyle.BuiltIn),
            Tooltip = signature.Reason ?? "Signature",
        };

        pageFields[0].Add(invisible);
        return invisible;
    }

    private static void WriteRawHex(ByteBuffer o, string key, byte[] data)
    {
        o.Ascii(key).Byte((byte)'<');
        foreach (var b in data)
            o.Hex8(b);
        o.Byte((byte)'>');
    }

    /// <summary>The /P value: every reserved bit is set, the bits of denied permissions are cleared.</summary>
    private static int PermissionBits(PdfPermissions permissions)
    {
        var bits = -1 & ~3; // bits 1 and 2 are always zero
        void Deny(PdfPermissions permission, int bit)
        {
            if (!permissions.HasFlag(permission))
                bits &= ~(1 << (bit - 1));
        }

        Deny(PdfPermissions.Print, 3);
        Deny(PdfPermissions.ModifyContents, 4);
        Deny(PdfPermissions.CopyContent, 5);
        Deny(PdfPermissions.Annotate, 6);
        Deny(PdfPermissions.FillForms, 9);
        Deny(PdfPermissions.ExtractForAccessibility, 10);
        Deny(PdfPermissions.AssembleDocument, 11);
        Deny(PdfPermissions.PrintHighResolution, 12);
        return bits;
    }

    /// <summary>Writes an embedded file and the file specification that refers to it.</summary>
    private static int WriteAttachment(PdfWriter writer, DocumentAttachment attachment, CompressionLevel? level, DateTimeOffset? documentDate)
    {
        var data = level is { } compression ? PdfWriter.Deflate(attachment.Data, compression) : attachment.Data;
        var streamId = writer.ReserveObject();
        var date = PdfDate(attachment.ModificationDate ?? documentDate ?? DateTimeOffset.Now);

        writer.WriteStream(streamId, data, b =>
        {
            b.Ascii("/Type/EmbeddedFile/Subtype").Name(attachment.MediaType)
                .Ascii("/Params<</Size ").Int(attachment.Data.Length).Ascii("/ModDate");
            writer.AsciiString(date);
            b.Ascii(">>");
        }, level != null);

        var fileSpecId = writer.ReserveObject();
        var o = writer.Out;
        writer.BeginObject(fileSpecId);
        o.Ascii("<</Type/Filespec/F");
        writer.TextString(attachment.FileName);
        o.Ascii("/UF");
        writer.TextString(attachment.FileName);
        if (!string.IsNullOrEmpty(attachment.Description))
        {
            o.Ascii("/Desc");
            writer.TextString(attachment.Description);
        }

        o.Ascii("/AFRelationship/").Ascii(attachment.Relationship.ToString())
            .Ascii("/EF<</F ").Int(streamId).Ascii(" 0 R/UF ").Int(streamId).Ascii(" 0 R>>>>");
        writer.EndObject();
        return fileSpecId;
    }

    /// <summary>Writes a gradient as a shading pattern; the stops become an exponential or stitching function.</summary>
    private static int WriteShadingPattern(PdfWriter writer, Shading shading)
    {
        var id = writer.ReserveObject();
        var o = writer.Out;
        var m = shading.Matrix;

        writer.BeginObject(id);
        o.Ascii("<</Type/Pattern/PatternType 2/Matrix[")
            .Real(m.A).Space().Real(m.B).Space().Real(m.C).Space().Real(m.D).Space().Real(m.E).Space().Real(m.F)
            .Ascii("]/Shading<</ShadingType ").Int(shading.Radial ? 3 : 2).Ascii("/ColorSpace/DeviceRGB/Coords[");

        WriteCoordinates(o, shading);
        o.Ascii("]/Extend[true true]/Function");
        WriteStopFunction(o, shading.Stops);
        o.Ascii(">>>>");
        writer.EndObject();
        return id;
    }

    /// <summary>
    /// A graphics state whose soft mask carries the transparency of a gradient. The mask is a form drawn
    /// in shades of grey — white where the gradient is opaque, black where it is not — which the reader
    /// then reads as the transparency of everything painted while the state is in effect.
    /// </summary>
    private static int WriteSoftMask(PdfWriter writer, Shading shading, float width, float height, bool compressed)
    {
        var patternId = writer.ReserveObject();
        var formId = writer.ReserveObject();
        var stateId = writer.ReserveObject();
        var o = writer.Out;
        var m = shading.Matrix;

        writer.BeginObject(patternId);
        o.Ascii("<</Type/Pattern/PatternType 2/Matrix[")
            .Real(m.A).Space().Real(m.B).Space().Real(m.C).Space().Real(m.D).Space().Real(m.E).Space().Real(m.F)
            .Ascii("]/Shading<</ShadingType ").Int(shading.Radial ? 3 : 2).Ascii("/ColorSpace/DeviceGray/Coords[");

        WriteCoordinates(o, shading);
        o.Ascii("]/Extend[true true]/Function");
        WriteAlphaFunction(o, shading.Stops);
        o.Ascii(">>>>");
        writer.EndObject();

        using var content = new ByteBuffer(64);
        content.Ascii("/Pattern cs /P0 scn 0 0 ").Real(width).Space().Real(height).Ascii(" re f\n");

        writer.WriteStream(
            formId,
            content.ToArray(),
            b => b.Ascii("/Type/XObject/Subtype/Form/FormType 1/BBox[0 0 ").Real(width).Space().Real(height)
                .Ascii("]/Group<</Type/Group/S/Transparency/CS/DeviceGray/I false/K false>>/Resources<</Pattern<</P0 ")
                .Int(patternId).Ascii(" 0 R>>>>"),
            compressed);

        writer.BeginObject(stateId);
        o.Ascii("<</Type/ExtGState/SMask<</Type/Mask/S/Luminosity/BC[0]/G ").Int(formId).Ascii(" 0 R>>>>");
        writer.EndObject();
        return stateId;
    }

    /// <summary>
    /// Writes a mask drawn from part of a drawing: the drawing goes into a form of its own, and what is
    /// drawn through the mask shows only where that form is light.
    /// </summary>
    private static int WriteMaskForm(PdfWriter writer, MaskForm mask, int resourcesId, CompressionLevel? level)
    {
        var formId = writer.ReserveObject();
        var compressed = level is { } compression ? PdfWriter.Deflate(mask.Content, compression) : null;

        writer.WriteStream(
            formId,
            compressed ?? mask.Content,
            b => b.Ascii("/Type/XObject/Subtype/Form/FormType 1/BBox[")
                .Real(mask.Left).Space().Real(mask.Bottom).Space().Real(mask.Right).Space().Real(mask.Top)
                .Ascii("]/Group<</Type/Group/S/Transparency/CS/DeviceRGB/I false/K false>>/Resources ")
                .Int(resourcesId).Ascii(" 0 R"),
            compressed != null);

        var stateId = writer.ReserveObject();
        writer.BeginObject(stateId);

        // The backdrop is black, so everything the mask does not draw on is hidden.
        writer.Out.Ascii("<</Type/ExtGState/SMask<</Type/Mask/S/Luminosity/BC[0 0 0]/G ").Int(formId).Ascii(" 0 R>>>>");
        writer.EndObject();
        return stateId;
    }

    /// <summary>Writes one tile of a pattern and how it is repeated over the page.</summary>
    private static int WriteTiling(PdfWriter writer, TilingPattern tiling, int resourcesId, CompressionLevel? level)
    {
        var id = writer.ReserveObject();
        var compressed = level is { } compression ? PdfWriter.Deflate(tiling.Content, compression) : null;
        var m = tiling.Matrix;

        writer.WriteStream(
            id,
            compressed ?? tiling.Content,
            b => b.Ascii("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[")
                .Real(tiling.Left).Space().Real(tiling.Bottom).Space().Real(tiling.Right).Space().Real(tiling.Top)
                .Ascii("]/XStep ").Real(tiling.XStep).Ascii("/YStep ").Real(tiling.YStep)
                .Ascii("/Matrix[").Real(m.A).Space().Real(m.B).Space().Real(m.C).Space().Real(m.D).Space().Real(m.E).Space().Real(m.F)
                .Ascii("]/Resources ").Int(resourcesId).Ascii(" 0 R"),
            compressed != null);

        return id;
    }

    private static void WriteCoordinates(ByteBuffer o, Shading shading)
    {
        if (shading.Radial)
        {
            o.Real(shading.X0).Space().Real(shading.Y0).Space().Real(shading.Radius0).Space()
                .Real(shading.X1).Space().Real(shading.Y1).Space().Real(shading.Radius1);
        }
        else
        {
            o.Real(shading.X0).Space().Real(shading.Y0).Space().Real(shading.X1).Space().Real(shading.Y1);
        }
    }

    /// <summary>The same stops as the colours, in shades of grey that stand for their transparency.</summary>
    private static void WriteAlphaFunction(ByteBuffer o, ColorStop[] stops)
    {
        if (stops.Length == 2)
        {
            WriteSegment(o, stops[0].Alpha, stops[1].Alpha);
            return;
        }

        o.Ascii("<</FunctionType 3/Domain[0 1]/Functions[");
        for (var i = 0; i + 1 < stops.Length; i++)
            WriteSegment(o, stops[i].Alpha, stops[i + 1].Alpha);

        o.Ascii("]/Bounds[");
        for (var i = 1; i + 1 < stops.Length; i++)
            o.Real(stops[i].Offset).Space();

        o.Ascii("]/Encode[");
        for (var i = 0; i + 1 < stops.Length; i++)
            o.Ascii("0 1 ");

        o.Ascii("]>>");

        static void WriteSegment(ByteBuffer o, float from, float to) =>
            o.Ascii("<</FunctionType 2/Domain[0 1]/N 1/C0[").Real(from).Ascii("]/C1[").Real(to).Ascii("]>>");
    }

    private static void WriteStopFunction(ByteBuffer o, ColorStop[] stops)
    {
        if (stops.Length == 2)
        {
            WriteSegment(o, stops[0].Color, stops[1].Color);
            return;
        }

        // Several stops: one segment per pair, stitched at the stop offsets.
        o.Ascii("<</FunctionType 3/Domain[0 1]/Functions[");
        for (var i = 0; i + 1 < stops.Length; i++)
            WriteSegment(o, stops[i].Color, stops[i + 1].Color);

        o.Ascii("]/Bounds[");
        for (var i = 1; i + 1 < stops.Length; i++)
            o.Real(stops[i].Offset).Space();

        o.Ascii("]/Encode[");
        for (var i = 0; i + 1 < stops.Length; i++)
            o.Ascii("0 1 ");
        o.Ascii("]>>");

        static void WriteSegment(ByteBuffer o, Color from, Color to) =>
            o.Ascii("<</FunctionType 2/Domain[0 1]/N 1/C0[")
                .Real(from.R / 255.0).Space().Real(from.G / 255.0).Space().Real(from.B / 255.0)
                .Ascii("]/C1[")
                .Real(to.R / 255.0).Space().Real(to.G / 255.0).Space().Real(to.B / 255.0)
                .Ascii("]>>");
    }

    /// <summary>
    /// Writes the structure of a tagged document: one object per element, a tree that mirrors what the
    /// document says, and the number tree that leads from a piece of a page back to the element it
    /// belongs to. This is what a reader for the blind follows instead of the page itself.
    /// </summary>
    private static int WriteStructure(
        PdfWriter writer,
        StructureTree structure,
        int[] pageIds,
        List<(StructureElement? Structure, int Id)> links,
        DocumentMetadata metadata)
    {
        var o = writer.Out;
        var rootId = writer.ReserveObject();

        var elements = StructureTree.Flatten(structure.Root).ToList();
        foreach (var element in elements)
            element.Id = writer.ReserveObject();

        // Which element each piece of content belongs to, page by page, and which one each link belongs to.
        var owners = new Dictionary<int, StructureElement[]>();
        for (var page = 0; page < pageIds.Length; page++)
            owners[page] = new StructureElement[structure.MarkedContentCount(page)];

        foreach (var element in elements)
        {
            foreach (var child in element.Children)
            {
                if (child is MarkedContent content && owners.TryGetValue(content.Page, out var page) && content.Mcid < page.Length)
                    page[content.Mcid] = element;
            }
        }

        foreach (var element in elements)
        {
            writer.BeginObject(element.Id);
            o.Ascii("<</Type/StructElem/S/").Ascii(element.Role);
            o.Ascii("/P ").Int(element.Parent?.Id ?? rootId).Ascii(" 0 R");

            // The layout attributes of a table cell: which cells a header heads, and how far a cell reaches.
            if (element.Role is "TH" or "TD" && (element.Role == "TH" || element.ColumnSpan > 1 || element.RowSpan > 1))
            {
                o.Ascii("/A<</O/Table");
                if (element.Role == "TH")
                    o.Ascii("/Scope/Column");
                if (element.ColumnSpan > 1)
                    o.Ascii("/ColSpan ").Int(element.ColumnSpan);
                if (element.RowSpan > 1)
                    o.Ascii("/RowSpan ").Int(element.RowSpan);

                o.Ascii(">>");
            }

            if (!string.IsNullOrEmpty(element.Alt))
            {
                o.Ascii("/Alt");
                writer.TextString(element.Alt);
            }

            // The page a piece of content sits on has to be named before it can be pointed at.
            var firstPage = element.Children.OfType<MarkedContent>().Select(content => content.Page).DefaultIfEmpty(-1).First();
            if (firstPage >= 0)
                o.Ascii("/Pg ").Int(pageIds[firstPage]).Ascii(" 0 R");

            o.Ascii("/K[");
            foreach (var child in element.Children)
            {
                switch (child)
                {
                    case StructureElement nested:
                        o.Int(nested.Id).Ascii(" 0 R ");
                        break;
                    case MarkedContent content when content.Page == firstPage:
                        o.Int(content.Mcid).Space();
                        break;
                    case MarkedContent content:
                        o.Ascii("<</Type/MCR/Pg ").Int(pageIds[content.Page]).Ascii(" 0 R/MCID ").Int(content.Mcid).Ascii(">> ");
                        break;
                    default:
                        break;
                }
            }

            // A link also holds the annotation a reader activates.
            foreach (var (owner, id) in links)
            {
                if (ReferenceEquals(owner, element))
                    o.Ascii("<</Type/OBJR/Obj ").Int(id).Ascii(" 0 R>> ");
            }

            o.Ascii("]>>");
            writer.EndObject();
        }

        // The number tree: one entry per page, holding the element of each piece of content on it, and
        // one entry per link annotation.
        var parentTreeId = writer.ReserveObject();
        writer.BeginObject(parentTreeId);
        o.Ascii("<</Nums[");

        for (var page = 0; page < pageIds.Length; page++)
        {
            o.Int(page).Ascii("[");
            foreach (var owner in owners[page])
                o.Int((owner ?? structure.Root).Id).Ascii(" 0 R ");

            o.Ascii("]");
        }

        for (var i = 0; i < links.Count; i++)
        {
            var owner = links[i].Structure ?? structure.Root;
            o.Int(pageIds.Length + i).Space().Int(owner.Id).Ascii(" 0 R ");
        }

        o.Ascii("]>>");
        writer.EndObject();

        writer.BeginObject(rootId);
        o.Ascii("<</Type/StructTreeRoot/K[").Int(structure.Root.Id).Ascii(" 0 R]/ParentTree ").Int(parentTreeId)
            .Ascii(" 0 R/ParentTreeNextKey ").Int(pageIds.Length + links.Count).Ascii(">>");
        writer.EndObject();

        _ = metadata;
        return rootId;
    }

    private static void WriteDestination(ByteBuffer o, Destination destination, int[] pageIds) =>
        o.Ascii("[").Int(pageIds[destination.PageIndex]).Ascii(" 0 R/XYZ ")
            .Real(destination.X).Space().Real(destination.Y).Ascii(" null]");

    private static void WriteLink(PdfWriter writer, int id, LinkArea link, Dictionary<string, Destination> sections, int[] pageIds, bool withAppearance, int structParent)
    {
        var o = writer.Out;

        // PDF/A wants an appearance stream on annotations; an empty form is enough for a link.
        var appearanceId = 0;
        if (withAppearance)
        {
            appearanceId = writer.ReserveObject();
            writer.WriteStream(appearanceId, [], b => b
                .Ascii("/Type/XObject/Subtype/Form/Resources<<>>/BBox[0 0 ")
                .Real(link.Right - link.Left).Space().Real(link.Top - link.Bottom).Ascii("]"), flateEncoded: false);
        }

        writer.BeginObject(id);
        o.Ascii("<</Type/Annot/Subtype/Link/F 4/Border[0 0 0]/Rect[")
            .Real(link.Left).Space().Real(link.Bottom).Space().Real(link.Right).Space().Real(link.Top).Ascii("]");

        if (structParent >= 0)
        {
            o.Ascii("/StructParent ").Int(structParent).Ascii("/Contents");
            writer.TextString(link.Uri ?? link.Section ?? string.Empty);
        }

        if (link.Uri != null)
        {
            o.Ascii("/A<</S/URI/URI");
            writer.AsciiString(link.Uri);
            o.Ascii(">>");
        }
        else
        {
            o.Ascii("/Dest");
            WriteDestination(o, sections[link.Section!], pageIds);
        }

        if (appearanceId != 0)
            o.Ascii("/AP<</N ").Int(appearanceId).Ascii(" 0 R>>");

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
                o.Ascii("/Title");
                writer.TextString(node.Bookmark!.Value.Title);
                o.Ascii("/Parent ").Int(parent.Id).Ascii(" 0 R");
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

        var program = FontSubsetter.Subset(usage.Font, glyphs, output.BaseFont);
        output.ProgramLength = program.Length;
        output.Program = level is { } l ? PdfWriter.Deflate(program, l) : program;

        var cmap = BuildToUnicode(output.Glyphs, map, usage.GlyphToText);
        output.ToUnicode = level is { } l2 ? PdfWriter.Deflate(cmap, l2) : cmap;
    }

    private static byte[] BuildToUnicode(ushort[] glyphs, int[] map, Dictionary<ushort, int[]> sequences)
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

                // A ligature glyph extracts as every character it replaced.
                if (sequences.TryGetValue(glyph, out var sequence))
                {
                    foreach (var value in sequence)
                        WriteCodepoint(b, value, pair);
                }
                else
                {
                    WriteCodepoint(b, map[glyph], pair);
                }

                b.Ascii(">\n");
            }

            b.Ascii("endbfchar\n");
        }

        b.Ascii("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return b.ToArray();

        static void WriteCodepoint(ByteBuffer b, int codepoint, Span<char> pair)
        {
            if (codepoint > 0xFFFF)
            {
                new System.Text.Rune(codepoint).EncodeToUtf16(pair);
                b.Hex16(pair[0]).Hex16(pair[1]);
            }
            else
            {
                b.Hex16((ushort)Math.Max(codepoint, 0));
            }
        }
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
        // Compact Font Format outlines are embedded as a CID-keyed font of type 0; TrueType glyphs as type 2.
        var cff = font.IsCff;
        o.Ascii("<</Type/Font/Subtype/").Ascii(cff ? "CIDFontType0" : "CIDFontType2").Ascii("/BaseFont/").Ascii(output.BaseFont)
            .Ascii("/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/FontDescriptor ").Int(descriptorId)
            .Ascii(" 0 R");

        if (!cff)
            o.Ascii("/CIDToGIDMap/Identity");

        o.Ascii("/W[");

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
            .Ascii(cff ? "/FontFile3 " : "/FontFile2 ").Int(programId).Ascii(" 0 R>>");
        writer.EndObject();

        var length = output.ProgramLength;
        writer.WriteStream(
            programId,
            output.Program,
            b =>
            {
                if (cff)
                    b.Ascii("/Subtype/CIDFontType0C");
                else
                    b.Ascii("/Length1 ").Int(length);
            },
            compressed);
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

    /// <summary>A date in PDF syntax, e.g. <c>D:20260927T…</c> with the UTC offset.</summary>
    private static string PdfDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{date:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'");
    }

    private static void WriteInfo(PdfWriter writer, int infoId, DocumentMetadata metadata)
    {
        var o = writer.Out;
        writer.BeginObject(infoId);
        o.Ascii("<<");

        void Entry(string key, string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            o.Byte((byte)'/').Ascii(key);
            writer.TextString(value);
        }

        Entry("Title", metadata.Title);
        Entry("Author", metadata.Author);
        Entry("Subject", metadata.Subject);
        Entry("Keywords", metadata.Keywords);
        Entry("Creator", metadata.Creator);
        Entry("Producer", metadata.Producer);

        var pdfDate = PdfDate(metadata.CreationDate ?? DateTimeOffset.Now);
        o.Ascii("/CreationDate");
        writer.AsciiString(pdfDate);
        o.Ascii("/ModDate");
        writer.AsciiString(pdfDate);
        o.Ascii(">>");
        writer.EndObject();
    }
}
