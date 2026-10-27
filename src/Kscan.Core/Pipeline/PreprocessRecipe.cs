namespace Kscan.Core.Pipeline;

/// <summary>一步预处理的种类。</summary>
public enum PreprocessKind
{
    /// <summary>去掉彩色叠加（亮色框线、荧光笔）。</summary>
    Decolor,

    /// <summary>按直方图两端分位拉伸对比度。</summary>
    Contrast,

    /// <summary>中值去噪。</summary>
    Median,

    /// <summary>局部均值自适应二值化（消除阴影）。</summary>
    Adaptive,
}

/// <summary>
/// 一步预处理 + 它的参数。
/// <para>
/// <b>一种 Kind 一份参数</b>：只用到与自己 <see cref="Kind"/> 对应的字段，其余保持默认。
/// 这样写进方法文件是平坦的 JSON，界面编辑也不用按 Kind 分支；
/// 代价是一个类里放了四组参数（见 <see cref="Label"/> 之外的注释）。
/// </para>
/// </summary>
public sealed record PreprocessStep(PreprocessKind Kind)
{
    /// <summary><see cref="PreprocessKind.Decolor"/>：饱和度阈值，越低去掉得越多。默认 48。</summary>
    public int SaturationThreshold { get; init; } = 48;

    /// <summary><see cref="PreprocessKind.Contrast"/>：两端各裁掉的千分比。默认 5（= 0.5%）。</summary>
    public int ContrastTailPermille { get; init; } = 5;

    /// <summary><see cref="PreprocessKind.Median"/>：中值半径，0 = 3x3、1 = 3x3? 见实现（半径 n → 边长 2n+1）。默认 1。</summary>
    public int MedianRadius { get; init; } = 1;

    /// <summary><see cref="PreprocessKind.Adaptive"/>：窗口半径除数，越大窗口越小。默认 12。</summary>
    public int AdaptiveRadiusDivisor { get; init; } = 12;

    /// <summary><see cref="PreprocessKind.Adaptive"/>：判定为前景需要的暗度偏置。默认 2。</summary>
    public int AdaptiveBias { get; init; } = 2;

    /// <summary>
    /// 稳定的短标识：写进方法文件、诊断日志与识别尝试记录，<b>不翻译</b>
    /// （界面上的中文名另由 i18n 出）。
    /// </summary>
    public string Label => Kind switch
    {
        PreprocessKind.Decolor => "decolor",
        PreprocessKind.Contrast => "contrast",
        PreprocessKind.Median => $"median{MedianRadius}",
        PreprocessKind.Adaptive => "adaptive",
        _ => "unknown",
    };
}

/// <summary>一套预处理步骤（<b>有序</b>）：这就是「方法」，可以存进 <c>user/method/</c> 分享。</summary>
public sealed record PreprocessRecipe
{
    /// <summary>历史默认链：去彩色 → 对比度 → 中值 → 局部阈值（与原 ScanPipeline 内置行为一致）。</summary>
    public static PreprocessRecipe Default { get; } = new()
    {
        Steps =
        [
            new(PreprocessKind.Decolor),
            new(PreprocessKind.Contrast),
            new(PreprocessKind.Median),
            new(PreprocessKind.Adaptive),
        ],
    };

    /// <summary>方法名（界面显示 / 保存时用）；空表示"未命名"。</summary>
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<PreprocessStep> Steps { get; init; } = [];

    /// <summary>
    /// 内容指纹：判断「这个方法是不是已经存过」。
    /// <para>
    /// 不用 <c>record</c> 自动生成的相等性 —— 它对 <see cref="IReadOnlyList{T}"/> 是<b>引用</b>相等，
    /// 两个内容相同的 recipe 会被判成不同。
    /// </para>
    /// </summary>
    public string Signature() => string.Join(
        '>',
        Steps.Select(step =>
            $"{step.Kind}:{step.SaturationThreshold},{step.ContrastTailPermille},{step.MedianRadius},{step.AdaptiveRadiusDivisor},{step.AdaptiveBias}"));

    /// <summary>两份 recipe 的步骤与参数是否完全一致（不看 <see cref="Name"/>）。</summary>
    public bool HasSameStepsAs(PreprocessRecipe other) =>
        other is not null && string.Equals(Signature(), other.Signature(), StringComparison.Ordinal);
}
