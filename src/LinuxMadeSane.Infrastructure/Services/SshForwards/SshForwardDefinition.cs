// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Net;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Security;
namespace LinuxMadeSane.Infrastructure.Services.SshForwards;
public static class SshForwardDefinition
{
    public static SshPortForward Validate(SshPortForward input)
    {
        var rule = input with { Name = input.Name.Trim(), Description = input.Description.Trim(),
            Server = input.Server.Trim(), Username = input.Username.Trim(), ListenAddress = input.ListenAddress.Trim(),
            TargetHost = input.TargetHost.Trim(), OutboundBindAddress = input.OutboundBindAddress.Trim() };
        if (rule.Name.Length is < 1 or > 80 || rule.Description.Length > 2000 || rule.Name.Any(char.IsControl))
            throw new InvalidOperationException("Enter a name (up to 80 characters) and notes (up to 2,000 characters).");
        if (rule.CredentialId == Guid.Empty) throw new InvalidOperationException("Choose saved SSH credentials.");
        Host(rule.Server, "SSH server");
        if (rule.Username.Length is < 1 or > 128 || rule.Username.StartsWith('-') ||
            rule.Username.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '@' or '/' or '\\'))
            throw new InvalidOperationException("Enter the username on the SSH server.");
        Port(rule.SshPort);
        if (!Enum.IsDefined(rule.Mode) || !Enum.IsDefined(rule.ListenKind) || !Enum.IsDefined(rule.TargetKind))
            throw new InvalidOperationException("Choose a supported forward type and endpoints.");
        if (rule.IsSocks && rule.ListenKind != SshForwardEndpointKind.Tcp)
            throw new InvalidOperationException("SOCKS proxies need a TCP listener.");
        if (rule.ListenKind == SshForwardEndpointKind.Tcp)
        {
            if (rule.ListenAddress is not "localhost" and not "*" && !IPAddress.TryParse(rule.ListenAddress, out _))
                throw new InvalidOperationException("The listen address must be an IP address, localhost or *.");
            if (!rule.IsRemote || rule.ListenPort != 0) Port(rule.ListenPort);
        }
        else Socket(rule.ListenSocket);
        if (!rule.IsSocks)
        {
            if (rule.TargetKind == SshForwardEndpointKind.Tcp) { Host(rule.TargetHost, "destination"); Port(rule.TargetPort); }
            else Socket(rule.TargetSocket);
        }
        if (rule.KeepAliveSeconds is < 5 or > 300 || rule.MissedKeepAlives is < 1 or > 10 || rule.RetrySeconds is < 1 or > 300)
            throw new InvalidOperationException("Keepalive must be 5–300 seconds, missed replies 1–10, and retry delay 1–300 seconds.");
        if (rule.AddressFamily is not "auto" and not "ipv4" and not "ipv6") throw new InvalidOperationException("Choose automatic, IPv4 or IPv6.");
        if (rule.OutboundBindAddress.Length > 0 && !IPAddress.TryParse(rule.OutboundBindAddress, out _))
            throw new InvalidOperationException("Outbound bind address must be a local IP address.");
        if (!Regex.IsMatch(rule.SocketMask, "^[0-7]{3,4}$")) throw new InvalidOperationException("Socket permission mask must be three or four octal digits, such as 0177.");
        if (rule.Mode == SshForwardMode.Local && rule.ListenKind == SshForwardEndpointKind.Tcp && rule.TargetKind == SshForwardEndpointKind.Tcp &&
            rule.Server is "localhost" or "127.0.0.1" or "::1" && rule.TargetHost == rule.ListenAddress && rule.TargetPort == rule.ListenPort)
            throw new InvalidOperationException("This forward would connect back to its own listener.");
        return rule;
    }
    public static string RuntimeRevision(SshPortForward rule) => System.Text.Json.JsonSerializer.Serialize(rule with
    { Name = "", Description = "", EdgeGatewayRouteId = null, PublicUrl = "" });
    public static IReadOnlyList<string> Arguments(SshPortForward rule, string identityPath, string knownHosts, string controlSocket)
    {
        var args = new List<string> { "-N", "-T", "-F", "/dev/null", "-M", "-S", controlSocket,
            "-o", "ControlPersist=no", "-o", "ExitOnForwardFailure=yes", "-o", "ConnectTimeout=15",
            "-o", "ServerAliveInterval=" + rule.KeepAliveSeconds, "-o", "ServerAliveCountMax=" + rule.MissedKeepAlives,
            "-o", "StrictHostKeyChecking=accept-new", "-o", "UserKnownHostsFile=" + knownHosts,
            "-o", "GlobalKnownHostsFile=/dev/null", "-o", "ForwardAgent=no", "-o", "IdentityAgent=none",
            "-o", "IdentitiesOnly=yes", "-o", "NumberOfPasswordPrompts=1", "-o", "StreamLocalBindMask=" + rule.SocketMask,
            "-o", "StreamLocalBindUnlink=" + (rule.ReplaceStaleSocket ? "yes" : "no") };
        if (identityPath.Length > 0) { args.AddRange(["-i", identityPath, "-o", "PreferredAuthentications=publickey,password,keyboard-interactive"]); }
        else args.AddRange(["-o", "PubkeyAuthentication=no", "-o", "PreferredAuthentications=password,keyboard-interactive"]);
        if (rule.AddressFamily != "auto") args.Add(rule.AddressFamily == "ipv4" ? "-4" : "-6");
        if (rule.Compression) args.Add("-C");
        if (rule.OutboundBindAddress.Length > 0) args.AddRange(["-b", rule.OutboundBindAddress]);
        var listen = rule.ListenKind == SshForwardEndpointKind.UnixSocket ? rule.ListenSocket : Endpoint(rule.ListenAddress, rule.ListenPort);
        var target = rule.TargetKind == SshForwardEndpointKind.UnixSocket ? rule.TargetSocket : Endpoint(rule.TargetHost, rule.TargetPort);
        args.Add(rule.Mode switch { SshForwardMode.Local => "-L", SshForwardMode.LocalSocks => "-D", _ => "-R" });
        args.Add(rule.IsSocks ? listen : listen + ":" + target);
        args.AddRange(["-p", rule.SshPort.ToString(), "-l", rule.Username, "--", rule.Server]);
        return args;
    }
    public static string Endpoint(string host, int port) => (host.Contains(':') ? "[" + host + "]" : host) + ":" + port;
    private static void Port(int port) { if (port is < 1 or > 65535) throw new InvalidOperationException("Ports must be between 1 and 65535."); }
    private static void Host(string text, string label)
    {
        if (text.Length is < 1 or > 255 || text.StartsWith('-') || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '@' or ',' or '[' or ']') ||
            text.Contains(':') && !IPAddress.TryParse(text, out _)) throw new InvalidOperationException($"Enter a valid {label} hostname or IP address, without a URL or path.");
    }
    private static void Socket(string path)
    {
        if (!path.StartsWith('/') || path.Length > 100 || path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == ':'))
            throw new InvalidOperationException("Use an absolute Unix socket path up to 100 characters, without spaces or colons.");
    }
}
