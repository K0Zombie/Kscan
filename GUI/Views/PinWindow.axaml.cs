using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Kscan.App.Services;
using Kscan.Core.Imaging;
using Kscan.Platform;

namespace Kscan.App.Views;

/// <summary>
/// 贴图窗口：无边框 + 置顶，显示一张图。
/// <para>
/// 交互：拖动移动（按物理像素换算，HiDPI 下不跑偏）、滚轮以光标为中心缩放、
/// Ctrl+滚轮调透明度、双击关闭、右键菜单（复制 / 另存为 / 原始尺寸 / 识别 / 穿透 / 关闭）。
/// </para>
/// <para>
/// 用无参构造 + <see cref="SetImage"/> 而不是构造参数：Avalonia 的 XAML 加载器要求窗口有公共无参构造函数，
/// 否则会报 AVLN3001（"won't be reachable via runtime loader"）。
/// </para>
/// </summary>
public partial class PinWindow : Window
{
    private const double MinSize = 40;

    /// <summary>兜底上限（逻辑单位）：拿不到屏幕信息时用。</summary>
    private const double MaxSize = 8192;

    /// <summary>
    /// 缩放上限（相对原图尺寸的倍数）。用固定倍数而不是"屏幕工作区"，缩放才可逆：
    /// 缩到很小之后一定能原路放大回来，不会被"当前尺寸夹取"吃掉基准。
    /// </summary>
    private const double MaxZoomIn = 8;

    private const double ZoomStep = 1.1;

    private Bitmap? _image;

    /// <summary>
    /// 我们自己维护的"当前窗口尺寸"（逻辑单位，= 旋转后的外接矩形）。
    /// <para>
    /// 不直接读 <c>Width</c>/<c>Height</c>（或 <c>Bounds</c>）：布局是异步的，连续滚轮时可能读到上一帧的值；
    /// 更要命的是**窗口一旦撞上系统的尺寸上限**（Windows 默认不让窗口超过屏幕 + 边框），
    /// Avalonia 的属性值就与真实窗口对不上了 —— 再按属性算，"以光标为不动点"会越来越偏，
    /// 实测连续放大 30 格后窗口直接跑到屏幕外（y = -1220），看起来就是疯狂乱跳/闪。
    /// </para>
    /// <para>
    /// 唯一来源是 <see cref="_zoom"/> × 原图像素 + 当前朝向（<see cref="DisplaySize"/>），
    /// 绝不从"当前位图尺寸"反推 —— 旋转 90° 会交换宽高，反推会把缩放算错。
    /// </para>
    /// </summary>
    private double _width;

    private double _height;

    /// <summary>缩放上限（逻辑单位）= 当前屏幕的工作区。超过它只会撞系统上限、把位置算乱。</summary>
    private double _maxWidth = MaxSize;

    private double _maxHeight = MaxSize;

    private string? _sourceName;

    private Point _grab;

    private bool _dragging;

    /// <summary>待应用的缩放（把一帧内收到的多格滚轮合并成一次窗口操作）。</summary>
    private double _pendingZoom = 1;

    private Point _pendingAnchor;

    private bool _zoomQueued;

    private bool _ownsImage;

    /// <summary>要落到的屏幕区域（物理像素）；<c>null</c> = 默认屏幕中央。</summary>
    private PixelRect? _targetRegion;
    private double? _scalingOverride;

    /// <summary>当前旋转角度（顺时针为正，0..360）。</summary>
    private double _rotation;

    private bool _mirrorH;
    private bool _mirrorV;

    /// <summary>
    /// 当前显示的位图（已经烘焙好朝向）。
    /// <c>null</c> 表示"就是原图"（没转向时不做多余拷贝）。
    /// </summary>
    private Bitmap? _oriented;

    /// <summary>原图的帧缓存（转向时反复重算用，避免每次编解码 PNG）。</summary>
    private ImageFrame? _sourceFrame;

    /// <summary>
    /// 当前缩放：每个<b>原图</b>像素占多少 DIP。窗口尺寸永远是
    /// <c>原图像素 × _zoom</c> 再按朝向取外接矩形（见 <see cref="DisplaySize"/>）。
    /// <para>
    /// 基准钉死在<b>原图</b>上：以前这里是"每个显示位图像素占多少 DIP"，旋转 90° 后
    /// 位图宽高互换，同一个字段表达的物理含义就变了 —— 转几次之后图会被拉变形、尺寸也漂。
    /// </para>
    /// </summary>
    private double _zoom = 1;

    /// <summary>
    /// 缩放基准（"刚打开时"的 <see cref="_zoom"/>）。放大上限 = 它 × <see cref="MaxZoomIn"/>，
    /// 这样缩到很小之后一定能原路放大回来，不会被"当前尺寸夹取"吃掉基准。
    /// "原始尺寸"菜单会把它重置成 1:1（一个图像像素对一个屏幕物理像素）。
    /// </summary>
    private double _zoomRef = 1;

    /// <summary>
    /// 窗口<b>视觉中心</b>的屏幕物理坐标（双精度），是位置计算的唯一基准。
    /// <para>
    /// 只在"我们主动摆位"时更新（打开、拖动、缩放、旋转），<b>绝不</b>由
    /// <c>Position + Width/2</c> 反算：<c>Position</c> 是整数物理像素，
    /// 每次反算最多差 0.5px，连按 Q/E 就会一路累积 —— 这就是"越转越偏"的根。
    /// </para>
    /// </summary>
    private double _anchorX;

    private double _anchorY;

    /// <summary>建立锚点时的屏幕缩放。缩放变了（窗口被拖到别的屏）锚点就失效，必须先重新同步。</summary>
    private double _anchorScaling = -1;

    private bool _anchorValid;

    /// <summary>是否正在用「Alt + 拖动」自由旋转。</summary>
    private bool _rotating;

    /// <summary>拖动中的目标角度（松手才烘焙成像素，拖动期间只做几何变换）。</summary>
    private double _previewRotation;

    private double _rotateStartAngle;
    private double _rotateStartRotation;
    private DispatcherTimer? _hintTimer;

    /// <summary>当前旋转角度（顺时针为正）。</summary>
    public double RotationDegrees => _rotation;

    public bool IsMirroredHorizontally => _mirrorH;

    public bool IsMirroredVertically => _mirrorV;

    /// <summary>所属分组 id；<c>null</c> = 未分组。</summary>
    public string? GroupId { get; private set; }

    /// <summary>来源文件名（"另存为"的默认名，也是管理窗口缩略图下的标签）。</summary>
    public string? SourceName => _sourceName;

    /// <summary>
    /// 原图（未旋转/未镜像）。分组库保存要用它：存"原图 + 旋转角度"才能在恢复后继续旋转，
    /// 直接存烘焙后的位图会失去方向信息。
    /// </summary>
    public Bitmap? OriginalBitmap => _image;

    /// <summary>
    /// 这张贴图在分组库里的文件路径（<c>data\user\pins\&lt;分组&gt;\xxx.png</c>）。
    /// <c>null</c> = 没进库（未分组，或刚从剪贴板贴出来还没归组）。
    /// </summary>
    public string? LibraryPath { get; internal set; }
    // 移出分组后仍保留原文件引用，重新归组/拖出动图时不能退化成首帧 PNG。
    internal string? RetainedSourcePath { get; set; }
    internal string? ImageFilePath => LibraryPath ?? RetainedSourcePath;

    /// <summary>
    /// 任何"需要落盘/刷新"的状态变了（位置/尺寸/旋转/镜像/透明度/置顶/穿透）。
    /// 分组库靠它做防抖落盘。
    /// </summary>
    public event EventHandler? StateChanged;

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Delete 第一次按下的时间（5 秒内再按一次才算确认）。</summary>
    private DateTime _deleteArmedAt = DateTime.MinValue;

    /// <summary>是否已经在"Delete 确认态"（刚刚提示过、还没超时）。</summary>
    public bool IsDeleteArmed => DateTime.UtcNow - _deleteArmedAt < TimeSpan.FromSeconds(5);

