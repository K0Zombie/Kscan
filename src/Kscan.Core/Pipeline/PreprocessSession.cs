using Kscan.Core.Imaging;

namespace Kscan.Core.Pipeline;

/// <summary>
/// 识别页的「预处理会话」：持有原图、方法（<see cref="PreprocessRecipe"/>）、逐层图像与当前层，
/// 并提供界面要的操作（切层 / 调参 / 增删步骤 / 调顺序）。
/// <para>
/// <b>纯逻辑，不碰 UI</b>：键位（<c>J</c>/<c>K</c>/<c>H</c>/<c>L</c>/<c>PgUp</c>/<c>PgDn</c>）由 GUI 层
/// 翻译成这里的方法调用，这样导航与调参可以单测。
/// </para>
/// </summary>
public sealed class PreprocessSession
{
    private readonly List<PreprocessLevel> _levels;

    public PreprocessSession(ImageFrame source, PreprocessRecipe? recipe = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        Source = source;
        Recipe = recipe ?? PreprocessRecipe.Default;
        _levels = [.. PreprocessRunner.Levels(source, Recipe)];
        Index = 0;   // 默认停在原图：识别页一进来看到的就是原始图
    }

    /// <summary>原始图（切层不会改动它）。</summary>
    public ImageFrame Source { get; }

    /// <summary>当前使用的方法。</summary>
    public PreprocessRecipe Recipe { get; private set; }

    /// <summary>逐层图像（至少 1 层：原图）。</summary>
    public IReadOnlyList<PreprocessLevel> Levels => _levels;

    /// <summary>当前层下标（<c>0</c> = 原图）。</summary>
    public int Index { get; private set; }

    /// <summary>当前层。</summary>
    public PreprocessLevel Current => _levels[Math.Clamp(Index, 0, _levels.Count - 1)];

    /// <summary>当前层是不是原图（原图层没有可调参数）。</summary>
    public bool IsAtOriginal => Current.StepIndex < 0;

    /// <summary><c>J</c>/<c>K</c>（或 <c>↑</c>/<c>↓</c>）：切到下一层 / 上一层。到两端返回 <c>false</c>。</summary>
    public bool MoveLevel(int delta)
    {
        if (delta == 0)
        {
            return false;
        }

        var next = Math.Clamp(Index + delta, 0, _levels.Count - 1);
        if (next == Index)
        {
            return false;
        }

        Index = next;
        ReleaseDistantFrames();
        return true;
    }

    /// <summary><c>PgUp</c>（首层）/ <c>PgDn</c>（末层）。已经在该端返回 <c>false</c>。</summary>
    public bool JumpToEdge(bool last)
    {
        var next = last ? _levels.Count - 1 : 0;
        if (next == Index)
        {
            return false;
        }

        Index = next;
        ReleaseDistantFrames();
        return true;
    }

    /// <summary>
    /// 只保留"当前层 ±1"已渲染的图像，其余层丢掉（下次访问时会按需重算）。
    /// <para>
    /// 全屏一层约 8 MB；方法有 6 步时全留着要 ~50 MB。用户实际只会来回看相邻几层，
    /// 远的层重新渲染也就是几十毫秒，换来的是"打开手动处理面板不再把内存顶上去"。
    /// </para>
    /// </summary>
    private void ReleaseDistantFrames()
    {
        for (var index = 1; index < _levels.Count; index++)   // 0 是原图（就是 Source），永远不释放
        {
            if (Math.Abs(index - Index) <= 1)
            {
                continue;
            }

            _levels[index].ReleaseFrame();
        }
    }

    /// <summary>
    /// <c>H</c>/<c>L</c>（或 <c>←</c>/<c>→</c>）：调<b>当前层那一步</b>的参数（减 / 加一档）。
    /// 原图层、或参数已经到边界时返回 <c>false</c>。改完会重建逐层图像。
    /// </summary>
    public bool AdjustParameter(int delta)
    {
        if (delta == 0 || IsAtOriginal)
        {
            return false;
        }

        var stepIndex = Current.StepIndex;
        var steps = Recipe.Steps.ToList();
        if (stepIndex < 0 || stepIndex >= steps.Count)
        {
            return false;
        }

        var adjusted = Adjust(steps[stepIndex], delta);
        if (adjusted == steps[stepIndex])
        {
            return false;   // 已经到边界
        }

        steps[stepIndex] = adjusted;
        Recipe = Recipe with { Steps = steps };
        Rebuild(stepIndex);
        return true;
    }

