using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.Platform;

namespace Kscan.App.Views;

/// <summary>Passive live borders: no screenshot, input activation, or screenshot selector.</summary>
internal static class RecordingRegionSelector
{
    internal static SelectionWindow CreateOverlay(ScreenInfo screen) => new(screen);
    internal static async Task<PixelRect?> SelectAsync(IReadOnlyList<ScreenInfo> screens)
    {
        if (screens.Count == 0) return null;
        var desktop = new PixelRect(screens.Min(s => s.Bounds.X), screens.Min(s => s.Bounds.Y),
            screens.Max(s => s.Bounds.Right) - screens.Min(s => s.Bounds.X),
            screens.Max(s => s.Bounds.Bottom) - screens.Min(s => s.Bounds.Y));
        var completion = new TaskCompletionSource<PixelRect?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new List<SelectionWindow>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        RecordingSelectionInput? input = null;
        PixelPoint? start = null;
        PixelRect? hovered = null;
        var lastHover = 0L;
        var escapeWasDown = (GetAsyncKeyState(0x1b) & 0x8000) != 0;
        var foregroundBefore = GetForegroundWindow();
        PixelRect FindArea(PixelPoint position)
        {
            var ignore = windows.FirstOrDefault(w => w.Screen.Bounds.Contains(position))?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            var target = PlatformServices.ForegroundApp.GetWindowHandleAt(position.X, position.Y, ignore);
            return target != IntPtr.Zero && !windows.Any(w => w.TryGetPlatformHandle()?.Handle == target)
                && !PlatformServices.ForegroundApp.IsDesktopSurface(target) && !PlatformServices.ForegroundApp.IsTaskbar(target)
                && PlatformServices.ForegroundApp.GetRectOf(target) is { } rect
                ? new PixelRect(rect.X, rect.Y, rect.Width, rect.Height).Intersect(desktop)
                : screens.FirstOrDefault(s => s.Bounds.Contains(position)).Bounds;
        }
        try
        {
            foreach (var screen in screens)
            {
                var window = CreateOverlay(screen);
                windows.Add(window); window.Show();
                RecordingDisplay.Exclude(window);
                if (!PlatformServices.ClickThrough.TrySetEnabled(window.TryGetPlatformHandle()!.Handle, true))
                    throw new InvalidOperationException("录制选区层无法启用鼠标穿透。");
                if (window.ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
                    throw new InvalidOperationException("当前渲染器不支持实时透明框选。");
            }
            if (GetForegroundWindow() != foregroundBefore)
                throw new InvalidOperationException("录制框选未能保持前台窗口焦点，已取消。");
            input = new RecordingSelectionInput();
            GuiLog.Write($"recording: passive selection start foreground=0x{foregroundBefore:X} screens={screens.Count}; no desktop snapshot");
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (completion.Task.IsCompleted) return;
                    if (input.Error is { } error) throw error;
                    var escape = (GetAsyncKeyState(0x1b) & 0x8000) != 0;
                    if (escape && !escapeWasDown) { completion.TrySetResult(null); return; }
                    escapeWasDown = escape;
                    foreach (var (message, point) in input.Drain())
                    {
                        var current = new PixelPoint(point.X, point.Y);
                        if (message == RecordingSelectionInput.RightUp) { completion.TrySetResult(null); return; }
                        if (message == RecordingSelectionInput.LeftDown) { start = current; hovered = FindArea(current); }
                        if (message == RecordingSelectionInput.LeftUp && start is { } origin)
                        {
                            var selection = RecordingSelectionGeometry.IsDrag(origin, current)
                                ? RecordingSelectionGeometry.Drag(origin, current, desktop) : hovered;
                            completion.TrySetResult(selection is { Width: >= 16, Height: >= 16 } ? selection : null);
                            return;
                        }
                    }
                    if (PlatformServices.ForegroundApp.GetCursorPosition() is not { } cursor) return;
                    var position = new PixelPoint(cursor.X, cursor.Y);
                    PixelRect? area;
                    if (start is { } from) area = RecordingSelectionGeometry.Drag(from, position, desktop);
                    else
                    {
                        if (Environment.TickCount64 - lastHover >= 80)
                        {
                            lastHover = Environment.TickCount64;
                            hovered = FindArea(position);
                        }
                        area = hovered;
                    }
                    foreach (var window in windows) window.SetSelection(area);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            };
            timer.Start();
            return await completion.Task;
        }
        finally
        {
            timer.Stop(); input?.Dispose();
            foreach (var window in windows) window.Close();
            GuiLog.Write($"recording: passive selection closed foreground=0x{GetForegroundWindow():X}");
        }
    }

    internal sealed class SelectionWindow : Window
    {
        internal ScreenInfo Screen { get; }
        private readonly SelectionVisual _visual;
        internal SelectionWindow(ScreenInfo screen)
        {
            Screen = screen;
            WindowDecorations = WindowDecorations.None;
            ShowActivated = false; ShowInTaskbar = false; Focusable = false; CanResize = false; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Width = screen.Bounds.Width / screen.Scaling; Height = screen.Bounds.Height / screen.Scaling;
            Position = screen.Bounds.Position;
            _visual = new SelectionVisual(screen); Content = _visual;
            Win32Properties.AddWindowStylesCallback(this, NoActivation);
        }
        private static (uint, uint) NoActivation(uint style, uint extended) => (style, extended | 0x08000000u | 0x80u);
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            SetWindowPos(TryGetPlatformHandle()!.Handle, new IntPtr(-1), Screen.Bounds.X, Screen.Bounds.Y, Screen.Bounds.Width, Screen.Bounds.Height, 0x10);
        }
        internal void SetSelection(PixelRect? selection) { if (_visual.Selection == selection) return; _visual.Selection = selection; _visual.InvalidateVisual(); }
    }

    private sealed class SelectionVisual(ScreenInfo screen) : Control
    {
        internal PixelRect? Selection { get; set; }
        public override void Render(DrawingContext context)
        {
            if (Selection is not { } selection) return;
            var scale = screen.Scaling;
            var local = new Rect((selection.X - screen.Bounds.X) / scale, (selection.Y - screen.Bounds.Y) / scale,
                selection.Width / scale, selection.Height / scale);
            using (context.PushClip(new Rect(Bounds.Size))) context.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 2 / scale), local);
        }
    }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
