using Avalonia.Threading;
using Kscan.Core.Configuration;
using Kscan.Platform;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Services;

/// <summary>
/// 全局热键的应用级封装：配置是唯一来源 —— 谁改配置都行，这个服务订阅配置变化后自己整体重注册，
/// 并把按下事件统一投递到 UI 线程。
/// <para>
/// 排除应用：焦点落在 <see cref="AppSettings.IgnoredApps"/> 里的程序时，普通热键不触发；
/// 平台层额外注册了"原组合 + <see cref="AppSettings.ForceHotkeyModifiers"/>（默认 Ctrl+Alt）"的变体，
/// 收到这种变体（<see cref="GlobalHotkeyPressedEventArgs.Forced"/>）时照常触发。
/// </para>
/// </summary>
public sealed class GlobalHotkeyService
{
    private readonly IGlobalHotkey _hotkey;
    private readonly Func<AppSettings> _settings;
    private readonly IForegroundApp? _foreground;

    // routeId（每行唯一）→ 这一行真正对应的动作与是否绕过排除列表
    private Dictionary<string, HotkeyRoute> _routes = [];

    private volatile string? _lastSuppressedApp;

    private GlobalHotkeyService()
        : this(PlatformServices.GlobalHotkey, () => AppSettingsService.Current.Settings, PlatformServices.ForegroundApp)
    {
        AppSettingsService.Current.Changed += (_, _) => ApplyFromSettings();
        UserCommandStore.Current.Changed += (_, _) => ApplyFromSettings();
    }

    internal GlobalHotkeyService(IGlobalHotkey hotkey, Func<AppSettings> settings, IForegroundApp? foreground = null)
    {
        _hotkey = hotkey;
        _settings = settings;
        _foreground = foreground;
        _hotkey.Pressed += OnPressed;
    }

    public static GlobalHotkeyService Current { get; } = new();

    /// <summary>某个动作的热键被按下（已投递到 UI 线程）。</summary>
    public event EventHandler<GlobalHotkeyPressedEventArgs>? Triggered;
    public event Action<HotkeyBinding>? CommandTriggered;
    public event EventHandler? RegistrationChanged;
    public IReadOnlyList<string> RegisteredKeys => _hotkey.RegisteredKeys;

    public bool IsSupported => _hotkey.IsSupported;

    /// <summary>当前<b>真正生效</b>的条目（同一动作可能出现多次 —— 一个动作可以绑多个键）。</summary>
    public IReadOnlyList<GlobalHotkeyBinding> Active => _hotkey.Registered;

    /// <summary>没注册上的条目与原因（写法不对 / 被别的程序占用 / 平台不支持）。</summary>
    public IReadOnlyList<GlobalHotkeyFailure> Failures { get; private set; } = [];

    /// <summary>最近一次"因为前台应用在排除列表里而没触发"的进程名（设置页可显示，便于排查）。</summary>
    public string? LastSuppressedApp => _lastSuppressedApp;

