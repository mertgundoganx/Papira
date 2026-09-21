using System.Diagnostics;
using System.Globalization;
using Papira;
using Papira.Samples;

var outputDir = Path.Combine(AppContext.BaseDirectory, "output");
Directory.CreateDirectory(outputDir);

var mode = args.Length > 0 ? args[0] : "all";

if (mode is "all" or "invoice")
{
    var path = Path.Combine(outputDir, "invoice.pdf");
    InvoiceDocument.Create(InvoiceData.Sample(itemCount: 60)).GeneratePdf(path);
    Console.WriteLine($"Invoice written: {path}");
}

if (mode is "all" or "features")
{
    var path = Path.Combine(outputDir, "features.pdf");
    FeaturesDocument.Create().GeneratePdf(path);
    Console.WriteLine($"Features written: {path}");
}

if (mode is "all" or "bench")
{
    var count = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 2000;
    var data = InvoiceData.Sample(itemCount: 20);

    // Warm-up (JIT, font parsing).
    for (var i = 0; i < 20; i++)
        InvoiceDocument.Create(data).GeneratePdf();

    var sw = Stopwatch.StartNew();
    for (var i = 0; i < 200; i++)
        InvoiceDocument.Create(data).GeneratePdf();
    var single = sw.Elapsed.TotalMilliseconds / 200;

    sw.Restart();
    long bytes = 0;
    var settings = new DocumentSettings { MaxDegreeOfParallelism = 1 };
    Parallel.For(0, count, _ =>
    {
        var pdf = InvoiceDocument.Create(data).WithSettings(settings).GeneratePdf();
        Interlocked.Add(ref bytes, pdf.Length);
    });
    var elapsed = sw.Elapsed;

    Console.WriteLine($"Single thread : {single:0.00} ms per invoice");
    Console.WriteLine($"All cores     : {count} invoices in {elapsed.TotalMilliseconds:0} ms " +
                      $"=> {count / elapsed.TotalSeconds:0} invoices/s ({Environment.ProcessorCount} cores), avg {bytes / count / 1024.0:0.0} KB");
}
