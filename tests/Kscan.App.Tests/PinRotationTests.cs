using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kscan.App.Views;

namespace Kscan.App.Tests;

/// <summary>
/// 贴图旋转的几何回归：连按 Q/E 不能漂（交接文档里"最急"的那条）。
/// <para>
/// 跑的是真实的窗口生命周期（Show → 摆尺寸/位置 → 键鼠事件），断言
/// <b>"窗口视觉中心"在任意次旋转后都不动</b>。以前中心是每次用
/// <c>Position + Width/2</c> 反算的，<c>Position</c> 又是整数物理像素，
/// 90° 交换宽高后来回取整会一路累积 —— 表现就是"越转越偏"。
/// </para>
/// <para>
/// 为什么这些断言能抓住它：锚点模型下，转 4 次必须**逐像素**回到原矩形。
/// 这套测试第一次跑就抓到了两个真问题：0.5px 的银行家舍入会让 Q 一次偏 1px；
/// 屏幕缩放变化（窗口被拖到别的屏）后锚点没重新同步会让中心直接飞掉。
/// </para>
/// </summary>
public sealed class PinRotationTests
{
    [AvaloniaFact]
    public void Four_quarter_turns_return_to_the_exact_same_frame()
    {
        using var pin = new Pin(800, 400, new PixelRect(200, 120, 800, 400), scaling: 1.0);
        var start = pin.Frame();

        for (var i = 0; i < 4; i++)
        {
            pin.Press(Key.E);
            pin.AssertCenterUnchanged(start, $"E x{i + 1}");
        }

        Assert.Equal(start, pin.Frame());
        pin.AssertSizeApplied();
    }

    [AvaloniaFact]
    public void Alternating_q_and_e_never_drifts()
    {
        using var pin = new Pin(640, 480, new PixelRect(300, 200, 640, 480), scaling: 1.0);
        var start = pin.Frame();

        // Q/E 交替 8 次：每次中心都不许动，最后应精确回到原矩形
        for (var i = 0; i < 8; i++)
        {
            pin.Press(i % 2 == 0 ? Key.Q : Key.E);
            pin.AssertCenterUnchanged(start, $"Q/E x{i + 1}");
        }

        Assert.Equal(start, pin.Frame());
    }

    [AvaloniaFact]
    public void Rotation_keeps_center_at_125_percent_scaling()
    {
        using var pin = new Pin(800, 400, new PixelRect(160, 96, 1000, 500), scaling: 1.0);
        pin.Window.SetRenderScaling(1.25);
        pin.Pump();

        var start = pin.Frame();

        for (var i = 0; i < 4; i++)
        {
            pin.Press(Key.E);
            pin.AssertCenterUnchanged(start, $"E x{i + 1} @1.25");
        }

        Assert.Equal(start, pin.Frame());
    }

    [AvaloniaFact]
    public void Zoom_keeps_the_point_under_the_cursor_and_is_reversible()
    {
        using var pin = new Pin(800, 400, new PixelRect(200, 120, 800, 400), scaling: 1.0);
        var start = pin.Frame();

        // 屏幕上的**固定**点（窗口中心）：真实滚轮事件里光标在屏幕上不动，
        // 窗口长大之后它的"窗口内坐标"会变 —— 测试必须按这个语义算，否则测的是假场景。
        var screenPoint = new Point(start.X + start.Width / 2.0, start.Y + start.Height / 2.0);

        pin.WheelAtScreen(screenPoint, +1);

        var grown = pin.Frame();
        Assert.True(grown.Width > start.Width, $"滚轮向上应放大：{start} -> {grown}");
        pin.AssertCenterUnchanged(start, "zoom in", tolerance: 1.0);

        pin.WheelAtScreen(screenPoint, -1);

        var back = pin.Frame();
        Assert.True(Math.Abs(back.Width - start.Width) <= 1, $"放大再缩小应回到原宽度：{start.Width} -> {back.Width}");
        Assert.True(Math.Abs(back.Height - start.Height) <= 1, $"放大再缩小应回到原高度：{start.Height} -> {back.Height}");
        pin.AssertCenterUnchanged(start, "zoom in + out", tolerance: 1.0);
    }

    [AvaloniaFact]
    public void Rotating_a_zoomed_pin_keeps_its_center()
    {
        using var pin = new Pin(800, 400, new PixelRect(200, 120, 800, 400), scaling: 1.0);
        var start0 = pin.Frame();
        var screenPoint = new Point(start0.X + start0.Width / 2.0, start0.Y + start0.Height / 2.0);

        for (var i = 0; i < 4; i++)
        {
            pin.WheelAtScreen(screenPoint, +1);
        }

        var start = pin.Frame();

        for (var i = 0; i < 4; i++)
        {
            pin.Press(Key.Q);
            pin.AssertCenterUnchanged(start, $"Q x{i + 1} (zoomed)");
        }

        Assert.Equal(start, pin.Frame());
    }

