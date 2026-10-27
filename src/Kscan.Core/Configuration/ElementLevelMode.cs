namespace Kscan.Core.Configuration;

/// <summary>
/// 框选遮罩里「子元素层级依次往上」的切换方式（任务书要求涉及的按键都可自定义）。
/// <para>语义统一为：<b>往外一层</b>（父级）/ <b>收回最内层</b>，两种方向；到窗口层后再往外不再变化。</para>
/// </summary>
public enum ElementLevelMode
{
    /// <summary>鼠标滚轮：向上滚 = 往外一层，向下滚 = 收回一层。默认。</summary>
    Wheel,

    /// <summary><c>Tab</c> = 往外一层，<c>Shift</c>+<c>Tab</c> = 收回一层。</summary>
    Tab,

    /// <summary><c>↑</c> = 往外一层，<c>↓</c> = 收回一层。</summary>
    Arrows,

    /// <summary>不绑定（层级固定在最内层）。</summary>
    None,
}

/// <summary>把配置字符串（<c>wheel</c> / <c>tab</c> / <c>arrows</c> / <c>none</c>）解析成 <see cref="ElementLevelMode"/>。</summary>
public static class ElementLevelModeParser
{
    /// <summary>
    /// 解析层级切换方式。无法识别的值（含 <c>null</c> / 空串）回落到 <see cref="ElementLevelMode.Wheel"/>，
    /// 与配置默认值一致 —— 手改配置文件写错了也不会丢掉这个功能。
    /// </summary>
    public static ElementLevelMode Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "tab" => ElementLevelMode.Tab,
        "arrows" or "arrow" or "updown" => ElementLevelMode.Arrows,
        "none" => ElementLevelMode.None,
        _ => ElementLevelMode.Wheel,
    };
}
