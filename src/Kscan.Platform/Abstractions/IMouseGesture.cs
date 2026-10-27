namespace Kscan.Platform.Abstractions;

/// <summary>鼠标手势用的按键。</summary>
public enum MouseGestureButton
{
    Left,
    Middle,
    Right,
}

/// <summary>
/// 鼠标手势配置：按住某个修饰键 + 用鼠标拖出一段距离，就当作一次"框选"手势。
/// <para>
/// 这是截图工具最顺手的触发方式（Snipaste / QQ 截图都是这个交互）：
/// Windows 上按住 <c>Win</c> 拖一下、Linux 上按住 <c>Super</c> 拖一下。
/// </para>
/// </summary>
public sealed record MouseGestureOptions
{
    /// <summary>默认手势：Win / Super + 左键拖拽。</summary>
    public static MouseGestureOptions Default { get; } = new();

    /// <summary>是否要求按住 Win 键（Linux 上即 Super）。</summary>
    public bool RequireWinKey { get; init; } = true;

    public bool RequireCtrl { get; init; }

    public bool RequireAlt { get; init; }

    public bool RequireShift { get; init; }

    public MouseGestureButton Button { get; init; } = MouseGestureButton.Left;

    /// <summary>拖动多少像素才算手势（避免把单击误判成拖拽）。</summary>
    public int DragThreshold { get; init; } = 12;

    /// <summary>给界面显示用的一句话描述。</summary>
    public string Describe()
    {
        var parts = new List<string>(4);
        if (RequireWinKey)
        {
            parts.Add(OperatingSystem.IsWindows() ? "Win" : "Super");
        }

        if (RequireCtrl)
        {
            parts.Add("Ctrl");
        }

        if (RequireAlt)
        {
            parts.Add("Alt");
        }

        if (RequireShift)
        {
            parts.Add("Shift");
        }

        var button = Button switch
        {
            MouseGestureButton.Middle => "中键",
            MouseGestureButton.Right => "右键",
            _ => "左键",
        };

        return parts.Count == 0 ? $"拖动{button}" : $"{string.Join('+', parts)} + 拖动{button}";
    }
}

/// <summary>
/// 全局鼠标手势监听。**只有 Windows 需要实现它** ——
/// Linux 上（niri/sway/Hyprland）由合成器自己的 `binds` 处理鼠标按钮，
/// 应用不该也拿不到全局鼠标钩子（Wayland 根本不提供）。
/// </summary>
public interface IMouseGesture
{
    bool IsSupported { get; }

    string Description { get; }

    /// <summary>手势完成（修饰键按住 + 拖拽超过阈值后松开）时触发。</summary>
    event EventHandler? Triggered;

    /// <summary>开始在 UI 线程上监听；返回是否成功（例如钩子安装失败）。</summary>
    bool Start(MouseGestureOptions options);

    void Stop();

    /// <summary>当前手势的显示文字（未启用时为空）。</summary>
    string CurrentGesture { get; }
}
