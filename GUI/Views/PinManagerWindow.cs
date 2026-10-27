using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Kscan.App.Services;
using Kscan.Core.Pins;

namespace Kscan.App.Views;

/// <summary>
/// 贴图库管理窗口：每个分组一张卡片，卡片里是<b>库里的全部图片</b>（不只是屏幕上的）。
/// <para>
/// 交互：双击 = 贴到屏幕（已在屏幕上则提到最前）；单击 = 选中；
/// Delete = 从库里彻底删除（连磁盘文件）；✕ = 收起（只关窗口，图留在库里）。
/// 分组：重命名（目录名跟着改）、删除分组（先问"连图一起删 / 只删分组"）、换颜色。
/// 拖拽：把缩略图拖到别的分组卡片上 = 移动分组（库里的文件也跟着搬目录）。
/// </para>
/// <para>
/// 层级规则：本窗口恒定置顶，屏幕上的贴图被压在它下面（<see cref="DemotePins"/>）；
/// 悬停只画"边框 + 蒙版"提示是哪一张，不动层级。
/// </para>
/// </summary>
internal sealed class PinManagerWindow : Window
{
    private static PinManagerWindow? _current;

    private readonly StackPanel _content = new() { Spacing = 12, Margin = new Thickness(16) };

    private readonly TextBlock _summary = new() { FontSize = 12, Opacity = 0.75 };

    /// <summary>操作提示（Delete 第一次的"再按一次"，或贴图失败的原因）。</summary>
    private readonly TextBlock _hint = new()
    {
        FontSize = 12,
        IsVisible = false,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>分组卡片 → 分组 id（<c>null</c> = 未分组）。每次重建时重置。</summary>
    private readonly Dictionary<Control, string?> _dropTargets = [];

    /// <summary>缩略图控件 → 它代表的那一项（悬停预览/拖拽用）。</summary>
    private readonly Dictionary<Control, Tile> _tiles = [];

    /// <summary>当前悬停目标的稳定标识（"pin:xxx" / "group:xxx" / ""），用于幂等短路。</summary>
    private string _hoverKey = string.Empty;

    /// <summary>被压到下层之前的置顶状态；关窗时按它恢复。</summary>
    private readonly Dictionary<PinWindow, bool> _topmostBeforeDemote = [];

    private Control? _dropHighlighted;

    private Tile? _dragTile;
    private Point _dragStart;
    private bool _dragMoved;
    private Control? _dragSource;
    private double _dragSourceOpacity;
    private Point _dragGrab = new(.5, .5);
    private bool _nativeDragging;

    private string? _selectedKey;
    private Border? _selectedFrame;

    /// <summary>跟着光标走的缩略图副本（拖拽"跟手"）。</summary>
    private readonly Canvas _ghostLayer = new() { IsHitTestVisible = false };

    private Border? _ghost;

    private const double GhostWidth = 96;
    private const double GhostHeight = 76;

    /// <summary>自己判双击：指针捕获/重建控件都会让 <c>DoubleTapped</c> 不可靠。</summary>
    private string? _lastClickKey;
    private DateTime _lastClickAt;

    private readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(450);

    /// <summary>Delete 第一次要"再按一次确认"，这段时间内有效。</summary>
    private readonly TimeSpan DeleteArmWindow = TimeSpan.FromSeconds(5);

    private string? _deleteArmedKey;
    private DateTime _deleteArmedAt;

    private DispatcherTimer? _hintTimer;

    /// <summary>一个格子：库里的某项（<see cref="Item"/>）或只有屏幕窗口的未分组贴图。</summary>
    private sealed class Tile
    {
        public PinLibraryItem? Item { get; init; }

        public PinWindow? Pin { get; set; }

        public string? GroupId { get; init; }

        public string Key => Item?.Key ?? ("pin:" + (Pin?.GetHashCode() ?? 0).ToString("X"));

        public bool OnScreen => Pin is not null;
    }

    internal PinManagerWindow()
    {
        BindTheme(this, BackgroundProperty, "KscanBackgroundBrush");
        BindTheme(this, ForegroundProperty, "KscanForegroundBrush");
        BindTheme(this, FontFamilyProperty, "KscanFontFamily");
        BindTheme(_hint, TextBlock.ForegroundProperty, "KscanWarningBrush");
        Title = I18n.T("Str.PinManager.Title");
        Width = 760;
        Height = 600;
        MinWidth = 460;
        MinHeight = 340;
        CanResize = true;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = new Grid
        {
            Children =
            {
                new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = new StackPanel
                    {
                        Spacing = 8,
                        Children = { BuildToolbar(), _summary, _hint, _content },
                    },
                },

                // 拖拽幽灵层：叠在内容之上，不吃鼠标
                _ghostLayer,
            },
        };

        // 拖拽与悬停预览都用窗口级指针事件（隧道阶段）统一处理
        AddHandler(PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Tunnel);

        // 鼠标移出窗口：立刻收掉预览
        PointerExited += (_, _) => SetHoverTarget(false, null, null);

        // Delete：删掉选中的那张（连库文件）。焦点在输入框里时不要抢 Delete（那是在删字）。
        // 第一次不直接删：先在提示条上说明"再按一次 Delete 彻底删除"，第二次才真删（不弹模态框）。
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Delete || e.Source is TextBox)
            {
                return;
            }

