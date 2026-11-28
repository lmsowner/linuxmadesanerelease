// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Application.Services.EdgeGateway;

public sealed record EdgeZoneSnapshot(CloudflareValidationResult Validation, DateTimeOffset CheckedUtc, bool Stale);

public sealed class EdgeGatewayZoneDirectory(TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1);
    private EdgeZoneSnapshot? last;
    private string? key;
    private DateTimeOffset nextCheck;

    public async Task<EdgeZoneSnapshot> ReadAsync(string token, ICloudflareZoneService zones, CancellationToken cancellation)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        await gate.WaitAsync(cancellation);
        try
        {
            if (key != fingerprint)
            {
                key = fingerprint;
                last = null;
            }
            if (last != null && clock.GetUtcNow() < nextCheck) return last;
            try
            {
                var accounts = await zones.ListAccountsAsync(token, cancellation);
                var current = await zones.ListZonesAsync(token, cancellation);
                last = new(new(true, accounts, current), clock.GetUtcNow(), false);
                nextCheck = last.CheckedUtc.AddDays(1);
            }
            catch (Exception) when (last != null && !cancellation.IsCancellationRequested)
            {
                last = last with { Stale = true };
                nextCheck = clock.GetUtcNow().AddHours(1);
            }
            return last;
        }
        finally { gate.Release(); }
    }
}
