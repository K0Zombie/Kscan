namespace Kscan.App.Services;

/// <summary>后台准备完成后，只允许最新请求发布，且不能覆盖期间由用户复制的新内容。</summary>
internal sealed class ClipboardCopyGate
{
    internal readonly record struct Ticket(long Id, long? Sequence);
    private long _latest;
    public Ticket Begin(long? sequence) => new(Interlocked.Increment(ref _latest), sequence);
    public bool CanPublish(Ticket ticket, long? sequence) => ticket.Id == Volatile.Read(ref _latest)
        && (ticket.Sequence is null || ticket.Sequence == sequence);
}
