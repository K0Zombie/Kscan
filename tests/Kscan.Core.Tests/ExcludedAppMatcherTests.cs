using Kscan.Core.Configuration;

namespace Kscan.Core.Tests;

/// <summary>热键排除列表的匹配规则：进程名 / 完整路径 / 大小写 / 空条目。</summary>
public class ExcludedAppMatcherTests
{
    [Fact]
    public void Matches_by_process_name_ignoring_case_and_extension()
    {
        var list = new AppList(["taskmgr.exe"]);

        Assert.True(ExcludedAppMatcher.IsExcluded(list, "Taskmgr.EXE", null));
        Assert.False(ExcludedAppMatcher.IsExcluded(list, "explorer.exe", null));

        // 手填不带扩展名也能匹配
        Assert.True(ExcludedAppMatcher.IsExcluded(new AppList(["taskmgr"]), "taskmgr.exe", null));
    }

    [Fact]
    public void Matches_full_path_case_insensitively()
    {
        var list = new AppList([@"C:\Games\SomeGame\game.exe"]);

        Assert.True(ExcludedAppMatcher.IsExcluded(list, "game.exe", @"c:\games\somegame\GAME.exe"));
        Assert.False(ExcludedAppMatcher.IsExcluded(list, "game.exe", @"D:\Other\game.exe"));
    }

    [Fact]
    public void Path_entries_do_not_fall_back_to_bare_process_names()
    {
        // 配的是路径就按路径比：同名但不同目录的程序不该被误排除
        var list = new AppList([@"C:\Games\A\game.exe"]);

        Assert.False(ExcludedAppMatcher.IsExcluded(list, "game.exe", null));
    }

    [Fact]
    public void Empty_list_excludes_nothing()
    {
        Assert.False(ExcludedAppMatcher.IsExcluded(AppList.Empty, "taskmgr.exe", @"C:\Windows\System32\Taskmgr.exe"));
    }

    [Fact]
    public void Blank_entries_are_ignored()
    {
        var list = new AppList(["", "   ", "\"taskmgr.exe\""]);

        Assert.True(ExcludedAppMatcher.IsExcluded(list, "taskmgr.exe", null));
        Assert.False(ExcludedAppMatcher.IsExcluded(new AppList(["  "]), "taskmgr.exe", null));
    }

    [Fact]
    public void NormalizeProcessName_prefers_the_executable_path()
    {
        Assert.Equal("game.exe", ExcludedAppMatcher.NormalizeProcessName("other.exe", @"C:\x\game.exe"));
        Assert.Equal("other.exe", ExcludedAppMatcher.NormalizeProcessName("other", null));
    }
}
