using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Kscan.Platform;
using System.Runtime.InteropServices;

namespace Kscan.App.Views;

/// <summary>每屏一个遮罩，通过原生窗口区域挖空采集区和工具栏，避免细条窗口的最小尺寸限制。</summary>
internal static class ScrollCaptureShade
{
    internal static IEnumerable<PixelRect> Outside(PixelRect screen, PixelRect region)
    {
        var hole = screen.Intersect(region);
        if (hole.Width <= 0 || hole.Height <= 0) { yield return screen; yield break; }
        var pieces = new[]
        {
            new PixelRect(screen.X, screen.Y, screen.Width, hole.Y - screen.Y),
            new PixelRect(screen.X, hole.Bottom, screen.Width, screen.Bottom - hole.Bottom),
            new PixelRect(screen.X, hole.Y, hole.X - screen.X, hole.Height),
            new PixelRect(hole.Right, hole.Y, screen.Right - hole.Right, hole.Height),
        };
        foreach (var piece in pieces)
            if (piece.Width > 0 && piece.Height > 0) yield return piece;
    }

    internal static IEnumerable<PixelRect> Outside(PixelRect screen, PixelRect region, PixelRect toolbar) =>
        Outside(screen, region).SelectMany(piece => Outside(piece, toolbar));

    public static List<Window> Show(IEnumerable<(PixelRect Bounds, double Scaling)> screens, PixelRect region, PixelRect toolbar, PixelRect? preview = null)
    {
        var windows = new List<Window>();
        try
        {
            foreach (var (bounds, scale) in screens)
            {
                // 不再把 10px 间隙创建成独立顶层窗口：Windows 可能把它撑大，盖住工具栏。
                var window = new Window
                {
                    WindowDecorations = WindowDecorations.None, CanResize = false,
                    Topmost = true, ShowInTaskbar = false, ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                    Background = Brushes.Transparent,
                    Width = bounds.Width / scale, Height = bounds.Height / scale,
                    MinWidth = 0, MinHeight = 0, Position = bounds.Position,
                    Content = new ShadeSurface(bounds, region, scale),
                };
                windows.Add(window);
                window.Show();
                window.Width = bounds.Width / scale;
                window.Height = bounds.Height / scale;
                window.Position = bounds.Position;
                var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (OperatingSystem.IsWindows()) ApplyClip(handle, bounds, region, toolbar, preview);
                // 不依赖多个置顶窗口的相对顺序；遮罩永远不截获鼠标。
                if (OperatingSystem.IsWindows() &&
                    !PlatformServices.ClickThrough.TrySetEnabled(handle, true))
                    throw new InvalidOperationException("无法启用滚动截图遮罩鼠标穿透，已取消采集");
            }
            return windows;
        }
        catch
        {
            foreach (var window in windows) window.Close();
            throw;
        }
    }

    internal static IntPtr CreateClip(PixelRect screen, PixelRect region, PixelRect toolbar, PixelRect? preview = null)
    {
        var clip = CreateRectRgn(0, 0, 0, 0);
        if (clip == IntPtr.Zero) throw new InvalidOperationException("无法创建遮罩区域");
        try
        {
            var pieces = Outside(screen, region, toolbar);
            if (preview is { } previewRect) pieces = pieces.SelectMany(piece => Outside(piece, previewRect));
            foreach (var piece in pieces)
            {
                var part = CreateRectRgn(piece.X - screen.X, piece.Y - screen.Y,
                    piece.Right - screen.X, piece.Bottom - screen.Y);
                if (part == IntPtr.Zero) throw new InvalidOperationException("无法创建遮罩分区");
                try
                {
                    if (CombineRgn(clip, clip, part, 2 /* RGN_OR */) == 0)
                        throw new InvalidOperationException("无法合并遮罩区域");
                }
                finally { DeleteObject(part); }
            }
            return clip;
        }
        catch { DeleteObject(clip); throw; }
    }

    private static void ApplyClip(IntPtr handle, PixelRect screen, PixelRect region, PixelRect toolbar, PixelRect? preview)
    {
        var clip = CreateClip(screen, region, toolbar, preview);
        // 成功后区域句柄由系统持有，失败时仍由调用方释放。
        if (SetWindowRgn(handle, clip, true) == 0)
        {
            DeleteObject(clip);
            throw new InvalidOperationException("无法裁切滚动截图遮罩，已取消采集");
        }
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    private sealed class ShadeSurface(PixelRect piece, PixelRect region, double scale) : Control
    {
        public override void Render(DrawingContext context)
        {
            using (context.PushClip(new Rect(Bounds.Size)))
            {
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), new Rect(Bounds.Size));
                context.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 4 / scale),
                    new Rect((region.X - piece.X) / scale, (region.Y - piece.Y) / scale,
                        region.Width / scale, region.Height / scale));
            }
        }
    }
}
