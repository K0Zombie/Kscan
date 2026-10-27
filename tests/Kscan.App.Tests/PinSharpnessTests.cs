using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kscan.App.Views;

namespace Kscan.App.Tests;

/// <summary>
/// 贴图"清不清晰"的回归：旋转前后都必须是**像素对像素**，不能被重采样。
/// <para>
/// 做法：贴一张 1px 棋盘格（最怕重采样），渲染取帧后数中间色像素。
/// 1:1 直出时中间色必须是 0；只要窗口比位图多/少 1px、或被拉伸 1.5 倍，
/// 中间色就会大面积出现 —— 那就是用户看到的"旋转后变糊"。
/// </para>
/// <para>
/// 这个测试抓到过：旋转 90° 时 <c>cos(90°)=6.1e-17</c> 让宽度算成
/// <c>100.00000000000001</c>，平台按物理像素向上取整成 101（位图只有 100），
/// 整张图被拉伸 1px → 实测 100% 像素变中间色、肉眼就是糊。
/// </para>
/// </summary>
public sealed class PinSharpnessTests
{
    [AvaloniaFact]
    public void Quarter_turn_stays_pixel_perfect_at_100_percent()
    {
        using var pin = new Pin(200, 100, scaling: 1.0);
        pin.AssertPixelPerfect("before");

        pin.Press(Key.E);
        pin.AssertPixelPerfect("after E");
    }

    [AvaloniaFact]
    public void Quarter_turn_stays_pixel_perfect_at_125_percent()
    {
        // 125% 屏上，"贴回原位"的物理像素就是图像像素：_zoom = 1/1.25，仍是 1:1 物理
        using var pin = new Pin(200, 100, scaling: 1.25);
        pin.AssertPixelPerfect("before");

        pin.Press(Key.Q);
        pin.AssertPixelPerfect("after Q");
    }

    private sealed class Pin : IDisposable
    {
        private readonly Bitmap _bitmap;
        private readonly int _pixelWidth;
        private readonly int _pixelHeight;

        public Pin(int pixelWidth, int pixelHeight, double scaling)
        {
            _pixelWidth = pixelWidth;
            _pixelHeight = pixelHeight;
            _bitmap = NewCheckerboard(pixelWidth, pixelHeight);

            Window = new PinWindow();
            Window.SetImage(_bitmap, "checker.png");

            // 目标区域用**物理像素**给（就是图像像素数），配合该屏缩放 → 一个图像像素对一个屏幕物理像素
            Window.SetPlacement(new PixelRect(100, 100, pixelWidth, pixelHeight), scaling);
            Window.SetRenderScaling(scaling);
            Window.Show();
            Pump();
        }

        public PinWindow Window { get; }

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

        /// <summary>窗口物理尺寸必须等于当前显示位图的像素尺寸，且渲染里没有任何中间色。</summary>
        public void AssertPixelPerfect(string step)
        {
            var scaling = Window.RenderScaling > 0 ? Window.RenderScaling : 1.0;
            var bitmap = Window.DisplayedBitmap
                         ?? throw new InvalidOperationException($"{step}: 没有显示位图");

            var physicalWidth = (int)Math.Round(Window.ClientSize.Width * scaling);
            var physicalHeight = (int)Math.Round(Window.ClientSize.Height * scaling);

            var (pure, mixed) = SampleInterior();

            Assert.True(
                physicalWidth == bitmap.PixelSize.Width && physicalHeight == bitmap.PixelSize.Height,
                $"{step}: 窗口物理 {physicalWidth}x{physicalHeight} ≠ 位图 {bitmap.PixelSize} " +
                $"(DIP {Window.ClientSize}, scale {scaling}) —— 差 1px 就会整张重采样变糊");

            Assert.True(
                mixed == 0,
                $"{step}: 有 {mixed} 个中间色像素（纯色 {pure}）—— 图片被重采样了，看起来就是糊");
        }

        /// <summary>只数**画面内侧**：避开"刚贴上"的 3px 蓝框和旋转提示胶囊（那是 UI，不是图片）。</summary>
        private (int Pure, int Mixed) SampleInterior()
        {
            using var frame = Window.CaptureRenderedFrame()
                              ?? throw new InvalidOperationException("no frame");

            var width = frame.PixelSize.Width;
            var height = frame.PixelSize.Height;
            var stride = width * 4;
            var buffer = new byte[stride * height];

            using (var fb = frame.Lock())
            {
                for (var y = 0; y < height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        fb.Address + (y * fb.RowBytes), buffer, y * stride, stride);
                }
            }

            var pure = 0;
            var mixed = 0;

            var x0 = 6;
            var x1 = Math.Max(x0 + 1, width - 6);
            var y0 = Math.Max(6, height / 2);
            var y1 = Math.Max(y0 + 1, height - 6);

            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var i = (y * stride) + (x * 4);
                    var b = buffer[i];
                    var g = buffer[i + 1];
                    var r = buffer[i + 2];
                    var a = buffer[i + 3];

                    if (a < 32)
                    {
                        continue;
                    }

                    // 棋盘只有纯黑与纯白两种颜色
                    var isBlack = r < 8 && g < 8 && b < 8;
                    var isWhite = r > 247 && g > 247 && b > 247;

                    if (isBlack || isWhite)
                    {
                        pure++;
                    }
                    else
                    {
                        mixed++;
                    }
                }
            }

            return (pure, mixed);
        }

        public void Dispose()
        {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            _bitmap.Dispose();
        }

        private static Bitmap NewCheckerboard(int width, int height)
        {
            using var skia = new SkiaSharp.SKBitmap(width, height);
            skia.Erase(SkiaSharp.SKColors.Black);

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (((x + y) & 1) == 0)
                    {
                        skia.SetPixel(x, y, SkiaSharp.SKColors.White);
                    }
                }
            }

            using var encoded = skia.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(encoded.ToArray());

            return new Bitmap(stream);
        }
    }
}
