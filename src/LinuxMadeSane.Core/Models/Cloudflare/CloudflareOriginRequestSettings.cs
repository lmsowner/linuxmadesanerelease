// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareOriginRequestSettings(
    string OriginServerName = "",
    string CertificateAuthorityPool = "",
    bool NoTlsVerify = false,
    int TlsTimeoutSeconds = 10,
    bool Http2Origin = false,
    bool MatchSniToHost = false,
    string HttpHostHeader = "",
    bool DisableChunkedEncoding = false,
    int ConnectTimeoutSeconds = 30,
    bool NoHappyEyeballs = false,
    string ProxyType = "",
    int KeepAliveTimeoutSeconds = 90,
    int KeepAliveConnections = 100,
    int TcpKeepAliveSeconds = 30)
{
    public static CloudflareOriginRequestSettings Default { get; } = new();
}
