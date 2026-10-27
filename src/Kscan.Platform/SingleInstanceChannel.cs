using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform;

/// <summary>
/// <see cref="ISingleInstanceChannel"/> 的跨平台实现：Windows 用具名管道，Linux/macOS 用 Unix domain socket。
/// </summary>
/// <remarks>
/// 几个刻意的取舍：
/// <list type="bullet">
/// <item>持有独占锁文件，并在返回前建好监听端点；进程崩溃后操作系统自动释放锁。
/// 兼容旧版时还会探测连接，只有锁的持有者能清理过期 socket。</item>
/// <item>命令是一行文本、单向发送（服务端不回执）：够用且少一类超时问题。</item>
/// <item>Linux 下端点放在 <c>$XDG_RUNTIME_DIR</c>（登录会话私有、退出即清理），并把 socket 权限设为 0600。</item>
/// </list>
/// </remarks>
internal sealed class SingleInstanceChannel : ISingleInstanceChannel
{
    /// <summary>命令长度上限：防止对面发来一坨东西把内存吃掉。</summary>
    private const int MaxCommandBytes = 1024;

    private const int ConnectTimeoutMs = 300;

    private readonly string _name;

    private CancellationTokenSource? _cts;
    private Task? _listenLoop;
    private bool _acquired;
    private FileStream? _ownership;
    private Socket? _unixListener;
    private NamedPipeServerStream? _pipeServer;

    public SingleInstanceChannel(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(['/', '\\']) >= 0 || Encoding.UTF8.GetByteCount(name) > 80)
            throw new ArgumentException("通道名称不能含路径分隔符，UTF-8 长度不能超过 80 字节。", nameof(name));
        _name = name;
    }

    public string Endpoint => OperatingSystem.IsWindows()
        ? $@"\\.\pipe\{_name}"
        : Path.Combine(RuntimeDirectory(), $"{_name}.sock");

    public bool TryAcquire(Func<string, Task> onCommand)
    {
        ArgumentNullException.ThrowIfNull(onCommand);

        if (_cts is not null) return false;

        // 锁文件只是"尽力而为"的互斥：拿不到就降级继续，**绝不因此让进程起不来**。
        // 以前只捕 IOException，而路径只读 / 被安全软件拦住 / 权限不足抛的是
        // UnauthorizedAccessException —— 它会一路冒到 Main，表现为"双击没反应"（进程秒崩）。
        // 锁文件不删除：删掉锁文件会让两个进程锁住不同 inode。
        try
        {
            _ownership = new FileStream(Path.Combine(RuntimeDirectory(), _name + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ownership = null;   // 降级：靠下面的"探测已有实例 + 独占管道"继续保证单实例
        }

        // 兼容尚未使用锁文件的旧版实例；锁没拿到但探测到有人，也算已有实例。
        if (CanConnect())
        {
            _ownership?.Dispose();
            _ownership = null;
            return false;
        }

        // 走到这里说明没有活着的实例：清掉可能残留的 socket 文件再重建
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                TryDelete(Endpoint);
                _unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _unixListener.Bind(new UnixDomainSocketEndPoint(Endpoint));
                _unixListener.Listen(16);
                TryRestrictPermissions(Endpoint);
            }
            catch
            {
                _unixListener?.Dispose();
                _unixListener = null;
                _ownership?.Dispose();
                _ownership = null;
                return false;   // 端点被占 / 不可用：当作"已有实例"，交给调用方转发，别崩
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                _pipeServer = new NamedPipeServerStream(_name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch
            {
                // 同名管道已存在 = 已有实例；返回 false 让调用方转发，而不是把异常抛到 Main
                _ownership?.Dispose();
                _ownership = null;
                return false;
            }
        }

        _acquired = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var listener = _unixListener;
        var pipe = _pipeServer;
        _listenLoop = Task.Run(() => ListenAsync(onCommand, listener, pipe, token));
        return true;
    }

    public async Task<bool> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = Encoding.UTF8.GetBytes(command.ReplaceLineEndings(" ") + "\n");
            if (payload.Length > MaxCommandBytes) return false;

            if (OperatingSystem.IsWindows())
            {
                await using var client = new NamedPipeClientStream(".", _name, PipeDirection.Out);
                await client.ConnectAsync(ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
                await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(Endpoint), cancellationToken).ConfigureAwait(false);
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception)
        {
            // 没有实例、对面刚退出、权限问题…… 一律当作"没送到"，由调用方决定怎么提示
            return false;
        }
    }

    public void Release()
    {
        // 用 _acquired 而不是 _ownership：锁文件降级拿不到时仍然要停掉监听、删掉端点
        if (!_acquired) return;
        _acquired = false;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过
        }

        _cts?.Dispose();
        _cts = null;
        _listenLoop = null;
        _unixListener?.Dispose();
        _unixListener = null;
        _pipeServer?.Dispose();
        _pipeServer = null;

        if (!OperatingSystem.IsWindows())
        {
            TryDelete(Endpoint);
        }
        _ownership?.Dispose();
        _ownership = null;
    }

    private bool CanConnect()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var client = new NamedPipeClientStream(".", _name, PipeDirection.Out);
                client.Connect(ConnectTimeoutMs);
                return true;
            }

            if (!File.Exists(Endpoint))
            {
                return false;
            }

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(Endpoint));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task ListenAsync(Func<string, Task> onCommand, Socket? listener, NamedPipeServerStream? pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        await pipe!.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                        await DispatchAsync(pipe, onCommand, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!cancellationToken.IsCancellationRequested && pipe!.IsConnected) pipe.Disconnect();
                    }
                }
                else
                {
                    using var connection = await listener!.AcceptAsync(cancellationToken).ConfigureAwait(false);
                    await using var stream = new NetworkStream(connection, ownsSocket: false);
                    await DispatchAsync(stream, onCommand, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // 单个连接出错不应该把整个监听循环干掉；稍等一下再继续
                try
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static async Task DispatchAsync(Stream stream, Func<string, Task> onCommand, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var buffer = new byte[MaxCommandBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            var newline = Array.IndexOf(buffer, (byte)'\n', count, read);
            count += read;
            if (newline >= 0) { count = newline; break; }
            if (count == buffer.Length) return;
        }

        var command = Encoding.UTF8.GetString(buffer, 0, count).Trim();
        if (command.Length == 0)
        {
            return;
        }

        await onCommand(command).ConfigureAwait(false);
    }

    private static string RuntimeDirectory()
    {
        // 优先 $XDG_RUNTIME_DIR（登录会话私有、退出即回收）
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime) && Directory.Exists(runtime))
        {
            return runtime;
        }

        var directory = Path.Combine(Path.GetTempPath(), "kscan-runtime-" + Environment.UserName);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    private static void TryRestrictPermissions(string path)
    {
        // 显式判断：分析器（CA1416）看不到"调用方只在 Unix 分支"这层关系
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // 文件系统不支持就忽略（例如某些挂载点）
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 删不掉也无妨：后面 bind 会给出明确错误
        }
    }
}