    private sealed class Pin : IDisposable
    {
        private readonly Bitmap _bitmap;

        public Pin(int pixelWidth, int pixelHeight, PixelRect region, double scaling)
        {
            _bitmap = NewBitmap(pixelWidth, pixelHeight);

            Window = new PinWindow();
            Window.SetImage(_bitmap, "test.png");
            Window.SetPlacement(region, scaling);
            Window.Show();
            Pump();

            Window.Focus();
            Pump();
        }

        public PinWindow Window { get; }

        public (int X, int Y, int Width, int Height) Frame()
        {
            var scaling = Window.RenderScaling > 0 ? Window.RenderScaling : 1.0;

            var width = Math.Max(1, (int)Math.Round(Window.Width * scaling));
            var height = Math.Max(1, (int)Math.Round(Window.Height * scaling));

            return (Window.Position.X, Window.Position.Y, width, height);
        }

        /// <summary>
        /// headless 里"平台窗口尺寸"要等一次渲染/layout 才会跟上 <c>Width</c>/<c>Height</c>；
        /// 不 pump 的话命中测试还按旧尺寸，滚轮事件会被直接丢掉（不是产品代码的问题）。
        /// </summary>
        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        public void Press(Key key)
        {
            var physical = key switch
            {
                Key.Q => PhysicalKey.Q,
                Key.E => PhysicalKey.E,
                _ => PhysicalKey.None,
            };

            Window.KeyPress(key, RawInputModifiers.None, physical, key.ToString());
            Window.KeyRelease(key, RawInputModifiers.None, physical, key.ToString());
            Pump();
        }

        public void Wheel(Point at, int direction)
        {
            // 先移动指针再滚轮：headless 的命中测试按"指针当前位置"算，
            // 不移动的话窗口长大/挪位之后的滚轮会被丢掉（这是 headless 的怪癖，不是产品行为）。
            Window.MouseMove(at);
            Pump();
            Window.MouseWheel(at, new Vector(0, direction), RawInputModifiers.None);
            Pump();
        }

        /// <summary>按"屏幕上固定一点"滚轮：每次先把屏幕坐标换算成该时刻的窗口内 DIP 坐标。</summary>
        public void WheelAtScreen(Point screenPoint, int direction)
        {
            var scaling = Window.RenderScaling > 0 ? Window.RenderScaling : 1.0;
            var local = new Point(
                (screenPoint.X - Window.Position.X) / scaling,
                (screenPoint.Y - Window.Position.Y) / scaling);

            Wheel(local, direction);
        }

        /// <summary>
        /// 窗口视觉中心不许动。允许 <paramref name="tolerance"/> 的整数取整噪声
        /// （物理宽高奇偶不同时半个像素的方向会变），但**绝不允许随次数累积**。
        /// </summary>
        public void AssertCenterUnchanged((int X, int Y, int Width, int Height) start, string step, double tolerance = 0.5)
        {
            var current = Frame();

            var startX = start.X + start.Width / 2.0;
            var startY = start.Y + start.Height / 2.0;
            var currentX = current.X + current.Width / 2.0;
            var currentY = current.Y + current.Height / 2.0;

            Assert.True(
                Math.Abs(currentX - startX) <= tolerance && Math.Abs(currentY - startY) <= tolerance,
                $"{step}: 中心漂了 start=({startX},{startY}) now=({currentX},{currentY}) " +
                $"frame {start} -> {current}");
        }

        /// <summary>请求的尺寸必须真的落到窗口上（否则后续命中测试/摆位全按旧尺寸算）。</summary>
        public void AssertSizeApplied()
        {
            Assert.True(
                Math.Abs(Window.ClientSize.Width - Window.Width) <= 1 &&
                Math.Abs(Window.ClientSize.Height - Window.Height) <= 1,
                $"窗口尺寸没落地：Width={Window.Width} Height={Window.Height} ClientSize={Window.ClientSize}");
        }

        public void Dispose()
        {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            _bitmap.Dispose();
        }

        private static Bitmap NewBitmap(int width, int height)
        {
            using var skia = new SkiaSharp.SKBitmap(width, height);
            skia.Erase(SkiaSharp.SKColors.SteelBlue);

            using var encoded = skia.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(encoded.ToArray());

            return new Bitmap(stream);
        }
    }
}