            e.Handled = true;

            if (_selectedKey is not { Length: > 0 } key || PinLibraryService.Find(key) is not { } item)
            {
                ShowHint(I18n.T("Str.PinManager.DeleteSelectFirst"));
                return;
            }

            RequestDelete(item);
        };

        // 回到本窗口 = 它要在最前面：所有非顶层态的贴图压回它下面（位置不动）
        Activated += (_, _) =>
        {
            Topmost = true;
            DemotePins();
        };

        PinManager.Current.Changed += OnSourceChanged;
        PinGroupStore.Current.Changed += OnSourceChanged;
        PinLibraryService.Changed += OnSourceChanged;
        I18n.Current.Changed += OnSourceChanged;

        Closed += (_, _) =>
        {
            PinManager.Current.Changed -= OnSourceChanged;
            PinGroupStore.Current.Changed -= OnSourceChanged;
            PinLibraryService.Changed -= OnSourceChanged;
            I18n.Current.Changed -= OnSourceChanged;

            // 关窗：收掉高亮、解除顶层态、恢复各贴图原来的置顶状态
            EndHover();
            RestorePins();
            _current = null;
        };
    }

    /// <summary>打开管理窗口；已经开着就提到前面并刷新（连按热键不会开一堆窗口）。</summary>
    public static void ShowOrActivate()
    {
        if (_current is { } existing)
        {
            existing.Activate();
            existing.Refresh();
            return;
        }

        var window = new PinManagerWindow();
        _current = window;
        window.Show();
        window.Activate();
        window.Refresh();
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        // 批量操作（建组 + 迁移成员）期间不要中途重建视图：
        // 重建会销毁正在编辑的输入框，进而触发它的 LostFocus 再次提交。
        if (_suppressRefresh)
        {
            return;
        }

        Refresh();
    }

    /// <summary>批量修改分组/成员时置位：期间忽略"来源变化"触发的重建。</summary>
    private bool _suppressRefresh;

    // ---------------- 顶部工具条 ----------------

    private Control BuildToolbar()
    {
        var refresh = ToolButton("Str.PinManager.Refresh", Refresh);

        var newGroup = ToolButton("Str.PinManager.NewGroup", () =>
        {
            PinGroupStore.Current.Create();
            Refresh();
        });

        var closeAll = ToolButton("Str.PinManager.CloseAll", () =>
        {
            PinManager.Current.CloseAll();
            Refresh();
        });

        var openFolder = ToolButton("Str.PinManager.OpenFolder", OpenLibraryFolder);

        return new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 6,
            Children = { refresh, newGroup, closeAll, openFolder },
        };
    }

    private static void BindTheme(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, new DynamicResourceExtension(key));

    private static Button StyledButton(Button button)
    {
        BindTheme(button, Button.CornerRadiusProperty, "KscanCornerRadius");
        return button;
    }

    private static Button ToolButton(string i18nKey, Action action) =>
        StyledButton(new()
        {
            Content = I18n.T(i18nKey),
            Padding = new Thickness(10, 3, 10, 3),
            Command = new RelayCommand(action),
        });

    private static Button CardButton(string i18nKey, Action action, bool enabled = true) =>
        StyledButton(new()
        {
            Content = I18n.T(i18nKey),
            Padding = new Thickness(8, 2, 8, 2),
            IsEnabled = enabled,
            Command = new RelayCommand(action),
        });

    private static TextBlock Hint(string i18nKey) =>
        new()
        {
            Text = I18n.T(i18nKey),
            Opacity = 0.6,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

    private static void OpenLibraryFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(PinLibraryService.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", PinLibraryService.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            GuiLog.Write($"library: 打开目录失败（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    // ---------------- 视图重建 ----------------

    internal void Refresh()
    {
        if (!IsVisible)
        {
            return;
        }

        // 重建会让"悬停中的缩略图"消失，先把高亮收干净
        EndHover();

        var windows = PinManager.Current.Windows;

        // 屏幕上的窗口按库文件路径索引：库项是否"在屏幕上"以它为准（避免 item.Window 过期）
        var onScreen = new Dictionary<string, PinWindow>(StringComparer.OrdinalIgnoreCase);

        foreach (var window in windows)
        {
            if (window.LibraryPath is { Length: > 0 } path)
            {
                onScreen[path] = window;
            }
        }

        var groups = PinGroupStore.Current.Groups;
        var libraryCount = PinLibraryService.All.Count;
        var onScreenCount = windows.Count;

        _summary.Text = I18n.F("Str.PinManager.Summary", libraryCount, onScreenCount, groups.Count);

        _content.Children.Clear();
        _dropTargets.Clear();
        _tiles.Clear();
        _dropHighlighted = null;
        _selectedFrame = null;

        foreach (var group in groups)
        {
            _content.Children.Add(BuildGroupCard(group, onScreen));
        }

        // "未分组"不是真分组，只借用同一个卡片样式（显示屏幕上还没归组的贴图）
        var ungrouped = windows.Where(window => PinGroupStore.Current.Find(window.GroupId) is null).ToList();
        _content.Children.Add(BuildUngroupedCard(ungrouped));

        if (libraryCount == 0 && onScreenCount == 0)
        {
            _content.Children.Add(new TextBlock
            {
                Text = I18n.T("Str.PinManager.Empty"),
                Opacity = 0.7,
                Margin = new Thickness(2, 12, 0, 0),
            });
        }
    }

    // ---------------- 分组卡片 ----------------

    private Control BuildGroupCard(PinGroup group, Dictionary<string, PinWindow> onScreen)
    {
        var color = ParseColor(group.Color);

        var card = new Border
        {
            Name = "PinGroupCard",
            // 分组描边/色块是用户指定的标识色，面板背景沿用软件主题。
            BorderBrush = new SolidColorBrush(color, 0.65),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
        };
        BindTheme(card, Border.BackgroundProperty, "KscanPanelBrush");
        BindTheme(card, Border.CornerRadiusProperty, "KscanCornerRadius");

        _dropTargets[card] = group.Id;

        var items = PinLibraryService.ItemsOf(group.Id);
        var tiles = items
            .Select(item => new Tile { Item = item, Pin = onScreen.GetValueOrDefault(item.FullPath), GroupId = group.Id })
            .ToList();

        var header = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };

        header.Children.Add(new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 名字就地改：失焦或回车即写回（目录名会跟着改）
        var name = new TextBox
        {
            Text = group.Name,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2),
            MinWidth = 120,
            Width = 180,
        };
        BindTheme(name, TextBox.ForegroundProperty, "KscanForegroundBrush");
        BindTheme(name, TextBox.CornerRadiusProperty, "KscanCornerRadius");

        // 名字就地改：失焦或回车即写回（目录名会跟着改）。
        // 只提交一次：写回会触发 Refresh 销毁这个输入框，而销毁又会再触发一次 LostFocus ——
        // 不挡住的话第二次会用旧文本把名字改回去（"改了没生效"）。
        var committed = false;

        void Commit()
        {
            if (committed)
            {
                return;
            }

            var text = name.Text?.Trim();

            if (string.IsNullOrEmpty(text) || string.Equals(text, group.Name, StringComparison.Ordinal))
            {
                return;
            }

            committed = true;
            _suppressRefresh = true;

            try
            {
                if (!PinGroupStore.Current.Rename(group.Id, text))
                {
                    name.Text = group.Name;   // 重名/非法名：改回去
                    committed = false;
                }
            }
            finally
            {
                _suppressRefresh = false;
            }

            Refresh();
        }

        name.LostFocus += (_, _) => Commit();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };

        header.Children.Add(name);

        header.Children.Add(new TextBlock
        {
            Text = I18n.F("Str.PinManager.GroupCount", tiles.Count, tiles.Count(tile => tile.OnScreen)),
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });

        header.Children.Add(CardButton("Str.PinManager.PinGroup", () => PinGroupToScreen(tiles), tiles.Count > 0));
        header.Children.Add(CardButton("Str.PinManager.CloseGroup", () => ClosePins(tiles), tiles.Any(tile => tile.OnScreen)));

        var delete = CardButton("Str.PinManager.DeleteGroup", () => _ = DeleteGroupAsync(group));

        ToolTip.SetTip(delete, I18n.T("Str.PinManager.DeleteGroupTip"));
        header.Children.Add(delete);

        header.Children.Add(CardButton("Str.PinManager.Color", () =>
        {
            // 在预设配色里轮转：保证和深色主题的对比度，也不会调出看不清的颜色
            var index = PinGroup.Palette.ToList().IndexOf(group.Color.ToUpperInvariant());
            PinGroupStore.Current.SetColor(group.Id, PinGroup.ColorAt(index + 1));
            Refresh();
        }));

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        body.Children.Add(tiles.Count == 0
            ? Hint("Str.PinManager.GroupEmpty")
            : BuildThumbnails(tiles));

        body.Children.Add(Hint("Str.PinManager.DoubleClickHint"));

        card.Child = new StackPanel { Children = { header, body } };

        // 悬停预览与"点空白处 = 贴这组"都由窗口级按位置判定，卡片上不绑 Entered/Exited
        return card;
    }

    private Control BuildUngroupedCard(List<PinWindow> pins)
    {
        var card = new Border
        {
            Name = "UngroupedCard",
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
        };
        BindTheme(card, Border.BackgroundProperty, "KscanPanelBrush");
        BindTheme(card, Border.BorderBrushProperty, "KscanBorderBrush");
        BindTheme(card, Border.CornerRadiusProperty, "KscanCornerRadius");

        _dropTargets[card] = null;   // 拖到这里 = 移出分组，保留屏幕贴图，原文件归档到 _trash。

        var header = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };

        // 「未分组」也能就地命名：改完就变成一个真分组，里面的贴图整体迁过去（并落进库）
        var name = new TextBox
        {
            Text = I18n.T("Str.PinManager.Ungrouped"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2),
            MinWidth = 120,
            Width = 180,
        };

        name.PlaceholderText = I18n.T("Str.PinManager.UngroupedRename");
        BindTheme(name, TextBox.ForegroundProperty, "KscanForegroundBrush");
        BindTheme(name, TextBox.CornerRadiusProperty, "KscanCornerRadius");

        var placeholder = I18n.T("Str.PinManager.Ungrouped");

        // 只允许提交一次：回车/失焦都会进来，而且 Refresh() 销毁输入框时还会再触发一次 LostFocus，
        // 不挡住的话会连着建两个分组，其中一个永远是空的。
        var committed = false;

        void Commit()
        {
            if (committed)
            {
                return;
            }

            var text = name.Text?.Trim();

            // 没改（或改回占位名）就什么都不做 —— 免得白白建一个空分组
            if (string.IsNullOrEmpty(text) || string.Equals(text, placeholder, StringComparison.Ordinal))
            {
                return;
            }

            committed = true;

            // 建组 + 批量迁移期间挂起"由 Changed 触发的重建"，
            // 否则第一次 Refresh 就会把输入框销毁、闭包再次提交。
            _suppressRefresh = true;

            try
            {
                var created = PinGroupStore.Current.Create(text);

                foreach (var pin in PinsOfGroup(null))
                {
                    pin.SetGroup(created.Id);
                }
            }
            finally
            {
                _suppressRefresh = false;
            }

            Refresh();
        }

        name.LostFocus += (_, _) => Commit();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };

        header.Children.Add(name);

        header.Children.Add(new TextBlock
        {
            Text = I18n.F("Str.PinManager.GroupCount", pins.Count, pins.Count),
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var tiles = pins.Select(pin => new Tile { Pin = pin, GroupId = null }).ToList();

        header.Children.Add(CardButton("Str.PinManager.PinGroup", () => PinGroupToScreen(tiles), tiles.Count > 0));
        header.Children.Add(CardButton("Str.PinManager.CloseGroup", () => ClosePins(tiles), tiles.Count > 0));

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        body.Children.Add(tiles.Count == 0
            ? Hint("Str.PinManager.UngroupedEmpty")
            : BuildThumbnails(tiles));

        card.Child = new StackPanel { Children = { header, body } };
        return card;
    }

    // ---------------- 缩略图 ----------------

    private Control BuildThumbnails(List<Tile> tiles)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var tile in tiles)
        {
            wrap.Children.Add(BuildThumbnail(tile));
        }

        return wrap;
    }

    private Control BuildThumbnail(Tile tile)
    {
        var stack = new StackPanel { Width = 104, Margin = new Thickness(0, 0, 8, 8), Spacing = 4 };

        var title = tile.Pin?.SourceName ?? tile.Item?.FileName ?? I18n.T("Str.PinManager.Pin");

        var frame = new Border
        {
            Name = "PinThumbnail",
            Height = 84,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,

            // **必须给背景**：Border 背景为 null 时空白处不参与命中测试，
            // 用户只有点在图片像素上才算"点到了缩略图"（宽扁图尤其明显：上下大片空白点不动）。
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(tile.Pin?.IsRaised == true ? 2 : 1),
            // 绿边 = 这张已经在屏幕上被提到最前（和贴图上的绿框一致）
            Opacity = tile.OnScreen ? 1 : 0.62,
        };
        BindTheme(frame, Border.CornerRadiusProperty, "KscanPreviewCornerRadius");
        BindTheme(frame, Border.BorderBrushProperty, tile.Pin?.IsRaised == true ? "KscanSuccessBrush" : "KscanBorderBrush");

        var bitmap = tile.Pin?.DisplayedBitmap
                     ?? (tile.Item is { } item ? PinLibraryService.LoadThumbnail(item) : null);

        if (bitmap is not null)
        {
            frame.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        }

        ToolTip.SetTip(
            frame,
            title + "\n" + I18n.T(tile.OnScreen ? "Str.PinManager.TipOnScreen" : "Str.PinManager.TipInLibrary"));

        if (string.Equals(tile.Key, _selectedKey, StringComparison.Ordinal))
        {
            frame.BorderThickness = new Thickness(2);
            BindTheme(frame, Border.BorderBrushProperty, "KscanAccentBrush");
            _selectedFrame = frame;
        }

        // 悬停预览由窗口级"按位置判定"驱动（见 UpdateHoverAt），这里只登记这张缩略图代表哪一项
        _tiles[frame] = tile;

        // 按下只记录；移动/抬起由窗口级处理器统一判断（是点击、双击还是拖到别的分组）
        frame.PointerPressed += (_, e) => BeginTileDrag(tile, frame, e);
        frame.DoubleTapped += (_, e) =>
        {
            ActivateTile(tile);
            e.Handled = true;
        };

        // ✕ 一直在（不在屏幕上的图也要能直接删掉）；库里的 = 彻底删除，未分组的 = 关掉这张
        var close = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Command = new RelayCommand(() =>
            {
                if (tile.Item is { } item)
                {
                    RequestDelete(item);
                    return;
                }

                if (tile.Pin is { } pin)
                {
                    PinManager.Current.CloseSoft(pin);
                }

                Refresh();
            }),
        };
        StyledButton(close);

        ToolTip.SetTip(
            close,
            I18n.T(tile.Item is null ? "Str.PinManager.TipClose" : "Str.PinManager.TipDelete"));

        // 右键菜单：贴到屏幕 / 收起 / 彻底删除
        frame.ContextMenu = BuildTileMenu(tile);

        stack.Children.Add(new Grid { Children = { frame, close } });

        // 动图角标：一眼能看出这张会动（库里缩略图用的是首帧）
        if (tile.Item is { } libraryItem && PinLibraryService.IsAnimated(libraryItem))
        {
            var badge = new Border
            {
                Name = "AnimatedBadge",
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 4, 0, 0),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = I18n.T("Str.PinManager.Animated"),
                    FontSize = 9,
                },
            };
            BindTheme(badge, Border.BackgroundProperty, "KscanAccentBrush");
            BindTheme(badge, Border.CornerRadiusProperty, "KscanCornerRadius");
            BindTheme(badge.Child!, TextBlock.ForegroundProperty, "KscanAccentForegroundBrush");

            ((Grid)stack.Children[^1]).Children.Add(badge);
        }
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            Opacity = 0.75,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (!tile.OnScreen)
        {
            stack.Children.Add(new TextBlock
            {
                Text = I18n.T("Str.PinManager.InLibrary"),
                FontSize = 10,
                Opacity = 0.55,
            });
        }

        return stack;
    }

    /// <summary>缩略图的右键菜单：贴到屏幕 / 收起 / 彻底删除。</summary>
    private ContextMenu BuildTileMenu(Tile tile)
    {
        var menu = new ContextMenu();
        BindTheme(menu, ContextMenu.BackgroundProperty, "KscanPanelBrush");
        BindTheme(menu, ContextMenu.ForegroundProperty, "KscanForegroundBrush");
        BindTheme(menu, ContextMenu.FontFamilyProperty, "KscanFontFamily");

        var pin = new MenuItem { Header = I18n.T("Str.PinManager.TilePin") };
        pin.Click += (_, _) => ActivateTile(tile);
        menu.Items.Add(pin);

        if (tile.OnScreen && tile.Item is not null)
        {
            var putAway = new MenuItem { Header = I18n.T("Str.PinManager.TileClose") };
            putAway.Click += (_, _) =>
            {
                if (tile.Pin is { } window)
                {
                    PinManager.Current.CloseSoft(window);
                }

                Refresh();
            };
            menu.Items.Add(putAway);
        }

        if (tile.Item is { } item)
        {
            menu.Items.Add(new Separator());

            var delete = new MenuItem { Header = I18n.T("Str.PinManager.TileDelete") };
            delete.Click += (_, _) => RequestDelete(item);
            menu.Items.Add(delete);
        }

        return menu;
    }

    /// <summary>
    /// 单击：屏幕上的贴图 = 顶层态开关；库里的 = 只选中（**双击**才贴出来）。
    /// 双击自己判：450ms 内同一格点两下就算。<c>DoubleTapped</c> 在指针捕获/控件重建时会丢，不能只靠它。
    /// </summary>
    private void HandleClick(Tile tile)
    {
        EndHover();

        var now = DateTime.UtcNow;
        var isDouble = string.Equals(_lastClickKey, tile.Key, StringComparison.Ordinal)
                       && now - _lastClickAt < DoubleClickWindow;

        _lastClickKey = tile.Key;
        _lastClickAt = now;

        if (isDouble)
        {
            _lastClickKey = null;
            ActivateTile(tile);
            return;
        }

        // 单选：库里的图只高亮选中框（不重建视图，否则双击的第二下就点空了）
        if (tile.Pin is { } pin)
        {
            if (pin.IsRaised)
            {
                pin.ExitRaised();
            }
            else
            {
                pin.EnterRaised();
                pin.Activate();
            }

            Refresh();
        }
    }

    /// <summary>双击：不在屏幕上就贴出来；在屏幕上就提到最前。</summary>
    /// <summary>在提示条上说一句（5 秒后自动收起）。</summary>
    private void ShowHint(string text)
    {
        _hint.Text = text;
        _hint.IsVisible = true;

        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer { Interval = DeleteArmWindow };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer?.Stop();
            _hint.IsVisible = false;
        };
        _hintTimer.Start();
    }

    /// <summary>
    /// Delete 删库里的一张：第一次只在提示条上说"再按一次 Delete 彻底删除"（5 秒内有效），
    /// 第二次才真删；用户确认过一次（`PinDeleteConfirmed`）之后就直删。
    /// </summary>
    private void RequestDelete(PinLibraryItem item)
    {
        var armed = string.Equals(_deleteArmedKey, item.Key, StringComparison.Ordinal)
                    && DateTime.UtcNow - _deleteArmedAt < DeleteArmWindow;

        if (!PinManager.Current.IsDeleteConfirmed && !armed)
        {
            _deleteArmedKey = item.Key;
            _deleteArmedAt = DateTime.UtcNow;
            ShowHint(I18n.F("Str.PinManager.DeleteArmHint", item.FileName));
            GuiLog.Write($"manager: Delete 第一次，进入确认态（{item.FileName}）");
            return;
        }

        PinManager.Current.ConfirmDeleteOnce();
        _deleteArmedKey = null;

        PinLibraryService.DeleteItem(item);
        ShowHint(I18n.F("Str.PinManager.DeletedHint", item.FileName));
        GuiLog.Write($"manager: 已彻底删除（{item.FileName}）");
        Refresh();
    }

    private void ActivateTile(Tile tile)
    {
        EndHover();

        if (tile.Item is { } item)
        {
            PinManager.Current.PinFromLibrary(item);
        }
        else if (tile.Pin is { } pin)
        {
            if (!pin.IsRaised)
            {
                pin.EnterRaised();
            }

            pin.Activate();
        }

        Refresh();
    }

    // ---------------- 悬停预览（按位置判定） ----------------

    /// <summary>
    /// 按光标位置判定悬停目标：缩略图 → 只预览那一张（不在屏幕上的不预览）；分组卡片 → 预览整组。
    /// </summary>
    private void UpdateHoverAt(Point positionInWindow)
    {
        foreach (var (thumbnail, tile) in _tiles)
        {
            if (Contains(thumbnail, positionInWindow))
            {
                if (tile.Pin is { } pin)
                {
                    SetHoverTarget(true, tile.GroupId, pin);
                }
                else
                {
                    SetHoverTarget(false, null, null);
                }

                return;
            }
        }

        foreach (var (card, groupId) in _dropTargets)
        {
            if (Contains(card, positionInWindow))
            {
                // 注意：groupId 为 null 表示"未分组"这张卡片，它是**有效的悬停目标**，
                // 不能和"什么都没悬停"混为一谈（否则未分组整组预览永远不出现）。
                SetHoverTarget(true, groupId, null);
                return;
            }
        }

        SetHoverTarget(false, null, null);
    }

    private bool Contains(Control control, Point positionInWindow) =>
        control.TranslatePoint(new Point(0, 0), this) is { } origin
        && new Rect(origin, control.Bounds.Size).Contains(positionInWindow);

    /// <summary>
    /// 设置悬停目标（幂等：同一目标重复调用直接返回）。
    /// 单张用"子元素识别"的橙色，整组（含"未分组"）用"窗口识别"的黄色；边框与蒙版同色。
    /// </summary>
    /// <param name="onCard">光标是否落在某个分组卡片上（含"未分组"卡片）。</param>
    private void SetHoverTarget(bool onCard, string? groupId, PinWindow? pin)
    {
        var key = pin is not null
            ? "pin:" + pin.GetHashCode().ToString("X")
            : onCard ? "card:" + (groupId ?? "<ungrouped>") : string.Empty;

        if (string.Equals(key, _hoverKey, StringComparison.Ordinal))
        {
            return;
        }

        _hoverKey = key;

        if (key.Length == 0)
        {
            EndHover();
            return;
        }

        var groupLevel = pin is null;

        foreach (var candidate in PinManager.Current.Windows)
        {
            var on = pin is not null
                ? ReferenceEquals(candidate, pin)
                : string.Equals(candidate.GroupId, groupId, StringComparison.Ordinal);

            candidate.SetHighlighted(on, groupLevel);
        }
    }

    /// <summary>结束悬停：熄灭全部高亮。</summary>
    private void EndHover()
    {
        _hoverKey = string.Empty;
        ClearHighlights();
    }

    /// <summary>把屏幕上所有贴图的高亮收掉。</summary>
    private static void ClearHighlights()
    {
        foreach (var pin in PinManager.Current.Windows)
        {
            pin.SetHighlighted(false);
        }
    }

    // ---------------- 拖拽改分组 ----------------

    private void BeginTileDrag(Tile tile, Control source, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragTile = tile;
        _dragSource = source;
        _dragSourceOpacity = source.Opacity;
        _dragMoved = false;
        _dragStart = e.GetPosition(this);
        var bitmap = tile.Pin?.DisplayedBitmap ?? (tile.Item is { } item ? PinLibraryService.LoadThumbnail(item) : null);
        _dragGrab = bitmap is not null
            ? PinDropPlacement.GrabFraction(e.GetPosition(source), source.Bounds.Size, bitmap.PixelSize)
            : new(.5, .5);

        // 选中态：Delete 键作用于它
        Select(tile, source);

        // 这里**不**捕获指针：按下就捕获会让 Avalonia 判不出双击；
        // 超过 6px 真的开始拖时再捕获（见 OnWindowPointerMoved）。
    }

    private void Select(Tile tile, Control frame)
    {
        _selectedKey = tile.Key;

        if (_selectedFrame is not null && !ReferenceEquals(_selectedFrame, frame))
        {
            _selectedFrame.BorderThickness = new Thickness(1);
            BindTheme(_selectedFrame, Border.BorderBrushProperty,
                _tiles.TryGetValue(_selectedFrame, out var previous) && previous.Pin?.IsRaised == true ? "KscanSuccessBrush" : "KscanBorderBrush");
        }

        if (frame is Border border)
        {
            border.BorderThickness = new Thickness(2);
            BindTheme(border, Border.BorderBrushProperty, "KscanAccentBrush");
            _selectedFrame = border;
        }
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_nativeDragging) return;
        var current = e.GetPosition(this);

        if (_dragTile is null)
        {
            UpdateHoverAt(current);
            return;
        }

        // 6px 死区：只是点一下（想"选中/提到最前"）不该被当成拖动
        if (!_dragMoved
            && Math.Abs(current.X - _dragStart.X) < 6
            && Math.Abs(current.Y - _dragStart.Y) < 6)
        {
            return;
        }

        if (!_dragMoved)
        {
            _dragMoved = true;

            if (OperatingSystem.IsWindows())
            {
                RunNativeDrag(_dragTile, e);
                return;
            }

            // 真的开始拖了才捕获指针（按下就捕获会让 Avalonia 判不出双击）
            e.Pointer.Capture(this);
        }

        UpdateGhost(_dragTile, current);
        UpdateDropHighlight(current);
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_nativeDragging) return;
        var position = e.GetPosition(this);

        if (_dragTile is null)
        {
            // 什么都不做：以前"点卡片空白 = 贴整组"太容易误触（点到缩略图的透明边就把整组贴出来），
            // 现在整组操作只走卡片头上的「贴这组」按钮。
            return;
        }

        var tile = _dragTile;
        var moved = _dragMoved;

        _dragTile = null;
        _dragMoved = false;
        ClearDropHighlight();
        HideGhost();
        e.Pointer.Capture(null);

        if (!moved)
        {
            HandleClick(tile);
            return;
        }

        MoveTileToGroup(tile, position);
    }

    private void MoveTileToGroup(Tile tile, Point position)
    {
        if (HitTestCard(position) is { } target && _dropTargets.TryGetValue(target, out var dropped))
        {
            var previous = _suppressRefresh;
            _suppressRefresh = true;
            try
            {
                if (tile.Item is { } item) PinLibraryService.MoveItem(item, dropped);
                else if (tile.Pin is { } dragged) dragged.SetGroup(dropped);
            }
            finally { _suppressRefresh = previous; }

            Refresh();
        }
    }

    private void RunNativeDrag(Tile tile, PointerEventArgs e)
    {
        _nativeDragging = true;
        _suppressRefresh = true;
        _lastClickKey = null;
        e.Handled = true;
        e.Pointer.Capture(null);
        EndHover();
        try
        {
            var path = tile.Item?.FullPath ?? tile.Pin?.ImageFilePath;
            if (path is null && tile.Pin?.DisplayedBitmap is { } bitmap)
                path = ClipboardWriter.TryWriteTempPngAsync("pin-drag", file =>
                {
                    bitmap.Save(file, new PngBitmapEncoderOptions());
                    return Task.CompletedTask;
                }).GetAwaiter().GetResult();
            if (path is null || !File.Exists(path))
            {
                ShowHint(I18n.T("Str.PinManager.DragMissing"));
                return;
            }
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            PinDragTarget Classify(PixelPoint point)
            {
                if (WindowsPinDrag.IsOverWindow(point, handle)) return PinDragTarget.Manager;
                return WindowsPinDrag.DesktopTarget(point);
            }
            var result = WindowsPinDrag.Run(path, handle, Classify,
                point =>
                {
                    if (WindowsPinDrag.IsOverWindow(point, handle))
                    {
                        var local = this.PointToClient(point);
                        UpdateGhost(tile, local);
                        UpdateDropHighlight(local);
                    }
                    else { HideGhost(clearSource: false); ClearDropHighlight(); }
                }, () => IsVisible);
            HideGhost();
            ClearDropHighlight();
            GuiLog.Write($"pin-drag: target={result.Target} accepted={result.Accepted} point={result.Position}");
            if (!IsVisible) return;
            if (result.Target == PinDragTarget.Manager) MoveTileToGroup(tile, this.PointToClient(result.Position));
            else if (result.ShouldPin)
            {
                var pin = tile.Item is { } item ? PinManager.Current.PinFromLibrary(item)
                    : tile.Pin is { IsVisible: true } existing ? existing : null;
                if (pin is null) ShowHint(I18n.T("Str.PinManager.DragMissing"));
                else
                {
                    pin.PlaceAtDrop(result.Position, _dragGrab);
                    pin.EnterRaised();
                }
            }
            else if (!result.Accepted) ShowHint(I18n.T("Str.PinManager.DragCancelled"));
        }
        catch (Exception ex)
        {
            GuiLog.Write($"pin-drag: failed {ex}");
            if (IsVisible) ShowHint(I18n.T("Str.PinManager.DragFailed"));
        }
        finally
        {
            _dragTile = null;
            _dragMoved = false;
            HideGhost();
            ClearDropHighlight();
            _nativeDragging = false;
            _suppressRefresh = false;
            if (IsVisible) Refresh();
        }
    }

    /// <summary>窗口坐标下，光标落在哪个分组卡片上（没落上返回 <c>null</c>）。</summary>
    private Control? HitTestCard(Point positionInWindow)
    {
        foreach (var card in _dropTargets.Keys)
        {
            if (Contains(card, positionInWindow))
            {
                return card;
            }
        }

        return null;
    }

    /// <summary>某个分组（<c>null</c> = 未分组）在屏幕上的贴图。</summary>
    private static List<PinWindow> PinsOfGroup(string? groupId) =>
        PinManager.Current.Windows
            .Where(pin => string.Equals(pin.GroupId, groupId, StringComparison.Ordinal))
            .ToList();

    /// <summary>让一个缩略图副本跟着光标走（"拖到哪个分组"这件事得看得见）。</summary>
    private void UpdateGhost(Tile tile, Point positionInWindow)
    {
        if (_ghost is null)
        {
            _ghost = new Border
            {
                Name = "PinDragGhost",
                Width = GhostWidth,
                Height = GhostHeight,
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Opacity = 0.9,
                BorderThickness = new Thickness(2),
                IsVisible = false,
                IsHitTestVisible = false,
            };
            BindTheme(_ghost, Border.BorderBrushProperty, "KscanAccentBrush");
            BindTheme(_ghost, Border.BackgroundProperty, "KscanPanelBrush");
            BindTheme(_ghost, Border.CornerRadiusProperty, "KscanPreviewCornerRadius");

            _ghostLayer.Children.Add(_ghost);
        }

        var bitmap = tile.Pin?.DisplayedBitmap
                     ?? (tile.Item is { } item ? PinLibraryService.LoadThumbnail(item) : null);

        _ghost.Child = bitmap is not null ? new Image { Source = bitmap, Stretch = Stretch.Uniform } : null;

        _ghost.IsVisible = true;
        Canvas.SetLeft(_ghost, positionInWindow.X - GhostWidth / 2);
        Canvas.SetTop(_ghost, positionInWindow.Y - GhostHeight / 2);

        // 原缩略图变淡，表示"这张正在被搬走"
        if (_dragSource is { } source)
        {
            source.Opacity = 0.35;
        }
    }

    private void HideGhost(bool clearSource = true)
    {
        if (_ghost is not null)
        {
            _ghost.IsVisible = false;
        }

        if (_dragSource is { } source)
        {
            source.Opacity = _dragSourceOpacity;
        }

        if (clearSource) _dragSource = null;
    }

    /// <summary>拖动时把"将要落到的分组框"描边加粗，让用户知道松手会放哪。</summary>
    private void UpdateDropHighlight(Point positionInWindow)
    {
        var target = HitTestCard(positionInWindow);

        if (ReferenceEquals(target, _dropHighlighted))
        {
            return;
        }

        ClearDropHighlight();

        if (target is Border border)
        {
            border.BorderThickness = new Thickness(3);
            _dropHighlighted = border;
        }
    }

    private void ClearDropHighlight()
    {
        if (_dropHighlighted is Border previous)
        {
            previous.BorderThickness = new Thickness(1);
        }

        _dropHighlighted = null;
    }

    // ---------------- 分组级动作 ----------------

    /// <summary>「贴这组」：库里没在屏幕上的全部贴出来，已在屏幕上的提到最前。</summary>
    private void PinGroupToScreen(List<Tile> tiles)
    {
        EndHover();

        foreach (var tile in tiles)
        {
            if (tile.Item is { } item && tile.Pin is null)
            {
                PinManager.Current.PinFromLibrary(item);
                continue;
            }

            if (tile.Pin is { } pin)
            {
                if (!pin.IsRaised)
                {
                    pin.EnterRaised();
                }

                pin.Activate();
            }
        }

        Refresh();
    }

    /// <summary>「收起这组」：只关窗口，图都还在库里。</summary>
    private void ClosePins(List<Tile> tiles)
    {
        foreach (var pin in tiles.Where(tile => tile.Pin is not null).Select(tile => tile.Pin!).ToList())
        {
            try
            {
                PinManager.Current.CloseSoft(pin);
            }
            catch (Exception)
            {
                // 已经关掉的忽略
            }
        }

        Refresh();
    }

    /// <summary>删除分组：先问用户"连图一起删"还是"只删分组"。</summary>
    private async Task DeleteGroupAsync(PinGroup group)
    {
        var choice = await PinDialog.ChooseAsync(
            this,
            I18n.T("Str.PinManager.DeleteGroupTitle"),
            I18n.F("Str.PinManager.DeleteGroupMessage", group.Name),
            I18n.T("Str.PinManager.DeleteGroupWithFiles"),
            I18n.T("Str.PinManager.DeleteGroupKeepFiles"),
            I18n.T("Str.Common.Cancel"));

        if (choice is < 0 or 2)
        {
            return;
        }

        PinLibraryService.DeleteGroup(group, deleteImages: choice == 0);
        Refresh();
    }

    /// <summary>
    /// 把所有非顶层态的贴图压到管理窗口下面（取消置顶），并记住它们原本是否置顶。
    /// 顶层态（用户点过、绿框亮着）跳过 —— 那是明确的用户意图，不该被自动撤销。
    /// </summary>
    private void DemotePins()
    {
        var changed = 0;

        foreach (var pin in PinManager.Current.Windows)
        {
            if (pin.IsRaised)
            {
                continue;
            }

            if (!_topmostBeforeDemote.ContainsKey(pin))
            {
                _topmostBeforeDemote[pin] = pin.IsTopmost;
            }

            if (pin.IsTopmost)
            {
                pin.SetTopmost(false);
                changed++;
            }
        }

        if (changed > 0)
        {
            GuiLog.Write($"z-order: manager demoted {changed} pin(s)");
        }
    }

    /// <summary>关窗时：解除顶层态，并恢复贴图原来的置顶状态。</summary>
    private void RestorePins()
    {
        foreach (var pin in PinManager.Current.Windows)
        {
            if (pin.IsRaised)
            {
                pin.ExitRaised();
            }
        }

        foreach (var (pin, wasTopmost) in _topmostBeforeDemote.ToList())
        {
            if (wasTopmost && !pin.IsHighlighted)
            {
                pin.SetTopmost(true);
            }
        }

        _topmostBeforeDemote.Clear();
    }

    private static Color ParseColor(string value)
    {
        try
        {
            return Color.Parse(PinGroup.NormalizeColor(value));
        }
        catch (Exception)
        {
            return Color.Parse(PinGroup.Palette[0]);
        }
    }
}