    /// <summary>
    /// 在当前步骤<b>之后</b>插入一步（默认参数），并把视图<b>切到新加的那一步</b>。
    /// <para>
    /// 特殊情形：停在<b>原图</b>层时插到<b>链首</b> —— 插到链尾的话，前面的步骤（尤其
    /// <c>adaptive</c> 二值化）早已改变了图像性质，后面再加减噪/对比度往往一点变化都没有，
    /// 用户看到的就是"点了 `+median` 没反应"。
    /// </para>
    /// 返回新步骤在 recipe 里的下标（调用方可用它判断这一步是否真的改变了图像）。
    /// </summary>
    public int AddStep(PreprocessKind kind)
    {
        var steps = Recipe.Steps.ToList();
        var insertAt = Current.StepIndex >= 0 ? Current.StepIndex + 1 : 0;
        var index = Math.Clamp(insertAt, 0, steps.Count);

        steps.Insert(index, new PreprocessStep(kind));
        Recipe = Recipe with { Steps = steps };
        Rebuild(index);
        return index;
    }

    /// <summary>删掉当前层对应的步骤（原图层无动作）。</summary>
    public bool RemoveCurrentStep()
    {
        var stepIndex = Current.StepIndex;
        if (stepIndex < 0 || stepIndex >= Recipe.Steps.Count)
        {
            return false;
        }

        var steps = Recipe.Steps.ToList();
        steps.RemoveAt(stepIndex);
        Recipe = Recipe with { Steps = steps };
        Rebuild(stepIndex - 1);   // 落到上一步，避免"删完不知道自己在哪"
        return true;
    }

    /// <summary>把当前步骤在上一步 / 下一步之间移动（<paramref name="delta"/> = −1 / +1）。</summary>
    public bool MoveCurrentStep(int delta)
    {
        var stepIndex = Current.StepIndex;
        if (delta == 0 || stepIndex < 0)
        {
            return false;
        }

        var target = stepIndex + delta;
        if (target < 0 || target >= Recipe.Steps.Count)
        {
            return false;
        }

        var steps = Recipe.Steps.ToList();
        (steps[stepIndex], steps[target]) = (steps[target], steps[stepIndex]);
        Recipe = Recipe with { Steps = steps };
        Rebuild(target);
        return true;
    }

    /// <summary>整套换一个方法（例如用户从 <c>user/method/</c> 里挑了一个）。</summary>
    public void UseRecipe(PreprocessRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        Recipe = recipe;
        Rebuild(-1);
    }

    /// <summary>
    /// 重建逐层图像，并尽量停在<b>同一步骤</b>上。
    /// 找不到原来那一步（被删了 / 参数改到该步不再生效）时退到最接近的层。
    /// </summary>
    private void Rebuild(int preferredStepIndex)
    {
        var rebuilt = PreprocessRunner.Levels(Source, Recipe);
        _levels.Clear();
        _levels.AddRange(rebuilt);

        var target = 0;
        for (var index = 0; index < _levels.Count; index++)
        {
            if (_levels[index].StepIndex == preferredStepIndex)
            {
                target = index;
                break;
            }
        }

        Index = Math.Clamp(target, 0, _levels.Count - 1);
        ReleaseDistantFrames();
    }

    /// <summary>
    /// 每种滤镜只调一个"最有感"的参数，步进与范围写死在这里（界面不用各写一套）。
    /// </summary>
    private static PreprocessStep Adjust(PreprocessStep step, int delta) => step.Kind switch
    {
        PreprocessKind.Decolor => step with
        {
            SaturationThreshold = Math.Clamp(step.SaturationThreshold + (delta * 8), 8, 160),
        },
        PreprocessKind.Contrast => step with
        {
            ContrastTailPermille = Math.Clamp(step.ContrastTailPermille + (delta * 5), 0, 200),
        },
        PreprocessKind.Median => step with
        {
            MedianRadius = Math.Clamp(step.MedianRadius + delta, 0, 6),
        },
        PreprocessKind.Adaptive => step with
        {
            AdaptiveRadiusDivisor = Math.Clamp(step.AdaptiveRadiusDivisor + (delta * 2), 4, 48),
        },
        _ => step,
    };
}
