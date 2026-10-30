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
        // Exported, axis-aligned QR images can contain thousands of false finder
        // candidates. Read their module grid directly before the general detector.
        var pure = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions
            {
                PureBarcode = true,
                TryInverted = options.TryInverted,
                PossibleFormats = [BarcodeFormat.QR_CODE],
            },
        };
        Result? direct = null;
        var formats = ToFormats(options.RestrictFormats);
        if (formats is null || formats.Contains(BarcodeFormat.QR_CODE))
        {
            var bitmap = new BinaryBitmap(new HybridBinarizer(source));
            var bounds = bitmap.BlackMatrix.getEnclosingRectangle();
            // A complete square symbol only: do not shortcut a multi-symbol image.
            if (bounds is { Length: 4 } && Math.Abs(bounds[2] - bounds[3]) <= 2)
            {
                direct = pure.Decode(source);
                direct ??= DecodeSquareGrid(bitmap.BlackMatrix, bounds);
                if (direct is not null)
                {
                    direct = new Result(direct.Text, direct.RawBytes,
                        [new ResultPoint(bounds[0], bounds[1] + bounds[3] - 1),
                         new ResultPoint(bounds[0], bounds[1]),
                         new ResultPoint(bounds[0] + bounds[2] - 1, bounds[1])],
                        direct.BarcodeFormat);
                }
            }
        }
        var raw = direct is not null ? Single(direct) : options.MultiCode
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

    private static Result? DecodeSquareGrid(BitMatrix pixels, int[] bounds)
    {
        // Screenshots may have been scaled to a non-integer module size. Infer
        // the version from the seven-module finder edge, then sample cell centres.
        var edge = 0;
        while (edge < bounds[2] && pixels[bounds[0] + edge, bounds[1]]) edge++;
        if (edge < 7 || edge >= bounds[2]) return null;
        var estimated = bounds[2] * 7.0 / edge;
        var version = (int)Math.Round((estimated - 17) / 4);
        for (var offset = -1; offset <= 1; offset++)
        {
            var candidate = version + offset;
            if (candidate is < 1 or > 40) continue;
            var dimension = 17 + 4 * candidate;
            var grid = new BitMatrix(dimension);
            for (var y = 0; y < dimension; y++)
                for (var x = 0; x < dimension; x++)
                    grid[x, y] = pixels[
                        bounds[0] + (int)((x + 0.5) * bounds[2] / dimension),
                        bounds[1] + (int)((y + 0.5) * bounds[3] / dimension)];
            var decoded = new ZXing.QrCode.Internal.Decoder().decode(grid, null);
            if (decoded is not null)
                return new Result(decoded.Text, decoded.RawBytes, [], BarcodeFormat.QR_CODE);
        }
        return null;
    }

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
