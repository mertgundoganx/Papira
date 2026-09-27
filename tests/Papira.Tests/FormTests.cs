using System.Text.RegularExpressions;
using static Papira.Tests.TestDocuments;

namespace Papira.Tests;

/// <summary>
/// Interactive forms. The files these tests produce were checked with veraPDF (PDF/A-3b and PDF/UA-1
/// both pass with fields on the page) and rendered through PDFKit, which reads every field back with
/// the name, kind and value it was given.
/// </summary>
public class FormTests
{
    private static PdfInspector Form(Action<IContainer> content, Action<PageDescriptor>? page = null) =>
        Inspect(Generate(content, page));

    /// <summary>The appearance stream Papira drew for a field, found by the marker of variable text.</summary>
    private static string Appearance(PdfInspector pdf, string contains) =>
        pdf.Streams().Single(s => s.Contains("/Tx BMC") && pdf.TextIn(s).Contains(contains, StringComparison.Ordinal));

    [Fact]
    public void A_text_field_becomes_a_widget_the_document_lists()
    {
        var pdf = Form(c => c.TextField("name").Value("Mert").Tooltip("Adınız"));

        Assert.Contains("/AcroForm<</Fields[", pdf.Raw);
        Assert.Contains("/Type/Annot/Subtype/Widget", pdf.Raw);
        Assert.Contains("/FT/Tx", pdf.Raw);
        // Names and values are written as text strings, which a PDF stores in UTF-16.
        Assert.Contains("name", pdf.ExtractStrings());
        Assert.Contains("Mert", pdf.ExtractStrings());

        // The page has to point at the widget, or no reader would find it.
        Assert.Matches(@"/Type/Page/.*?/Annots\[\d+ 0 R", pdf.Raw);
    }

    [Fact]
    public void The_value_is_drawn_so_that_it_shows_before_anything_is_filled_in()
    {
        // A field whose text only appears once a particular viewer has redrawn it prints blank elsewhere,
        // so Papira draws the appearance itself and says the file needs no regeneration.
        var pdf = Form(c => c.TextField("name").Value("Gündoğan"));

        Assert.Contains("/NeedAppearances false", pdf.Raw);
        Assert.Contains("/Type/XObject/Subtype/Form", pdf.Raw);
        Assert.Equal("Gündoğan", pdf.TextIn(Appearance(pdf, "Gündoğan")));
    }

    [Fact]
    public void A_field_carries_what_it_is_for()
    {
        var pdf = Form(c => c.TextField("iban").Tooltip("IBAN numaranız"));

        Assert.Contains("/TU", pdf.Raw);
        Assert.Contains("IBAN numaranız", pdf.ExtractStrings());
    }

    [Fact]
    public void A_field_without_a_tooltip_falls_back_to_its_name()
    {
        // PDF/UA-1 requires every field to describe itself; the name is a poor description but not none.
        var pdf = Form(c => c.TextField("iban"));

        Assert.Equal(2, pdf.ExtractStrings().Count(s => s == "iban"));
    }

    [Fact]
    public void A_checkbox_has_a_ticked_and_an_empty_appearance()
    {
        var pdf = Form(c => c.Checkbox("agree").Checked());

        Assert.Contains("/FT/Btn", pdf.Raw);
        Assert.Contains("/V/Yes/DV/Yes/AS/Yes", pdf.Raw);
        Assert.Contains("/AP<</N<</Yes ", pdf.Raw);
        Assert.Contains("/Off ", pdf.Raw);
    }

    [Fact]
    public void An_unticked_checkbox_starts_out_empty()
    {
        var pdf = Form(c => c.Checkbox("agree"));

        Assert.Contains("/V/Off/DV/Off/AS/Off", pdf.Raw);
    }

    [Fact]
    public void A_checkbox_is_square_and_as_tall_as_the_text_around_it()
    {
        var pdf = Form(c => c.Checkbox("agree"), page => page.DefaultTextStyle(t => t.FontSize(20)));

        var rect = Regex.Match(pdf.Raw, @"/Subtype/Widget/F 4/Rect\[(?<l>[\d.]+) (?<b>[\d.]+) (?<r>[\d.]+) (?<t>[\d.]+)\]");
        Assert.True(rect.Success);
        var width = Number(rect, "r") - Number(rect, "l");
        var height = Number(rect, "t") - Number(rect, "b");
        Assert.Equal(20, width, 1);
        Assert.Equal(width, height, 3);
    }

