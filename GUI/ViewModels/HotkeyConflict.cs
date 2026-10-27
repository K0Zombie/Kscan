using Kscan.App.Services;
using Kscan.Core.Configuration;
using Kscan.Platform.Abstractions;

namespace Kscan.App.ViewModels;

/// <summary>
/// 录制热键之后的冲突检查：先看自家配置里有没有同一条绑定，
/// 再去系统里真试一次注册（能查出被别的程序占用）。
/// <para>
/// 只负责"说清楚冲突是什么"，不阻止用户确认 —— 有时用户就是想换过去。
/// </para>
/// </summary>
internal static class HotkeyConflict
{
    /// <summary>
    /// 检查 <paramref name="text"/> 能不能用作 <paramref name="selfActionId"/> 这条绑定的键。
    /// 返回可读的冲突说明；没有冲突返回 <c>null</c>。
    /// </summary>
    public static string? Describe(string text, string? selfActionId)
    {
        if (!HotkeyDefinition.TryParse(text, out var target, out _) || target is null)
        {
            return null;   // 写不对的情况由「应用」那边报
        }

        // ① 自家已有绑定：同一个键绑给别的动作会很困惑（系统只让最后注册的那个生效）
        foreach (var binding in AppSettingsService.Current.Settings.Hotkeys)
        {
            if (string.IsNullOrWhiteSpace(binding.Keys) || binding.ActionId == selfActionId)
            {
                continue;
            }

            if (HotkeyDefinition.TryParse(binding.Keys, out var existing, out _)
                && existing is not null
                && existing.Modifiers == target.Modifiers
                && string.Equals(existing.Key, target.Key, StringComparison.OrdinalIgnoreCase))
            {
                return I18n.F("Str.Settings.HotkeyConflictOwn", Label(binding.ActionId), binding.Keys);
            }
        }

        // ② 系统里被别人占用：趁全局热键处于挂起状态，真注册一次试试
        if (GlobalHotkeyService.Current.ProbeAvailability(text) is { Length: > 0 } reason)
        {
            return I18n.F("Str.Settings.HotkeyConflictOs", text, reason);
        }

        return null;
    }

    private static string Label(string actionId) => I18n.T(HotkeyActionOption.LabelKeyFor(actionId));
}