    /// <summary>第一次按 Delete：不删，只提示"再按一次就会彻底删除"。</summary>
    public void ArmDeleteHint()
    {
        _deleteArmedAt = DateTime.UtcNow;
        ShowHint(I18n.T("Str.Pin.DeleteHint"), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// 亮起/熄灭高亮边框。管理窗口里悬停某张缩略图时用，方便在屏幕上认出"是哪一张"。
    /// <para>
    /// 高亮期间这张贴图会：抬到最上层（含管理窗口之上）、半透明、鼠标穿透 ——
    /// 这样即使管理窗口挡着它，也能看见整张图而不被窗口遮住，也不会挡住你点管理窗口。
    /// 取消高亮时把这三项都恢复原值。
    /// </para>
    /// </summary>
    /// <summary>
    /// 设置悬停高亮：画一圈边框 + 一层同色的半透明蒙版，颜色与截图时的识别高亮一致。
    /// <para>
    /// <b>不动</b>透明度、<b>不动</b>穿透、<b>不动</b>层级 —— 悬停只是"让你在屏幕上认出是哪一张"，
    /// 之前那套"浮到管理窗口上面"的做法在多窗口 z-order 上做不到稳定，已经取消。
    /// </para>
    /// </summary>
    /// <param name="groupLevel">
    /// <c>true</c> = 整组预览（用"窗口识别"的黄色）；<c>false</c> = 单张预览（用"子元素识别"的橙色）。
    /// </param>
    public void SetHighlighted(bool highlighted, bool groupLevel = false)
    {
        if (_highlighted == highlighted && _highlightGroupLevel == groupLevel)
        {
            return;
        }

        _highlighted = highlighted;
        _highlightGroupLevel = groupLevel;
        HighlightFrame.IsVisible = highlighted;

        if (!highlighted)
        {
            // 顶层态（绿框）在悬停结束后要回显
            if (IsRaised)
            {
                RaisedFrame.IsVisible = true;
            }

            return;
        }

        // 有绿框时先让橙/黄框盖住它，移开后再回显
        if (IsRaised)
        {
            RaisedFrame.IsVisible = false;
        }

        // 与 RegionSelector 里的一致：窗口识别 #FFCC00（20α 填充）、子元素识别 #FF8C00（28α 填充）
        var color = groupLevel
            ? Color.FromRgb(0xFF, 0xCC, 0x00)
            : Color.FromRgb(0xFF, 0x8C, 0x00);

        var alpha = groupLevel ? (byte)20 : (byte)28;

        HighlightFrame.BorderBrush = new SolidColorBrush(color);
        HighlightFrame.Background = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private bool _highlightGroupLevel;

    /// <summary>
    /// 是否处于「被管理窗口点击提到最前」的持久状态。
    /// 与悬停无关：鼠标移开不会掉回下层，也不会变透明。
    /// </summary>
    public bool IsRaised { get; private set; }

    private bool _raiseSaved;
    private double _opacityBeforeRaise = 1;
    private bool _clickThroughBeforeRaise;

    /// <summary>
    /// 进入顶层态：置顶 + 取消穿透 + 恢复"原本的透明度"，并点亮绿色边框。
    /// 原本的透明度取进入时的值（用户用 Ctrl+滚轮调过就保留）。
    /// </summary>
    public void EnterRaised()
    {
        if (IsRaised)
        {
            return;
        }

        // 顺序很关键（这里踩过坑）：
        //   1. **先**解除悬停高亮 —— 此时 IsRaised 还是 false，才能真的把透明度还原成它自己设定的值；
        //   2. **再**把该值记成"原值"；
        //   3. 最后才置 IsRaised = true。
        // 如果先把 IsRaised 置成 true，SetHighlighted(false) 会走"顶层态分支"直接 return，
        // 透明度就停在 0.45 —— 现象是"单击提上去的贴图一直是半透明的"。
        SetHighlighted(false);

        if (!_raiseSaved)
        {
            _opacityBeforeRaise = Opacity;
            _clickThroughBeforeRaise = IsClickThrough;
            _raiseSaved = true;
        }

        IsRaised = true;

        Opacity = _opacityBeforeRaise;

        if (IsClickThrough)
        {
            SetClickThrough(false);
        }

        SetTopmost(true);

        HighlightFrame.IsVisible = false;
        RaisedFrame.IsVisible = true;

        GuiLog.Write($"z-order: pin raised (opacity={Opacity:0.##}, clickThrough={IsClickThrough})");
    }

    /// <summary>退出顶层态：回到管理窗口下面，透明度与穿透恢复成进入前的状态。</summary>
    public void ExitRaised()
    {
        if (!IsRaised)
        {
            return;
        }

        IsRaised = false;
        RaisedFrame.IsVisible = false;

        if (_raiseSaved)
        {
            Opacity = _opacityBeforeRaise;

            if (_clickThroughBeforeRaise != IsClickThrough)
            {
                SetClickThrough(_clickThroughBeforeRaise);
            }

            _raiseSaved = false;
        }

        // 管理窗口还在时压回它下面；不在时就按普通窗口层级待着
        SetTopmost(false);

        GuiLog.Write($"z-order: pin lowered (opacity={Opacity:0.##}, clickThrough={IsClickThrough})");
    }

    /// <summary>是否处于悬停高亮状态。</summary>
    public bool IsHighlighted => _highlighted;

    private bool _highlighted;

    /// <summary>
    /// 归到某个分组（<c>null</c> = 移出分组）。
    /// <para>
    /// 分过组的贴图会立刻落进分组库（PNG 写进该分组的目录 + 参数记进 library.json）；
    /// 移出分组 = 从库里删掉那张文件（屏幕上的贴图还在，只是不再保存）。
    /// </para>
    /// </summary>
    public void SetGroup(string? groupId)
    {
        if (string.Equals(GroupId, groupId, StringComparison.Ordinal))
        {
            return;
        }

        var previous = GroupId;
        GroupId = groupId;

        try
        {
            PinLibraryService.Attach(this, previous, groupId);
        }
        catch (Exception ex)
        {
            GroupId = previous;
            // 写库失败（磁盘满/文件被占用）不能把界面带崩：贴图照常显示，只是没进库
            GuiLog.Write($"library: 归组失败（{ex.GetType().Name}: {ex.Message}）");
        }

        ScheduleMenuRebuild();
        PinGroupStore.Current.RaiseChanged();
    }

    /// <summary>
    /// 只改内存里的分组标记、<b>不</b>倒腾库文件（从库里贴回来时用：文件本来就在那儿）。
    /// </summary>
    internal void SetGroupSilently(string? groupId)
    {
        GroupId = groupId;
        ScheduleMenuRebuild();
    }

    /// <summary>从库里的某一项贴回屏幕：认领文件路径、分组、朝向、透明度与层级。</summary>
    internal void AttachToLibrary(PinLibraryItem item)
    {
        LibraryPath = item.FullPath;
        SetGroupSilently(item.GroupId);

        var entry = item.Entry;

        SetTransform(entry.Rotation, entry.MirrorH, entry.MirrorV);
        Opacity = Math.Clamp(entry.Opacity, 0.1, 1.0);
        SetTopmost(entry.Topmost);

        if (entry.ClickThrough)
        {
            SetClickThrough(true);
        }
    }

    /// <summary>
    /// 延后重建右键菜单。
    /// <para>
    /// 不能在菜单项自己的 Click 里直接 <see cref="BuildMenu"/>：那会把正在关闭的
    /// <c>ContextMenu</c> 整个换掉，点击事件的处理链就此中断 —— 表现就是"点了分组没反应"。
    /// </para>
    /// </summary>
    private void ScheduleMenuRebuild() =>
        Dispatcher.UIThread.Post(BuildMenu, DispatcherPriority.Background);

    public PinWindow()
    {
        InitializeComponent();

        // 与 RegionSelector 同一套做法：Avalonia 12 用 WindowDecorations（枚举与属性同名，需完全限定）
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        if (OperatingSystem.IsWindows()) Win32Properties.AddWndProcHookCallback(this, OnNativeSizing);

        // 窗口自己得是可聚焦的，键盘焦点才落得下来 —— 否则 ESC 收不到
        Focusable = true;

        PointerPressed += OnPointerPressed;
        ContextRequested += (_, e) =>
        {
            if (!_suppressFrozenFrameMenu) return;
            e.Handled = true;
            _suppressFrozenFrameMenu = false;
        };
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        DoubleTapped += (_, _) => Close();

        // 光标停在贴图上时给提示：动图说"按住 X 定格"，静态图说"能转"
        PointerEntered += (_, _) =>
        {
            if (_rotating)
            {
                return;
            }

            if (_player is not null && FreezeKeyLabel is { } label)
            {
                ShowRotateHint(I18n.F("Str.Pin.AnimatedFreezeHint", label));
                StartFreezeWatch();
                return;
            }

            ShowRotateHint(I18n.T("Str.Pin.RotateHint"));
        };

        PointerExited += (_, _) =>
        {
            StopFreezeWatch();

            if (!_rotating)
            {
                RotateHint.Opacity = 0;
            }
        };

        I18n.Current.Changed += OnLanguageChanged;

        // 分组是全局共享的：别的贴图里新建/改名/删掉分组后，这张贴图的右键子菜单必须跟着更新，
        // 否则它显示的永远是"创建这个窗口时"的分组快照（用户看到的就是"不显示已有分组"）。
        PinGroupStore.Current.Changed += OnGroupsChanged;
        Closed += (_, _) =>
        {
            if (OperatingSystem.IsWindows()) Win32Properties.RemoveWndProcHookCallback(this, OnNativeSizing);
            _isClosed = true;
            I18n.Current.Changed -= OnLanguageChanged;
            PinGroupStore.Current.Changed -= OnGroupsChanged;

            StopFreezeWatch();

            // 动图：先断开 Image 的引用再释放帧（否则可能画到已经释放的位图）
            Picture.Source = null;
            StopAnimation();

            // 从剪贴板读出来的位图是我们自己创建的，窗口关了就释放；预览图归 ViewModel 持有，不能碰
            if (_ownsImage)
            {
                _image?.Dispose();
            }

            // 烘焙出来的朝向图始终是我们自己的，一定释放
            _oriented?.Dispose();
            _oriented = null;
            _sourceFrame = null;
        };

        BuildMenu();
    }

    /// <summary>是否处于鼠标穿透状态（开启后它收不到鼠标，只能从主窗口或"恢复交互"救回来）。</summary>
    public bool IsClickThrough { get; private set; }

    /// <summary>设置要显示的图（必须在 <c>Show()</c> 之前调用）。</summary>
    /// <param name="ownsImage">为 <c>true</c> 时窗口关闭会释放这张位图。</param>
    public void SetImage(Bitmap image, string? sourceName = null, bool ownsImage = false)
    {
        ArgumentNullException.ThrowIfNull(image);

        _image = image;
        _sourceName = sourceName;
        _ownsImage = ownsImage;

        // 换图 = 回到原图（朝向状态由 SetTransform 另行恢复）
        StopAnimation();
        _isClosed = false;
        _oriented?.Dispose();
        _oriented = null;
        _sourceFrame = null;
        Picture.Source = image;
        Picture.RenderTransform = null;

        // 窗口**不透明**：缩放时窗口尺寸先变、Avalonia 的内容重绘随后才到，
        // 透明背景会让那一瞬间直接透出桌面（看起来就是画面里闪）。
        // 取图片左上角的颜色当底色，即使真闪一下也几乎看不出来。
        Background = new SolidColorBrush(SampleCornerColor(image));

        BuildMenu();
    }

    /// <summary>
    /// 恢复旋转/镜像（重启恢复、或从别处复制状态时用）。参数与 <see cref="RotationDegrees"/> 一致。
    /// <para>
    /// 动图**不做**朝向：旋转要把像素烘焙成新位图，多帧烘焙等于只留一帧。所以这里直接清零。
    /// </para>
    /// </summary>
    public void SetTransform(double degrees, bool mirrorHorizontal, bool mirrorVertical)
    {
        if (_player is not null)
        {
            if (Math.Abs(ImageTransform.Normalize(degrees)) > 0.01 || mirrorHorizontal || mirrorVertical)
            {
                GuiLog.Write($"animated: 忽略库里存的朝向（{degrees:0.#}°, mirror={mirrorHorizontal}/{mirrorVertical}）");
            }

            _rotation = 0;
            _mirrorH = false;
            _mirrorV = false;
            AdoptTargetRegion();
            return;
        }

        _rotation = ImageTransform.Normalize(degrees);
        _mirrorH = mirrorHorizontal;
        _mirrorV = mirrorVertical;

        // 会话里存的是"旋转后"的屏幕矩形：必须先知道朝向再把缩放反推回来，否则宽高互换后尺寸不对
        AdoptTargetRegion();
        UpdateOriented(keepAnchor: true);
    }

    /// <summary>
    /// 有目标区域时（贴回原位 / 恢复会话），把缩放反推成"当前朝向的窗口正好等于目标矩形"，
    /// 并把锚点定成目标矩形的中心。
    /// <para>
    /// 目标矩形是<b>旋转后</b>的外接矩形，所以要用"原图 × 朝向"的外接矩形当单位尺寸：
    /// 直接拿原图宽高去除，90° 时会得到 <c>高/宽</c> 倍的错误缩放（图明显变大或变小）。
    /// </para>
    /// </summary>
    private void AdoptTargetRegion()
    {
        if (_targetRegion is not { } region || _image is not { } image)
        {
            return;
        }

        var unit = BoundsOf(image.PixelSize.Width, image.PixelSize.Height, _rotation);

        if (unit.Width <= 0 || unit.Height <= 0)
        {
            return;
        }

        var scaling = Scaling;
        var zoomX = region.Width / scaling / unit.Width;
        var zoomY = region.Height / scaling / unit.Height;

        // 两个方向各算一次再取平均：目标矩形是整数像素，单看一边会有 ±0.5px 的取整噪声
        _zoom = PositiveAverage(zoomX, zoomY, 1 / scaling);
        _zoomRef = _zoom;

        _anchorX = region.X + region.Width / 2.0;
        _anchorY = region.Y + region.Height / 2.0;
        _anchorScaling = scaling;
        _anchorValid = true;
    }

    /// <summary>取两个正数估计值的平均；有一个不可用时用另一个，都不可用时用兜底值。</summary>
    private static double PositiveAverage(double a, double b, double fallback)
    {
        var validA = a > 1e-6 && !double.IsNaN(a) && !double.IsInfinity(a);
        var validB = b > 1e-6 && !double.IsNaN(b) && !double.IsInfinity(b);

        return (validA, validB) switch
        {
            (true, true) => (a + b) / 2,
            (true, false) => a,
            (false, true) => b,
            _ => fallback,
        };
    }

    /// <summary>
    /// 诊断：这张贴图现在有没有盖住任务栏。
    /// <para>
    /// 用户复现"任务栏闪"时，日志里能直接看到贴图矩形、工作区与显示器矩形，
    /// 不用再靠猜是"盖住了"还是"尺寸取整差 1px 被判成全屏"。
    /// </para>
    /// </summary>
    private void LogTaskbarCoverage()
    {
        if (!OperatingSystem.IsWindows() || Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        // 缩放时 ApplySize 会被连续调用，别把日志刷爆
        var now = Environment.TickCount64;

        if (now - _lastCoverageLogTicks < 2000)
        {
            return;
        }

        var area = GetScreenRect();
        var work = screen.WorkingArea;
        var monitor = screen.Bounds;

        var coversTaskbarBottom = area.Y + area.Height > work.Y + work.Height;
        var coversWholeMonitor = area.X <= monitor.X && area.Y <= monitor.Y
            && area.X + area.Width >= monitor.X + monitor.Width
            && area.Y + area.Height >= monitor.Y + monitor.Height;

        if (coversTaskbarBottom || coversWholeMonitor)
        {
            _lastCoverageLogTicks = now;

            GuiLog.Write(
                $"pin: 盖住任务栏 area={area} workArea={work} monitor={monitor} " +
                $"coversWholeMonitor={coversWholeMonitor} scaling={Scaling:0.##}");
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_image is not { } image)
        {
            Close();   // 没设图片就没得贴（正常流程不会走到这里，防呆）
            return;
        }

        // 刚贴上来先闪一圈边框提示"这张是新的"；用户一移动就收掉。
        // 用户明确要求 Linux 不做这个（那边贴图本身的交互能力就有限）。
        NewPinFrame.IsVisible = OperatingSystem.IsWindows();

        // 混合缩放的关键：**先按贴图目标区域所在的屏**取缩放，
        // 窗口此时还没摆好，ScreenFromWindow 会拿到主屏（100%），于是副屏 150% 的图上会偏大 1.5 倍、比例也不对。
        var screen = (_targetRegion is { } targetRegion
                ? Screens.ScreenFromPoint(new PixelPoint(
                    targetRegion.X + (targetRegion.Width / 2),
                    targetRegion.Y + (targetRegion.Height / 2)))
                : null)
            ?? Screens.ScreenFromWindow(this)
            ?? Screens.Primary;
        // 优先用调用方给的缩放（框选路径知道目标屏，最可靠）；否则退回按屏/窗口推断。
        var scaling = _scalingOverride ?? screen?.Scaling ?? 1.0;

        if (screen is { } work)
        {
            // 缩放上限 = 工作区（排除任务栏）
            _maxWidth = work.WorkingArea.Width / scaling;
            _maxHeight = work.WorkingArea.Height / scaling;
        }

        // 贴图尺寸提示：把"物理像素"写进工具提示（悬停在贴图上即可看到），便于核对缩放是否正确。
        if (_targetRegion is { } sizeHint)
        {
            ToolTip.SetTip(this, "贴图 " + sizeHint.Width + " x " + sizeHint.Height + " 像素（屏幕物理）");
        }

        if (_targetRegion is { } region)
        {
            // 贴回原位：位置、尺寸都还原成截图当时的样子（不套"工作区 60%"那套收缩）
            // 先把窗口摆到目标屏，**再**用窗口在那块屏上实际得到的 RenderScaling 换算尺寸：
            // 否则窗口还在主屏时拿到的是主屏缩放，副屏 125% 就会大 25%（用户报的"500x500 看起来不对"）。
            Position = new PixelPoint(region.X, region.Y);

            var liveScaling = RenderScaling > 0 ? RenderScaling : scaling;

            // 目标矩形是"屏幕上要占的物理像素"。恢复会话时它可能是**旋转后**的外接矩形
            //（宽高已互换），而此刻朝向还是 0 —— 所以按**面积**反推缩放：面积不随 90° 旋转变化，
            // 用宽高任何一个去反推都会在旋转 90° 的贴图上得到错误尺寸（差 (h/w)² 倍）。
            var area = (double)region.Width * region.Height;
            var pixels = (double)image.PixelSize.Width * image.PixelSize.Height;

            _zoom = area > 0 && pixels > 0
                ? Math.Sqrt(area / pixels) / liveScaling
                : 1 / liveScaling;
            _zoomRef = _zoom;

            // 尺寸按整物理像素对齐（否则 1px 误差会把位图拉伸 1px → 整张糊）
            (_width, _height) = FrameDipSize(0);
            ApplySize();

            // 目标矩形是整数的，实际窗口中心就在它中心；锚点从这里起步，之后只按意图更新
            _anchorX = region.X + region.Width / 2.0;
            _anchorY = region.Y + region.Height / 2.0;
            _anchorScaling = liveScaling;
            _anchorValid = true;
            return;
        }

        // 逻辑单位下的"1:1"尺寸
        _zoom = 1 / scaling;

        var naturalWidth = image.PixelSize.Width * _zoom;
        var naturalHeight = image.PixelSize.Height * _zoom;

        // 初始不超过工作区的 60%：一张 4K 截图不该直接盖满整屏
        var shrink = Math.Min(1.0, Math.Min(_maxWidth * 0.6 / naturalWidth, _maxHeight * 0.6 / naturalHeight));
        _zoom *= shrink;

        // 放大上限的基准 = 打开时的尺寸（和以前一致：缩到底再放大一定能回到刚贴出来的样子）
        _zoomRef = _zoom;

        // 尺寸按整物理像素对齐（否则 1px 误差会把位图拉伸 1px → 整张糊）
        (_width, _height) = FrameDipSize(0);
        ApplySize();

        // 多张贴图错开一点，不然会叠成一摞分不清
        var cascade = PinManager.Current.Count % 6 * 28;

        // 位置：**无论有没有屏幕信息都要摆**。
        // 以前 screen 为 null 时这里整段跳过，窗口就停在 (0,0) —— 归组时把 (0,0) 当位置存进库，
        // 之后"贴回原位"永远跑到左上角（实测库里出现过 x=0,y=0 的条目）。
        var placedLeft = 100;
        var placedTop = 100;

        if (screen is { } target)
        {
            placedLeft = target.WorkingArea.X + (int)((target.WorkingArea.Width - _width * scaling) / 2);
            placedTop = target.WorkingArea.Y + (int)((target.WorkingArea.Height - _height * scaling) / 2);
        }

        Position = new PixelPoint(placedLeft + cascade, placedTop + cascade);

        // 位置定下来了才反推锚点：这是<b>唯一</b>一处"从 Position 反算中心"，之后只用锚点
        AnchorFromWindow();

        GuiLog.Write(
            $"pin: opened at {Position.X},{Position.Y} size {_width:0}x{_height:0} " +
            $"screen={(screen is null ? "null" : screen.WorkingArea.ToString())} scaling={scaling:0.##}");
    }

    /// <summary>
    /// ESC = 收起（关窗口、保留在分组库）；Delete = 彻底删除（连库文件一起删）；
    /// Q / E 逆、顺时针转 90°。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                PinManager.Current.CloseSoft(this);
                e.Handled = true;
                return;

            // Delete = 硬删除：关窗 + 从分组移除 + 删磁盘文件（第一次先提示"再按一次"）
            case Key.Delete when !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                PinManager.Current.RequestDelete(this);
                e.Handled = true;
                return;

            // Q = 逆时针，E = 顺时针。只在贴图窗口拿到焦点时生效 ——
            // 不做成全局热键：那样会把 Q/E 从所有程序手里抢走，打字全废。
            case Key.Q when !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                RotateBy(-90);
                BuildMenu();
                e.Handled = true;
                return;

            case Key.E when !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                RotateBy(90);
                BuildMenu();
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// 指定贴图落到屏幕的哪块区域（物理像素）。用于"截图贴回原位"：位置与尺寸都按截图当时的样子还原。
    /// 传 <c>null</c>（默认）走"屏幕中央"。
    /// </summary>
    public void SetPlacement(PixelRect? region, double? scaling = null)
    {
        _targetRegion = region;
        _scalingOverride = scaling;
    }

    /// <summary>管理器拖出：保持已有物理尺寸，把缩略图上的抓取点落到鼠标处。</summary>
    internal void PlaceAtDrop(PixelPoint point, Point grab)
    {
        var size = GetScreenRect().Size;
        var target = PinDropPlacement.At(point, size, grab);
        Position = target.Position;
        void Apply()
        {
            if (_isClosed) return;
            _targetRegion = target;
            AdoptTargetRegion();
            if (_player is null) UpdateOriented(keepAnchor: true);
            else { (_width, _height) = FrameDipSize(_rotation); ApplySize(keepAnchor: true); }
            RaiseStateChanged();
        }
        Apply();
        // 跨 DPI 屏幕时，等平台完成缩放通知后再对齐一次物理矩形。
        Dispatcher.UIThread.Post(Apply, DispatcherPriority.Loaded);
    }

    // ---------------- 动图（GIF / 动画 WebP） ----------------

    private AnimatedPinPlayer? _player;

    /// <summary>这是不是一张动图（动图不支持旋转/镜像：那些操作要把像素烘焙成新图，等于只留一帧）。</summary>
    public bool IsAnimated => _player is not null;

    /// <summary>接管一个已解码好的动图并开始播放（帧数据由 <paramref name="player"/> 负责释放）。</summary>
    internal void ShowAnimation(AnimatedPinPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);

        StopAnimation();
        _player = player;

        player.FrameShown += OnAnimationFrame;
        player.Start();

        // 动图一律从"未旋转"开始：库里就算存了角度也没法烘焙多帧
        _rotation = 0;
        _mirrorH = false;
        _mirrorV = false;

        BuildMenu();
        RaiseStateChanged();

        GuiLog.Write($"animated: 开始播放 {player.FrameCount} 帧");
    }

    private void OnAnimationFrame(object? sender, Bitmap frame)
    {
        if (!_isClosed)
        {
            Picture.Source = frame;
        }
    }

    private bool _isClosed;

    /// <summary>
    /// 停掉并释放动图帧（关窗/换图时调）。
    /// </summary>
    private void StopAnimation()
    {
        StopFreezeWatch();

        if (_player is null)
        {
            return;
        }

        _player.FrameShown -= OnAnimationFrame;
        _player.Dispose();
        _player = null;
    }

    // ---------------- 动图定格（按住 Shift 停在当前帧） ----------------

    private DispatcherTimer? _freezeTimer;

    /// <summary>上次记"盖住任务栏"诊断的时间（节流用）。</summary>
    private long _lastCoverageLogTicks;

    private bool _frozen;
    private bool _suppressFrozenFrameMenu;

    /// <summary>
    /// 当前设置下的定格键显示名（"Shift" / "Ctrl" / "Alt"）；
    /// 设置成"关闭"或平台不支持查询按键时返回 <c>null</c>，提示与功能一起隐藏。
    /// </summary>
    private static string? FreezeKeyLabel
    {
        get
        {
            if (!PlatformServices.KeyboardState.IsSupported)
            {
                return null;
            }

            return FreezeKey.ToModifier(AppSettingsService.Current.Settings.AnimatedFreezeKey) is { } modifier
                ? FreezeKey.Label(modifier)
                : null;
        }
    }

    /// <summary>光标进入动图时开始"看按键"：100ms 一次，离开就停（不动时零开销）。</summary>
    private void StartFreezeWatch()
    {
        if (_freezeTimer is not null)
        {
            return;
        }

        _freezeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _freezeTimer.Tick += (_, _) => PollFreezeKey();
        _freezeTimer.Start();
    }

    private void StopFreezeWatch()
    {
        _freezeTimer?.Stop();
        _freezeTimer = null;

        // 光标移开 = 松开：恢复播放
        if (_frozen)
        {
            _frozen = false;
            _player?.Resume();
        }
    }

    private void PollFreezeKey()
    {
        if (_player is null || FreezeKey.ToModifier(AppSettingsService.Current.Settings.AnimatedFreezeKey) is not { } modifier)
        {
            StopFreezeWatch();
            return;
        }

        var down = PlatformServices.KeyboardState.IsDown(modifier);

        if (down == _frozen)
        {
            return;
        }

        _frozen = down;

        if (down)
        {
            _player.Pause();
            ShowRotateHint(I18n.F("Str.Pin.AnimatedFrozen", FreezeKey.Label(modifier)));
        }
        else
        {
            _player.Resume();
            ShowRotateHint(I18n.F("Str.Pin.AnimatedResumed", FreezeKey.Label(modifier)));
        }
    }

    /// <summary>设置/取消鼠标穿透（Windows 走 WS_EX_TRANSPARENT）；返回实际是否生效。</summary>
    public bool SetClickThrough(bool enabled)
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var applied = PlatformServices.ClickThrough.TrySetEnabled(handle, enabled);

        if (applied)
        {
            IsClickThrough = enabled;
            BuildMenu();   // 勾选状态要跟着变
            RaiseStateChanged();
        }

        return applied;
    }

    /// <summary>是否处于"始终置顶"（默认 <c>true</c>，和窗口创建时一致）。</summary>
    public bool IsTopmost { get; private set; } = true;

    /// <summary>
    /// 设置/取消置顶。取消后这张贴图会落到普通窗口层级，可以被别的窗口压住。
    /// <para>
    /// 优先走平台 API（<c>HWND_TOPMOST</c> / <c>HWND_NOTOPMOST</c>）：不激活、不重排窗口。
    /// 平台不支持时才退回 Avalonia 自己的 <see cref="Window.Topmost"/> 属性。
    /// </para>
    /// </summary>
    public bool SetTopmost(bool enabled)
    {
        if (enabled == IsTopmost && Topmost == enabled)
        {
            return true;
        }

        // **唯一真相是 Avalonia 的 Topmost 属性**：只调平台 API 的话，Avalonia 仍认为自己是置顶的，
        // 之后它会在某些时机（重新显示、属性重应用）再把窗口顶回 topmost 带 —— 表现就是
        // "层级随机乱跳/和别的窗口抢最上层"。所以属性与平台两边都设。
        Topmost = enabled;
        IsTopmost = enabled;

        if (enabled)
        {
            // 同一带内再抢一次：这样能压过同样置顶的管理窗口
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            var raised = PlatformServices.WindowPlacement.ForceTopmost(handle);
            GuiLog.Write($"z-order: pin topmost=true raised={raised}");
        }
        else
        {
            GuiLog.Write("z-order: pin topmost=false");
        }

        BuildMenu();   // 菜单里的勾选状态要跟着变
        RaiseStateChanged();
        return true;
    }

    /// <summary>
    /// 这张图<b>当前在屏幕上的物理像素矩形</b>：位置是窗口位置，宽高按当前缩放换算。
    /// 热键要判断"光标下是哪张贴图"时用它。
    /// </summary>
    public PixelRect GetScreenRect()
    {
        var scaling = Scaling;
        return new PixelRect(
            Position.X,
            Position.Y,
            Math.Max(1, RoundPx(_width * scaling)),
            Math.Max(1, RoundPx(_height * scaling)));
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 点一下就把它变成"当前聚焦的那张"：之后按 ESC 关掉的就是它
        Focus();
        _suppressFrozenFrameMenu = false;

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (_player is not null)
            {
                PollFreezeKey();
                if (_frozen)
                {
                    _suppressFrozenFrameMenu = true;
                    e.Handled = true;
                    _ = CopyFrameAsync();
                }
            }
            return;   // 右键留给 ContextMenu
        }

        // 按住 Alt = 自由旋转（相对几何中心）。左键拖动仍然是"挪窗口"。
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (_player is not null)
            {
                ShowRotateHint(I18n.T("Str.Pin.AnimatedNoRotate"));
                return;
            }

            // 中心 = 锚点（物理像素，双精度）。按下时定住，之后整个拖动过程都用它，绝不反算。
            EnsureAnchor();

            _rotateStartRotation = _rotation;
            _previewRotation = _rotation;
            _rotateStartAngle = CursorAngle(_anchorX, _anchorY);
            _rotating = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            ShowRotateHint(I18n.F("Str.Pin.Rotating", Math.Round(_rotation)));

            BeginRotatePreview();
            return;
        }

        _dragging = true;
        _grab = e.GetPosition(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    /// <summary>
    /// 进入"拖动预览"：换回原图 + 只做 <c>RenderTransform</c>。
    /// <para>
    /// 拖动期间<b>绝不烘焙像素</b>：每帧重算一次位图（2K 图约十几毫秒）会直接卡成 PPT。
    /// 松手时再烘焙一次即可 —— 视觉结果完全一致（变换和像素旋转等价）。
    /// </para>
    /// </summary>
    private void BeginRotatePreview()
    {
        if (_image is not { } source)
        {
            return;
        }

        var previous = _oriented;
        Picture.Source = source;
        _oriented = null;
        previous?.Dispose();

        ApplyPreviewTransform();
    }

    /// <summary>按 <see cref="_previewRotation"/> 摆好变换与窗口尺寸（不碰像素）。</summary>
    private void ApplyPreviewTransform()
    {
        var transforms = new TransformGroup();

        if (_mirrorH || _mirrorV)
        {
            transforms.Children.Add(new ScaleTransform(_mirrorH ? -1 : 1, _mirrorV ? -1 : 1));
        }

        transforms.Children.Add(new RotateTransform(_previewRotation));
        Picture.RenderTransform = transforms;

        // 预览期间 Picture 是原图本身（图控件的宽高由 <see cref="LayoutPicture"/> 按"原图 × 缩放"摆）
        SetFrameAroundAnchor(_previewRotation);

        // 任意角度下外接矩形四角是空的：必须透明，否则会看到一圈实色
        Background = Brushes.Transparent;
    }

    /// <summary>
    /// 把窗口设成"当前朝向的尺寸 + 绕锚点"—— 旋转/预览的唯一摆位出口。
    /// <para>
    /// 锚点是双精度常量，<c>Position</c> 只在最后取整一次，所以转多少帧都不会累积误差。
    /// </para>
    /// </summary>
    private void SetFrameAroundAnchor(double degrees)
    {
        EnsureAnchor();

        (_width, _height) = FrameDipSize(degrees);

        ApplySize(keepAnchor: true);
    }

    /// <summary>
    /// 我们"想要"的窗口左上角（物理像素，双精度）：由锚点减去半个物理窗口宽度得到。
    /// 计算锚点偏移一律用它，而不是 <see cref="Window.Position"/> —— 后者已被取整。
    /// </summary>
    private (double X, double Y) FrameOrigin()
    {
        var scaling = Scaling;

        return (
            RoundPx(_anchorX - RoundPx(_width * scaling) / 2.0),
            RoundPx(_anchorY - RoundPx(_height * scaling) / 2.0));
    }

    /// <summary>
    /// 物理像素取整：**永远 .5 向上取整**（向 +∞，不是默认的"银行家舍入"，也不是"远离零"）。
    /// <para>
    /// 为什么不默认 / 不 away-from-zero：锚点常常落在 .5 上（奇偶宽高交替时），
    /// 默认舍入会按奇偶往两边凑、away-from-zero 在负坐标（左侧副屏）方向相反 ——
    /// 结果是 90° 交换宽高后窗口中心差 1px（实测 Q 一次就偏 1px）。
    /// 统一向 +∞ 取整，同一锚点在两种朝向下得到同一个中心。
    /// </para>
    /// </summary>
    private static int RoundPx(double value) => (int)Math.Floor(value + 0.5);

    /// <summary>当前屏幕缩放（拿不到就按 100%）。</summary>
    private double Scaling => RenderScaling > 0 ? RenderScaling : 1.0;

    /// <summary>未旋转时图片的显示尺寸（DIP）= 原图像素 × 缩放。</summary>
    private (double Width, double Height) NaturalSize()
        => _image is { } image
            ? (image.PixelSize.Width * _zoom, image.PixelSize.Height * _zoom)
            : (_width, _height);

    /// <summary>按当前朝向得到的窗口尺寸（DIP，未对齐）。只用于需要"逻辑尺寸"的地方。</summary>
    private (double Width, double Height) DisplaySize(double degrees)
    {
        var (naturalWidth, naturalHeight) = NaturalSize();
        return BoundsOf(naturalWidth, naturalHeight, degrees);
    }

    /// <summary>
    /// 窗口尺寸（DIP）——**真正写进窗口的那个值**：按整物理像素对齐。
    /// <para>
    /// 为什么必须对齐：平台把 DIP 换成物理像素时会向上取整。旋转 90° 时
    /// <c>cos(90°)</c> 不是 0 而是 6.1e-17，于是宽度算出来是 <c>100.00000000000001</c>，
    /// 取整变成 <b>101</b>，而烘焙后的位图是 100 像素宽 —— 图片被拉伸 1px 重采样，
    /// 整张图就糊了（实测 1px 棋盘格旋转后 100% 像素变中间色）。
    /// 这里把尺寸钉在"整物理像素"上，100 就一定是 100。
    /// </para>
    /// </summary>
    private (double Width, double Height) FrameDipSize(double degrees)
    {
        var scaling = Scaling;
        var (width, height) = DisplaySize(degrees);

        return (
            Math.Max(MinSize, SnapDip(width, scaling)),
            Math.Max(MinSize, SnapDip(height, scaling)));
    }

    /// <summary>把 DIP 尺寸对到整物理像素，并保证 <c>结果 × 缩放</c> 在浮点里正好等于那个整数。</summary>
    private static double SnapDip(double dip, double scaling)
    {
        var pixels = Math.Max(1, RoundPx(dip * scaling));
        var snapped = pixels / scaling;

        // 除回去再乘一遍可能差 1ulp（100.00000000000001 又会被平台向上取整成 101），
        // 沿 ulp 走几步找一个乘出来**正好等于整数**的值。
        for (var i = 0; i < 8 && snapped * scaling != pixels; i++)
        {
            snapped = snapped * scaling > pixels ? Math.BitDecrement(snapped) : Math.BitIncrement(snapped);
        }

        return snapped;
    }

    /// <summary>
    /// 锚点还没建立、或建立后屏幕缩放变了（窗口被拖到别的屏 → 物理尺寸整体缩放）时，
    /// 从当前窗口反推一次。缩放一变，锚点在物理坐标里的含义就变了，必须重新同步。
    /// </summary>
    private void EnsureAnchor()
    {
        if (!_anchorValid || Math.Abs(Scaling - _anchorScaling) > 1e-6)
        {
            AnchorFromWindow();
        }
    }

    /// <summary>
    /// 从窗口当前位置反推锚点。只有两处会用到：窗口刚摆好、以及缩放变化/锚点缺失时的重新同步。
    /// 平时<b>绝不</b>用：反推一次最多差 0.5px，反复反推就是漂移的来源。
    /// </summary>
    private void AnchorFromWindow()
    {
        var scaling = Scaling;

        _anchorX = Position.X + RoundPx(_width * scaling) / 2.0;
        _anchorY = Position.Y + RoundPx(_height * scaling) / 2.0;
        _anchorScaling = scaling;
        _anchorValid = true;
    }

    /// <summary>按锚点把窗口摆到位（尺寸已写进 <see cref="_width"/> / <see cref="_height"/>）。</summary>
    private void PlaceAtAnchor()
    {
        var (left, top) = FrameOrigin();
        var next = new PixelPoint((int)left, (int)top);

        if (next != Position)
        {
            Position = next;
        }
    }

    /// <summary>旋转后窗口尺寸（DIP）。</summary>
    private static (double Width, double Height) BoundsOf(double width, double height, double degrees)
    {
        var radians = ImageTransform.Normalize(degrees) * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));

        return (width * cos + height * sin, width * sin + height * cos);
    }