    /// <summary>按当前配置整体重注册一次（启动时、以及配置变化后都会走这里）。</summary>
    public void ApplyFromSettings()
    {
        // 录制按键期间（Suspend 生效中）不要注册：否则用户按的组合键会被自己抢走
        if (Volatile.Read(ref _suspendDepth) > 0)
        {
            return;
        }

        var settings = _settings();
        var forceModifiers = ParseForceModifiers(settings.ForceHotkeyModifiers);

        var wanted = new List<GlobalHotkeyBinding>();
        var failures = new List<GlobalHotkeyFailure>();
        var routes = new Dictionary<string, HotkeyRoute>();

        foreach (var binding in settings.Hotkeys.Concat(UserCommandStore.Current.ToHotkeyBindings()))
        {
            if (string.IsNullOrWhiteSpace(binding.Keys))
            {
                continue;   // 键留空 = 这条不绑（界面上就是一个还没填的行）
            }

            if (!HotkeyDefinition.TryParse(binding.Keys, out var definition, out var parseError))
            {
                failures.Add(new GlobalHotkeyFailure(binding.ActionId, binding.Keys, parseError!));
                continue;
            }

            if (binding.ActionId == HotkeyActionIds.RunCommand && string.IsNullOrWhiteSpace(binding.Command))
            {
                failures.Add(new GlobalHotkeyFailure(binding.ActionId, binding.Keys, I18n.T("Str.Settings.CommandRequired")));
                continue;
            }

            // 每一行有独立的路由 id：同一动作绑多个键时，平台层的事件才能对应回正确的行
            // （多个"执行命令"行更是必须分开，否则会执行错行）。
            var routeId = $"{binding.ActionId}#{Guid.NewGuid():N}";

            // 这条热键自己已经带上了全部强制修饰键（例如 Ctrl+Alt+F5）—— 它在排除应用里也照常触发
            var bypass = forceModifiers != HotkeyModifiers.None
                && (definition!.Modifiers & forceModifiers) == forceModifiers;

            wanted.Add(new GlobalHotkeyBinding(
                routeId,
                definition!,
                bypass ? HotkeyModifiers.None : forceModifiers));
            routes[routeId] = new HotkeyRoute(binding, bypass);
        }

        // 平台层的失败原因（被占用之类）和解析错误并进同一张表；
        // 平台层只知道 routeId，这里映射回配置里的真实动作 id，界面才显示得对。
        foreach (var failure in _hotkey.Apply(wanted))
        {
            var other = wanted.FirstOrDefault(x => x.ActionId != failure.ActionId &&
                (x.Hotkey.ToString() == failure.Keys || (x.ForceModifiers != HotkeyModifiers.None &&
                new HotkeyDefinition(x.Hotkey.Modifiers | x.ForceModifiers, x.Hotkey.Key).ToString() == failure.Keys)));
            var reason = other is not null && routes.TryGetValue(other.ActionId, out var owner)
                ? I18n.F("Str.Settings.HotkeyDuplicateRegistration", I18n.T(ViewModels.HotkeyActionOption.LabelKeyFor(owner.Binding.ActionId)), failure.Keys)
                : failure.Reason;
            failures.Add(routes.TryGetValue(failure.ActionId, out var route)
                ? failure with { ActionId = route.Binding.ActionId, Reason = reason } : failure);
        }

        _routes = routes;
        Failures = failures;
        RegistrationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>取消全部注册（不碰配置）。</summary>
    public void UnregisterAll()
    {
        _hotkey.UnregisterAll();
        _routes = [];
        Failures = [];
    }

    /// <summary>
    /// 临时挂起全部全局热键（录制按键时必须这么做，否则用户按的 <c>Ctrl+F3</c> 会先被 kscan 自己吃掉）。
    /// <c>Dispose</c> 时按当前配置恢复注册；支持嵌套。
    /// </summary>
    public IDisposable Suspend()
    {
        if (Interlocked.Increment(ref _suspendDepth) == 1)
        {
            _hotkey.UnregisterAll();
        }

        return new SuspendScope(this);
    }

    /// <summary>
    /// 试注册一条热键，看它能不能用（能查出"被别的程序占用"）。
    /// 返回 <c>null</c> 表示可用，否则是可读原因。
    /// <para>
    /// 探测期间会先注销现有注册、探完再恢复 —— 所以只能在"已经挂起"的时候调用，
    /// 这也是它被录制窗口使用的原因。
    /// </para>
    /// </summary>
    public string? ProbeAvailability(string text)
    {
        if (!HotkeyDefinition.TryParse(text, out var definition, out var parseError) || definition is null)
        {
            return parseError;
        }

        if (!_hotkey.IsSupported)
        {
            return null;   // 平台不支持时无从判断，交给界面上的说明
        }

        var failures = _hotkey.Apply([new GlobalHotkeyBinding("probe", definition, HotkeyModifiers.None)]);
        _hotkey.UnregisterAll();

        if (Volatile.Read(ref _suspendDepth) == 0)
        {
            ApplyFromSettings();   // 不在挂起状态（理论上不该发生）：立刻恢复
        }

        return failures.Count == 0 ? null : failures[0].Reason;
    }

    /// <summary>挂起计数：&gt;0 时不注册任何热键。</summary>
    private int _suspendDepth;

    private void Resume()
    {
        if (Interlocked.Decrement(ref _suspendDepth) > 0)
        {
            return;
        }

        ApplyFromSettings();
    }

    private sealed class SuspendScope : IDisposable
    {
        private GlobalHotkeyService? _owner;

        public SuspendScope(GlobalHotkeyService owner) => _owner = owner;

        public void Dispose()
        {
            _owner?.Resume();
            _owner = null;
        }
    }

    /// <summary>最近一次全局热键按下的时间戳（毫秒）。用来量"按键 → 命令真正开始"的派发延迟。</summary>
    public static long LastPressedTicks { get; private set; }

    private void OnPressed(object? sender, GlobalHotkeyPressedEventArgs e)
    {
        LastPressedTicks = Environment.TickCount64;

        // 热键线程 → UI 线程
        var routes = _routes;
        if (!routes.TryGetValue(e.ActionId, out var route))
        {
            return;
        }

        // 强制变体只在配置了强制修饰键时才算数（否则排除应用里怎么按都不该触发）
        var forced = e.Forced && ParseForceModifiers(_settings().ForceHotkeyModifiers) != HotkeyModifiers.None;

        if (!forced && !route.BypassExclusion && IsSuppressedByForegroundApp())
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(routes, _routes)) return;

            if (route.Binding.ActionId == HotkeyActionIds.RunCommand) CommandTriggered?.Invoke(route.Binding);
            else Triggered?.Invoke(this, new GlobalHotkeyPressedEventArgs(route.Binding.ActionId, forced));
        });
    }

    /// <summary>
    /// 前台应用在排除列表里吗？在热键线程调用（不必等 UI 线程，晚了前台可能已经变了）。
    /// </summary>
    private bool IsSuppressedByForegroundApp()
    {
        var info = _foreground?.GetForeground();
        if (info is null)
        {
            return false;
        }

        var excluded = ExcludedAppMatcher.IsExcluded(
            _settings().IgnoredApps,
            info.ProcessName,
            info.ExecutablePath);

        _lastSuppressedApp = excluded ? info.ProcessName : null;
        return excluded;
    }

    internal static HotkeyModifiers ParseForceModifiers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return HotkeyModifiers.None;
        }

        // 借用热键解析器：追加一个假主键，只取修饰键部分（"Ctrl+Alt" → Ctrl|Alt）
        return HotkeyDefinition.TryParse(text.Trim() + "+F24", out var definition, out _)
            ? definition!.Modifiers
            : HotkeyModifiers.None;
    }

    private sealed record HotkeyRoute(HotkeyBinding Binding, bool BypassExclusion);
}
