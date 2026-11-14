// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
