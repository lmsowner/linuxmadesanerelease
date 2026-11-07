// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Core.Models.RemoteDesktop;

public enum RemoteDesktopProtocol { Rdp, Vnc }
public sealed record RemoteDesktopOptions
{
    public string SecurityMode { get; init; } = "nla";
    public bool AcceptUntrustedCertificate { get; init; }
    public int ColourDepth { get; init; } = 24;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public string ResizeMethod { get; init; } = "display-update";
    public bool Clipboard { get; init; } = true;
    public bool Audio { get; init; }
    public bool Drive { get; init; }
    public string VncEncoding { get; init; } = "auto";
    public bool VncCopyRect { get; init; } = true;
    public bool VncLossless { get; init; }
    public bool ReadOnly { get; init; }
}
public sealed record RemoteDesktopConnection(Guid Id, Guid OwnerUserId, Guid? HostId, string Name,
    string Hostname, RemoteDesktopProtocol Protocol, int Port, Guid CredentialId,
    RemoteDesktopOptions Options, long Revision, DateTimeOffset UpdatedAtUtc);
public sealed record RemoteDesktopCandidate(string Hostname, RemoteDesktopProtocol Protocol, int Port,
    bool ProtocolIdentified, string Source, string? DnsName = null);

public sealed record RemoteDesktopProbeResult(string Hostname, int Port, RemoteDesktopCandidate? Candidate);
