using Papira.Barcodes;
using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Elements;

/// <summary>Draws a QR code, including the four-module quiet zone required by the standard.</summary>
internal sealed class QrCodeElement(string data, QrErrorCorrection correction, Color color) : Element
{
    private const int QuietZone = 4;

    private QrEncoder? _code;
    private bool _drawn;

    private QrEncoder Code => _code ??= QrEncoder.Encode(data, correction);

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        if (_drawn)
            return SpacePlan.Empty;

        var side = Math.Min(available.Width, available.Height);
        return side < 1 ? SpacePlan.Wrap : SpacePlan.Full(side, side);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        if (_drawn)
            return;

        _drawn = true;
        var side = Math.Min(available.Width, available.Height);
        var module = side / (Code.Size + 2 * QuietZone);
        var canvas = context.Canvas;

        for (var y = 0; y < Code.Size; y++)
        {
            // Runs of dark modules are drawn as one rectangle.
            var x = 0;
            while (x < Code.Size)
            {
                if (!Code[x, y])
                {
                    x++;
                    continue;
                }

                var run = 1;
                while (x + run < Code.Size && Code[x + run, y])
                    run++;

                canvas.FillRectangle((QuietZone + x) * module, (QuietZone + y) * module, run * module, module, color);
                x += run;
            }
        }
    }

    internal override void Reset() => _drawn = false;
}

/// <summary>Draws a Code 128 barcode with the quiet zones on both sides.</summary>
internal sealed class BarcodeElement(string value, Color color) : Element
{
    private const int QuietZoneModules = 10;
    private const float DefaultHeight = 40;

    private int[]? _widths;
    private bool _drawn;

    private int[] Widths => _widths ??= Code128Encoder.Encode(value);

    private int TotalModules => Widths.Sum() + 2 * QuietZoneModules;

    internal override SpacePlan Measure(Size available, LayoutContext context)
    {
        if (_drawn)
            return SpacePlan.Empty;

        var height = Math.Min(available.Height, DefaultHeight);
        return height < 1 || available.Width < TotalModules * 0.1f ? SpacePlan.Wrap : SpacePlan.Full(available.Width, height);
    }

    internal override void Draw(Size available, LayoutContext context)
    {
        if (_drawn)
            return;

        _drawn = true;
        var module = available.Width / TotalModules;
        var height = Math.Min(available.Height, DefaultHeight);
        var x = QuietZoneModules * module;

        for (var i = 0; i < Widths.Length; i++)
        {
            var width = Widths[i] * module;
            if (i % 2 == 0) // even entries are bars, odd ones spaces
                context.Canvas.FillRectangle(x, 0, width, height, color);
            x += width;
        }
    }

    internal override void Reset() => _drawn = false;
}
