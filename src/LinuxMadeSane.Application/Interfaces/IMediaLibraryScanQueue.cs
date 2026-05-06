namespace LinuxMadeSane.Application.Interfaces;

public interface IMediaLibraryScanQueue
{
    ValueTask EnqueueScanAsync(Guid? rootId, CancellationToken cancellationToken = default);
}