    /// <summary>
    /// 把图片控件按"窗口里居中"摆好（用 Margin 显式定位，不依赖 HorizontalAlignment）。
    /// <para>
    /// 为什么不用对齐：旋转时窗口被撑成外接矩形，图片比窗口小一圈，
    /// 对齐方式一旦被别处的样式/布局影响，图片就会贴到左上角 —— 表现出来就是"绕左上角旋转"。
    /// 显式边距没有这种歧义。
    /// </para>
    /// </summary>
    private void LayoutPicture()
    {
        // 注意：必须写全名 Avalonia.Layout.HorizontalAlignment —— 本类的 HorizontalAlignment 属性会遮蔽同名枚举
        Picture.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        Picture.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

        // 平时：显示的是"已烘焙成当前朝向"的位图（窗口尺寸 = 位图尺寸），所以图控件铺满窗口。
        // Alt 拖动预览期：Picture 换回了原图、只用 RenderTransform 转，图控件必须保持原图尺寸。
        var (pictureWidth, pictureHeight) = _rotating ? NaturalSize() : (_width, _height);

        Picture.Width = pictureWidth;
        Picture.Height = pictureHeight;

        // **允许负边距**：旋转接近 90°/270° 时，图片自身宽度会大于窗口宽度
        //（窗口是"旋转后的外接矩形"，宽高互换了），此时只有负偏移才能让图片中心对齐窗口中心。
        // 之前这里夹了 Math.Max(0, …)，图片被推到左上角，旋转后大半跑到窗口之外 ——
        // 表现就是"接近 90° 时贴图超出框、看不见，松手才回来"。
        var offsetX = (Width - pictureWidth) / 2;
        var offsetY = (Height - pictureHeight) / 2;

        Picture.Margin = new Thickness(offsetX, offsetY, 0, 0);
        Picture.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
    }

