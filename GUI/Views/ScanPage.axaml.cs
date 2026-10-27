using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Kscan.App.ViewModels;

namespace Kscan.App.Views;

public partial class ScanPage : UserControl
{
    private static readonly IBrush HighlightBorder = new SolidColorBrush(Color.Parse("#FFC107"));

    private ScanViewModel? _model;

    public ScanPage()
    {
        InitializeComponent();

        // 支持把图片直接拖进页面
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);

        // 键位走隧道阶段：面板里的按钮/列表拿到焦点时也能收（J/K/H/L 不是输入框里会用的键）
        AddHandler(KeyDownEvent, OnPreprocessKeyDown, RoutingStrategies.Tunnel);

        // DataContext 由 MainWindow 后置绑定，所以要监听变化而不是在构造函数里取
        DataContextChanged += (_, _) => AttachModel();
    }

    /// <summary>
    /// 手动处理面板的键位（D21：<c>J</c>/<c>K</c> 切层、<c>H</c>/<c>L</c> 调参数、<c>PgUp</c>/<c>PgDn</c> 跳首尾）。
    /// 面板没打开时一律不接管，避免干扰页面上的其它控件。
    /// </summary>
    private void OnPreprocessKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is not { IsPreprocessOpen: true } model)
        {
            return;
        }

        // 在文本框里打字时不抢键
        if (e.Source is TextBox)
        {
            return;
        }

        var handled = e.Key switch
        {
            Key.J or Key.Down => model.MovePreprocessLevel(1),
            Key.K or Key.Up => model.MovePreprocessLevel(-1),
            Key.H or Key.Left => model.AdjustPreprocessParameter(-1),
            Key.L or Key.Right => model.AdjustPreprocessParameter(1),
            Key.PageUp => model.JumpPreprocessEdge(last: false),
            Key.PageDown => model.JumpPreprocessEdge(last: true),
            _ => false,
        };

        if (handled)
        {
            e.Handled = true;
        }
    }

    /// <summary>点层列表里的某一层。</summary>
    private void OnLevelSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_model is null || sender is not ListBox list || list.SelectedIndex < 0)
        {
            return;
        }

        _model.SelectPreprocessLevel(list.SelectedIndex);
    }

    private void AttachModel()
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
        }

        _model = DataContext as ScanViewModel;

        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanViewModel.HighlightedIndex))
        {
            SyncHighlight();
        }
    }

    /// <summary>把「当前高亮的码」同步到列表：加一道边、并滚动到可见。</summary>
    private void SyncHighlight()
    {
        if (_model is null)
        {
            return;
        }

        for (var i = 0; i < _model.Results.Count; i++)
        {
            if (FindCard(ResultsList.ContainerFromIndex(i)) is not { } card)
            {
                continue;
            }

            var active = i == _model.HighlightedIndex;
            card.BorderBrush = active ? HighlightBorder : Brushes.Transparent;
            card.BorderThickness = new Avalonia.Thickness(active ? 1 : 0);

            if (active)
            {
                card.BringIntoView();
            }
        }
    }

    private static Border? FindCard(Control? container) => container switch
    {
        ContentPresenter presenter => presenter.Child as Border,
        Border border => border,
        _ => null,
    };

    // 悬停列表项 → 预览图上对应的框也高亮（反向联动由绑定自动完成）
    private void OnCardPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_model is not null && sender is Control { DataContext: ResultItem item })
        {
            _model.HighlightedIndex = item.Index;
        }
    }

    private void OnCardPointerExited(object? sender, PointerEventArgs e)
    {
        if (_model is not null)
        {
            _model.HighlightedIndex = -1;
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        // Avalonia 12 的拖放走 IDataTransfer：用 DataFormat.File 判断是不是文件
        e.DragEffects = e.DataTransfer?.Contains(DataFormat.File) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ScanViewModel model)
        {
            return;
        }

        var files = e.DataTransfer?.TryGetFiles();
        if (files is null)
        {
            return;
        }

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { Length: > 0 } path)
            {
                await model.LoadFileAsync(path);
            }
        }
    }
}
