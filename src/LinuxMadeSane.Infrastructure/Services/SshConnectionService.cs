// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Net.Sockets;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SshConnectionService(
    ManagedHostSshConnectionFactory sshConnectionFactory,
    ILogger<SshConnectionService> logger) : ISshConnectionService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    public async Task<HostConnectionTestResult> TestConnectionAsync(
        ManagedHost host,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogInformation("Testing SSH connection for host {HostId} at {Hostname}:{Port}", host.Id, host.Hostname, host.Port);

        if (string.IsNullOrWhiteSpace(host.Hostname))
        {
            return BuildResult(
                ConnectionTestStatus.InvalidConfiguration,
                "The SSH host is incomplete.",
                "Enter a hostname before testing the SSH connection.");
        }

        if (host.Port is < 1 or > 65535)
        {
            return BuildResult(
                ConnectionTestStatus.InvalidConfiguration,
                "The SSH port is invalid.",
                "Use a port between 1 and 65535.");
        }

        ManagedHostSshCredentials credentials;
        try
        {
            credentials = await sshConnectionFactory.ResolveStoredCredentialsAsync(host, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return BuildResult(
                ConnectionTestStatus.InvalidConfiguration,
                "The SSH credentials are incomplete.",
                exception.Message);
        }

        try
        {
            using var client = sshConnectionFactory.CreateSshClient(host, credentials, ConnectTimeout, KeepAliveInterval);

            client.Connect();

            try
            {
                using var stream = client.CreateShellStream("xterm-256color", 120, 32, 0, 0, 1024);
                return BuildResult(
                    ConnectionTestStatus.Succeeded,
                    "SSH login succeeded.",
                    $"Linux Made Sane authenticated to {host.Hostname} and opened a shell session.");
            }
            finally
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
            }
        }
        catch (InvalidOperationException exception)
        {
            return BuildResult(
                ConnectionTestStatus.InvalidConfiguration,
                "The SSH credentials are not usable.",
                exception.Message);
        }
        catch (SshAuthenticationException exception)
        {
            return BuildResult(
                ConnectionTestStatus.Failed,
                "SSH authentication failed.",
                exception.Message);
        }
        catch (SshOperationTimeoutException)
        {
            return BuildResult(
                ConnectionTestStatus.TimedOut,
                "Timed out establishing the SSH session.",
                $"Linux Made Sane could not authenticate to {host.Hostname}:{host.Port} within {ConnectTimeout.TotalSeconds:0} seconds.");
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut)
        {
            return BuildResult(
                ConnectionTestStatus.TimedOut,
                "Timed out establishing the SSH session.",
                exception.Message);
        }
        catch (SocketException exception)
        {
            return BuildResult(
                ConnectionTestStatus.Failed,
                "The SSH endpoint could not be reached.",
                exception.Message);
        }
        catch (SshConnectionException exception)
        {
            return BuildResult(
                ConnectionTestStatus.Failed,
                "The SSH session could not be established.",
                exception.Message);
        }
        catch (SshException exception)
        {
            return BuildResult(
                ConnectionTestStatus.Failed,
                "The SSH session failed during setup.",
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return BuildResult(
                ConnectionTestStatus.Failed,
                "The SSH probe failed while opening the session.",
                exception.Message);
        }
    }

    private static HostConnectionTestResult BuildResult(
        ConnectionTestStatus status,
        string summary,
        string? detail) =>
        new(
            status,
            summary,
            detail,
            DateTimeOffset.UtcNow);
}