    /// <summary>光标相对某个屏幕点（物理像素）的角度（度）。</summary>
    private static double CursorAngle(double centerX, double centerY)
    {
        if (PlatformServices.ForegroundApp.GetCursorPosition() is not { } cursor)
        {
            return 0;
        }

        return Math.Atan2(cursor.Y - centerY, cursor.X - centerX) * 180 / Math.PI;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // Alt 拖动中：只更新几何变换（零像素开销），松手才烘焙
        if (_rotating)
        {
            var angle = CursorAngle(_anchorX, _anchorY);
            _previewRotation = ImageTransform.Normalize(_rotateStartRotation + (angle - _rotateStartAngle));

            ApplyPreviewTransform();
            RotateHintText.Text = I18n.F("Str.Pin.Rotating", Math.Round(_previewRotation));
            return;
        }

        if (!_dragging)
        {
            return;
        }

        // 窗口 Position 是物理像素、指针坐标是逻辑单位 —— 差值必须乘缩放，否则 150% 下越拖越偏
        var current = e.GetPosition(this);
        var scaling = RenderScaling;

        var next = new PixelPoint(
            Position.X + (int)Math.Round((current.X - _grab.X) * scaling),
            Position.Y + (int)Math.Round((current.Y - _grab.Y) * scaling));

        if (next == Position)
        {
            return;   // 还没真的动，别白跑一次窗口移动
        }

        // 锚点跟着平移**同样的整数像素**（不是按新位置反算）：中心与窗口一起挪，误差不累积
        var deltaX = next.X - Position.X;
        var deltaY = next.Y - Position.Y;

        Position = next;

        if (_anchorValid)
        {
            _anchorX += deltaX;
            _anchorY += deltaY;
        }

        // 用户开始挪它了 —— "新贴的"提示可以收了
        StopNewPinHint();
    }

