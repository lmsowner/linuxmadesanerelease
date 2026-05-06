namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class MediaItemEntity
{
    public Guid Id { get; set; }
    public Guid LibraryRootId { get; set; }
    public MediaLibraryRootEntity? LibraryRoot { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public int MediaKind { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public double? DurationSeconds { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Container { get; set; }
    public bool? IsBrowserCompatible { get; set; }
    public bool? IsVlcCompatible { get; set; }
    public bool? RequiresRemux { get; set; }
    public bool? RequiresTranscode { get; set; }
    public DateTimeOffset IndexedUtc { get; set; }
}
