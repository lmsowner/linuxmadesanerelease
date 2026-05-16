// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
