using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kscan.App.Views;
using Kscan.Core.Pins;
using Kscan.Platform;

namespace Kscan.App.Services;

/// <summary>贴图库里一张图的显示参数（位置/尺寸/朝向/透明度/置顶/穿透）。</summary>
internal sealed class PinLibraryEntry
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public double Rotation { get; set; }

    public bool MirrorH { get; set; }

    public bool MirrorV { get; set; }

    public double Opacity { get; set; } = 1;

    public bool Topmost { get; set; } = true;

    public bool ClickThrough { get; set; }
}

/// <summary><c>data\user\library.json</c> 的内容：分组 + 每张图的显示参数。</summary>
internal sealed class PinLibraryDocument
{
    public int Version { get; set; } = 1;

    public List<PinGroup> Groups { get; set; } = [];

    /// <summary>键 = <c>&lt;分组 id&gt;/&lt;文件名&gt;</c>（分组改名后键不变）。</summary>
    public Dictionary<string, PinLibraryEntry> Pins { get; set; } = [];
}

/// <summary>
/// 贴图库的一项：磁盘上的一张图，可能同时在屏幕上（<see cref="Window"/> 非空）。
/// <para>
/// 库的实体就是<b>文件夹里的图片文件</b>（用户可以直接往里丢/删），
/// 显示参数放在 <c>data\user\library.json</c>；没有参数记录的图用默认值贴出。
/// </para>
/// </summary>
public sealed class PinLibraryItem
{
    internal PinLibraryItem(PinGroup group, string fullPath, PinLibraryEntry entry)
    {
        Group = group;
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        Entry = entry;
    }

    public PinGroup Group { get; }

    public string GroupId => Group.Id;

    public string FullPath { get; }

    public string FileName { get; }

    /// <summary>显示参数（文件刚被丢进来时是默认值）。</summary>
    internal PinLibraryEntry Entry { get; set; }

    /// <summary>这张图当前在屏幕上的窗口；<c>null</c> = 只在库里。</summary>
    public PinWindow? Window => PinManager.Current.Windows.FirstOrDefault(
        window => string.Equals(window.LibraryPath, FullPath, StringComparison.OrdinalIgnoreCase));

    public bool OnScreen => Window is not null;

    internal string Key => Group.Id + "/" + FileName;
}

/// <summary>
/// 贴图库：分组 = <c>data\user\pins\&lt;分组名&gt;\</c> 一个目录，元数据在 <c>data\user\library.json</c>。
/// <para>
/// 只有<b>分过组</b>的贴图才在库里（未分组的散图关掉就没了）。
/// 用户自己往目录里丢图 / 从资源管理器删图都会实时生效（<see cref="FileSystemWatcher"/>）。
/// </para>
/// <para>
/// 收（ESC / Alt+Q）= 只关窗口，库文件与参数都留着；删（Delete）= 连文件带参数一起删。
/// </para>
/// </summary>
internal static class PinLibraryService
{
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif", ".apng", ".svg"];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly List<PinLibraryItem> Items = [];

    private static PinLibraryDocument _doc = new();

    private static FileSystemWatcher? _watcher;

    private static DispatcherTimer? _scanTimer;
    private static DispatcherTimer? _saveTimer;

    private static int _selfWriteDepth;

    private static bool _loaded;

    private static readonly Dictionary<string, (DateTime Stamp, Bitmap Bitmap)> Thumbs = [];

    /// <summary>"这个文件是不是动图"的缓存（管理窗口每次刷新都会问，别每次都去开头文件）。</summary>
    private static readonly Dictionary<string, (DateTime Stamp, bool Animated)> AnimatedFlags = [];

    /// <summary>库内容（文件增删/改名）变化。</summary>
    public static event EventHandler? Changed;

    /// <summary>测试用：把库根目录指到临时目录。</summary>
    internal static string? RootOverride { get; set; }

    /// <summary>库根目录（便携版 = <c>data\user\pins</c>）。</summary>
    public static string Root => RootOverride ?? Path.Combine(PlatformPaths.UserDirectory, "pins");

    /// <summary>动作被保留的目录（不当作分组）。</summary>
    private static bool IsReservedName(string name) => name.StartsWith('_') || name.StartsWith('.');

    private static string IndexFile => Path.Combine(Path.GetDirectoryName(Root) ?? Root, "library.json");

