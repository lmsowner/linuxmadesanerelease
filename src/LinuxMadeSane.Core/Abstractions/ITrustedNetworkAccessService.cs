using System.Net;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ITrustedNetworkAccessService
{
    Task<TrustedNetworkAccessResult> EvaluateAsync(
        IPAddress? remoteAddress,
        string? requestHost,
        CancellationToken cancellationToken = default);
}
