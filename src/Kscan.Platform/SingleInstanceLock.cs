namespace Kscan.Platform;

/// <summary>
/// 操作系统级的单实例锁（具名 <see cref="Mutex"/>）。
/// <para>
/// 为什么在 IPC 之外还要这一层：管道/锁文件都可能因为"一边管理员一边普通"、
/// 名称冲突、监听循环正好在忙而连不上 —— 旧实现在这种情况下会"降级直接启动"，
/// 结果就是同时跑起两个 kscan（两个托盘、两个库监视器、互相覆盖 library.json）。
/// Mutex 由内核保证，抢不到就是抢不到，没有降级空间。
/// </para>
/// <para>
/// 上一个进程崩溃后 Mutex 会变成"被遗弃"状态，<see cref="TryAcquire"/> 会接管它
/// （抛 <see cref="AbandonedMutexException"/> 是正常的接管信号，不是错误）。
/// </para>
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly string _name;

    private Mutex? _mutex;
    private bool _owned;

    public SingleInstanceLock(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>拿到锁返回 <c>true</c>；已经有实例在跑返回 <c>false</c>。</summary>
    public bool TryAcquire()
    {
        if (_owned)
        {
            return false;
        }

        Mutex mutex;

        try
        {
            mutex = new Mutex(initiallyOwned: false, _name);
        }
        catch (Exception)
        {
            // 名字非法/权限不足：宁可让本次启动继续（不能因为锁把进程卡死），
            // 但会由管道那一层继续兜底
            return false;
        }

        try
        {
            // 0 毫秒：立刻回答"有没有别人"
            _owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例异常退出，锁被遗弃 —— 这次接管它
            _owned = true;
        }
        catch (Exception)
        {
            _owned = false;
        }

        if (!_owned)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        return true;
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (_owned)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 不是本线程持有的（理论上不会发生）：忽略
            }
        }

        _mutex.Dispose();
        _mutex = null;
        _owned = false;
    }
}
