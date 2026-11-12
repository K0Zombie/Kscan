using Kscan.Core.Configuration;

namespace Kscan.Platform.Abstractions;

/// <summary>某个动作的全局热键被按下。</summary>
public sealed class GlobalHotkeyPressedEventArgs(string actionId, bool forced = false) : EventArgs
{
    /// <summary>
    /// 动作 id，取值见 <see cref="HotkeyActionIds"/> —— 那份定义放在 Core，
    /// 因为它同时是配置字段名与界面行序的来源，只有一处定义才不会漂移。
    /// </summary>
    public string ActionId { get; } = actionId;

    /// <summary>
    /// 按下的这条是"带强制修饰键"的变体（例如热键是 <c>F2</c>、用户按了 <c>Ctrl+Alt+F2</c>）。
    /// 即使前台应用在排除列表里，这类事件也应该触发。
    /// </summary>
    public bool Forced { get; } = forced;
}

/// <summary>
/// 一条（待注册或已注册的）热键：动作 + 已解析的热键。
/// <para>
/// 是<b>一对一条目</b>而不是"动作 → 一条热键"的映射：同一个动作可以绑多个键
/// （例如框选既绑 <c>F1</c> 又绑 <c>Ctrl+Alt+S</c>），两条互不影响 —— 一条被占用时另一条照常生效。
/// </para>
/// </summary>
public sealed record GlobalHotkeyBinding(
    string ActionId,
    HotkeyDefinition Hotkey,
    HotkeyModifiers ForceModifiers = HotkeyModifiers.None);

/// <summary>一条没注册上的热键：动作 + 用户写的键 + 可读原因（写法不对 / 被占用 / 平台不支持）。</summary>
public sealed record GlobalHotkeyFailure(string ActionId, string Keys, string Reason);

/// <summary>
/// 全局热键：一个应用可以绑多条，不管哪个窗口在前台都生效。
/// <para>
/// Windows 用 <c>RegisterHotKey</c>；Wayland 这边<b>没有</b>等价能力 ——
/// niri 与 <c>xdg-desktop-portal-wlr</c> 都未实现 <c>GlobalShortcuts</c> portal（见 <c>DESIGN.md</c> §3），
/// 所以 Linux 侧一律降级为"不支持"，改由 niri 的 <c>binds</c> 把键绑到 <c>kscan</c> CLI。
/// </para>
/// </summary>
public interface IGlobalHotkey
{
    bool IsSupported { get; }

    /// <summary>给界面显示的一句话说明。</summary>
    string Description { get; }

    /// <summary>i18n key（界面优先用它）。</summary>
    string? DescriptionKey { get; }

    /// <summary>
    /// 某条热键被按下。**可能来自非 UI 线程**（实现方自己起消息循环），
    /// 订阅方负责投递到 UI 线程。
    /// </summary>
    event EventHandler<GlobalHotkeyPressedEventArgs>? Pressed;

    /// <summary>当前<b>真正注册成功</b>的条目（顺序同传入顺序；同一动作可能出现多次）。</summary>
    IReadOnlyList<GlobalHotkeyBinding> Registered { get; }
    IReadOnlyList<string> RegisteredKeys => Registered.Select(x => x.Hotkey.ToString()).ToArray();

    /// <summary>
    /// 用一批热键整体替换当前注册（先全部注销）。返回**注册失败的条目与可读原因**；
    /// 成功的条目不出现在返回值里。整批替换而不是逐条增删，是为了让"配置是唯一来源"这件事成立。
    /// </summary>
    IReadOnlyList<GlobalHotkeyFailure> Apply(IReadOnlyList<GlobalHotkeyBinding> hotkeys);

    /// <summary>全部注销（未注册时是空操作）。</summary>
    void UnregisterAll();
}
