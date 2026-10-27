using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.App.Views;
using Kscan.Core.Pins;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kscan.Core.Configuration;

namespace Kscan.App.Tests;

/// <summary>
/// 贴图库的回归：分组 = 磁盘目录，元数据在 library.json。
/// <para>
/// 盯的行为：归组即落盘、收盘只关窗口（文件还在）、重启后库还在但屏幕干净、
/// 用户自己往目录丢图会被认出来、Delete 才删文件、改分组名目录跟着改、
/// 删分组按用户选择"连图删 / 只删分组"。
/// </para>
/// </summary>
public sealed class PinLibraryTests
{
    [AvaloniaFact]
    public void Svg_library_file_has_a_thumbnail_and_can_be_pinned()
    {
        using var library = new Library();
        var group = PinGroupStore.Current.Create("SVG");
        var directory = PinLibraryService.GroupDirectory(group);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "shape.svg"), "<svg xmlns='http://www.w3.org/2000/svg' width='200' height='100'><circle cx='100' cy='50' r='40' fill='red'/></svg>");
        PinLibraryService.Sync(); Pump();
        var item = Assert.Single(PinLibraryService.ItemsOf(group.Id));
        Assert.NotNull(PinLibraryService.LoadThumbnail(item));
        var window = PinManager.Current.PinFromLibrary(item);
        Assert.NotNull(window); Pump();
        Assert.EndsWith("shape.svg", window.LibraryPath);
        Assert.Equal(group.Id, window.GroupId);
        PinManager.Current.CloseSoft(window);
        Assert.True(File.Exists(item.FullPath));
    }

    [AvaloniaFact]
    public void Animated_original_survives_group_ungroup_regroup_and_reopen_without_png_conversion()
    {
        using var library = new Library();
        var first = PinGroupStore.Current.Create("动图甲");
        var second = PinGroupStore.Current.Create("动图乙");
        var directory = Path.Combine(PinLibraryService.Root, first.Name);
        Directory.CreateDirectory(directory);
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "anim.gif"));
        File.WriteAllBytes(Path.Combine(directory, "original.gif"), bytes);
        PinLibraryService.Sync();
        var pin = PinManager.Current.PinFromLibrary(Assert.Single(PinLibraryService.ItemsOf(first.Id)))!;
        Pump();
        Assert.True(pin.IsAnimated);
        for (var i = 0; i < 2; i++)
        {
            PinLibraryService.MoveItem(Assert.Single(PinLibraryService.ItemsOf(first.Id)), second.Id);
            Pump();
            Assert.Equal(second.Id, pin.GroupId);
            var item = Assert.Single(PinLibraryService.ItemsOf(second.Id));
            Assert.Same(pin, item.Window);
            PinLibraryService.MoveItem(item, null);
            Pump();
            Assert.Null(pin.LibraryPath);
            Assert.NotNull(pin.ImageFilePath);
            Assert.Equal(bytes, File.ReadAllBytes(pin.ImageFilePath));
            Assert.EndsWith(".gif", pin.ImageFilePath);
            pin.SetGroup(first.Id);
            Pump();
            Assert.Equal(first.Id, pin.GroupId);
            Assert.Null(pin.RetainedSourcePath);
            Assert.True(pin.IsAnimated);
            Assert.Equal(bytes, File.ReadAllBytes(pin.LibraryPath!));
            using var decoded = AnimatedImage.TryLoad(pin.LibraryPath!, out _);
            Assert.Equal(4, decoded!.Frames.Count);
        }
        // 原文件被外部删除时，也不能静默退化成 PNG。
        pin.SetGroup(null);
        Pump();
        var retained = pin.ImageFilePath!;
        File.Move(retained, retained + ".missing");
        pin.SetGroup(first.Id);
        Pump();
        Assert.Null(pin.GroupId);
        Assert.Empty(PinLibraryService.ItemsOf(first.Id));
        File.Move(retained + ".missing", retained);
        pin.SetGroup(first.Id);
        PinManager.Current.CloseSoft(pin);
        var reopened = PinManager.Current.PinFromLibrary(Assert.Single(PinLibraryService.ItemsOf(first.Id)));
        Pump();
        Assert.True(reopened!.IsAnimated);
        Assert.Equal(bytes, File.ReadAllBytes(reopened.ImageFilePath!));
    }

    [AvaloniaFact]
    public void Moving_closed_library_image_preserves_its_geometry_and_orientation()
    {
        using var library = new Library();
        var first = PinGroupStore.Current.Create("关闭换组甲");
        var second = PinGroupStore.Current.Create("关闭换组乙");
        var pin = library.Pin("move.png", 200, 100, new(300, 200, 200, 100));
        pin.SetGroup(first.Id);
        pin.SetTransform(90, false, false);
        Pump();
        var expected = pin.GetScreenRect();
        PinManager.Current.CloseSoft(pin);
        PinLibraryService.MoveItem(Assert.Single(PinLibraryService.ItemsOf(first.Id)), second.Id);
        Pump();
        Assert.Empty(PinLibraryService.ItemsOf(first.Id));
        var reopened = PinManager.Current.PinFromLibrary(Assert.Single(PinLibraryService.ItemsOf(second.Id)));
        Pump();
        Assert.Equal(second.Id, reopened!.GroupId);
        Assert.Equal(expected, reopened.GetScreenRect());
        Assert.Equal(90, reopened.RotationDegrees);
    }

    [AvaloniaFact]
    public void Manager_tracks_live_theme_including_selected_thumbnail_and_control_corners()
    {
        using var library = new Library();
        var theme = ThemeService.Current.CurrentTheme;
        var language = I18n.Current.Language;
        PinManagerWindow? manager = null;
        try
        {
            I18n.Current.Apply(I18n.Chinese);
            var group = PinGroupStore.Current.Create("主题测试");
            var pin = library.Pin("theme.png", 200, 100, new(100, 100, 200, 100));
            pin.SetGroup(group.Id);
            ThemeService.Current.Apply(ThemeDefinition.Light);
            manager = new PinManagerWindow();
            manager.Show();
            manager.Refresh();
            Pump();
            Assert.Equal(Color.Parse(ThemeDefinition.Light.Colors.Background), ((ISolidColorBrush)manager.Background!).Color);
            var thumbnail = manager.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PinThumbnail");
            var click = thumbnail.TranslatePoint(new Point(30, 30), manager)!.Value;
            manager.MouseDown(click, MouseButton.Left);
            manager.MouseUp(click, MouseButton.Left);
            Pump();
            SaveManagerPreview(manager, "light");
            var source = manager.GetVisualDescendants().OfType<Image>().Single().Source;
            ThemeService.Current.Apply(ThemeDefinition.Dark with
            {
                FontFamily = "Courier New", CornerRadius = 14, PreviewCornerRadius = 7,
                Colors = ThemeDefinition.Dark.Colors with { Background = "#18252F", Panel = "#273642", Foreground = "#F1E9D2", Accent = "#FFD060", Border = "#8D9770" },
            });
            Pump();
            Assert.Equal(Color.Parse("#18252F"), ((ISolidColorBrush)manager.Background!).Color);
            Assert.Equal(Color.Parse("#F1E9D2"), ((ISolidColorBrush)manager.Foreground!).Color);
            Assert.Equal("Courier New", manager.FontFamily.Name);
            foreach (var card in manager.GetVisualDescendants().OfType<Border>().Where(x => x.Name is "PinGroupCard" or "UngroupedCard"))
            {
                Assert.Equal(Color.Parse("#273642"), ((ISolidColorBrush)card.Background!).Color);
                Assert.Equal(14, card.CornerRadius.TopLeft);
            }
            thumbnail = manager.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PinThumbnail");
            Assert.Same(source, manager.GetVisualDescendants().OfType<Image>().Single().Source);
            Assert.Equal(Color.Parse("#FFD060"), ((ISolidColorBrush)thumbnail.BorderBrush!).Color);
            Assert.Equal(7, thumbnail.CornerRadius.TopLeft);
            Assert.All(manager.GetVisualDescendants().OfType<Button>(), button => Assert.Equal(14, button.CornerRadius.TopLeft));
            SaveManagerPreview(manager, "custom");
            manager.Width = 460;
            Pump();
            foreach (var button in manager.GetVisualDescendants().OfType<Button>())
            {
                var location = button.TranslatePoint(default, manager)!.Value;
                Assert.True(location.X + button.Bounds.Width <= manager.Bounds.Width + 1);
            }
            SaveManagerPreview(manager, "narrow");
        }
        finally { manager?.Close(); ThemeService.Current.Apply(theme); I18n.Current.Apply(language); }
    }

    private static void SaveManagerPreview(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("KSCAN_PINMANAGER_PREVIEW") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(path);
        foreach (var control in window.GetVisualDescendants().OfType<Control>()) control.InvalidateVisual();
        Pump();
        using var bitmap = window.CaptureRenderedFrame();
        bitmap?.Save(Path.Combine(path, $"pin-manager-{name}.png"));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Moving_library_item_to_ungrouped_keeps_one_visible_pin_and_archives_original(bool alreadyVisible)
    {
        using var library = new Library();
        var group = PinGroupStore.Current.Create("移出测试");
        var original = library.Pin("ungroup.png", 200, 100, new(100, 100, 200, 100));
        original.SetGroup(group.Id);
        Pump();
        if (!alreadyVisible) { PinManager.Current.CloseSoft(original); Pump(); }
        var item = Assert.Single(PinLibraryService.ItemsOf(group.Id));
        PinLibraryService.MoveItem(item, null);
        Pump();
        var pin = Assert.Single(PinManager.Current.Windows);
        Assert.True(pin.IsVisible);
        Assert.Null(pin.GroupId);
        Assert.Null(pin.LibraryPath);
        Assert.Empty(PinLibraryService.ItemsOf(group.Id));
        Assert.False(File.Exists(item.FullPath));
        Assert.Single(Directory.GetFiles(Path.Combine(PinLibraryService.Root, "_trash"), "*.png"));
        Assert.Equal(new PixelSize(200, 100), pin.DisplayedBitmap!.PixelSize);
        if (alreadyVisible) Assert.Same(original, pin);
    }

    [AvaloniaFact]
    public void Desktop_drop_moves_existing_pin_without_duplicate_and_persists_new_anchor()
    {
        using var library = new Library();
        var group = PinGroupStore.Current.Create("拖出定位");
        var pin = library.Pin("drop.png", 240, 120, new PixelRect(150, 150, 240, 120));
        pin.SetGroup(group.Id);
        pin.SetTransform(90, false, false);
        Pump();
        var size = pin.GetScreenRect().Size;
        var grab = new Point(.25, .75);
        var expected = PinDropPlacement.At(new PixelPoint(650, 550), size, grab);
        pin.PlaceAtDrop(new(650, 550), grab);
        Pump();
        Assert.Equal(expected, pin.GetScreenRect());
        pin.SetTransform(90, false, false);
        Pump();
        Assert.Equal(expected, pin.GetScreenRect());
        Assert.Equal(1, PinManager.Current.Count);
        PinManager.Current.CloseSoft(pin);
        Pump();
        var item = Assert.Single(PinLibraryService.ItemsOf(group.Id));
        var reopened = PinManager.Current.PinFromLibrary(item);
        Pump();
        Assert.NotNull(reopened);
        Assert.Equal(expected, reopened.GetScreenRect());
    }

    [AvaloniaFact]
    public void First_delete_only_arms_the_hint_and_second_delete_removes_the_file()
    {
        using var library = new Library();
        var group = PinGroupStore.Current.Create("删除两段式");

        var pin = library.Pin("a.png", 60, 30, new PixelRect(150, 150, 60, 30));
        pin.SetGroup(group.Id);
        Pump();

        var path = pin.LibraryPath!;

        PinManager.DeleteConfirmedOverride = false;

        // 第一次：只提示"再按一次"，不删
        PinManager.Current.RequestDelete(pin);
        Pump();

        Assert.True(pin.IsDeleteArmed, "第一次按 Delete 应该进入确认态");
        Assert.Equal(1, PinManager.Current.Count);
        Assert.True(File.Exists(path), "第一次按 Delete 不能删文件");

        // 第二次（5 秒内）：真删
        PinManager.Current.RequestDelete(pin);
        Pump();

        Assert.Equal(0, PinManager.Current.Count);
        Assert.False(File.Exists(path), "确认之后要连文件一起删");
        Assert.Empty(PinLibraryService.ItemsOf(group.Id));
    }

    [AvaloniaFact]
    public void Confirmed_delete_is_immediate()
    {
        using var library = new Library();
        var group = PinGroupStore.Current.Create("确认过");

        var pin = library.Pin("a.png", 40, 20, new PixelRect(90, 90, 40, 20));
        pin.SetGroup(group.Id);
        Pump();

        var path = pin.LibraryPath!;
        PinManager.DeleteConfirmedOverride = true;

        PinManager.Current.RequestDelete(pin);
        Pump();

        Assert.Equal(0, PinManager.Current.Count);
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public void Grouping_writes_the_image_then_soft_close_keeps_it_in_the_library()
    {
        using var library = new Library();
        var group = PinGroupStore.Current.Create("截图");

        var pin = library.Pin("a.png", 120, 60, new PixelRect(300, 200, 120, 60));
        pin.SetGroup(group.Id);
        Pump();

        var path = pin.LibraryPath;

        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(File.Exists(path), $"归组后应该写出 PNG：{path}");
        Assert.StartsWith(Path.Combine(PinLibraryService.Root, "截图"), path);
        Assert.Single(PinLibraryService.ItemsOf(group.Id));

        var savedRect = pin.GetScreenRect();
        var rotation = pin.RotationDegrees;

        // 归组那一刻就应该把位置写进 library.json
        Assert.Equal(300, PinLibraryService.ItemsOf(group.Id).Single().Entry.X);
        Assert.Equal(200, PinLibraryService.ItemsOf(group.Id).Single().Entry.Y);

        // 软关闭：只关窗口，文件与参数都在
        PinManager.Current.CloseSoft(pin);
        Pump();

        Assert.Equal(0, PinManager.Current.Count);
        Assert.True(File.Exists(path), "软关闭不该删库文件");
        Assert.Single(PinLibraryService.ItemsOf(group.Id));

        // 模拟重启：内存清空后重新加载
        library.Restart();

        var reloadedGroup = PinGroupStore.Current.FindByName("截图");
        Assert.NotNull(reloadedGroup);

        var item = Assert.Single(PinLibraryService.ItemsOf(reloadedGroup!.Id));
        Assert.Equal(rotation, item.Entry.Rotation);
        Assert.Equal(300, item.Entry.X);
        Assert.Equal(200, item.Entry.Y);
        Assert.Equal(120, item.Entry.Width);

        // 屏幕上是空的（不自动贴图），用户自己点出来
        Assert.Equal(0, PinManager.Current.Count);

        var reopened = PinManager.Current.PinFromLibrary(item);
        Pump();

        Assert.NotNull(reopened);
        Assert.Equal(item.FullPath, reopened!.LibraryPath);
        AssertClose(savedRect, reopened.GetScreenRect());
    }

    [AvaloniaFact]
    public void New_library_image_without_geometry_opens_centered_at_natural_size()
    {
        using var library = new Library();
        var group = PinGroupStore.Current.Create("手动");
        var folder = PinLibraryService.GroupDirectory(group);

        // 用户直接丢进目录的图：库里没有它的几何记录（宽高都是 0）
        File.WriteAllBytes(Path.Combine(folder, "dropped.png"), Png(200, 100));
        PinLibraryService.Sync();

        var item = Assert.Single(PinLibraryService.ItemsOf(group.Id));
        Assert.Equal(0, item.Entry.Width);

        // 历史 bug 留下的"40x40（最小尺寸）"记录也要被当成没摆过
        item.Entry.Width = 40;
        item.Entry.Height = 40;

        var window = PinManager.Current.PinFromLibrary(item);
        Pump();

        Assert.NotNull(window);
        Assert.True(window!.Width >= 40, $"窗口太窄（以前是 1px）：{window.Width}");
        Assert.False(window.Position.X == 0 && window.Position.Y == 0, $"应该居中，不该贴左上角：{window.Position}");
    }

    [AvaloniaFact]
    public void Animated_image_from_library_opens_with_real_size_and_plays()
    {
        using var library = new Library();
        var group = PinGroupStore.Current.Create("动图组");
        var folder = PinLibraryService.GroupDirectory(group);

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Assets", "anim.gif"),
            Path.Combine(folder, "anim.gif"));

        PinLibraryService.Sync();

        var item = Assert.Single(PinLibraryService.ItemsOf(group.Id));
        var window = PinManager.Current.PinFromLibrary(item);
        Pump();

        Assert.NotNull(window);
        Assert.True(window!.IsAnimated, "GIF 应该按动图播");
        Assert.True(window.Width >= 40, $"动图窗口太窄（以前是 1px）：{window.Width}");
        Assert.False(window.Position.X == 0 && window.Position.Y == 0, $"应该居中，不该贴左上角：{window.Position}");
    }

    [AvaloniaFact]
    public void Images_dropped_into_a_folder_are_picked_up_as_a_new_group()
    {
        using var library = new Library();

        var group = PinGroupStore.Current.Create("手动");
        var folder = PinLibraryService.GroupDirectory(group);

        File.WriteAllBytes(Path.Combine(folder, "自己放的.png"), Png(64, 32));
        PinLibraryService.Sync();

        var item = Assert.Single(PinLibraryService.ItemsOf(group.Id));
        Assert.Equal("自己放的.png", item.FileName);

        // 整个新目录丢进来 = 新分组
        var newFolder = Path.Combine(PinLibraryService.Root, "外面拖进来的");
        System.IO.Directory.CreateDirectory(newFolder);
        File.WriteAllBytes(Path.Combine(newFolder, "b.png"), Png(40, 20));
        PinLibraryService.Sync();

        var adopted = PinGroupStore.Current.FindByName("外面拖进来的");
        Assert.NotNull(adopted);
        Assert.Single(PinLibraryService.ItemsOf(adopted!.Id));
    }

    [AvaloniaFact]
    public void Renaming_a_group_renames_the_folder_and_keeps_the_settings()
    {
        using var library = new Library();
        var group = PinGroupStore.Current.Create("旧名字");

        var pin = library.Pin("a.png", 80, 40, new PixelRect(120, 80, 80, 40));
        pin.SetGroup(group.Id);
        Pump();

        var path = pin.LibraryPath!;
        var entry = PinLibraryService.ItemsOf(group.Id).Single().Entry;
        var rect = pin.GetScreenRect();

        Assert.True(PinGroupStore.Current.Rename(group.Id, "新名字"));
        Pump();

        var renamed = PinGroupStore.Current.Find(group.Id)!;
        Assert.Equal("新名字", renamed.Name);
        Assert.True(System.IO.Directory.Exists(Path.Combine(PinLibraryService.Root, "新名字")));
        Assert.False(System.IO.Directory.Exists(Path.Combine(PinLibraryService.Root, "旧名字")));

        // 参数（按分组 id 索引）不能丢
        var item = Assert.Single(PinLibraryService.ItemsOf(group.Id));
        Assert.Same(entry, item.Entry);
        AssertClose(rect, pin.GetScreenRect());
        Assert.Equal(Path.Combine(PinLibraryService.Root, "新名字"), Path.GetDirectoryName(pin.LibraryPath));
        Assert.NotEqual(path, pin.LibraryPath);
    }

    [AvaloniaFact]
    public void Delete_removes_the_file_and_the_metadata()
    {
        using var library = new Library();
        var group = PinGroupStore.Current.Create("要删的");

        var pin = library.Pin("a.png", 60, 30, new PixelRect(100, 100, 60, 30));
        pin.SetGroup(group.Id);
        Pump();

        var path = pin.LibraryPath!;
        var item = PinLibraryService.ItemsOf(group.Id).Single();

        PinLibraryService.DeleteItem(item);
        Pump();

        Assert.False(File.Exists(path), "硬删除必须删掉库文件");
        Assert.Empty(PinLibraryService.ItemsOf(group.Id));
        Assert.Equal(0, PinManager.Current.Count);

        // 元数据也不能留着
        File.Delete(Path.Combine(PinLibraryService.Root, "占位"));   // 忽略
    }

    [AvaloniaFact]
    public void Deleting_a_group_respects_the_choice_about_images()
    {
        using var library = new Library();

        var withFiles = PinGroupStore.Current.Create("连图删");
        var keepFiles = PinGroupStore.Current.Create("只删组");

        var a = library.Pin("a.png", 50, 20, new PixelRect(10, 10, 50, 20));
        a.SetGroup(withFiles.Id);
        var b = library.Pin("b.png", 50, 20, new PixelRect(80, 10, 50, 20));
        b.SetGroup(keepFiles.Id);
        Pump();

        var aPath = a.LibraryPath!;
        var bPath = b.LibraryPath!;

        PinLibraryService.DeleteGroup(withFiles, deleteImages: true);
        PinLibraryService.DeleteGroup(keepFiles, deleteImages: false);
        Pump();

        Assert.False(File.Exists(aPath), "连图删：文件应该没了");
        Assert.False(System.IO.Directory.Exists(Path.Combine(PinLibraryService.Root, "连图删")));

        Assert.False(File.Exists(bPath), "只删分组：文件应该离开原目录");
        Assert.True(System.IO.Directory.Exists(Path.Combine(PinLibraryService.Root, "_trash")), "只删分组应把图片挪进 _trash");

        Assert.Null(PinGroupStore.Current.FindByName("连图删"));
        Assert.Null(PinGroupStore.Current.FindByName("只删组"));
    }

    [AvaloniaFact]
    public void Moving_an_item_between_groups_moves_the_file()
    {
        using var library = new Library();

        var source = PinGroupStore.Current.Create("来源");
        var target = PinGroupStore.Current.Create("目标");

        var pin = library.Pin("a.png", 70, 30, new PixelRect(200, 150, 70, 30));
        pin.SetGroup(source.Id);
        Pump();

        var item = PinLibraryService.ItemsOf(source.Id).Single();
        PinLibraryService.MoveItem(item, target.Id);
        Pump();

        Assert.Empty(PinLibraryService.ItemsOf(source.Id));
        var moved = Assert.Single(PinLibraryService.ItemsOf(target.Id));
        Assert.Equal("目标", moved.Group.Name);
        Assert.True(File.Exists(moved.FullPath));
        Assert.StartsWith(Path.Combine(PinLibraryService.Root, "目标"), moved.FullPath);
    }

    private static void AssertClose(PixelRect expected, PixelRect actual)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) <= 1 &&
            Math.Abs(expected.Y - actual.Y) <= 1 &&
            Math.Abs(expected.Width - actual.Width) <= 1 &&
            Math.Abs(expected.Height - actual.Height) <= 1,
            $"位置/尺寸没还原：期望 {expected} 实际 {actual}");
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static byte[] Png(int width, int height)
    {
        using var skia = new SkiaSharp.SKBitmap(width, height);
        skia.Erase(SkiaSharp.SKColors.SteelBlue);
        using var encoded = skia.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>每个测试一个独立的临时库；用完把分组与屏幕贴图恢复原状。</summary>
    private sealed class Library : IDisposable
    {
        private readonly string _root;
        private readonly List<PinGroup> _groupsBefore = PinGroupStore.Current.Groups.ToList();

        public Library()
        {
            _root = Path.Combine(Path.GetTempPath(), "kscan-library-" + Guid.NewGuid().ToString("N"));

            PinManager.Current.CloseAll();
            PinLibraryService.ResetForTests();
            PinLibraryService.RootOverride = Path.Combine(_root, "pins");
            PinGroupStore.Current.ReplaceAll([]);
            PinLibraryService.Load();
            Pump();
        }

        public PinWindow Pin(string name, int width, int height, PixelRect region)
        {
            var pin = PinManager.Current.Pin(NewBitmap(width, height), name, ownsImage: true, targetRegion: region);
            Pump();
            return pin;
        }

        /// <summary>模拟"退出再启动"：清掉内存态，从同一份磁盘数据重新加载。</summary>
        public void Restart()
        {
            PinManager.Current.CloseAll();
            Pump();

            PinLibraryService.ResetForTests();
            PinLibraryService.RootOverride = Path.Combine(_root, "pins");
            PinLibraryService.Load();
            Pump();
        }

        public void Dispose()
        {
            PinManager.DeleteConfirmedOverride = null;

            PinManager.Current.CloseAll();
            Pump();

            PinLibraryService.ResetForTests();
            PinGroupStore.Current.ReplaceAll(_groupsBefore);

            try
            {
                System.IO.Directory.Delete(_root, recursive: true);
            }
            catch (Exception)
            {
                // 临时目录删不掉就算了
            }
        }

        private static Bitmap NewBitmap(int width, int height)
        {
            using var stream = new MemoryStream(Png(width, height));
            return new Bitmap(stream);
        }
    }
}