    private static float Number(Match match, string group) =>
        float.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_buttons_of_a_radio_group_are_one_field_with_a_widget_each()
    {
        var pdf = Form(c => c.Column(column =>
        {
            column.Item().Radio("payment", "card").Tooltip("Kredi kartı");
            column.Item().Radio("payment", "transfer").Checked().Tooltip("Havale");
            column.Item().Radio("payment", "cash").Tooltip("Nakit");
        }));

        // One field holds the group; the three buttons are its widgets.
        Assert.Equal(3, Regex.Count(pdf.Raw, "/Subtype/Widget"));
        Assert.Single(Regex.Matches(pdf.Raw, @"/FT/Btn/T<"));
        Assert.Contains("/Kids[", pdf.Raw);

        // Radio (bit 16) and one button always chosen (bit 15).
        Assert.Contains("/Ff 49152", pdf.Raw);
        Assert.Contains("/V/transfer", pdf.Raw);
        Assert.Equal(1, Regex.Count(pdf.Raw, @"/AS/transfer"));
        Assert.Equal(2, Regex.Count(pdf.Raw, @"/AS/Off"));
    }

    [Fact]
    public void A_button_of_a_group_is_drawn_as_a_circle()
    {
        var pdf = Form(c => c.Radio("payment", "card").Checked());
        var appearances = pdf.Streams().Where(s => s.Contains(" c ")).ToList();

        // A ring, and the dot inside it that says this is the one.
        Assert.NotEmpty(appearances);
        Assert.Contains(appearances, a => a.Contains(" S") && a.Contains(" rg"));
    }