    /// <summary>收掉"刚贴上"的闪烁边框。</summary>
    private void StopNewPinHint()
    {
        if (!NewPinFrame.IsVisible)
        {
            return;
        }

        NewPinFrame.Classes.Remove("newPin");
        NewPinFrame.IsVisible = false;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_rotating)
        {
            _rotating = false;
            Cursor = Cursor.Default;

            // 松手：把最终角度一次性烘焙成像素（这一步的代价只付一次），
            // 并再按锚点摆一次 —— 烘焙后窗口尺寸等于预览时的外接矩形，中心应完全不动。
            _rotation = _previewRotation;
            Picture.RenderTransform = null;
            UpdateOriented(keepAnchor: true);
            ApplyBackground();
            ShowRotateHint(I18n.F("Str.Pin.Rotated", Math.Round(_rotation)));
            return;
        }

        _dragging = false;
        Cursor = Cursor.Default;

        // 拖完才通知（拖动中每帧都发的话，防抖也扛不住大图的 PNG 编码）
        RaiseStateChanged();
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y;
        if (delta == 0)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Opacity = Math.Clamp(Opacity + (delta > 0 ? 0.05 : -0.05), 0.1, 1.0);

            // 透明度变了，底色策略跟着变：半透明时必须透明，否则实色底会糊成一层彩色遮罩
            ApplyBackground();
            RaiseStateChanged();
            return;
        }

        // 连续滚动时一帧可能收到好几格：合并成一次窗口操作。
        // 每格都单独改一次窗口尺寸的话，窗口尺寸变化与 Avalonia 的重绘不同步，画面里会闪。
        _pendingAnchor = e.GetPosition(this);
        _pendingZoom *= delta > 0 ? ZoomStep : 1 / ZoomStep;

        if (_zoomQueued)
        {
            return;
        }

        _zoomQueued = true;
        Dispatcher.UIThread.Post(ApplyPendingZoom, DispatcherPriority.Render);
    }

    /// <summary>把攒下来的缩放一次性应用（挂在渲染优先级上，正好跟着画面节奏走）。</summary>
    private void ApplyPendingZoom()
    {
        _zoomQueued = false;

        var factor = _pendingZoom;
        _pendingZoom = 1;

        if (Math.Abs(factor - 1) < 1e-9)
        {
            return;
        }

        Zoom(_pendingAnchor, factor);
    }

    /// <summary>取图片左上角像素当窗口底色（拿不到就用黑）。</summary>
    private static Color SampleCornerColor(Bitmap image)
    {
        try
        {
            var buffer = Marshal.AllocHGlobal(4);

            try
            {
                image.CopyPixels(new PixelRect(0, 0, 1, 1), buffer, 4, 4);
                return Color.FromArgb(
                    Marshal.ReadByte(buffer, 3),
                    Marshal.ReadByte(buffer, 2),
                    Marshal.ReadByte(buffer, 1),
                    Marshal.ReadByte(buffer, 0));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return Avalonia.Media.Colors.Black;
        }
    }

    /// <summary>以 <paramref name="anchor"/>（窗口内的逻辑坐标）为不动点缩放。</summary>
    private void Zoom(Point anchor, double factor)
    {
        if (_image is not { } image || _width <= 0 || _height <= 0)
        {
            return;
        }

        EnsureAnchor();

        var scaling = Scaling;

        // 上限必须是**固定值**（打开时的尺寸 × 固定倍率），不能依赖"当前尺寸"：
        // 以前夹到屏幕工作区，第一次放大就被截断、丢掉了原始尺寸，
        // 于是缩小之后再放大永远回不到原来那么大 —— 缩放的基准被上限吃掉了。
        var maxZoom = Math.Max(
            _zoomRef * MaxZoomIn,
            MinSize / Math.Max(1, Math.Min(image.PixelSize.Width, image.PixelSize.Height)));

        // 硬上限：窗口再大也没意义，而且每次旋转都要按新尺寸重新烘焙位图，
        // 尺寸过大时那张位图会吃掉巨量内存（表现就是按 Q 旋转直接崩溃/消失）。
        maxZoom = Math.Min(maxZoom, MaxSize / Math.Max(image.PixelSize.Width, image.PixelSize.Height));

        // 下限保证最短边不小于 MinSize（旋转 90° 后会交换宽高，所以按原图的短边算）
        var minZoom = MinSize / Math.Max(1, image.PixelSize.Width);
        var zoom = Math.Clamp(_zoom * factor, Math.Min(minZoom, maxZoom), Math.Max(minZoom, maxZoom));

        if (Math.Abs(zoom - _zoom) < 1e-9)
        {
            return;
        }

        // 光标下的那个图像点保持不动，全部用"锚点 + 尺寸"换算，不回读 Position：
        //   屏幕点 = 窗口左上角 + 光标在窗口内的 DIP × 缩放
        var (left, top) = FrameOrigin();
        var pointX = left + anchor.X * scaling;
        var pointY = top + anchor.Y * scaling;

        var applied = zoom / _zoom;
        _zoom = zoom;

        // 尺寸按整物理像素对齐：图片在 1:1 时才是"像素对像素"，否则会被重采样变糊
        (_width, _height) = FrameDipSize(_rotation);

        // 尺寸变了之后，同一个图像点在窗口内的 DIP 偏移也按同一比例变大：
        // 反推出新的左上角，再换算回新锚点（**只在这里取整一次**）
        var nextLeft = pointX - anchor.X * applied * scaling;
        var nextTop = pointY - anchor.Y * applied * scaling;

        _anchorX = nextLeft + RoundPx(_width * scaling) / 2.0;
        _anchorY = nextTop + RoundPx(_height * scaling) / 2.0;
        _anchorScaling = scaling;

        // 顺序要紧：先缩放（触发一次重绘），再移动（DWM 平移窗口位图，不重绘）。
        // 反过来会出现"窗口已经挪走、内容还是旧尺寸"的一帧 —— 看起来就是闪。
        ApplySize();
        PlaceAtAnchor();

        RaiseStateChanged();
    }

    /// <summary>
    /// 把 <see cref="_width"/> / <see cref="_height"/> 写进窗口 —— 改尺寸的唯一出口。
    /// 图像像素已经被烘焙成当前朝向，所以窗口尺寸**就等于**图片显示尺寸，不存在"外接矩形留白"。
    /// </summary>
    /// <param name="keepAnchor">改完尺寸后围绕锚点把窗口摆回去（旋转/缩放时用）。</param>
    private void ApplySize(bool keepAnchor = false)
    {
        PinNativeSizeLimit.Refresh(this);
        Width = _width;
        Height = _height;

        // 窗口尺寸变了之后才知道"留白多少"，所以先设尺寸再摆图片
        LayoutPicture();

        if (keepAnchor && _anchorValid)
        {
            PlaceAtAnchor();
        }

        LogTaskbarCoverage();
    }

    private IntPtr OnNativeSizing(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        PinNativeSizeLimit.Apply(message, lParam, MaxSize, Scaling);
        return IntPtr.Zero;
    }

    /// <summary>
    /// 按当前角度/镜像**烘焙**出显示用的位图，并按新尺寸调整窗口。
    /// <para>
    /// 为什么烘焙而不是用 <c>RenderTransform</c>：视觉变换下窗口是"外接矩形"、图片仍是原尺寸，
    /// 一旦对齐不精确就会透出窗口底色（用户报的"被其他颜色填充"）。
    /// 烘焙后窗口尺寸 = 图片尺寸，从根上没这个问题。
    /// </para>
    /// </summary>
    private void UpdateOriented(bool keepAnchor)
    {
        if (_image is not { } source)
        {
            return;
        }

        // 动图不能被烘焙成一张静态图（那等于只剩一帧），所有朝向操作对它都不生效
        if (_player is not null)
        {
            return;
        }

        if (keepAnchor)
        {
            // 缩放变过（窗口被拖到别的屏）就先重新同步锚点，否则会按旧缩放的物理尺寸摆位
            EnsureAnchor();
        }

        // 1) 先按"原图像素 × 缩放"做尺寸保护：旋转后的位图和窗口一样大，
        //    尺寸过大时那张位图会吃掉巨量内存（表现就是按 Q 旋转直接崩溃/消失）。
        //    **必须在烘焙之前**：以前是烘焙完才缩，等发现时内存已经吃掉了。
        var limit = Math.Min(MaxSize, 12000.0);
        var (rawWidth, rawHeight) = DisplaySize(_rotation);
        var oversize = Math.Max(rawWidth / limit, rawHeight / limit);

        if (oversize > 1)
        {
            _zoom /= oversize;
            GuiLog.Write($"pin: rotation shrunk layout by {oversize:0.##}x to keep window bounded");
        }

        // 2) 烘焙像素（90° 走逐像素搬运，质量最好；任意角度走 Skia）
        var identity = Math.Abs(_rotation) < 0.01 && !_mirrorH && !_mirrorV;

        Bitmap next;
        var owned = false;

        if (identity)
        {
            next = source;
        }
        else
        {
            try
            {
                var frame = GetSourceFrame(source);
                next = ResultActions.ToBitmap(ImageTransform.Apply(frame, _rotation, _mirrorH, _mirrorV));
                owned = true;
            }
            catch (Exception ex)
            {
                GuiLog.Write($"pin: orientation failed ({ex.GetType().Name}: {ex.Message})");
                return;
            }
        }

        var previous = _oriented;
        Picture.Source = next;
        _oriented = owned ? next : null;
        previous?.Dispose();

        // 位图已经是最终朝向，几何变换必须清掉（否则会二次旋转）
        Picture.RenderTransform = null;

        // 3) 窗口尺寸只由"原图 × 缩放 + 朝向"决定，**不从烘焙结果的像素尺寸反推**：
        //    两者混用的话，取整/尺寸保护会让它们互相带偏，转几次就明显漂。
        //    并且必须对齐到整物理像素：差 1px 就会把位图拉伸 1px → 整张糊（实测）。
        (_width, _height) = FrameDipSize(_rotation);

        _previewRotation = _rotation;

        // 直角（0/90/180/270）时图片铺满窗口，可以保留不透明底色（避免缩放时闪）；
        // 任意角度会有透明边，必须让窗口透明，否则透明处会透出底色。
        ApplyBackground();

        ApplySize(keepAnchor);
        BuildMenu();
        RaiseStateChanged();   // 旋转/镜像/重置方向都会走到这里 → 会话防抖落盘
    }

    /// <summary>
    /// 决定窗口底色。
    /// <para>
    /// 只有"直角且完全不透明"时用取色自图片角落的实色当底（缩放/重排的瞬间不至于闪出桌面）；
    /// <b>其它情况一律透明</b>：半透明（Ctrl+滚轮调过透明度）或任意旋转角度下，
    /// 实色底会变成一层盖在桌面上的彩色遮罩 —— 就是用户报的"旋转后有时有其它颜色罩住"。
    /// </para>
    /// </summary>
    private void ApplyBackground()
    {
        if (Picture.Source is not Bitmap displayed)
        {
            Background = Brushes.Transparent;
            return;
        }

        Background = ImageTransform.IsRightAngle(_rotation) && Opacity >= 0.999
            ? new SolidColorBrush(SampleCornerColor(displayed))
            : Brushes.Transparent;
    }

    /// <summary>把 Avalonia 位图转成与 UI 无关的帧（走 PNG，保证 alpha 正确）。</summary>
    private static ImageFrame ToFrame(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return SkiaImageCodec.FromEncoded(stream);
    }

    /// <summary>
    /// 原图的 <see cref="ImageFrame"/> 缓存。转向要反复重算，每次都走一趟 PNG 编解码的话
    /// 大图会明显卡（尤其是 Alt 拖动、每 33ms 一次）。
    /// </summary>
    private ImageFrame GetSourceFrame(Bitmap source) => _sourceFrame ??= ToFrame(source);

    /// <summary>相对当前角度再转 <paramref name="delta"/> 度（Q/E 用 ±90）。</summary>
    private void RotateBy(double delta)
    {
        if (_player is not null)
        {
            ShowRotateHint(I18n.T("Str.Pin.AnimatedNoRotate"));
            return;
        }

        // 旋转中心 = 持久锚点：只在打开/拖动/缩放时更新，**绝不**在这里用
        // Position + Width/2 反算 —— Position 是整数物理像素，90° 交换宽高后
        // 来回取整会累积误差，连按几次就明显漂。
        EnsureAnchor();

        var previous = _rotation;
        _rotation = ImageTransform.Normalize(_rotation + delta);

        try
        {
            UpdateOriented(keepAnchor: true);
        }
        catch (Exception ex)
        {
            _rotation = previous;
            GuiLog.Write($"pin: rotate failed at size {_width:0}x{_height:0}: {ex.GetType().Name} {ex.Message}");
            ShowRotateHint(I18n.T("Str.Pin.RotateFailed"));
            return;
        }

        // 诊断用：锚点（我们的意图）与窗口实际中心必须一致（差 ≤0.5px 就是取整噪声，不会累积）
        LogRotation(delta);
        ShowRotateHint(I18n.F("Str.Pin.Rotated", Math.Round(_rotation)));
    }

    /// <summary>
    /// 记一行旋转诊断：锚点 vs 窗口实际中心、尺寸、缩放。
    /// 用户反馈"漂移"时，看连续几行 <c>center</c> 有没有变化就能确定是几何算错还是别的原因 —— 不靠猜。
    /// </summary>
    private void LogRotation(double delta)
    {
        var scaling = Scaling;
        var actualX = Position.X + RoundPx(_width * scaling) / 2.0;
        var actualY = Position.Y + RoundPx(_height * scaling) / 2.0;

        GuiLog.Write(
            $"pin: rotate {delta:+0;-0}° -> {_rotation:0.##}° " +
            $"center {_anchorX:0.#},{_anchorY:0.#} (window {actualX:0.#},{actualY:0.#}) " +
            $"size {_width:0.#}x{_height:0.#} zoom {_zoom:0.####}");
    }

    private void ToggleMirror(bool horizontal)
    {
        if (_player is not null)
        {
            ShowRotateHint(I18n.T("Str.Pin.AnimatedNoRotate"));
            return;
        }

        if (horizontal)
        {
            _mirrorH = !_mirrorH;
        }
        else
        {
            _mirrorV = !_mirrorV;
        }

        UpdateOriented(keepAnchor: true);
    }

    private void ResetTransform()
    {
        if (_player is not null)
        {
            ShowRotateHint(I18n.T("Str.Pin.AnimatedNoRotate"));
            return;
        }

        _rotation = 0;
        _mirrorH = false;
        _mirrorV = false;
        UpdateOriented(keepAnchor: true);
    }

    /// <summary>悬停/操作时的胶囊提示（默认 1.6 秒后自己淡出）。</summary>
    private void ShowRotateHint(string text) => ShowHint(text, TimeSpan.FromSeconds(1.6));

    /// <summary>显示一条胶囊提示，<paramref name="duration"/> 之后淡出。</summary>
    private void ShowHint(string text, TimeSpan duration)
    {
        RotateHintText.Text = text;
        RotateHint.IsVisible = true;
        RotateHint.Opacity = 1;

        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer { Interval = duration };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer?.Stop();
            RotateHint.Opacity = 0;
        };
        _hintTimer.Start();
    }

    /// <summary>回到"一个图像像素对一个屏幕物理像素"（中心不动）。</summary>
    private void ResetToPixelSize()
    {
        if (_image is null)
        {
            return;
        }

        EnsureAnchor();

        // 基准钉在原图上：一个图像像素占一个屏幕物理像素 = 1 DIP / 缩放
        _zoom = 1 / Scaling;
        _zoomRef = _zoom;

        // 朝向不用管：FrameDipSize 会按当前角度取外接矩形（90° 时宽高自动互换）并对齐整物理像素
        (_width, _height) = FrameDipSize(_rotation);

        ApplySize(keepAnchor: true);
        RaiseStateChanged();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => BuildMenu();

    private void OnGroupsChanged(object? sender, EventArgs e) => ScheduleMenuRebuild();

    /// <summary>
    /// 「设置分组」子菜单：列出已有分组（带颜色小方块，勾选当前所属）、"新建分组"、"移出分组"。
    /// 归组只是给贴图打个标记，不动图、不动位置。
    /// </summary>
    private MenuItem BuildGroupMenu()
    {
        var groups = PinGroupStore.Current.Groups;
        var current = PinGroupStore.Current.Find(GroupId);

        var parent = new MenuItem
        {
            Header = current is null
                ? I18n.T("Str.Pin.Group")
                : I18n.F("Str.Pin.GroupCurrent", current.Name),
            Items =
            {
                Item("Str.Pin.GroupUngrouped", () =>
                {
                    SetGroup(null);
                    return Task.CompletedTask;
                }),
                new Separator(),
            },
        };

        foreach (var group in groups)
        {
            // 不用 Radio 类型：Avalonia 里带 ToggleType 的 MenuItem 在同组互斥时
            // 可能吞掉 Click（用户实测"已有分组时点不动"）。用前缀勾号最稳。
            var isCurrent = string.Equals(group.Id, GroupId, StringComparison.Ordinal);
            var item = new MenuItem
            {
                Header = (isCurrent ? "✓ " : "    ") + group.Name,
                Icon = new Border
                {
                    Width = 12,
                    Height = 12,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.Parse(group.Color)),
                    Margin = new Thickness(6, 0, 2, 0),
                },
            };

            var id = group.Id;
            item.Click += (_, _) => SetGroup(id);
            parent.Items.Add(item);
        }

        parent.Items.Add(new Separator());
        parent.Items.Add(Item("Str.Pin.GroupNew", () =>
        {
            var created = PinGroupStore.Current.Create();
            SetGroup(created.Id);
            return Task.CompletedTask;
        }));

        // 分组子菜单里直接给"贴图管理"入口：改分组之后通常就想去看全部贴图
        parent.Items.Add(new Separator());
        parent.Items.Add(Item("Str.PinManager.Title", () =>
        {
            Views.PinManagerWindow.ShowOrActivate();
            return Task.CompletedTask;
        }));

        return parent;
    }

    private void BuildMenu()
    {
        // 动图不能旋转/镜像（多帧没法烘焙），这些菜单项直接置灰
        var canOrient = _player is null;

        var menu = new ContextMenu();

        // 「复制图片」对动图复制原文件；定格时右键直接复制当前帧。
        menu.Items.Add(Item("Str.Pin.Copy", CopyImageAsync));

        menu.Items.Add(Item("Str.Pin.SaveAs", SaveAsAsync));
        menu.Items.Add(Item("Str.Pin.OriginalSize", () =>
        {
            ResetToPixelSize();
            return Task.CompletedTask;
        }));
        menu.Items.Add(Item("Str.Pin.Scan", ScanAsync));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Str.Pin.RotateLeft", () =>
        {
            RotateBy(-90);
            BuildMenu();
            return Task.CompletedTask;
        }, canOrient));
        menu.Items.Add(Item("Str.Pin.RotateRight", () =>
        {
            RotateBy(90);
            BuildMenu();
            return Task.CompletedTask;
        }, canOrient));
        menu.Items.Add(Item("Str.Pin.MirrorHorizontal", () =>
        {
            ToggleMirror(horizontal: true);
            return Task.CompletedTask;
        }, canOrient));
        menu.Items.Add(Item("Str.Pin.MirrorVertical", () =>
        {
            ToggleMirror(horizontal: false);
            return Task.CompletedTask;
        }, canOrient));
        menu.Items.Add(Item("Str.Pin.ResetOrientation", () =>
        {
            ResetTransform();
            return Task.CompletedTask;
        }, canOrient));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildGroupMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(Toggle("Str.Pin.Topmost", IsTopmost, () => SetTopmost(!IsTopmost)));
        menu.Items.Add(Toggle("Str.Pin.ClickThrough", IsClickThrough, () => SetClickThrough(!IsClickThrough)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Str.Pin.Close", () =>
        {
            PinManager.Current.CloseSoft(this);
            return Task.CompletedTask;
        }));
        menu.Items.Add(Item("Str.Pin.Delete", () =>
        {
            PinManager.Current.RequestDelete(this);
            return Task.CompletedTask;
        }));

        ContextMenu = menu;
    }

    private static MenuItem Item(string i18nKey, Func<Task> action, bool enabled = true)
    {
        var item = new MenuItem { Header = I18n.T(i18nKey), IsEnabled = enabled };
        item.Click += (_, _) => _ = action();
        return item;
    }

    private static MenuItem Toggle(string i18nKey, bool isChecked, Action action)
    {
        var item = new MenuItem
        {
            Header = I18n.T(i18nKey),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = isChecked,
        };

        item.Click += (_, _) => action();
        return item;
    }

    private async Task CopyImageAsync()
    {
        // 动图：复制的是**原始文件**（粘到聊天/浏览器里还是动图，而不是一帧静态图）
        if (_player is not null && ImageFilePath is { Length: > 0 } source && File.Exists(source))
        {
            if (await ClipboardWriter.CopyFileAsync(source))
            {
                return;
            }

            GuiLog.Write("pin: 复制动图文件失败，退回复制当前帧");
        }

        if (DisplayedBitmap is { } image)
        {
            // 位图 + PNG 文件一起进剪贴板：这样在资源管理器里也能直接粘贴。
            // DisplayedBitmap 已经是"屏幕上看到的朝向"（转向是烘焙过的）。
            await ClipboardWriter.CopyAsync(image, "pin");
        }
    }

    /// <summary>只复制动图的**当前帧**（静态图没有这一项）。</summary>
    private async Task CopyFrameAsync()
    {
        if (DisplayedBitmap is { } frame)
        {
            await ClipboardWriter.CopyAsync(frame, "pin-frame");
        }
    }

    private async Task SaveAsAsync()
    {
        // 动图：直接存原始文件（另存成 PNG 只会得到当前一帧，不是用户想要的）
        if (_player is not null && ImageFilePath is { Length: > 0 } source && File.Exists(source))
        {
            var extension = Path.GetExtension(source).TrimStart('.');
            var copyPath = await ResultActions.PickSaveImageAsync(FileNaming.Suggested("pin", extension, title: Path.GetFileNameWithoutExtension(source)), extension);

            if (copyPath is null)
            {
                return;
            }

            try
            {
                File.Copy(source, copyPath, overwrite: true);
            }
            catch (Exception ex)
            {
                GuiLog.Write($"pin: 另存动图失败（{ex.GetType().Name}: {ex.Message}）");
            }

            return;
        }

        if (DisplayedBitmap is not { } image)
        {
            return;
        }

        var path = await ResultActions.PickSavePngAsync(
            FileNaming.Suggested("pin", "png", image.PixelSize.Width, image.PixelSize.Height, _sourceName ?? ""));

        if (path is null)
        {
            return;
        }

        image.Save(path, new PngBitmapEncoderOptions());
    }

    /// <summary>
    /// 当前显示的那张位图（= 屏幕上看到的朝向）。缩略图、复制、另存为都用它。
    /// </summary>
    public Bitmap? DisplayedBitmap => Picture.Source as Bitmap;

    /// <summary>识别这张贴图里的码，结果送给主窗口的识别页（对贴图做二次识别）。</summary>
    private async Task ScanAsync()
    {
        if (DisplayedBitmap is not { } image)
        {
            return;
        }

        var frame = await Task.Run(() =>
        {
            using var stream = new MemoryStream();
            image.Save(stream, new PngBitmapEncoderOptions());
            stream.Position = 0;
            return Core.Imaging.SkiaImageCodec.FromEncoded(stream);
        });

        if (Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow?.DataContext is not ViewModels.MainViewModel main)
        {
            return;
        }

        await main.Scan.LoadAsync(frame, "Str.Scan.SourcePin");
        desktop.MainWindow.Activate();
    }
}
