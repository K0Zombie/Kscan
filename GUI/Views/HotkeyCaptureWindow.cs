using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Views;

/// <summary>
/// 按键录制窗口：让用户<b>直接按</b>要绑定的组合键，松开后显示"确认绑定 Ctrl+Alt+S？"，
/// 点「确认」才返回结果 —— 这样不会因为手一抖把某个键绑上去。
/// <para>
/// 为什么不做成"在输入框里直接按"：全局热键一旦绑错（尤其是无修饰键的字母），
/// 会把那个键从所有程序手里抢走，事后很难发现。多一步确认值得。
/// </para>
/// <para>
/// 用代码建 UI（不是 XAML）：窗口必须带"当前值/回调"才能工作，
/// 而 Avalonia 的 XAML 加载器要求窗口有公共无参构造函数（同 <see cref="RegionSelector"/>）。
/// </para>
/// </summary>
internal sealed class HotkeyCaptureWindow : Window
{
    /// <summary>只收这些键当"修饰键"，它们本身不能当主键。</summary>
    private static readonly Key[] ModifierKeys =
    [
        Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift,
        Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin,
    ];

    private readonly TaskCompletionSource<string?> _completion = new();
    private readonly TextBlock _preview;
    private readonly TextBlock _hint;
    private readonly StackPanel _actions;
    private readonly Button _confirm;
    private readonly Button _retry;
    private readonly Button _cancel;

    private HotkeyModifiers _modifiers = HotkeyModifiers.None;
    private string? _pendingKey;
    private bool _captured;
    private readonly bool _modifierOnly;
    private readonly Func<string, string?>? _describeConflict;
    private readonly TextBlock _warning;

