using Kscan.Core.Imaging;

namespace Kscan.Core.Pipeline;

/// <summary>
/// 一层预处理的结果。
/// <see cref="Label"/> 是给界面看的累积链（如 <c>原图+decolor+contrast</c>）；
/// <see cref="Suffix"/> 是给诊断用的后缀（如 <c>+decolor+contrast</c>），原图那层为 <c>null</c>。
/// <see cref="Step"/> 为 <c>null</c> 表示这就是原图那一层；
/// <see cref="StepIndex"/> 是它在 recipe 里的下标（原图为 <c>-1</c>）——
/// 界面用 <c>H</c>/<c>L</c> 调参数时要靠它回写，因为「没变化的步骤不占层」会让层序号与步骤序号错开。
/// <para>
/// <b>图像是按需渲染的</b>（<see cref="Frame"/> 第一次访问时才跑预处理），
/// 而且会话可以把离当前层较远的层<b>释放</b>掉（<see cref="ReleaseFrame"/>）：
/// 全屏 1920×1080 一层就是 ~8 MB，方法有 6 步就是 ~50 MB，没理由常年占着。
/// </para>
/// </summary>
public sealed class PreprocessLevel
{
    private readonly Func<ImageFrame>? _render;
    private readonly bool _keepFrame;
    private ImageFrame? _frame;

    /// <summary>原图那一层：直接持有原图实例（永不释放，它就是 <c>Source</c> 本身）。</summary>
    public PreprocessLevel(string label, string? suffix, PreprocessStep? step, int stepIndex, ImageFrame frame)
    {
        Label = label;
        Suffix = suffix;
        Step = step;
        StepIndex = stepIndex;
        _frame = frame;
        _keepFrame = true;
    }

    /// <summary>按需渲染的一层：<paramref name="render"/> 只在第一次读 <see cref="Frame"/> 时执行。</summary>
    public PreprocessLevel(string label, string? suffix, PreprocessStep? step, int stepIndex, Func<ImageFrame> render)
    {
        Label = label;
        Suffix = suffix;
        Step = step;
        StepIndex = stepIndex;
        _render = render;
    }

    public string Label { get; }

    public string? Suffix { get; }

    public PreprocessStep? Step { get; }

    public int StepIndex { get; }

    /// <summary>这一层的图像；没渲染过且已被释放时会就地重算。</summary>
    public ImageFrame Frame => _frame ??= _render!();

    /// <summary>当前是否已经把图像算出来了（测试与内存诊断用）。</summary>
    internal bool IsMaterialized => _frame is not null;

    /// <summary>把已经算出来的图像丢掉（下次访问重新渲染）。原图层不受影响。</summary>
    internal void ReleaseFrame()
    {
        if (!_keepFrame)
        {
            _frame = null;
        }
    }
}

/// <summary>
/// 按 recipe 逐级套用预处理，并<b>保留每一层的图像</b> ——
/// 「查看不同层级预处理后的图像」与「只对某一层再识别」都要靠它。
/// </summary>
public static class PreprocessRunner
{
    /// <summary>原图那一层的标签（界面显示用；诊断 id 用 <c>null</c> 表示不加后缀）。</summary>
    public const string OriginalLabel = "原图";

    /// <summary>
    /// 逐级处理：返回「原图 + 每个真正改变了图像的步骤」。
    /// <b>没有任何变化的步骤不占一层</b>（例如纯色图跳过对比度拉伸），
    /// 否则界面上会出现两层看起来一模一样的图。
    /// </summary>
    public static IReadOnlyList<PreprocessLevel> Levels(ImageFrame source, PreprocessRecipe? recipe = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        // 显式传进来的空 recipe = 用户把步骤全删了（只看原图），不能悄悄退回默认链
        var steps = recipe?.Steps ?? PreprocessRecipe.Default.Steps;

        // 第一遍只确认"哪些步骤真的改变了图像"（纯色图会跳过对比度拉伸之类），
        // 顺便把每层的标签算好 —— 这一遍产生的中间帧**不留**，交给 GC。
        var effective = new List<(PreprocessStep Step, int StepIndex, string Label, string? Suffix)>(steps.Count);
        var probe = source;
        var label = OriginalLabel;
        string? suffix = null;

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var next = Apply(probe, step);
            if (ReferenceEquals(next, probe))
            {
                continue;   // 该步对这张图没有作用，不占一层
            }

            probe = next;
            label = $"{label}+{step.Label}";
            suffix = $"{suffix}+{step.Label}";
            effective.Add((step, index, label, suffix));
        }

        var levels = new List<PreprocessLevel>(effective.Count + 1)
        {
            new(OriginalLabel, null, null, -1, source),
        };

        // 后面的层全部按需渲染：打开面板不再一次性把 5~6 张全屏帧留在内存里
        for (var target = 0; target < effective.Count; target++)
        {
            var levelIndex = target;
            levels.Add(new PreprocessLevel(
                effective[levelIndex].Label,
                effective[levelIndex].Suffix,
                effective[levelIndex].Step,
                effective[levelIndex].StepIndex,
                () => RenderLevel(source, effective, levelIndex)));
        }

        return levels;
    }

    /// <summary>从原图开始套到第 <paramref name="target"/> 层（含），返回那一层的帧。</summary>
    private static ImageFrame RenderLevel(
        ImageFrame source,
        List<(PreprocessStep Step, int StepIndex, string Label, string? Suffix)> effective,
        int target)
    {
        var frame = source;

        for (var index = 0; index <= target && index < effective.Count; index++)
        {
            var next = Apply(frame, effective[index].Step);
            if (ReferenceEquals(next, frame) && !ReferenceEquals(frame, source))
            {
                // 理论上不会发生（effective 里的步骤都验证过有效），保险起见不覆盖已算出的结果
                continue;
            }

            frame = next;
        }

        return frame;
    }

    /// <summary>
    /// <b>流式</b>逐层产出，<b>不保留</b>历史层 —— 给识别管线用。
    /// <para>
    /// 与 <see cref="Levels"/> 的区别是内存：界面上的「查看各层」必须把每层都留着
    /// （全屏 2560×1440 一层约 15 MB，5 层 ≈ 70 MB），而识别只需要"当前这一层"，
    /// 上一步的图丢掉就能被 GC 回收，峰值只剩原图 + 当前层。
    /// </para>
    /// </summary>
    public static IEnumerable<(string? Suffix, ImageFrame Frame)> Stream(
        ImageFrame source,
        PreprocessRecipe? recipe = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        yield return (null, source);

        var steps = recipe?.Steps ?? PreprocessRecipe.Default.Steps;
        var working = source;
        string? suffix = null;

        foreach (var step in steps)
        {
            var next = Apply(working, step);
            if (ReferenceEquals(next, working))
            {
                continue;
            }

            working = next;
            suffix = $"{suffix}+{step.Label}";
            yield return (suffix, working);
        }
    }

    /// <summary>套用一步预处理。滤镜自己判断"对这张图没意义"时会返回<b>同一个实例</b>。</summary>
    public static ImageFrame Apply(ImageFrame source, PreprocessStep step)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(step);

        return step.Kind switch
        {
            PreprocessKind.Decolor => ImageFilters.RemoveColoredOverlay(source, step.SaturationThreshold),
            PreprocessKind.Contrast => ImageFilters.StretchContrast(source, step.ContrastTailPermille),
            PreprocessKind.Median => ImageFilters.MedianBlur(source, step.MedianRadius),
            PreprocessKind.Adaptive => ImageFilters.AdaptiveThreshold(source, step.AdaptiveRadiusDivisor, step.AdaptiveBias),
            _ => source,
        };
    }
}
