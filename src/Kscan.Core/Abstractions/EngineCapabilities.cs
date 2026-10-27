namespace Kscan.Core.Abstractions;

/// <summary>引擎能力声明，供设置界面与降级逻辑使用。</summary>
public sealed record EngineCapabilities
{
    /// <summary>是否支持一次识别多个码。</summary>
    public bool SupportsMultiCode { get; init; }

    /// <summary>是否返回码的定位角点。</summary>
    public bool SupportsLocation { get; init; }

    /// <summary>是否依赖平台相关的 native 库（缺失时需要显式提示用户）。</summary>
    public bool RequiresNativeLibrary { get; init; }

    /// <summary>native 依赖的说明（例如需要安装的包名）。</summary>
    public string? NativeLibraryHint { get; init; }
}
