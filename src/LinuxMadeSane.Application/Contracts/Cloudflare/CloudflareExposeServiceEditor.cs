// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Cloudflare;

public sealed class CloudflareExposeServiceEditor
{
    public Guid? ConfigId { get; set; }

    public string ApiTokenInput { get; set; } = string.Empty;

    public bool SaveApiToken { get; set; } = true;

    public bool ClearSavedApiToken { get; set; }

    public string AccountId { get; set; } = string.Empty;

    [Required(ErrorMessage = "Choose a Cloudflare zone from the validated results.")]
    public string ZoneId { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a service name.")]
    public string ServiceName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a subdomain.")]
    public string Subdomain { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a local service URL.")]
    [Url(ErrorMessage = "Enter an absolute local service URL such as http://localhost:8080.")]
    public string LocalServiceUrl { get; set; } = "http://localhost:8080";

    public bool NoTlsVerify { get; set; } = true;

    public string OriginServerName { get; set; } = string.Empty;

    public string CertificateAuthorityPool { get; set; } = string.Empty;

    public int TlsTimeoutSeconds { get; set; } = 10;

    public bool Http2Origin { get; set; }

    public bool MatchSniToHost { get; set; }

    public string HttpHostHeader { get; set; } = string.Empty;

    public bool DisableChunkedEncoding { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 30;

    public bool NoHappyEyeballs { get; set; }

    public string ProxyType { get; set; } = string.Empty;

    public int KeepAliveTimeoutSeconds { get; set; } = 90;

    public int KeepAliveConnections { get; set; } = 100;

    public int TcpKeepAliveSeconds { get; set; } = 30;

    public string TunnelId { get; set; } = string.Empty;

    public bool CreateNewTunnel { get; set; }

    public bool RunConnectorInstallOnHost { get; set; } = true;

    public ExposedServiceAccessMode AccessMode { get; set; } = ExposedServiceAccessMode.NoAccessProtection;

    public string EmailAllowList { get; set; } = string.Empty;

    public string EmailDomainAllowList { get; set; } = string.Empty;

    public bool ConfirmDangerousExposure { get; set; }

    public bool ConfirmDnsRecordReplacement { get; set; }
}
