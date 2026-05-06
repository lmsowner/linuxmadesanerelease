namespace LinuxMadeSane.Application.Interfaces;

public interface IAiChatRunQueue
{
    ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default);
    void Cancel(Guid runId);
}
