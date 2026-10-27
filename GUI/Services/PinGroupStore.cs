using Kscan.Core.Pins;

namespace Kscan.App.Services;

/// <summary>
/// 贴图分组的持有者（分组名 / 颜色）。
/// <para>
/// 现在分组的实体是<b>磁盘上的目录</b>（<c>data\user\pins\&lt;分组名&gt;\</c>）：
/// 分组名就是目录名，改名会同步改目录名；分组里的图片由 <see cref="PinLibraryService"/> 管。
/// 空分组也保留（目录留着），不会被自动删掉。
/// </para>
/// </summary>
public sealed class PinGroupStore
{
    private readonly List<PinGroup> _groups = [];

    public static PinGroupStore Current { get; } = new();

    /// <summary>分组有增删改时触发（管理窗口据此刷新）。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<PinGroup> Groups => _groups;

    public PinGroup? Find(string? id) =>
        id is null ? null : _groups.FirstOrDefault(group => group.Id == id);

    public PinGroup? FindByName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : _groups.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>新建一个分组（名字留空时给"分组 N"；重名会自动加后缀）。</summary>
    public PinGroup Create(string? name = null)
    {
        var group = new PinGroup(
            Id: Guid.NewGuid().ToString("N")[..8],
            Name: UniqueName(Sanitize(name, NextDefaultName)),
            Color: PinGroup.ColorAt(_groups.Count));

        _groups.Add(group);
        PinLibraryService.GroupDirectory(group);
        RaiseChanged();
        return group;
    }

    /// <summary>改名字（空名字忽略）。重名返回 <c>false</c>；目录名会跟着改。</summary>
    public bool Rename(string id, string? name)
    {
        var index = _groups.FindIndex(group => group.Id == id);

        if (index < 0 || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var current = _groups[index];
        var next = Sanitize(name, () => current.Name);

        if (string.Equals(next, current.Name, StringComparison.Ordinal))
        {
            return true;
        }

        if (FindByName(next) is not null)
        {
            return false;   // 目录名不能重复
        }

        _groups[index] = current with { Name = next };

        PinLibraryService.RenameGroup(_groups[index], current.Name, next);
        RaiseChanged();
        return true;
    }

    /// <summary>改颜色。</summary>
    public bool SetColor(string id, string? color)
    {
        var index = _groups.FindIndex(group => group.Id == id);

        if (index < 0)
        {
            return false;
        }

        _groups[index] = _groups[index] with { Color = PinGroup.NormalizeColor(color) };
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 删除分组：目录里的图怎么处理由 <see cref="PinLibraryService.DeleteGroup"/> 决定
    /// （连图一起删 / 移到 <c>_trash</c>），这里只负责内存与刷新。
    /// </summary>
    public bool Delete(string id)
    {
        var group = Find(id);

        if (group is null)
        {
            return false;
        }

        // 默认"只删分组、图片移到 _trash"（安全优先）；界面上会先问用户再传参
        PinLibraryService.DeleteGroup(group, deleteImages: false);
        return true;
    }

    /// <summary>只把分组从内存里摘掉（<see cref="PinLibraryService"/> 删完目录后调用）。</summary>
    internal void RemoveGroup(string id)
    {
        var index = _groups.FindIndex(group => group.Id == id);

        if (index < 0)
        {
            return;
        }

        _groups.RemoveAt(index);
        RaiseChanged();
    }

    /// <summary>用库里的分组整体替换（启动加载时用）。</summary>
    public void ReplaceAll(IEnumerable<PinGroup> groups)
    {
        _groups.Clear();
        _groups.AddRange(groups.Select(group => group with { Color = PinGroup.NormalizeColor(group.Color) }));
        RaiseChanged();
    }

    public void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);

        // 分组列表变化 → 落盘 + 保证目录存在
        PinLibraryService.SaveGroups();
    }

    private string NextDefaultName()
    {
        for (var i = 1; ; i++)
        {
            var name = $"分组 {i}";

            if (FindByName(name) is null)
            {
                return name;
            }
        }
    }

    /// <summary>目录名安全化：去掉 Windows 非法字符、首尾空白与点；空串用兜底名。</summary>
    private static string Sanitize(string? name, Func<string> fallback)
    {
        var text = name?.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback();
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            text = text.Replace(invalid, '_');
        }

        text = text.Trim().TrimEnd('.');

        return string.IsNullOrWhiteSpace(text) ? fallback() : text;
    }

    private string UniqueName(string name)
    {
        if (FindByName(name) is null)
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{name} {i}";

            if (FindByName(candidate) is null)
            {
                return candidate;
            }
        }
    }
}
