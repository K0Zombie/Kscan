using Kscan.Core.Configuration;
using Kscan.Platform;

namespace Kscan.App.Services;

/// <summary>
/// 配置的唯一入口：整个界面都从这里读，改完自动落盘。
/// 启动时调一次 <see cref="Load"/>（放在创建窗口之前，语言之类的设置要能被第一个窗口用上）。
/// </summary>
public sealed class AppSettingsService
{
    private static readonly Lazy<AppSettingsService> LazyInstance = new(() => new AppSettingsService(PlatformPaths.SettingsFile));

    private readonly JsonSettingsStore _store;

    private AppSettingsService(string filePath)
    {
        _store = new JsonSettingsStore(filePath);
        Settings = AppSettings.Default;
    }

    public static AppSettingsService Current => LazyInstance.Value;

    /// <summary>当前生效的配置（未被 <see cref="Load"/> 覆盖时是默认值）。</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>配置文件路径，设置页会显示出来方便排查。</summary>
    public string FilePath => _store.FilePath;

    /// <summary>最近一次读写失败的原因；正常时为 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    /// <summary>配置变化时触发（内存已经更新，磁盘写入可能还在进行）。</summary>
    public event EventHandler? Changed;

    /// <summary>从磁盘读取配置；失败时保留默认值，并把原因留在 <see cref="LastError"/>。</summary>
    public void Load()
    {
        var source = SelectStartupStore(_store.FilePath, AppContext.BaseDirectory, PlatformPaths.IsPortable);
        Settings = source.Load();
        LastError = source.LastError;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static JsonSettingsStore SelectStartupStore(string settingsFile, string applicationDirectory, bool portable)
    {
        var defaults = Path.Combine(applicationDirectory, "portable-defaults.json");
        return new JsonSettingsStore(portable && !File.Exists(settingsFile) && File.Exists(defaults) ? defaults : settingsFile);
    }

    /// <summary>改一项配置并立即落盘。值没变则什么都不做。</summary>
    public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        var next = mutate(Settings);
        if (next == Settings)
        {
            return;
        }

        Settings = next;
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            await _store.SaveAsync(next, cancellationToken);
            LastError = null;
        }
        catch (Exception ex)
        {
            // 写盘失败不影响本次会话里的行为，但要让设置页能显示出来
            LastError = ex.Message;
        }
    }
}
