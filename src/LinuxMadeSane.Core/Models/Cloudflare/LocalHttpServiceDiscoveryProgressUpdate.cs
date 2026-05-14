namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record LocalHttpServiceDiscoveryProgressUpdate(
    string Message,
    int ProbedCount,
    int TotalProbeCount,
    int FoundCount,
    LocalHttpServiceEndpoint? FoundEndpoint = null,
    bool IsCompleted = false);