    [Fact]
    public void Two_buttons_of_a_group_cannot_stand_for_the_same_thing()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Generate(c => c.Column(column =>
        {
            column.Item().Radio("payment", "card");
            column.Item().Radio("payment", "card");
        })));

        Assert.Contains("stand for 'card'", exception.Message);
    }

    [Fact]
    public void A_radio_group_cannot_share_its_name_with_another_field()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Generate(c => c.Column(column =>
        {
            column.Item().TextField("payment");
            column.Item().Radio("payment", "card");
        })));

        Assert.Contains("name of both a radio group and another field", exception.Message);
    }

    [Fact]
    public void A_place_for_a_signature_is_a_field_of_its_own()
    {
        var pdf = Form(c => c.SignatureField("imza").Tooltip("Yetkili imzası"));

        Assert.Contains("/FT/Sig", pdf.Raw);
        Assert.Contains("/SigFlags 3", pdf.Raw);
        Assert.Contains("Yetkili imzası", pdf.ExtractStrings());
    }

    [Fact]
    public void A_dropdown_offers_its_options()
    {
        var pdf = Form(c => c.Dropdown("country", "Türkiye", "Deutschland").Value("Türkiye"));

        Assert.Contains("/FT/Ch", pdf.Raw);
        Assert.Contains("/Opt[", pdf.Raw);
        Assert.Contains("/Ff 131072", pdf.Raw);   // a dropdown rather than a list box
        Assert.Equal("Türkiye", pdf.TextIn(Appearance(pdf, "Türkiye")));

        var strings = pdf.ExtractStrings();
        Assert.Contains("Türkiye", strings);
        Assert.Contains("Deutschland", strings);
    }

    [Fact]
    public void The_value_of_a_dropdown_has_to_be_one_of_its_options()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Generate(c => c.Dropdown("country", "Türkiye").Value("France")));

        Assert.Contains("'France' is not one of the options", exception.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Read_only_and_required_are_recorded_as_flags(int expected)
    {
        var pdf = Form(c => c.TextField("name")
            .ReadOnly((expected & 1) != 0)
            .Required((expected & 2) != 0));

        Assert.Contains($"/Ff {expected}", pdf.Raw);
    }

    [Fact]
    public void A_field_of_several_lines_says_so_and_wraps_its_value()
    {
        var pdf = Form(c => c.Width(200).TextField("notes").Multiline()
            .Value("Bu metin tek satıra sığmayacak kadar uzun olduğu için ikinci satıra taşmalı."));

        Assert.Contains("/Ff 4096", pdf.Raw);

        // The value is broken at spaces into as many lines as the box holds, each at its own baseline.
        var appearance = Appearance(pdf, "Bu metin");
        Assert.Equal(3, Regex.Count(appearance, @" Tm <"));
        Assert.Equal(
            "Bumetinteksatırasığmayacakkadaruzunolduğuiçinikincisatırataşmalı.",
            pdf.TextIn(appearance).Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void A_length_limit_is_passed_on_to_the_reader()
    {
        var pdf = Form(c => c.TextField("code").MaxLength(8));

        Assert.Contains("/MaxLen 8", pdf.Raw);
    }

    [Fact]
    public void The_colours_of_a_field_are_both_drawn_and_described()
    {
        // The appearance is what a reader sees; /MK is what a viewer redraws the field with.
        var pdf = Form(c => c.TextField("name").BackgroundColor(Colors.Grey.Lighten3).BorderColor(Colors.Blue));

        Assert.Contains("/BC[0.118 0.533 0.898]", pdf.Raw);
        Assert.Contains("/BG[0.933 0.933 0.933]", pdf.Raw);

        var appearance = pdf.Streams().Single(s => s.Contains("/Tx BMC"));
        Assert.Contains("0.933 0.933 0.933 rg", appearance);
        Assert.Contains("0.118 0.533 0.898 RG", appearance);
    }

    [Fact]
    public void Two_fields_cannot_share_a_name()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Generate(c => c.Column(column =>
        {
            column.Item().TextField("name");
            column.Item().TextField("name");
        })));

        Assert.Contains("Two form fields are named 'name'", exception.Message);
    }

    [Fact]
    public void A_name_cannot_contain_a_dot()
    {
        var exception = Assert.Throws<ArgumentException>(() => Generate(c => c.TextField("customer.name")));

        Assert.Contains("cannot contain a dot", exception.Message);
    }

    [Fact]
    public void A_field_in_the_page_furniture_is_refused()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Generate(c => c.Text("Gövde"), page => page.Footer().TextField("name")));

        Assert.Contains("would repeat on every page", exception.Message);
    }

    [Fact]
    public void Fields_land_on_the_page_they_were_laid_out_on()
    {
        var pdf = Inspect(Generate(c => c.Column(column =>
        {
            column.Item().TextField("first");
            column.Item().PageBreak();
            column.Item().TextField("second");
        })));

        Assert.Equal(2, pdf.PageCount);

        // Each page names exactly one widget.
        var annots = Regex.Matches(pdf.Raw, @"/Type/Page/.*?/Annots\[(?<ids>[^\]]*)\]")
            .Select(m => m.Groups["ids"].Value.Trim())
            .ToList();
        Assert.Equal(2, annots.Count);
        Assert.All(annots, ids => Assert.Single(Regex.Matches(ids, @"\d+ 0 R")));
        Assert.NotEqual(annots[0], annots[1]);
    }

    [Fact]
    public void A_field_moves_to_the_next_page_rather_than_being_cut_in_half()
    {
        var pdf = Inspect(Generate(c => c.Column(column =>
        {
            column.Item().Height(750).Background(Colors.Grey.Lighten4);
            column.Item().TextField("name");
        })));

        Assert.Equal(2, pdf.PageCount);
        Assert.Single(Regex.Matches(pdf.Raw, "/Subtype/Widget"));
    }

    [Fact]
    public void A_form_survives_the_second_pass_of_a_document_that_counts_its_pages()
    {
        // Showing the total page count lays the document out twice; the field must be written once.
        var pdf = Inspect(Generate(
            c => c.TextField("name").Value("Mert"),
            page => page.Footer().Text(text =>
            {
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            })));

        Assert.Single(Regex.Matches(pdf.Raw, "/Subtype/Widget"));
        Assert.Single(Regex.Matches(pdf.Raw, @"/AcroForm<</Fields\[\d+ 0 R \]"));
    }

    [Fact]
    public void A_tagged_document_gives_every_field_a_place_in_its_structure()
    {
        var pdf = Inspect(Document.Create(document => document.Page(page =>
            {
                page.Size(PageSizes.A4).Margin(40);
                page.Content().Column(column =>
                {
                    column.Item().TextField("name").Tooltip("Adınız");
                    column.Item().Checkbox("agree").Tooltip("Şartları kabul ediyorum");
                });
            }))
            .WithMetadata(new DocumentMetadata { Title = "Form", Language = "tr-TR" })
            .WithSettings(new DocumentSettings { Tagged = true })
            .GeneratePdf());

        Assert.Equal(2, Regex.Count(pdf.Raw, "/S/Form"));
        Assert.Equal(2, Regex.Count(pdf.Raw, "/Type/OBJR"));
        Assert.Contains("/StructParent 1", pdf.Raw);
        Assert.Contains("/StructParent 2", pdf.Raw);
    }

    [Fact]
    public void The_parent_tree_stays_readable_with_several_annotations_on_a_page()
    {
        var pdf = Inspect(Document.Create(document => document.Page(page =>
            {
                page.Size(PageSizes.A4).Margin(40);
                page.Content().Column(column =>
                {
                    column.Item().TextField("first").Tooltip("İlk");
                    column.Item().TextField("second").Tooltip("İkinci");
                    column.Item().Hyperlink("https://example.com").Text("Bağlantı");
                });
            }))
            .WithMetadata(new DocumentMetadata { Title = "Form", Language = "tr-TR" })
            .WithSettings(new DocumentSettings { Tagged = true })
            .GeneratePdf());

        // Entries of the number tree have to be separated; "0 R2" would run two of them together.
        var nums = Regex.Match(pdf.Raw, @"/Nums\[(?<entries>.*?)\]>>", RegexOptions.Singleline).Groups["entries"].Value;
        Assert.DoesNotMatch(@"0 R\d", nums);

        // One entry per page, then one per annotation: two fields and a link.
        var annotationEntries = Regex.Replace(nums, @"\d+\[[^\]]*\]", string.Empty);
        Assert.Equal(3, Regex.Count(annotationEntries, @"\d+ \d+ 0 R"));
    }
}
