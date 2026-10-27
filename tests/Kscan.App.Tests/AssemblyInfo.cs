using Xunit;

// 这套测试大量共享进程级单例（AppSettingsService / I18n / ThemeService / PinGroupStore / PinLibraryService）
// 和临时目录里的文件；并行跑时彼此踩状态，会出现"23 个用例随机红"的假红灯（实测连跑几次全绿）。
// 关掉并行：慢几秒，但结果稳定可信。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
