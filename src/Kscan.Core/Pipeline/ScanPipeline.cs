using System.Diagnostics;
using Kscan.Core.Abstractions;
using Kscan.Core.Engines;
using Kscan.Core.Imaging;
using Kscan.Core.Model;

namespace Kscan.Core.Pipeline;

/// <summary>单个引擎在本次识别中的表现，用于诊断与耗时展示。</summary>
public sealed record EngineAttempt(string EngineId, bool Succeeded, long ElapsedMs, string? Error);

/// <summary>一次识别的完整结果。</summary>
public sealed record ScanOutcome(IReadOnlyList<ScanResult> Results, IReadOnlyList<EngineAttempt> Attempts)
{
    public bool Success => Results.Count > 0;

    public long TotalMs { get; init; }

    public string? FirstText => Results.Count > 0 ? Results[0].Text : null;
}

/// <summary>
/// 识别管线：按注册顺序依次尝试可用引擎，**第一个出结果的引擎即返回**
/// （即回退链）。这样后续加入 native 引擎（zbar / OpenCV）时无需改动调用方。
/// </summary>
public sealed class ScanPipeline
{
    private readonly List<IBarcodeEngine> _engines;

    public ScanPipeline(IEnumerable<IBarcodeEngine> engines)
    {
        ArgumentNullException.ThrowIfNull(engines);
        _engines = [.. engines.Where(e => e.IsAvailable)];
    }

    /// <summary>当前环境下的默认管线（仅纯托管的 ZXing 引擎）。</summary>
    public static ScanPipeline CreateDefault() => new([new ZxingEngine()]);

    public IReadOnlyList<IBarcodeEngine> Engines => _engines;

    public ScanOutcome Scan(ImageFrame frame, ScanOptions? options = null, PreprocessRecipe? recipe = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        options ??= ScanOptions.Default;

        var attempts = new List<EngineAttempt>(_engines.Count);
        var stopwatch = Stopwatch.StartNew();
        if (_engines.Count == 0) return new ScanOutcome([], attempts);

        // 两层回退：外层为渐进式预处理，内层为引擎链；每一步仅在前面全部失败后才执行。
        // 只在前面全部失败后才做下一步，所以正常情况下一次就返回。
        // 用 Stream 而不是 Levels：识别不需要保留历史层，峰值从"整条链"降到"原图 + 当前层"。
        foreach (var (suffix, variant) in LevelsFor(frame, options, recipe))
        {
            foreach (var engine in _engines)
            {
                var engineStopwatch = Stopwatch.StartNew();
                try
                {
                    var results = engine.Decode(variant, options);
                    engineStopwatch.Stop();

                    var id = suffix is null ? engine.Id : $"{engine.Id}{suffix}";
                    attempts.Add(new EngineAttempt(id, results.Count > 0, engineStopwatch.ElapsedMilliseconds, null));

                    if (results.Count > 0)
                    {
                        stopwatch.Stop();
                        return new ScanOutcome(results, attempts) { TotalMs = stopwatch.ElapsedMilliseconds };
                    }
                }
                catch (Exception ex)
                {
                    engineStopwatch.Stop();
                    var id = suffix is null ? engine.Id : $"{engine.Id}{suffix}";
                    attempts.Add(new EngineAttempt(id, false, engineStopwatch.ElapsedMilliseconds, ex.Message));
                }
            }
        }

        stopwatch.Stop();
        return new ScanOutcome([], attempts) { TotalMs = stopwatch.ElapsedMilliseconds };
    }

    /// <summary>
    /// 要尝试的图像版本：原图 + recipe 里真正改变了图像的步骤（<b>流式、不保留历史层</b>）。
    /// <c>TryDenoise</c> 关掉时只试原图（这时 recipe 不起作用）。
    /// </summary>
    private static IEnumerable<(string? Suffix, ImageFrame Frame)> LevelsFor(
        ImageFrame frame,
        ScanOptions options,
        PreprocessRecipe? recipe) =>
        options.TryDenoise
            ? PreprocessRunner.Stream(frame, recipe)
            : [(null, frame)];
}
