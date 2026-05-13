namespace LinuxMadeSane.Core.Models;

public sealed class FileAccessPathNotFoundException : InvalidOperationException
{
    public FileAccessPathNotFoundException(string requestedPath, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        RequestedPath = requestedPath;
    }

    public string RequestedPath { get; }
}
