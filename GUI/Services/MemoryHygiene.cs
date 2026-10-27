using System;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace Kscan.App.Services;

/// <summary>
/// 内存整理（只做"空闲时顺手收紧"，绝不放慢处理速度）。
/// <para>
/// 为什么需要：一次框选会在<b>大对象堆</b>上留下十几 MB 的全屏快照（<c>RegisterSelector</c> 里已及时 Dispose），
/// 但 .NET 的 GC 默认不会主动回缩 —— 连按几次框选，任务管理器里的占用会一路涨上去，体验上像"漏内存"。
/// </para>
/// <para>
/// 为什么不在关遮罩时直接 <c>GC.Collect()</c>：<b>关遮罩的下一步就是解码/识别</b>，主线程上压缩 LOH 会拖慢那一步。
/// 所以这里延后到结果出来之后、用户正在看结果的 idle 窗口里，在<b>线程池</b>上跑一次；
/// 它不会阻塞 UI 线程，也不影响任何一次识别的耗时。
/// </para>
/// </summary>
public static class MemoryHygiene
{
    /// <summary>延迟多久再整理（给"结果面板渲染 + 用户开始看"留出时间，避免和刚完成的工作抢 CPU）。</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(1500);

    /// <summary>是否已经排了一次（避免连点框选时排队多次 GC）。</summary>
    private static int _scheduled;

    /// <summary>
    /// 在后台排队一次"回收 + 压缩大对象堆"。重复调用只会生效一次，代价是线程池里一次后台 GC。
    /// </summary>
    public static void CompactWhenIdle()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Delay).ConfigureAwait(false);

                // 有贴图在屏幕上时**不压缩**大对象堆：贴图位图本身就是几十 MB 的活对象，
                // 压缩需要把它们整块搬走（memcpy 几十 MB），期间所有托管线程暂停 ——
                // 表现就是"刚截完图、贴图刚出现，卡一下"。有贴图时只做普通回收即可。
                var compacting = PinManager.Current.Count == 0;

                var started = Environment.TickCount64;

                if (compacting)
                {
                    // CompactOnce 只对下一次 GC 生效：把 LOH 里的空洞一起压掉，进程常驻才会真的回落
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                }

                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: compacting);

                GuiLog.Write(
                    $"gc: collect compacting={compacting} pins={PinManager.Current.Count} took={Environment.TickCount64 - started}ms");
            }
            catch (Exception)
            {
                // 整理内存失败不影响任何功能
            }
            finally
            {
                Interlocked.Exchange(ref _scheduled, 0);
            }
        });
    }
}
