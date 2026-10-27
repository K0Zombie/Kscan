# 贴图放大窗口尺寸回归

Windows默认最大跟踪尺寸可能只等于虚拟桌面大小。Avalonia 12.1.4将该值缓存为MaxAutoSizeHint；图片仍按放大尺寸布局时，窗口客户区较小，右侧/底部就被裁掉。

PinWindow单独安装WM_GETMINMAXINFO回调，将最大跟踪尺寸设为既有8192 DIP安全上限对应的物理尺寸（原生坐标不超过32760），保留最小尺寸、最大化工作区与位置。回调不标记handled，让Avalonia同步缓存；每次应用尺寸前刷新该缓存。截图及其他窗口不安装此回调。

自动测试：PinNativeSizeLimitTests验证100%/125%/150%/200%及其他尺寸字段不变，PinRotationTests、PinSharpnessTests、PinDragTests验证旋转、清晰度和拖动回归。

原生验证项目：tests/Kscan.Pin.NativeChecks，在Windows运行。它在屏幕外创建自己的测试贴图，依次放大/缩小/放大，读取真实窗口ClientSize与Picture尺寸，结束即关闭，不操作已有贴图。

```powershell
dotnet build tests/Kscan.Pin.NativeChecks/Kscan.Pin.NativeChecks.csproj -m:1
dotnet tests/Kscan.Pin.NativeChecks/bin/Debug/net10.0/Kscan.Pin.NativeChecks.dll
```

2026-10-27实测：3200×2400 → 800×600 → 3200×2400，每一步客户区都与图片尺寸一致（实际显示器DPI=100%）；26项贴图相关测试通过。
