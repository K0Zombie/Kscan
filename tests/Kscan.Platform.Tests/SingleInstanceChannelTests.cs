using Kscan.Platform;

namespace Kscan.Platform.Tests;

/// <summary>
/// 单实例通道的行为约定：同进程内不同通道验证端点与流协议，跨进程生命周期另做发布包冒烟。
/// <para>
/// 注意：本机（Windows）只跑得通<b>具名管道</b>分支；Linux 的 Unix domain socket 分支
/// 另在 Arch WSL 中使用 self-contained 验证入口运行这些用例。
/// </para>
/// </summary>
public class SingleInstanceChannelTests
{
    [Fact]
    public async Task Non_owner_release_does_not_break_the_owner()
    {
        var name = NewName();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new SingleInstanceChannel(name);
        var other = new SingleInstanceChannel(name);
        Assert.True(owner.TryAcquire(command => { received.TrySetResult(command); return Task.CompletedTask; }));
        try
        {
            Assert.False(other.TryAcquire(_ => Task.CompletedTask));
            other.Release();
            Assert.True(await other.SendAsync("still-alive"));
            Assert.Equal("still-alive", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { owner.Release(); }
    }

    [Fact]
    public async Task Consecutive_commands_do_not_need_endpoint_retries()
    {
        var name = NewName();
        var messages = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var owner = new SingleInstanceChannel(name);
        Assert.True(owner.TryAcquire(command => { messages.Writer.TryWrite(command); return Task.CompletedTask; }));
        try
        {
            var sender = new SingleInstanceChannel(name);
            for (var i = 0; i < 30; i++)
            {
                Assert.True(await sender.SendAsync("command-" + i));
                Assert.Equal("command-" + i, await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.False(await sender.SendAsync(new string('中', 400)));
        }
        finally { owner.Release(); }
    }

    [Fact]
    public async Task Partial_stream_reads_are_assembled_until_newline()
    {
        var name = NewName();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new SingleInstanceChannel(name);
        Assert.True(owner.TryAcquire(command => { received.TrySetResult(command); return Task.CompletedTask; }));
        try
        {
            Stream stream;
            if (OperatingSystem.IsWindows())
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.Out);
                await pipe.ConnectAsync(1000);
                stream = pipe;
            }
            else
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(owner.Endpoint));
                stream = new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            await using (stream)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes("capture-中文\n");
                foreach (var value in bytes)
                {
                    await stream.WriteAsync(new byte[] { value });
                    await stream.FlushAsync();
                    await Task.Delay(5);
                }
                Assert.Equal("capture-中文", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }
        finally { owner.Release(); }
    }

    /// <summary>每个测试用独立端点名，避免并行/重复运行时互相抢端点。</summary>
    private static string NewName() => $"kscan-tests-{Guid.NewGuid():N}";

    /// <summary>
    /// 保留旧用例的发送辅助；新用例另行验证返回时端点就绪及连续发送无需重试。
    /// </summary>
    private static async Task<bool> WaitUntilDeliveredAsync(
        SingleInstanceChannel sender,
        string command,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (await sender.SendAsync(command))
            {
                return true;
            }

            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    [Fact]
    public void First_acquire_wins_and_the_second_sees_an_existing_instance()
    {
        var name = NewName();
        var owner = new SingleInstanceChannel(name);
        var later = new SingleInstanceChannel(name);

        Assert.True(owner.TryAcquire(_ => Task.CompletedTask));

        try
        {
            Assert.False(later.TryAcquire(_ => Task.CompletedTask));
        }
        finally
        {
            owner.Release();
        }
    }

    [Fact]
    public async Task Command_from_another_channel_reaches_the_owner()
    {
        var name = NewName();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var owner = new SingleInstanceChannel(name);
        Assert.True(owner.TryAcquire(command =>
        {
            received.TrySetResult(command);
            return Task.CompletedTask;
        }));

        try
        {
            var sender = new SingleInstanceChannel(name);

            Assert.True(await WaitUntilDeliveredAsync(sender, "activate", TimeSpan.FromSeconds(5)));
            Assert.Equal("activate", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            owner.Release();
        }
    }

    [Fact]
    public async Task Multi_line_command_is_flattened_to_a_single_line()
    {
        // 协议是"一行命令"：不压平的话服务端只读到第一行，后面的参数会静默丢掉
        var name = NewName();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var owner = new SingleInstanceChannel(name);
        Assert.True(owner.TryAcquire(command =>
        {
            received.TrySetResult(command);
            return Task.CompletedTask;
        }));

        try
        {
            var sender = new SingleInstanceChannel(name);

            Assert.True(await WaitUntilDeliveredAsync(
                sender,
                "capture-region\n--region",
                TimeSpan.FromSeconds(5)));

            Assert.Equal("capture-region --region", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            owner.Release();
        }
    }

    [Fact]
    public async Task Sending_with_no_instance_fails_quickly_instead_of_hanging()
    {
        var sender = new SingleInstanceChannel(NewName());
        var started = DateTime.UtcNow;

        Assert.False(await sender.SendAsync("activate"));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Release_frees_the_endpoint_for_a_new_instance()
    {
        // 退出/崩溃后端点必须能被重新占用，否则一次异常退出就把用户永久挡在外面
        var name = NewName();

        var first = new SingleInstanceChannel(name);
        Assert.True(first.TryAcquire(_ => Task.CompletedTask));
        first.Release();

        var second = new SingleInstanceChannel(name);
        Assert.True(second.TryAcquire(_ => Task.CompletedTask));
        second.Release();
    }

    [Fact]
    public void Endpoint_contains_the_channel_name()
    {
        var name = NewName();
        var channel = new SingleInstanceChannel(name);

        Assert.Contains(name, channel.Endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_name_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SingleInstanceChannel("   "));
    }
}
