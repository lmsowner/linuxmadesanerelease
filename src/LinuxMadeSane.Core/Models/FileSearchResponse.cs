namespace LinuxMadeSane.Core.Models;

public sealed record FileSearchResponse(
    string RootPath,
    IReadOnlyList<FileSearchMatch> Results,
    bool LimitReached);
