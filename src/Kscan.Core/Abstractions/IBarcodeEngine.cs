using Kscan.Core.Imaging;
using Kscan.Core.Model;

namespace Kscan.Core.Abstractions;

/// <summary>
/// 一个条码/二维码识别引擎。新增引擎只需实现本接口并注册进
/// <see cref="Pipeline.ScanPipeline"/>，不需要改动识别流程。
/// </summary>
public interface IBarcodeEngine
{
    /// <summary>稳定标识，用于配置与诊断（例如 <c>zxing</c>）。</summary>
    string Id { get; }

    /// <summary>界面显示名。</summary>
    string DisplayName { get; }

    /// <summary>当前环境下是否可用（native 缺失、平台不支持时为 <c>false</c>）。</summary>
    bool IsAvailable { get; }

    EngineCapabilities Capabilities { get; }

    /// <summary>
    /// 识别一帧图像。没有任何码时返回空集合，**不要**返回 <c>null</c>；
    /// 引擎内部错误可抛异常，由管线捕获并记入诊断。
    /// </summary>
    IReadOnlyList<ScanResult> Decode(ImageFrame frame, ScanOptions options);
}
