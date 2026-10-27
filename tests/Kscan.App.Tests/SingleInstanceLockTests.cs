using Kscan.Platform;

namespace Kscan.App.Tests;

/// <summary>
/// 单实例锁：同一台机器/同一用户只允许一个 kscan。
/// <para>
/// 这是"同时开了两个 kscan"的根治手段：以前靠锁文件 + 管道，连不上时会**降级再开一个**，
/// 结果两个实例各建一个托盘、各挂一个库监视器，互相覆盖 library.json。
/// Mutex 由内核保证，抢不到就只能退出。
/// </para>
/// </summary>
public sealed class SingleInstanceLockTests
{
    [Fact]
    public void Only_one_owner_at_a_time()
    {
        var name = "kscan-lock-test-" + Guid.NewGuid().ToString("N");
        var first = new SingleInstanceLock(name);

        Assert.True(first.TryAcquire());

        // 注意：同一个线程重复 WaitOne 同一个 Mutex 是"递归成功"，所以第二次必须在别的线程里试
        var second = RunOnOtherThread(name);
        Assert.False(second, "已经有实例拿着锁，第二个必须抢不到");

        first.Dispose();

        var third = RunOnOtherThread(name);
        Assert.True(third, "前一个释放之后，新实例应该能拿到锁");
    }

    [Fact]
    public void Reacquiring_from_the_same_instance_returns_false()
    {
        var name = "kscan-lock-test-" + Guid.NewGuid().ToString("N");
        using var lockA = new SingleInstanceLock(name);

        Assert.True(lockA.TryAcquire());
        Assert.False(lockA.TryAcquire());
    }

    /// <summary>在**独立的线程**上抢锁（Mutex 归线程所有：同线程重复 WaitOne 是递归成功）。</summary>
    private static bool RunOnOtherThread(string name)
    {
        var acquired = false;

        var thread = new Thread(() =>
        {
            using var other = new SingleInstanceLock(name);
            acquired = other.TryAcquire();
        });

        thread.Start();
        thread.Join();
        return acquired;
    }
}
