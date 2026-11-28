// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Tests.Infrastructure;

public sealed class MailRelayProviderPrerequisitesTests
{
    [Fact]
    public async Task ExistingRuntimeNeedsNoInstallationOrElevation()
    {
        var runner = new Runner([0]);
        Assert.Equal("pwsh", await MailProviderPrerequisites.EnsurePowerShellAsync(runner, _ => { }, default));
        Assert.Single(runner.Requests); Assert.False(runner.Requests[0].RequiresSudo);
    }
    [Fact]
    public async Task ExistingManagedRuntimeIsReusedAfterAnOldSystemRuntime()
    {
        var runner = new Runner([78, 0]);
        Assert.Equal(MailProviderPrerequisites.ManagedPowerShell, await MailProviderPrerequisites.EnsurePowerShellAsync(runner, _ => { }, default));
        Assert.All(runner.Requests, request => Assert.False(request.RequiresSudo));
    }
    [Fact]
    public async Task MissingRuntimeIsInstalledWithElevationThenVerifiedBeforeUse()
    {
        var runner = new Runner([127, 127, 0, 0]); var progress = new List<MicrosoftMailRegistrationProgress>();
        Assert.Equal(MailProviderPrerequisites.ManagedPowerShell, await MailProviderPrerequisites.EnsurePowerShellAsync(runner, progress.Add, default));
        Assert.Equal(new[] { false, false, true, false }, runner.Requests.Select(r => r.RequiresSudo));
        Assert.Equal("bash", runner.Requests[2].FileName);
        Assert.Equal(MailProviderPrerequisites.InstallPowerShell, runner.Requests[2].Arguments[^1]);
        Assert.Contains(progress, p => p.Message.Contains("automatically"));
        var directory = Environment.GetEnvironmentVariable("LMS_MAIL_UI_FIXTURE_DIR");
        if (directory is not null) { Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, "Install-PowerShell.sh"), runner.Requests[2].Arguments[^1]); }
    }
    [Fact]
    public async Task FailedInstallationStopsInsteadOfLaunchingRegistration()
    {
        var runner = new Runner([127, 127, 100]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MailProviderPrerequisites.EnsurePowerShellAsync(runner, _ => { }, default));
        Assert.Equal(3, runner.Requests.Count); Assert.Contains("LMS could not finish", error.Message);
        Assert.DoesNotContain("Install the", error.Message);
    }
    [Fact]
    public async Task CancellationDoesNotInterruptPackageInstallationMidTransaction()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new Runner([127, 127, 0], request => { if (request.RequiresSudo) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MailProviderPrerequisites.EnsurePowerShellAsync(runner, _ => { }, cancellation.Token));
        Assert.False(runner.InstallTokenCanBeCancelled);
    }
    private sealed class Runner(int[] results, Action<LinuxCommandRequest>? action = null) : IStreamingLinuxCommandRunner
    {
        public List<LinuxCommandRequest> Requests { get; } = [];
        public bool InstallTokenCanBeCancelled;
        public Task<LinuxCommandResult> RunStreamingAsync(LinuxCommandRequest request, bool dryRun, Action<LinuxCommandOutput> output, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request);
            if (request.RequiresSudo) InstallTokenCanBeCancelled = token.CanBeCanceled;
            action?.Invoke(request);
            return Task.FromResult(new LinuxCommandResult("fixture", results[Requests.Count - 1], "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false));
        }
    }
}
