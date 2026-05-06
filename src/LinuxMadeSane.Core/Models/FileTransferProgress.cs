namespace LinuxMadeSane.Core.Models;

public sealed record FileTransferProgress(
    long BytesTransferred,
    long? TotalBytes,
    string? Detail = null);
