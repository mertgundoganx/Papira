namespace Papira.Tests;

internal static class TestDocuments
{
    public static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    public static byte[] Generate(Action<IContainer> content, Action<PageDescriptor>? page = null) =>
        Document.Create(d => d.Page(p =>
        {
            p.Size(PageSizes.A4).Margin(40);
            page?.Invoke(p);
            content(p.Content());
        })).GeneratePdf();

    public static PdfInspector Inspect(byte[] pdf)
    {
        var inspector = new PdfInspector(pdf);
        inspector.AssertValidStructure();
        return inspector;
    }
}
