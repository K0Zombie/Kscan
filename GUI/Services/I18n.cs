using System.Globalization;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;

namespace Kscan.App.Services;

/// <summary>
/// 界面语言：资源字典（<c>Assets/i18n/*.axaml</c>）+ 运行时替换 <c>MergedDictionaries</c>。
/// </summary>
/// <remarks>
/// 分工是刻意的：
/// <list type="bullet">
/// <item>XAML 里一律用 <c>{DynamicResource Str.Xxx}</c> —— 换语言时字典一换，绑定自动刷新，不用重建窗口；</item>
/// <item>代码里（状态栏、错误提示）用 <see cref="T"/> / <see cref="F"/>，这些是命令执行时算好的字符串，
/// 换语言<b>不会</b>自动重算，所以相关的 ViewModel 要订阅 <see cref="Changed"/> 主动刷新。</item>
/// </list>
/// </remarks>
public sealed class I18n
{
    public const string Chinese = "zh-CN";

    public const string English = "en-US";

    private static I18n? _instance;

    private ResourceDictionary? _dictionary;

    private I18n()
    {
    }

    public static I18n Current => _instance ??= new I18n();

    /// <summary>当前生效的语言（始终是 <see cref="Chinese"/> 或 <see cref="English"/>）。</summary>
    public string Language { get; private set; } = Chinese;

    /// <summary>语言变化后触发（字典已经换好，DynamicResource 会自动刷新）。</summary>
    public event EventHandler? Changed;

    /// <summary>可选语言：空串表示"跟随系统"。</summary>
    public static IReadOnlyList<string> AvailableLanguages { get; } = ["", Chinese, English];

    /// <summary>系统区域对应的语言。</summary>
    public static string SystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : English;

    /// <summary>取一条界面文案；缺 key 时返回 key 本身（便于一眼看出漏翻）。</summary>
    public static string T(string key) => Text(key);

    /// <summary>
    /// 取一条带参数的文案（资源里写 <c>{0}</c> 这样的占位符）。
    /// 参数可以是 <c>Func&lt;string&gt;</c>：格式化时才求值 —— 状态栏这类"算好的字符串"里若嵌了
    /// 另一条翻译（例如"识别到 N 个码"后面的"已复制到剪贴板"），换语言重算时才会拿到新语言，
    /// 否则那段文本会在参数里被固化。
    /// </summary>
    public static string F(string key, params object?[] args)
    {
        // 注意：不要就地改写入参数组 —— 调用方（如状态栏）会把参数存起来以便换语言时重算，
        // 一旦在这里把 Func 换成求值结果，重算就拿不到新语言的文本了。
        var resolved = new object?[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            resolved[i] = args[i] is Func<string> deferred ? deferred() : args[i];
        }

        return string.Format(CultureInfo.InvariantCulture, Text(key), resolved);
    }

    /// <summary>把配置里的语言取值归一成实际语言（<c>null</c> / 空 / 未知值 → 跟随系统）。</summary>
    public static string Normalize(string? language) =>
        string.IsNullOrWhiteSpace(language)
            ? SystemLanguage()
            : language.Trim().ToLowerInvariant() switch
            {
                "zh" or "zh-cn" or "zh-hans" or "zh-sg" => Chinese,
                "en" or "en-us" or "en-gb" => English,
                _ => SystemLanguage(),
            };

    /// <summary>
    /// 应用语言：换掉资源字典。传 <c>null</c> / 空串表示跟随系统区域。
    /// 必须在 <c>Application.Current</c> 就绪之后、创建窗口之前调用一次。
    /// </summary>
    public void Apply(string? language)
    {
        var target = Normalize(language);

        if (Application.Current is { } app)
        {
            var merged = app.Resources.MergedDictionaries;
            var dictionary = LoadDictionary(target);

            if (_dictionary is not null)
            {
                merged.Remove(_dictionary);
                _dictionary = null;
            }

            if (dictionary is not null)
            {
                merged.Add(dictionary);
                _dictionary = dictionary;
            }
        }

        if (Language == target)
        {
            return;
        }

        Language = target;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Text(string key) =>
        Application.Current is { } app && app.TryFindResource(key, out var value) && value is string text
            ? text
            : key;

    private static ResourceDictionary? LoadDictionary(string language)
    {
        var assembly = typeof(I18n).Assembly.GetName().Name;

        try
        {
            return AvaloniaXamlLoader.Load(new Uri($"avares://{assembly}/Assets/i18n/{language.ToLowerInvariant()}.axaml"))
                as ResourceDictionary;
        }
        catch (Exception)
        {
            // 字典缺失/写坏时不要连界面都起不来：T() 会退化成返回 key
            return null;
        }
    }
}