    internal HotkeyCaptureWindow(string? current, bool modifierOnly, Func<string, string?>? describeConflict)
    {
        _modifierOnly = modifierOnly;
        _describeConflict = describeConflict;

        Title = I18n.T(modifierOnly
            ? "Str.Settings.ModifierCaptureTitle"
            : "Str.Settings.HotkeyCaptureTitle");
        Width = 460;
        Height = 210;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24));

        _preview = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Text = I18n.T("Str.Settings.HotkeyCaptureWaiting"),
        };

        _hint = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB8)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Text = current is { Length: > 0 }
                ? I18n.F("Str.Settings.HotkeyCaptureCurrent", current)
                : I18n.T("Str.Settings.HotkeyCaptureHint"),
        };

        _confirm = MakeButton("Str.Settings.HotkeyCaptureConfirm", Confirm);
        _retry = MakeButton("Str.Settings.HotkeyCaptureRetry", Retry);
        _cancel = MakeButton("Str.Settings.HotkeyCaptureCancel", Cancel);

        // 冲突提示（自家已绑定 / 被别的程序占用）：橙色，放在确认按钮上方
        _warning = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0x00)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = false,
            Children = { _retry, _confirm, _cancel },
        };

        Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _hint, _preview, _warning, _actions },
        };

        Closed += (_, _) => _completion.TrySetResult(null);
    }

    /// <summary>录制结果（已确认的组合键文本）；取消时为 <c>null</c>。</summary>
    public Task<string?> Result => _completion.Task;

    /// <summary>打开录制窗口并等用户确认。返回 <c>null</c> 表示用户取消。</summary>
    /// <param name="modifierOnly">
    /// <c>true</c> = 只录修饰键组合（如 <c>Ctrl+Alt</c>），用于"强制修饰键"这类只需要修饰键的字段。
    /// </param>
    /// <param name="describeConflict">
    /// 冲突检查：传入录制到的组合键，返回可读的冲突说明（没有冲突返回 <c>null</c>）。
    /// 窗口里只负责显示，不拦着不让确认。
    /// </param>
    public static async Task<string?> CaptureAsync(
        string? current,
        bool modifierOnly = false,
        Func<string, string?>? describeConflict = null)
    {
        // **录制期间注销全部全局热键**：否则用户按 Ctrl+F3 这类已绑定的键时，
        // 事件会先被 kscan 自己吃掉（去截图/贴图），录制窗根本收不到这次按键。
        using var suspend = GlobalHotkeyService.Current.Suspend();

        var window = new HotkeyCaptureWindow(current, modifierOnly, describeConflict);
        window.Show();
        window.Activate();

        // 等窗口真的显示出来再抢焦点：否则第一次按键会落到主窗口上
        Dispatcher.UIThread.Post(() =>
        {
            window.Activate();
            window.Focus();
        }, DispatcherPriority.Loaded);

        return await window.Result;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 已经录到组合键：Enter 确认、Esc 取消（防手误，必须显式确认）
        if (_captured)
        {
            if (e.Key == Key.Enter)
            {
                Confirm();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
            return;
        }

        // 只录修饰键：按住修饰键，全部松开即算录完
        if (_modifierOnly)
        {
            if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && _captured)
            {
                Confirm();
                e.Handled = true;
                return;
            }

            if (IsModifier(e.Key))
            {
                _modifiers = ToModifiers(e.KeyModifiers);
                _preview.Text = Format(_modifiers, null);
                e.Handled = true;
                return;
            }

            e.Handled = true;   // 只有修饰键有意义，主键忽略
            return;
        }

        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
            return;
        }

        // 只按下修饰键：先记下来，等主键
        if (IsModifier(e.Key))
        {
            _modifiers = ToModifiers(e.KeyModifiers);
            ShowPreview(e.KeyModifiers, null);
            e.Handled = true;
            return;
        }

        if (ToKeyName(e.Key) is not { } name)
        {
            _hint.Text = I18n.F("Str.Settings.KeyUnsupported", e.Key);
            e.Handled = true;
            return;
        }

        _modifiers = ToModifiers(e.KeyModifiers);
        _pendingKey = name;
        ShowPreview(e.KeyModifiers, name);
        e.Handled = true;
    }

    /// <summary>录制完成后的收尾：显示结果、查冲突、亮出确认按钮。</summary>
    private void FinishCapture(string text)
    {
        _captured = true;
        _preview.Text = text;
        _hint.Text = I18n.F("Str.Settings.HotkeyCaptureConfirmTip", text);
        _actions.IsVisible = true;

        if (_describeConflict?.Invoke(text) is { Length: > 0 } conflict)
        {
            _warning.Text = conflict;
            _warning.IsVisible = true;
        }
        else
        {
            _warning.IsVisible = false;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // 只录修饰键：最后一个修饰键也松开了，就算这一组录完
        if (_modifierOnly)
        {
            if (!_captured
                && _modifiers != HotkeyModifiers.None
                && e.KeyModifiers == KeyModifiers.None
                && IsModifier(e.Key))
            {
                FinishCapture(Format(_modifiers, null));
            }

            base.OnKeyUp(e);
            return;
        }

        if (_captured || _pendingKey is null)
        {
            base.OnKeyUp(e);
            return;
        }

        // 主键松开 = 这一组按完了。修饰键还按着也没关系（组合键就是"按住修饰键再按主键"）
        if (string.Equals(ToKeyName(e.Key), _pendingKey, StringComparison.OrdinalIgnoreCase))
        {
            FinishCapture(Format(_modifiers, _pendingKey));
        }

        base.OnKeyUp(e);
    }

    private void ShowPreview(KeyModifiers pressed, string? key)
    {
        if (key is null)
        {
            _preview.Text = DescribeModifiers(pressed) is { Length: > 0 } text
                ? text + "+…"
                : I18n.T("Str.Settings.HotkeyCaptureWaiting");
            return;
        }

        _preview.Text = Format(ToModifiers(pressed), key);
    }

    private void Confirm()
    {
        if (!_captured || (!_modifierOnly && _pendingKey is null))
        {
            return;
        }

        _completion.TrySetResult(_preview.Text);
        Close();
    }

    private void Retry()
    {
        _captured = false;
        _pendingKey = null;
        _modifiers = HotkeyModifiers.None;
        _preview.Text = I18n.T("Str.Settings.HotkeyCaptureWaiting");
        _hint.Text = I18n.T("Str.Settings.HotkeyCaptureHint");
        _actions.IsVisible = false;
        _warning.IsVisible = false;
    }

    private void Cancel()
    {
        _completion.TrySetResult(null);
        Close();
    }

    private static Button MakeButton(string i18nKey, Action action)
    {
        var button = new Button
        {
            Content = I18n.T(i18nKey),
            Padding = new Thickness(14, 4, 14, 4),
        };

        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>组合键的规范写法（修饰键顺序与 <see cref="HotkeyDefinition.ToString"/> 一致）。</summary>
    private static string Format(HotkeyModifiers modifiers, string? key)
    {
        if (key is not null)
        {
            return new HotkeyDefinition(modifiers, key).ToString();
        }

        var parts = new List<string>(4);

        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");

        return string.Join('+', parts);
    }

    private static string DescribeModifiers(KeyModifiers modifiers) => Format(ToModifiers(modifiers), null);

    private static bool IsModifier(Key key) => ModifierKeys.Contains(key);

    private static HotkeyModifiers ToModifiers(KeyModifiers modifiers)
    {
        var result = HotkeyModifiers.None;

        if (modifiers.HasFlag(KeyModifiers.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= HotkeyModifiers.Win;

        return result;
    }

    /// <summary>Avalonia 按键 → 配置里的键名（认不出返回 <c>null</c>）。</summary>
    private static string? ToKeyName(Key key)
    {
        if (IsModifier(key))
        {
            return null;
        }

        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        // 其余交给既有归一化（Escape→Esc、Return→Enter 这些别名它都认；F1..F24 也在这里）
        return HotkeyDefinition.NormalizeKey(key.ToString());
    }
}
