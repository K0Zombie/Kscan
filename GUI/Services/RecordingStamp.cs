using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Kscan.Core.Configuration;
using Kscan.Platform;
using Kscan.App.Views;

namespace Kscan.App.Services;

/// <summary>Optional capture-visible text; unrelated to the excluded recording controls.</summary>
internal sealed class RecordingStamp : IDisposable
{
    internal static string TextAt(NamingContext context, NamingSettings settings, DateTime now) =>
        FileNaming.Render(context with { Time = settings.LiveStamp ? now : context.Time }, settings, true);
    internal static Window CreateOverlay(ScreenInfo screen, PixelRect region, NamingSettings settings, string text)
    {
        var window = new StampWindow(screen.Bounds, screen.Scaling, region, settings); window.Update(text); return window;
    }
    private readonly List<StampWindow> _windows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _disposed;
    internal RecordingStamp(PixelRect region, NamingContext context, NamingSettings settings)
    {
        Dispatcher.UIThread.VerifyAccess();
        try
        {
            foreach (var screen in TopLevelAccessor.GetScreens()!.All.Where(s => s.Bounds.Intersects(region)))
            {
                var window = new StampWindow(screen.Bounds, screen.Scaling, region, settings);
                _windows.Add(window); window.Show();
                if (window.ActualTransparencyLevel != WindowTransparencyLevel.Transparent
                    || !PlatformServices.ClickThrough.TrySetEnabled(window.TryGetPlatformHandle()!.Handle, true))
                    throw new InvalidOperationException("无法启用录制文字的透明穿透图层。");
            }
            void Update()
            {
                var text = TextAt(context, settings, DateTime.Now);
                foreach (var window in _windows) window.Update(text);
            }
            Update();
            _timer.Tick += (_, _) => { try { Update(); } catch (Exception ex) { GuiLog.Write($"recording: stamp failed {ex}"); _timer.Stop(); } };
            if (settings.LiveStamp) _timer.Start();
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        void Close() { if (_disposed) return; _disposed = true; _timer.Stop(); foreach (var window in _windows) window.Close(); _windows.Clear(); }
        if (Dispatcher.UIThread.CheckAccess()) Close(); else Dispatcher.UIThread.Post(Close);
    }
    private sealed class StampWindow : Window
    {
        private readonly TextBlock _text;
        private readonly PixelRect _screen, _region;
        private readonly double _scale;
        private readonly NamingSettings _settings;
        internal StampWindow(PixelRect screen, double scale, PixelRect region, NamingSettings settings)
        {
            _screen = screen; _region = region; _scale = scale; _settings = settings;
            WindowDecorations = WindowDecorations.None; ShowActivated = false; Focusable = false;
            CanResize = false; ShowInTaskbar = false; Topmost = true;
            Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Width = screen.Width / scale; Height = screen.Height / scale; Position = screen.Position;
            Bind(FontFamilyProperty, new DynamicResourceExtension("KscanFontFamily"));
            var color = Color.Parse(settings.StampColor);
            _text = new TextBlock { FontSize = settings.StampSize / scale, Foreground = new SolidColorBrush(color),
                Background = new SolidColorBrush(color.R + color.G + color.B > 384 ? Color.FromArgb(160, 0, 0, 0) : Color.FromArgb(160, 255, 255, 255)),
                Padding = new Thickness(4 / scale), ClipToBounds = true,
                Opacity = settings.StampOpacity, MaxWidth = Math.Max(1, (region.Width - 24) / scale), MaxHeight = Math.Max(1, (region.Height - 24) / scale), TextWrapping = TextWrapping.Wrap };
            Content = new Canvas { ClipToBounds = true, Children = { _text } };
            Win32Properties.AddWindowStylesCallback(this, (style, extended) => (style, extended | 0x080000A0u));
        }
        internal void Update(string text)
        {
            _text.Text = text; _text.Measure(Size.Infinity);
            var x = _settings.StampPosition.EndsWith("right") ? _region.Right - 12 - _text.DesiredSize.Width * _scale : _region.X + 12;
            var y = _settings.StampPosition.StartsWith("bottom") ? _region.Bottom - 12 - _text.DesiredSize.Height * _scale : _region.Y + 12;
            Canvas.SetLeft(_text, (Math.Max(_region.X, x) - _screen.X) / _scale);
            Canvas.SetTop(_text, (Math.Max(_region.Y, y) - _screen.Y) / _scale);
        }
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e); SetWindowPos(TryGetPlatformHandle()!.Handle, new IntPtr(-1), _screen.X, _screen.Y, _screen.Width, _screen.Height, 0x10);
        }
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
    }
}
