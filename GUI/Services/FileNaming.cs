using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Kscan.Core.Configuration;

namespace Kscan.App.Services;

public static class FileNaming
{
    private static long _counter;
    private static readonly object PathGate = new();
    private static readonly HashSet<string> ReservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Func<NamingContext, string>> Tokens = new(StringComparer.Ordinal);
    public static void RegisterToken(string name, Func<NamingContext, string> resolver)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^custom_[a-zA-Z0-9_]+$")) throw new ArgumentException("扩展变量必须以 custom_ 开头。");
        Tokens[name] = resolver;
    }
    public static NamingContext Context(string kind, int width = 0, int height = 0, string title = "", string extension = "") =>
        new(kind, DateTime.Now, width, height, Interlocked.Increment(ref _counter), string.IsNullOrEmpty(title) ? ForegroundTitle() : title, extension.TrimStart('.'));
    public static string Render(NamingContext context, NamingSettings? settings = null, bool stamp = false)
    {
        settings ??= AppSettingsService.Current.Settings.Naming ?? new();
        var template = stamp ? settings.StampTemplate : settings.Templates?.GetValueOrDefault(context.Type) ?? NamingSettings.DefaultTemplate;
        try { return NameTemplate.Render(template, context, !stamp, (token, c) => Tokens.TryGetValue(token, out var resolver) ? resolver(c) : null); }
        catch (Exception ex)
        {
            GuiLog.Write($"naming: invalid rule, using default: {ex.Message}");
            return NameTemplate.Render(stamp ? "{date:yyyy-MM-dd} {time:HH:mm:ss}" : NamingSettings.DefaultTemplate, context, !stamp);
        }
    }
    public static string Suggested(string kind, string extension, int width = 0, int height = 0, string title = "") =>
        Render(Context(kind, width, height, title, extension)) + "." + extension.TrimStart('.');
    private static string NextPath(string directory, string extension, NamingContext context)
    {
        directory = Path.GetFullPath(directory);
        var name = Render(context);
        var path = Path.Combine(directory, name + extension);
        for (var n = 1; File.Exists(path) || File.Exists(Path.ChangeExtension(path, ".partial" + extension)) || ReservedPaths.Contains(path); n++)
            path = Path.Combine(directory, name + "-" + n + extension);
        return path;
    }
    internal sealed class PathLease(string path) : IDisposable
    {
        private int _disposed;
        internal string Path => path;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; lock (PathGate) ReservedPaths.Remove(path); }
    }
    internal static PathLease ReservePath(string directory, string extension, NamingContext context)
    {
        lock (PathGate)
        {
            var path = NextPath(directory, extension, context); ReservedPaths.Add(path); return new PathLease(path);
        }
    }
    private static string ForegroundTitle()
    {
        if (!OperatingSystem.IsWindows()) return "";
        var text = new StringBuilder(512);
        return GetWindowText(GetForegroundWindow(), text, text.Capacity) > 0 ? text.ToString() : "";
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int length);
}
