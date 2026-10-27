using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kscan.App.Services;
using Kscan.Core.Imaging;
using Kscan.Core.Model;

namespace Kscan.App.ViewModels;

/// <summary>
/// 生成页的模板项：<see cref="Key"/> 才能参与逻辑判断，<see cref="DisplayName"/> 只是给用户看的。
/// （早期版本直接拿中文显示名当标识，一切语言就会崩 —— 这里彻底分开。）
/// </summary>
public sealed record TemplateOption(string Key, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>生成页：文本 / 模板 → 条码预览 → 保存。</summary>
public sealed partial class GenerateViewModel : ObservableObject
{
    /// <summary>模板 key 的固定顺序（显示名由 i18n 提供）。</summary>
    private static readonly string[] TemplateKeys = ["Text", "Wifi", "Url", "VCard", "Geo"];

    /// <summary>当前 Logo 的像素帧（随 <see cref="LogoPath"/> 变化重新加载）；null 表示不叠加。</summary>
    private ImageFrame? _logoFrame;

    /// <summary>上一次状态的 key 与参数；为 null 表示状态是外部文本（Core 的校验消息 / 异常信息），不参与重算。</summary>
    private string? _statusKey;

    private object?[] _statusArgs = [];

    private bool _suppressTemplateRebuild;

    public GenerateViewModel()
    {
        RebuildTemplates();
        I18n.Current.Changed += (_, _) => RefreshLocalizedText();
        SetStatus("Str.Generate.Status.Hint");
        Rebuild();
    }

    /// <summary>可生成的码制（来自 Core 的清单）。</summary>
    public IReadOnlyList<string> Formats => BarcodeFormats.Writable;

    public ObservableCollection<TemplateOption> Templates { get; } = [];

    public IReadOnlyList<string> ErrorCorrections { get; } = ["L", "M", "Q", "H"];

    public IReadOnlyList<string> WifiSecurities { get; } = ["WPA", "WEP", "nopass"];

    [ObservableProperty]
    private TemplateOption _template = new("Text", "Text");

    [ObservableProperty]
    private string _text = "https://example.com";

    [ObservableProperty]
    private string _selectedFormat = "QR_CODE";

    [ObservableProperty]
    private int _size = 360;

    [ObservableProperty]
    private int _margin = 4;

    [ObservableProperty]
    private string _errorCorrection = "M";

    /// <summary>叠加到条码中心的 Logo 图片路径；空表示不叠加。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogo))]
    [NotifyPropertyChangedFor(nameof(LogoName))]
    private string? _logoPath;

    /// <summary>Logo 宽度占条码宽度的比例。</summary>
    [ObservableProperty]
    private double _logoScale = LogoOverlay.DefaultRatio;

    [ObservableProperty]
    private Bitmap? _preview;

    [ObservableProperty]
    private string _payloadPreview = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    // ---- 模板字段 ----
    [ObservableProperty]
    private string _wifiSsid = string.Empty;

    [ObservableProperty]
    private string _wifiPassword = string.Empty;

    [ObservableProperty]
    private string _wifiSecurity = "WPA";

    [ObservableProperty]
    private string _url = "example.com";

    [ObservableProperty]
    private string _vcardName = string.Empty;

    [ObservableProperty]
    private string _vcardPhone = string.Empty;

    [ObservableProperty]
    private string _vcardEmail = string.Empty;

    [ObservableProperty]
    private string _latitude = "39.9042";

    [ObservableProperty]
    private string _longitude = "116.4074";

    /// <summary>是否已选择 Logo。</summary>
    public bool HasLogo => !string.IsNullOrEmpty(LogoPath);

    /// <summary>显示用的 Logo 文件名。</summary>
    public string LogoName => string.IsNullOrEmpty(LogoPath) ? string.Empty : Path.GetFileName(LogoPath);

    public bool IsPlainText => Template.Key == "Text";

    public bool IsWifi => Template.Key == "Wifi";

    public bool IsUrl => Template.Key == "Url";

    public bool IsVCard => Template.Key == "VCard";

    public bool IsGeo => Template.Key == "Geo";

    /// <summary>把当前模板字段拼成最终要编码的内容。</summary>
    public string BuildPayload()
    {
        try
        {
            return Template.Key switch
            {
                "Wifi" when WifiSsid.Length > 0 => BarcodeContent.Wifi(new WifiCredentials(WifiSsid, WifiPassword, WifiSecurity)),
                "Wifi" => string.Empty,
                "Url" when Url.Length > 0 => BarcodeContent.Url(Url),
                "Url" => string.Empty,
                "VCard" when VcardName.Length > 0 => BarcodeContent.VCard(new VCardFields(VcardName, Phone: VcardPhone, Email: VcardEmail)),
                "VCard" => string.Empty,
                "Geo" => BarcodeContent.Geo(new GeoPoint(
                    double.Parse(Latitude, CultureInfo.InvariantCulture),
                    double.Parse(Longitude, CultureInfo.InvariantCulture))),
                _ => Text,
            };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or ArgumentOutOfRangeException)
        {
            // Core 抛出的消息是中文（CLI 也用它），GUI 这里原样透传
            SetExternalStatus(ex.Message);
            return string.Empty;
        }
    }

    /// <summary>重新生成预览。</summary>
    public void Rebuild()
    {
        var payload = BuildPayload();
        PayloadPreview = payload;

        if (string.IsNullOrEmpty(payload))
        {
            Preview = null;
            SetStatus(Template.Key is "Wifi" or "VCard"
                ? "Str.Generate.Status.NeedFields"
                : "Str.Generate.Status.Empty");
            return;
        }

        if (BarcodeEncoder.ValidateContent(SelectedFormat, payload) is { } problem)
        {
            Preview = null;
            SetExternalStatus(problem);
            return;
        }

        try
        {
            var request = new BarcodeGenerationRequest
            {
                Text = payload,
                Format = SelectedFormat,
                Width = Math.Clamp(Size, 64, 4096),
                Margin = Math.Clamp(Margin, 0, 64),
                ErrorCorrection = ErrorCorrection,
            };

            var frame = WithLogo(BarcodeRenderer.ToImageFrame(BarcodeEncoder.Encode(request), request));

            // 换预览时释放旧位图：Bitmap 持非托管像素，靠 GC 回收太慢（反复改尺寸就会一路涨）
            var previous = Preview;
            Preview = ResultActions.ToBitmap(frame);
            (previous as IDisposable)?.Dispose();
            // 用码制的技术名（与"码制"下拉里的取值一致）：Core 的 DisplayName 是中文，不参与 i18n
            // Logo 后缀同样用 Func 延迟翻译，换语言后会重新求值
            SetStatus(
                "Str.Generate.Status.SizeInfo",
                SelectedFormat,
                frame.Width,
                frame.Height,
                (Func<string>)(() => _logoFrame is null ? string.Empty : I18n.T("Str.Generate.Status.LogoApplied")));
        }
        catch (Exception ex)
        {
            Preview = null;
            SetExternalStatus(ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Preview is null)
        {
            SetStatus("Str.Generate.Status.NothingToSave");
            return;
        }

        var path = await ResultActions.PickSavePngAsync($"{SelectedFormat.ToLowerInvariant()}.png");
        if (path is null)
        {
            return;
        }

        try
        {
            string suffix;

            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                // SVG 是矢量输出，位图 Logo 无法叠加（与 CLI 的 --logo + --svg 互斥提示一致）
                var request = BuildRequest();
                await File.WriteAllTextAsync(path, BarcodeRenderer.ToSvg(BarcodeEncoder.Encode(request), request));
                suffix = HasLogo ? I18n.T("Str.Generate.Status.SvgIgnoresLogo") : string.Empty;
            }
            else
            {
                var frame = BuildFrame();
                if (frame is null)
                {
                    SetStatus("Str.Generate.Status.NothingToSave");
                    return;
                }

                await File.WriteAllBytesAsync(path, SkiaImageCodec.EncodePng(frame));
                suffix = string.Empty;
            }

            SetStatus("Str.Generate.Status.Saved", path + suffix);
        }
        catch (Exception ex)
        {
            SetStatus("Str.Generate.Status.SaveFailed", ex.Message);
        }
    }

    /// <summary>把生成的条码贴到屏幕上（无边框置顶窗口）。</summary>
    [RelayCommand]
    private void PinImage()
    {
        if (Preview is not { } preview)
        {
            SetStatus("Str.Generate.Status.NothingToCopy");
            return;
        }

        try
        {
            PinManager.Current.Pin(preview, "barcode.png");
            SetStatus("Str.Pin.Status.Pinned");
        }
        catch (Exception ex)
        {
            SetStatus("Str.Pin.Status.PinFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CopyImageAsync()
    {
        if (Preview is not { } preview)
        {
            return;
        }

        // 走 ClipboardWriter：剪贴板里同时有位图和 PNG 文件，
        // 于是既能粘到聊天窗口，也能在资源管理器里直接粘贴出图片文件
        SetStatus(await ClipboardWriter.CopyAsync(preview, "barcode")
            ? "Str.Generate.Status.ImageCopied"
            : "Str.Generate.Status.CopyFailed", string.Empty);
    }

    [RelayCommand]
    private async Task CopyPayloadAsync()
    {
        if (string.IsNullOrEmpty(PayloadPreview))
        {
            SetStatus("Str.Generate.Status.NothingToCopy");
            return;
        }

        SetStatus(await ResultActions.CopyAsync(PayloadPreview)
            ? "Str.Generate.Status.PayloadCopied"
            : "Str.Generate.Status.PayloadCopyFailed");
    }

    private BarcodeGenerationRequest BuildRequest() => new()
    {
        Text = BuildPayload(),
        Format = SelectedFormat,
        Width = Math.Clamp(Size, 64, 4096),
        Margin = Math.Clamp(Margin, 0, 64),
        ErrorCorrection = ErrorCorrection,
    };

    private ImageFrame? BuildFrame()
    {
        var request = BuildRequest();
        if (string.IsNullOrEmpty(request.Text))
        {
            return null;
        }

        return WithLogo(BarcodeRenderer.ToImageFrame(BarcodeEncoder.Encode(request), request));
    }

    /// <summary>已选 Logo 时把它的像素帧叠到条码中心；未选则原样返回。</summary>
    private ImageFrame WithLogo(ImageFrame frame) =>
        _logoFrame is null ? frame : LogoOverlay.Apply(frame, _logoFrame, Math.Clamp(LogoScale, 0.02, 0.6));

    // ---- Logo ----

    [RelayCommand]
    private async Task PickLogoAsync()
    {
        if (await ResultActions.PickImageAsync(I18n.T("Str.Generate.PickLogoTitle")) is { } path)
        {
            LogoPath = path;
        }
    }

    [RelayCommand]
    private void ClearLogo() => LogoPath = null;

    // 任意输入变化都重建预览
    partial void OnTextChanged(string value) => Rebuild();

    partial void OnSelectedFormatChanged(string value) => Rebuild();

    partial void OnSizeChanged(int value) => Rebuild();

    partial void OnMarginChanged(int value) => Rebuild();

    partial void OnErrorCorrectionChanged(string value) => Rebuild();

    partial void OnLogoScaleChanged(double value) => Rebuild();

    partial void OnLogoPathChanged(string? value)
    {
        _logoFrame = null;

        if (!string.IsNullOrEmpty(value))
        {
            try
            {
                _logoFrame = SkiaImageCodec.Load(value);
            }
            catch (Exception ex)
            {
                // 读不出来就当没选：清空路径，但把原因留在状态栏（Rebuild 会写状态，所以放在它之后）
                _logoPath = null;
                OnPropertyChanged(nameof(LogoPath));
                OnPropertyChanged(nameof(HasLogo));
                OnPropertyChanged(nameof(LogoName));
                Rebuild();
                SetStatus("Str.Generate.Status.LogoFailed", ex.Message);
                return;
            }
        }

        Rebuild();
    }

    partial void OnTemplateChanged(TemplateOption value)
    {
        OnPropertyChanged(nameof(IsPlainText));
        OnPropertyChanged(nameof(IsWifi));
        OnPropertyChanged(nameof(IsUrl));
        OnPropertyChanged(nameof(IsVCard));
        OnPropertyChanged(nameof(IsGeo));

        if (!_suppressTemplateRebuild)
        {
            Rebuild();
        }
    }

    partial void OnWifiSsidChanged(string value) => Rebuild();

    partial void OnWifiPasswordChanged(string value) => Rebuild();

    partial void OnWifiSecurityChanged(string value) => Rebuild();

    partial void OnUrlChanged(string value) => Rebuild();

    partial void OnVcardNameChanged(string value) => Rebuild();

    partial void OnVcardPhoneChanged(string value) => Rebuild();

    partial void OnVcardEmailChanged(string value) => Rebuild();

    partial void OnLatitudeChanged(string value) => Rebuild();

    partial void OnLongitudeChanged(string value) => Rebuild();

    /// <summary>记下 key 与参数，语言一变就能把状态原样重算成新语言。</summary>
    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        Status = I18n.F(key, args);
    }

    /// <summary>状态来自 Core / 异常信息（中文、不参与 i18n），换语言时保持原样。</summary>
    private void SetExternalStatus(string text)
    {
        _statusKey = null;
        _statusArgs = [];
        Status = text;
    }

    private void RefreshLocalizedText()
    {
        RebuildTemplates();

        if (_statusKey is not null)
        {
            Status = I18n.F(_statusKey, _statusArgs);
        }
    }

    /// <summary>重建模板下拉（显示名跟着语言走），并保住当前选中的模板。</summary>
    private void RebuildTemplates()
    {
        var selected = Template.Key;

        _suppressTemplateRebuild = true;
        Templates.Clear();
        foreach (var key in TemplateKeys)
        {
            Templates.Add(new TemplateOption(key, I18n.T($"Str.Generate.Template.{key}")));
        }

        var restored = Templates.FirstOrDefault(t => t.Key == selected) ?? Templates[0];
        if (!ReferenceEquals(restored, Template))
        {
            Template = restored;
        }

        _suppressTemplateRebuild = false;
    }
}