    private static void SelfWrite(Action action)
    {
        _selfWriteDepth++;

        try
        {
            action();
        }
        finally
        {
            _selfWriteDepth--;
        }
    }

    // ---------------- 加载 / 扫描 ----------------

    /// <summary>启动时调一次：读元数据、认领目录、挂监控。</summary>
    public static void Load()
    {
        if (_loaded)
        {
            Sync();
            return;
        }

        _loaded = true;

        try
        {
            Directory.CreateDirectory(Root);
            _doc = ReadDocument();

            // 先把分组灌进内存（ReplaceAll 会触发保存钩子，这里吞掉）
            SelfWrite(() => PinGroupStore.Current.ReplaceAll(_doc.Groups));

            Sync();
            StartWatcher();
        }
        catch (Exception ex)
        {
            GuiLog.Write($"library: 加载失败（{ex.GetType().Name}: {ex.Message}）");
        }

        GuiLog.Write($"library: loaded {Items.Count} item(s), {PinGroupStore.Current.Groups.Count} group(s) @ {Root}");
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>测试用：清空内存态并停掉监控/定时器（模拟"重启软件"时再调一次 <see cref="Load"/>）。</summary>
    internal static void ResetForTests()
    {
        _watcher?.Dispose();
        _watcher = null;
        _scanTimer?.Stop();
        _scanTimer = null;
        _saveTimer?.Stop();
        _saveTimer = null;

        _doc = new PinLibraryDocument();
        Items.Clear();
        Thumbs.Clear();
        _selfWriteDepth = 0;
        _loaded = false;
        RootOverride = null;
    }

    /// <summary>当前库里的全部图（按分组顺序）。</summary>
    public static IReadOnlyList<PinLibraryItem> All => Items;

    /// <summary>某个分组在库里的图。</summary>
    public static IReadOnlyList<PinLibraryItem> ItemsOf(string? groupId) =>
        groupId is null
            ? []
            : Items.Where(item => string.Equals(item.GroupId, groupId, StringComparison.Ordinal)).ToList();

    /// <summary>按 <c>&lt;分组 id&gt;/&lt;文件名&gt;</c> 的键找一项。</summary>
    public static PinLibraryItem? Find(string? key) =>
        key is null ? null : Items.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));

    public static PinLibraryItem? FindByPath(string? path) =>
        path is null
            ? null
            : Items.FirstOrDefault(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>各分组的目录（不存在就建）。</summary>
    public static string GroupDirectory(PinGroup group)
    {
        var dir = Path.Combine(Root, group.Name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 扫一遍磁盘：把新目录认领成新分组、给空分组补目录、重新收集图片、清掉失效的元数据。
    /// </summary>
    public static void Sync()
    {
        try
        {
            Directory.CreateDirectory(Root);

            var groups = PinGroupStore.Current.Groups.ToList();

            // 1) 用户自己新建/改名的目录 → 当作新分组（参数用默认值）
            foreach (var dir in Directory.GetDirectories(Root))
            {
                var name = Path.GetFileName(dir);

                if (IsReservedName(name))
                {
                    continue;
                }

                if (groups.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                SelfWrite(() => PinGroupStore.Current.Create(name));
                GuiLog.Write($"library: 发现新目录「{name}」，已识别为新分组");
                groups = PinGroupStore.Current.Groups.ToList();
            }

            // 2) 每个分组都要有目录（空分组也保留）
            foreach (var group in PinGroupStore.Current.Groups)
            {
                Directory.CreateDirectory(Path.Combine(Root, group.Name));
            }

            // 3) 收集图片
            Items.Clear();

            foreach (var group in PinGroupStore.Current.Groups)
            {
                var dir = Path.Combine(Root, group.Name);

                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var file in Directory.GetFiles(dir))
                {
                    if (!IsImage(file))
                    {
                        continue;
                    }

                    var key = group.Id + "/" + Path.GetFileName(file);

                    if (!_doc.Pins.TryGetValue(key, out var entry))
                    {
                        entry = new PinLibraryEntry();
                        _doc.Pins[key] = entry;
                    }

                    Items.Add(new PinLibraryItem(group, file, entry));
                }
            }

            // 4) 文件已经不在的元数据清掉
            var alive = Items.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dead = _doc.Pins.Keys.Where(key => !alive.Contains(key)).ToList();

            foreach (var key in dead)
            {
                _doc.Pins.Remove(key);
            }

            if (dead.Count > 0)
            {
                WriteDocument();
            }
        }
        catch (Exception ex)
        {
            GuiLog.Write($"library: 扫描失败（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    private static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // ---------------- 监控 ----------------

    private static void StartWatcher()
    {
        if (_watcher is not null || !Directory.Exists(Root))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            watcher.Created += (_, _) => ScheduleScan();
            watcher.Deleted += (_, _) => ScheduleScan();
            watcher.Changed += (_, _) => ScheduleScan();
            watcher.Renamed += (_, _) => ScheduleScan();

            _watcher = watcher;
        }
        catch (Exception ex)
        {
            GuiLog.Write($"library: 目录监控启动失败（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    private static void ScheduleScan()
    {
        if (_selfWriteDepth > 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _scanTimer ??= CreateScanTimer();
            _scanTimer.Stop();
            _scanTimer.Start();
        });
    }

    private static DispatcherTimer CreateScanTimer()
    {
        // 防抖：用户往目录里拖一张大图会连发好几个事件，也避开"文件还没写完"
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Sync();
            Changed?.Invoke(null, EventArgs.Empty);
        };

        return timer;
    }

    // ---------------- 参数落盘 ----------------

    /// <summary>把某个窗口的当前状态写进库参数（不涉及文件本身）。</summary>
    public static void SaveWindowState(PinWindow window, bool immediate = false)
    {
        if (window.LibraryPath is not { Length: > 0 } path || window.GroupId is not { Length: > 0 } groupId)
        {
            return;
        }

        var rect = window.GetScreenRect();
        var key = groupId + "/" + Path.GetFileName(path);

        // (0,0) 是合法位置（主屏左上角），但历史上"没摆过位"也会是 (0,0)；
        // 记一行日志，便于下次从 gui.log 判断是"用户真放在左上角"还是"没摆位"。
        if (rect.X == 0 && rect.Y == 0)
        {
            GuiLog.Write($"library: 保存参数时位置为 (0,0)（{Path.GetFileName(path)}，size {rect.Width}x{rect.Height}）");
        }

        _doc.Pins[key] = new PinLibraryEntry
        {
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
            Rotation = window.RotationDegrees,
            MirrorH = window.IsMirroredHorizontally,
            MirrorV = window.IsMirroredVertically,
            Opacity = window.Opacity,
            Topmost = window.IsTopmost,
            ClickThrough = window.IsClickThrough,
        };

        // 管理器保留的库项也要同步，否则收起后立即重贴会读到旧位置。
        if (FindByPath(path) is { } item) item.Entry = _doc.Pins[key];

        if (immediate)
        {
            WriteDocument();
        }
        else
        {
            ScheduleSave();
        }
    }

    private static void ScheduleSave()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _saveTimer ??= CreateSaveTimer();
            _saveTimer.Stop();
            _saveTimer.Start();
        });
    }

    private static DispatcherTimer CreateSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            WriteDocument();
        };

        return timer;
    }

    /// <summary>分组列表变了（新建/改名/改颜色/删除）→ 落盘 + 同步目录。</summary>
    public static void SaveGroups()
    {
        if (_selfWriteDepth > 0)
        {
            return;
        }

        WriteDocument();
    }

    private static void WriteDocument()
    {
        try
        {
            _doc.Groups = PinGroupStore.Current.Groups.ToList();

            var directory = Path.GetDirectoryName(IndexFile);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_doc, Json);
            var temp = IndexFile + ".tmp";

            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, IndexFile, overwrite: true);
        }
        catch (Exception ex)
        {
            GuiLog.Write($"library: 元数据写入失败（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    private static PinLibraryDocument ReadDocument()
    {
        try
        {
            if (!File.Exists(IndexFile))
            {
                return new PinLibraryDocument();
            }

            return JsonSerializer.Deserialize<PinLibraryDocument>(File.ReadAllText(IndexFile), Json)
                   ?? new PinLibraryDocument();
        }
        catch (Exception ex)
        {
            GuiLog.Write($"library: 元数据读取失败，按空库处理（{ex.GetType().Name}: {ex.Message}）");
            return new PinLibraryDocument();
        }
    }

    // ---------------- 加入 / 移出分组 ----------------

    /// <summary>
    /// 贴图被归到某个分组：把 PNG 写进该分组目录（或把已有文件搬过去），并记下参数。
    /// <paramref name="previousGroupId"/> 用来清掉旧分组下的参数（换组时）。
    /// </summary>
    public static void Attach(PinWindow window, string? previousGroupId, string? groupId)
    {
        if (window.OriginalBitmap is not { } bitmap)
        {
            return;
        }

        // 移出分组 = 从库里删除（未分组的图不进库）
        if (groupId is null)
        {
            Detach(window, previousGroupId);
            return;
        }

        var group = PinGroupStore.Current.Find(groupId);

        if (group is null)
        {
            return;
        }

        var directory = GroupDirectory(group);
        var current = window.ImageFilePath;

        // 已经在同一个目录：什么都不用搬，只更新参数
        if (current is not null && File.Exists(current) &&
            string.Equals(Path.GetDirectoryName(current), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
        {
            window.SetGroupSilently(groupId);
            window.LibraryPath = current;
            window.RetainedSourcePath = null;
            SaveWindowState(window, immediate: true);
            Sync();
            return;
        }

        SelfWrite(() =>
        {
            if (current is not null && File.Exists(current))
            {
                // 换组：文件跟着搬过去（名字冲突就加后缀）
                var target = Path.Combine(directory, UniqueName(directory, Path.GetFileName(current)));
                File.Move(current, target);

                if (previousGroupId is { Length: > 0 } old)
                {
                    _doc.Pins.Remove(old + "/" + Path.GetFileName(current));
                }

                window.LibraryPath = target;
            }
            else
            {
                if (window.IsAnimated) throw new FileNotFoundException("动图原文件不可用，无法保留动画；已取消归组。", current);
                var name = UniqueName(directory, SuggestName(window.SourceName));
                var path = Path.Combine(directory, name);

                bitmap.Save(path, new PngBitmapEncoderOptions());
                window.LibraryPath = path;
            }

            window.SetGroupSilently(groupId);
            window.RetainedSourcePath = null;
            SaveWindowState(window, immediate: true);
        });

        WriteDocument();
        Sync();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 移出分组：把库文件挪进 <c>_trash</c>（不进回收站之外的任何地方），贴图本身留在屏幕上。
    /// <para>
    /// 不直接删：拖错一下就把图删掉太狠了；<c>_trash</c> 不参与扫描，等于"已移出库"。
    /// </para>
    /// </summary>
    private static void Detach(PinWindow window, string? previousGroupId)
    {
        var current = window.LibraryPath;

        if (current is null)
        {
            return;
        }

        SelfWrite(() =>
        {
            var retained = current;
            if (File.Exists(current))
            {
                var trash = Path.Combine(Root, "_trash");
                Directory.CreateDirectory(trash);
                retained = Path.Combine(trash, UniqueName(trash, Path.GetFileName(current)));
                File.Move(current, retained);
            }

            if (previousGroupId is { Length: > 0 } old)
            {
                _doc.Pins.Remove(old + "/" + Path.GetFileName(current));
            }

            window.RetainedSourcePath = retained;
            window.LibraryPath = null;
        });

        WriteDocument();
        Sync();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // ---------------- 删除 ----------------

    /// <summary>删掉库里的这一张（文件 + 参数 + 屏幕上的窗口）。</summary>
    public static void DeleteItem(PinLibraryItem item)
    {
        if (item.Window is { } window)
        {
            // 先落一次状态，避免"刚转完就删"留下脏参数（其实马上要删掉，纯属防万一）
            SaveWindowState(window, immediate: false);
            window.LibraryPath = null;   // 别让它再往回写
            window.Close();
        }

        SelfWrite(() =>
        {
            try
            {
                if (File.Exists(item.FullPath))
                {
                    File.Delete(item.FullPath);
                }
            }
            catch (Exception ex)
            {
                GuiLog.Write($"library: 删除图片失败（{ex.GetType().Name}: {ex.Message}）");
            }

            _doc.Pins.Remove(item.Key);
        });

        WriteDocument();
        Sync();
        Changed?.Invoke(null, EventArgs.Empty);
        GuiLog.Write($"library: 已删除 {item.Group.Name}/{item.FileName}");
    }

    /// <summary>删掉某个窗口对应的库文件（未分组的窗口只关窗）。</summary>
    public static void DeleteWindow(PinWindow window)
    {
        if (window.LibraryPath is { Length: > 0 } path && FindByPath(path) is { } item)
        {
            DeleteItem(item);
            return;
        }

        window.Close();
    }

    /// <summary>
    /// 删除分组。<paramref name="deleteImages"/> = 连里面的图一起删；
    /// <c>false</c> = 只删分组，图片挪到 <c>pins\_trash\</c>（可手动找回）。
    /// </summary>
    public static void DeleteGroup(PinGroup group, bool deleteImages)
    {
        foreach (var window in PinManager.Current.Windows
                     .Where(window => string.Equals(window.GroupId, group.Id, StringComparison.Ordinal))
                     .ToList())
        {
            window.LibraryPath = null;
            window.Close();
        }

        var dir = Path.Combine(Root, group.Name);

        SelfWrite(() =>
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    if (deleteImages)
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    else
                    {
                        var trash = Path.Combine(Root, "_trash");
                        Directory.CreateDirectory(trash);
                        Directory.Move(dir, Path.Combine(trash, UniqueDirectory(trash, group.Name)));
                    }
                }
            }
            catch (Exception ex)
            {
                GuiLog.Write($"library: 删除分组目录失败（{ex.GetType().Name}: {ex.Message}）");
            }

            foreach (var key in _doc.Pins.Keys
                         .Where(key => key.StartsWith(group.Id + "/", StringComparison.Ordinal))
                         .ToList())
            {
                _doc.Pins.Remove(key);
            }

            PinGroupStore.Current.RemoveGroup(group.Id);
        });

        WriteDocument();
        Sync();
        Changed?.Invoke(null, EventArgs.Empty);
        GuiLog.Write($"library: 删除分组「{group.Name}」（图片{(deleteImages ? "已删除" : "已移到 _trash")}）");
    }

    // ---------------- 改名 / 移动 ----------------

    /// <summary>分组改名：目录名跟着改（分组 id 不变，参数键也不变）。</summary>
    public static void RenameGroup(PinGroup group, string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        var from = Path.Combine(Root, oldName);
        var to = Path.Combine(Root, newName);

        SelfWrite(() =>
        {
            try
            {
                if (Directory.Exists(from) && !Directory.Exists(to))
                {
                    Directory.Move(from, to);
                }

                foreach (var window in PinManager.Current.Windows
                             .Where(window => string.Equals(window.GroupId, group.Id, StringComparison.Ordinal)))
                {
                    if (window.LibraryPath is { Length: > 0 } path)
                    {
                        window.LibraryPath = Path.Combine(to, Path.GetFileName(path));
                    }
                }
            }
            catch (Exception ex)
            {
                GuiLog.Write($"library: 分组目录改名失败（{ex.GetType().Name}: {ex.Message}）");
            }
        });

        WriteDocument();
        Sync();
        Changed?.Invoke(null, EventArgs.Empty);
        GuiLog.Write($"library: 分组「{oldName}」改名为「{newName}」");
    }

    /// <summary>把库里的一张图移到另一个分组（文件跟着搬）。</summary>
    public static void MoveItem(PinLibraryItem item, string? targetGroupId)
    {
        if (targetGroupId is not { Length: > 0 })
        {
            // 未分组只表示屏幕上的临时贴图；库中尚未贴出的图片也要先有窗口。
            var window = item.Window ?? PinManager.Current.PinFromLibrary(item);
            window?.SetGroup(null);
            return;
        }
        if (string.Equals(item.GroupId, targetGroupId, StringComparison.Ordinal))
        {
            return;
        }

        var group = PinGroupStore.Current.Find(targetGroupId);

        if (group is null)
        {
            return;
        }

        var directory = GroupDirectory(group);
        // Window 通过旧路径动态查找：必须在修改路径前记住，否则更新路径后会找不到自己。
        var liveWindow = item.Window;
        var entry = item.Entry;

        SelfWrite(() =>
        {
            try
            {
                var target = Path.Combine(directory, UniqueName(directory, item.FileName));
                File.Move(item.FullPath, target);

                _doc.Pins.Remove(item.Key);
                _doc.Pins[group.Id + "/" + Path.GetFileName(target)] = entry;
                if (liveWindow is not null)
                {
                    liveWindow.LibraryPath = target;
                    liveWindow.SetGroupSilently(group.Id);
                }

                if (liveWindow is { } window)
                {
                    SaveWindowState(window, immediate: true);
                }
            }
            catch (Exception ex)
            {
                GuiLog.Write($"library: 移动图片失败（{ex.GetType().Name}: {ex.Message}）");
            }
        });

        WriteDocument();
        Sync();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // ---------------- 打开（贴回屏幕） ----------------

    /// <summary>载入原图（找不到文件返回 <c>null</c>）。</summary>
    public static Bitmap? LoadBitmap(PinLibraryItem item)
    {
        try
        {
            if (SvgPinRenderer.IsSvgFile(item.FullPath)) return SvgPinRenderer.Load(item.FullPath);
            using var stream = File.OpenRead(item.FullPath);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            GuiLog.Write($"library: 读取图片失败 {item.FileName}（{ex.GetType().Name}: {ex.Message}）");
            return null;
        }
    }

    /// <summary>库里这一项是不是动图（管理窗口打"动图"角标用；按文件修改时间缓存）。</summary>
    public static bool IsAnimated(PinLibraryItem item)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(item.FullPath);

            if (AnimatedFlags.TryGetValue(item.FullPath, out var cached) && cached.Stamp == stamp)
            {
                return cached.Animated;
            }

            var animated = AnimatedImage.IsAnimatedFile(item.FullPath);
            AnimatedFlags[item.FullPath] = (stamp, animated);

            return animated;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>库里缩略图（首帧、最大边 160px，带缓存）。</summary>
    public static Bitmap? LoadThumbnail(PinLibraryItem item)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(item.FullPath);

            if (Thumbs.TryGetValue(item.FullPath, out var cached) && cached.Stamp == stamp)
            {
                return cached.Bitmap;
            }

            using var svgBitmap = SvgPinRenderer.IsSvgFile(item.FullPath) ? SvgPinRenderer.Load(item.FullPath, 160) : null;
            using var svgStream = svgBitmap is null ? null : new MemoryStream();
            if (svgBitmap is not null) { svgBitmap.Save(svgStream!, new PngBitmapEncoderOptions()); svgStream!.Position = 0; }
            using var source = svgStream is not null ? SkiaSharp.SKBitmap.Decode(svgStream) : SkiaSharp.SKBitmap.Decode(item.FullPath);

            if (source is null)
            {
                return null;
            }

            var scale = Math.Min(1.0, 160.0 / Math.Max(1, Math.Max(source.Width, source.Height)));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var target = new SkiaSharp.SKBitmap(width, height);
            using (var canvas = new SkiaSharp.SKCanvas(target))
            using (var paint = new SkiaSharp.SKPaint { IsAntialias = true })
            {
                canvas.DrawBitmap(
                    source,
                    new SkiaSharp.SKRect(0, 0, width, height),
                    new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear),
                    paint);
            }

            using var image = SkiaSharp.SKImage.FromBitmap(target);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            using var stream = new MemoryStream(data.ToArray());
            var bitmap = new Bitmap(stream);

            if (Thumbs.Count > 160)
            {
                Thumbs.Clear();
            }

            Thumbs[item.FullPath] = (stamp, bitmap);
            return bitmap;
        }
        catch (Exception ex)
        {
            GuiLog.Write($"library: 缩略图失败 {item.FileName}（{ex.GetType().Name}: {ex.Message}）");
            return null;
        }
    }

    // ---------------- 小工具 ----------------

    private static string SuggestName(string? sourceName)
    {
        var stem = string.IsNullOrWhiteSpace(sourceName)
            ? "pin"
            : Path.GetFileNameWithoutExtension(sourceName.Trim());

        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "pin";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(invalid, '_');
        }

        return stem + ".png";
    }

    private static string UniqueName(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = fileName;

        for (var i = 2; File.Exists(Path.Combine(directory, candidate)); i++)
        {
            candidate = $"{stem}-{i}{extension}";
        }

        return candidate;
    }

    private static string UniqueDirectory(string parent, string name)
    {
        var candidate = name;

        for (var i = 2; Directory.Exists(Path.Combine(parent, candidate)); i++)
        {
            candidate = $"{name}-{i}";
        }

        return candidate;
    }
}
