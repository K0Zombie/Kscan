using System.Diagnostics;
using Kscan.Core.Abstractions;
using Kscan.Core.Imaging;
using Kscan.Core.Model;
using ZXing;
using ZXing.Common;

namespace Kscan.Core.Engines;

/// <summary>
/// ZXing.Net 引擎：纯托管、无 native 依赖，Windows 与 Linux 行为一致，
/// 因此是默认也是主力引擎。
/// </summary>
public sealed class ZxingEngine : IBarcodeEngine
{
    public string Id => "zxing";

    public string DisplayName => "ZXing.Net";

    public bool IsAvailable => true;

    public EngineCapabilities Capabilities { get; } = new()
    {
        SupportsMultiCode = true,
        SupportsLocation = true,
        RequiresNativeLibrary = false,
    };

    public IReadOnlyList<ScanResult> Decode(ImageFrame frame, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        options ??= ScanOptions.Default;

        var source = new RGBLuminanceSource(
            frame.Pixels, frame.Width, frame.Height, RGBLuminanceSource.BitmapFormat.BGRA32);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = options.TryHarder,
                TryInverted = options.TryInverted,
                PossibleFormats = ToFormats(options.RestrictFormats),
            },
        };

        var stopwatch = Stopwatch.StartNew();
        var raw = options.MultiCode
            ? reader.DecodeMultiple(source)
            : Single(reader.Decode(source));
        stopwatch.Stop();

        if (raw is null || raw.Length == 0)
        {
            return [];
        }

        var seen = options.IgnoreDuplicates ? new HashSet<string>(StringComparer.Ordinal) : null;
        var results = new List<ScanResult>(raw.Length);

        foreach (var result in raw)
        {
            if (result is null || string.IsNullOrEmpty(result.Text))
            {
                continue;
            }

            if (seen is not null && !seen.Add(result.Text))
            {
                continue;
            }

            results.Add(new ScanResult
            {
                Text = result.Text,
                Format = result.BarcodeFormat.ToString(),
                Points = result.ResultPoints is null
                    ? []
                    : [.. result.ResultPoints.OfType<ResultPoint>().Select(p => new PointD(p.X, p.Y))],
                EngineId = Id,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            });
        }

        return results;
    }

    private static Result[]? Single(Result? result) => result is null ? null : [result];

    private static List<BarcodeFormat>? ToFormats(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0)
        {
            return null;
        }

        var formats = new List<BarcodeFormat>(names.Count);
        foreach (var name in names)
        {
            if (Enum.TryParse<BarcodeFormat>(name, ignoreCase: true, out var format))
            {
                formats.Add(format);
            }
        }

        return formats.Count == 0 ? null : formats;
    }
}
