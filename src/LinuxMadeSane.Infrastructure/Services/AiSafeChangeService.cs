using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

// Guardrail: safe-change analysis uses the same host-aware file and command abstractions
// as the rest of the product. Do not add direct SFTP/SSH/local execution branches here.
public sealed class AiSafeChangeService(
    IAiConversationStore conversationStore,
    IManagedHostStore hostStore,
    ICommandExecutionService commandExecutionService,
    IManagedHostFileAccessService fileAccessService) : IAiSafeChangeService
{
    private const int SnapshotPreviewBytes = 262144;
    private const string BackupRootPath = "/tmp/linuxmadesane-ai-safe-change";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<AiSafeChangeState?> AnalyzeAsync(
        Guid threadId,
        AiProposedActionProposal proposal,
        CancellationToken cancellationToken = default)
    {
        if (proposal.SafeChange is not null)
        {
            return proposal.SafeChange;
        }

        return proposal.ToolName switch
        {
            AiToolNames.RestartService => await AnalyzeRestartServiceAsync(threadId, proposal, cancellationToken),
            AiToolNames.WriteFileWithConfirmation => await AnalyzeWriteFileAsync(threadId, proposal, cancellationToken),
            AiToolNames.InstallPackageWithConfirmation => await AnalyzeInstallPackagesAsync(threadId, proposal, cancellationToken),
            AiToolNames.RunCommand when proposal.RiskLevel != AiActionRiskLevel.ReadOnly => AnalyzeGenericCommand(proposal),
            _ => null
        };
    }

    public async Task<AiSafeChangeExecutionResult> ExecuteAsync(
        AiChatThread thread,
        AiProposedAction action,
        AiToolInvocation invocation,
        Func<CancellationToken, Task<AiToolExecutionResult>> executeToolAsync,
        CancellationToken cancellationToken = default)
    {
        if (action.SafeChange is null)
        {
            return new AiSafeChangeExecutionResult(action, await executeToolAsync(cancellationToken));
        }

        var snapshot = await CaptureSnapshotAsync(thread.Id, action, cancellationToken);
        var actionWithSnapshot = action with
        {
            SafeChange = action.SafeChange with
            {
                Snapshot = snapshot
            }
        };

        AiToolExecutionResult executionResult;
        try
        {
            executionResult = await executeToolAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            executionResult = CreateFailureExecutionResult(action, invocation, exception.Message);
        }

        var verification = await VerifyExecutionAsync(thread.Id, actionWithSnapshot, executionResult, cancellationToken);
        var summary = BuildChangeSummary(actionWithSnapshot, executionResult, verification);

        return new AiSafeChangeExecutionResult(
            actionWithSnapshot with
            {
                Outcome = executionResult.PersistedResult.Outcome,
                SafeChange = actionWithSnapshot.SafeChange with
                {
                    ChangeSummary = summary,
                    VerificationResult = verification
                }
            },
            executionResult);
    }

    public async Task<AiProposedActionProposal> CreateRollbackProposalAsync(
        Guid threadId,
        Guid originalActionId,
        CancellationToken cancellationToken = default)
    {
        var action = await FindActionAsync(threadId, originalActionId, cancellationToken);
        var safeChange = action.SafeChange ?? throw new InvalidOperationException("This change does not have safe rollback data.");
        var snapshot = safeChange.Snapshot ?? throw new InvalidOperationException("Rollback is not available because no targeted snapshot was captured.");

        if (!safeChange.SupportsRollback)
        {
            throw new InvalidOperationException("Rollback is not available for this change.");
        }

        var rollbackState = new AiSafeChangeState(
            AiSafeChangeOperationKind.Rollback,
            new AiSafeChangeImpactPreview(
                $"Restore the captured pre-change state for {action.Title}.",
                "The original Deep Fix change can be reversed using the targeted snapshot Linux Made Sane captured before it ran.",
                safeChange.ImpactPreview.AffectedTargets,
                "Rollback may stop, start, or re-write the affected service, package, or file to restore the earlier state.",
                "A brief interruption is possible while the previous state is restored.",
                $"Rollback support: {safeChange.RollbackPlan.SupportLevel}.",
                []),
            new AiSafeChangeRollbackPlan(
                AiRollbackSupportLevel.NotAvailable,
                "Linux Made Sane does not automatically rollback a rollback action.",
                [],
                ["If this rollback is wrong, create a new corrective change from the restored state."],
                "Rollback-of-rollback is intentionally disabled to avoid loops."),
            new AiSafeChangeVerificationPlan(
                $"Verify that the captured pre-change state from snapshot {snapshot.Id} is restored.",
                BuildRollbackVerificationSteps(action)))
        {
            ChangeSummary = $"Rollback prepared for {action.Title} using snapshot {snapshot.Id}."
        };

        return new AiProposedActionProposal
        {
            Title = $"Rollback {action.Title}",
            Description = $"Restore the targeted before-state captured for {action.Title}.",
            ToolName = AiToolNames.RollbackSafeChange,
            ProviderToolCallId = $"manual-rollback:{originalActionId}:{Guid.NewGuid():N}",
            ToolArgumentsJson = JsonSerializer.Serialize(new RollbackSafeChangeToolRequest(originalActionId), SerializerOptions),
            CommandPreview = $"Restore snapshot {snapshot.Id} for {action.Title}.",
            RiskLevel = action.RiskLevel,
            SafeChange = rollbackState
        };
    }

    public async Task<AiToolExecutionResult> ExecuteRollbackAsync(
        AiChatThread thread,
        AiToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var request = DeserializeRequest<RollbackSafeChangeToolRequest>(invocation.ArgumentsJson);
        var action = await FindActionAsync(thread.Id, request.OriginalActionId, cancellationToken);
        var safeChange = action.SafeChange ?? throw new InvalidOperationException("The original action does not carry safe-change data.");
        var snapshot = safeChange.Snapshot ?? throw new InvalidOperationException("The original action has no captured snapshot to rollback.");
        var host = await ResolveHostForActionAsync(thread.Id, action, cancellationToken);

        var restoredItems = new List<string>();
        var remainingItems = new List<string>();
        var builder = new StringBuilder();

        switch (safeChange.OperationKind)
        {
            case AiSafeChangeOperationKind.FileWrite:
                await RollbackFileWriteAsync(host, snapshot, restoredItems, remainingItems, builder, cancellationToken);
                break;
            case AiSafeChangeOperationKind.ServiceRestart:
                await RollbackServiceRestartAsync(host, snapshot, restoredItems, remainingItems, builder, cancellationToken);
                break;
            case AiSafeChangeOperationKind.PackageInstall:
                await RollbackPackageInstallAsync(host, snapshot, restoredItems, remainingItems, builder, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Linux Made Sane cannot automatically rollback this action.");
        }

        var rollbackVerification = await VerifyRollbackAsync(thread.Id, action, snapshot, restoredItems, remainingItems, cancellationToken);
        var rollbackOutcome = rollbackVerification.Succeeded && remainingItems.Count == 0
            ? AiExecutionOutcome.Succeeded
            : rollbackVerification.Succeeded
                ? AiExecutionOutcome.Succeeded
                : AiExecutionOutcome.Failed;

        var rollbackResult = new AiSafeChangeRollbackResult(
            rollbackOutcome,
            remainingItems.Count == 0
                ? $"Rollback restored the targeted state for {action.Title}."
                : $"Rollback restored part of the targeted state for {action.Title}.",
            restoredItems,
            remainingItems,
            DateTimeOffset.UtcNow)
        {
            VerificationResult = rollbackVerification
        };

        await PersistUpdatedActionAsync(
            action with
            {
                SafeChange = safeChange with
                {
                    RollbackResult = rollbackResult
                }
            },
            cancellationToken);

        var response = new RollbackSafeChangeToolResponse(
            action.Id,
            rollbackResult.Summary,
            rollbackOutcome == AiExecutionOutcome.Succeeded,
            builder.ToString().Trim(),
            remainingItems.Count == 0 ? string.Empty : string.Join(Environment.NewLine, remainingItems),
            rollbackOutcome == AiExecutionOutcome.Succeeded ? 0 : 1,
            rollbackResult.ExecutedAtUtc);

        return new AiToolExecutionResult(
            new AiToolDefinition(
                AiToolNames.RollbackSafeChange,
                "Rollback a previously captured safe change.",
                typeof(RollbackSafeChangeToolRequest),
                typeof(RollbackSafeChangeToolResponse),
                new AiToolApprovalMetadata(
                    action.RiskLevel,
                    AiApprovalRequirement.UserConfirmation,
                    true,
                    true,
                    false),
                "IAiSafeChangeService",
                false),
            response,
            new AiToolResult(
                Guid.NewGuid(),
                invocation.Id,
                rollbackOutcome,
                rollbackResult.Summary,
                builder.ToString().Trim(),
                remainingItems.Count == 0 ? string.Empty : string.Join(Environment.NewLine, remainingItems),
                JsonSerializer.Serialize(response, SerializerOptions),
                rollbackOutcome == AiExecutionOutcome.Succeeded ? 0 : 1,
                rollbackResult.ExecutedAtUtc));
    }

    private async Task<AiSafeChangeState> AnalyzeRestartServiceAsync(
        Guid threadId,
        AiProposedActionProposal proposal,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<RestartServiceToolRequest>(proposal.ToolArgumentsJson);
        var host = await ResolveHostAsync(threadId, request.ServerId, cancellationToken);
        var activeState = await ReadServiceActiveStateAsync(host, request.ServiceName.Trim(), cancellationToken);
        var enabledState = await ReadServiceEnabledStateAsync(host, request.ServiceName.Trim(), cancellationToken);

        return new AiSafeChangeState(
            AiSafeChangeOperationKind.ServiceRestart,
            new AiSafeChangeImpactPreview(
                $"Restart the systemd service {request.ServiceName.Trim()} on {host.Name}.",
                "The service must be restarted so the requested fix can take effect or recover the unit.",
                [new AiSafeChangeTarget(AiSafeChangeTargetKind.Service, request.ServiceName.Trim(), host.Name)],
                "Linux Made Sane will call systemctl restart and then verify the service is active.",
                "Brief interruption while the service restarts.",
                "Medium operational risk because service availability can blip during restart.",
                []),
            new AiSafeChangeRollbackPlan(
                AiRollbackSupportLevel.Partial,
                activeState
                    ? "Linux Made Sane can try to restore the prior active/inactive state, but it cannot reconstruct the exact in-memory process state from before the restart."
                    : "Linux Made Sane can stop the service again if it was previously inactive.",
                ["Restore the prior active/inactive state of the systemd unit."],
                ["If the service still behaves incorrectly after rollback, inspect its logs and configuration manually."],
                $"Previous enabled state was '{enabledState}'. A restart does not revert runtime state perfectly."),
            new AiSafeChangeVerificationPlan(
                "Verify that the service is active after the restart.",
                [$"Run systemctl is-active {request.ServiceName.Trim()} and expect 'active'."]));
    }

    private async Task<AiSafeChangeState> AnalyzeWriteFileAsync(
        Guid threadId,
        AiProposedActionProposal proposal,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<WriteFileWithConfirmationToolRequest>(proposal.ToolArgumentsJson);
        var host = await ResolveHostAsync(threadId, request.ServerId, cancellationToken);
        var metadata = await TryReadFileMetadataAsync(host, request.Path.Trim(), cancellationToken);
        var warnings = new List<string>();
        var rollbackSupport = AiRollbackSupportLevel.Full;
        var rollbackSummary = "Linux Made Sane can restore the previous file contents or remove the new file if the target did not exist.";

        if (metadata.Exists && metadata.SizeBytes > SnapshotPreviewBytes)
        {
            rollbackSupport = AiRollbackSupportLevel.ManualOnly;
            rollbackSummary = $"The target file is {metadata.SizeBytes} bytes, which exceeds the safe inline snapshot limit. Linux Made Sane will not claim automatic rollback unless the targeted snapshot succeeds.";
            warnings.Add("The current file is larger than the preferred targeted snapshot limit, so automatic rollback may degrade to manual restore.");
        }

        return new AiSafeChangeState(
            AiSafeChangeOperationKind.FileWrite,
            new AiSafeChangeImpactPreview(
                $"Write {request.Content.Length} byte(s) to {request.Path.Trim()} on {host.Name}.",
                "The fix needs to update file contents on the target host.",
                [new AiSafeChangeTarget(AiSafeChangeTargetKind.File, request.Path.Trim(), host.Name)],
                "No service restart is automatic. A separate fix step may still reload or restart a dependent service afterwards.",
                "No direct interruption expected from the file write itself.",
                metadata.Exists
                    ? "High configuration risk because an existing file will be replaced."
                    : "Medium configuration risk because a new file will be created.",
                warnings),
            new AiSafeChangeRollbackPlan(
                rollbackSupport,
                rollbackSummary,
                metadata.Exists
                    ? [$"Restore the previous contents of {request.Path.Trim()} from the targeted backup snapshot."]
                    : [$"Delete {request.Path.Trim()} if the file was newly created by this change."],
                ["If dependent services cache the old file, manually reload them after rollback if needed."],
                metadata.Exists
                    ? $"Current file size: {metadata.SizeBytes} byte(s)."
                    : "The target file does not currently exist."),
            new AiSafeChangeVerificationPlan(
                "Read the file back after write and verify its contents match the requested payload.",
                [$"Read {request.Path.Trim()} and compare its content hash with the planned payload."]));
    }

    private async Task<AiSafeChangeState> AnalyzeInstallPackagesAsync(
        Guid threadId,
        AiProposedActionProposal proposal,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<InstallPackageWithConfirmationToolRequest>(proposal.ToolArgumentsJson);
        var host = await ResolveHostAsync(threadId, request.ServerId, cancellationToken);
        var packageNames = NormalizePackageNames(request.PackageNames);
        var states = await ReadPackageStatesAsync(host, packageNames, cancellationToken);
        var allAbsent = states.All(state => !state.WasInstalled);
        var rollbackSupport = allAbsent ? AiRollbackSupportLevel.Full : AiRollbackSupportLevel.Partial;

        return new AiSafeChangeState(
            AiSafeChangeOperationKind.PackageInstall,
            new AiSafeChangeImpactPreview(
                $"Install {string.Join(", ", packageNames)} on {host.Name}.",
                "The requested fix needs the package set to exist on the target host.",
                packageNames.Select(name => new AiSafeChangeTarget(AiSafeChangeTargetKind.Package, name, host.Name)).ToArray(),
                "Package install can pull in dependencies and may affect services owned by those packages.",
                "Possible brief interruption if package hooks restart services.",
                "Privileged mutation because apt will change system packages.",
                []),
            new AiSafeChangeRollbackPlan(
                rollbackSupport,
                allAbsent
                    ? "Linux Made Sane can remove packages that were absent before this install."
                    : "Linux Made Sane can remove newly added packages, but it will not automatically downgrade packages that already existed with another version.",
                ["Remove packages that were absent before the install."],
                ["Manually restore previous package versions if apt upgraded an already-installed package."],
                states.Any(state => state.WasInstalled)
                    ? "One or more packages already exist, so version rollback remains manual."
                    : "All packages are currently absent."),
            new AiSafeChangeVerificationPlan(
                "Verify that dpkg reports each requested package as installed after the action.",
                packageNames.Select(name => $"Check that package {name} is installed with dpkg-query.").ToArray()));
    }

    private static AiSafeChangeState AnalyzeGenericCommand(AiProposedActionProposal proposal) =>
        new(
            AiSafeChangeOperationKind.GenericMutation,
            new AiSafeChangeImpactPreview(
                "Run an arbitrary shell command on the target Linux host.",
                "The provider requested a direct shell mutation instead of a narrower Linux Made Sane operation.",
                [new AiSafeChangeTarget(AiSafeChangeTargetKind.Command, proposal.Title, proposal.CommandPreview)],
                "Service impact is unknown until the command runs.",
                "Interruption is unknown because the command is arbitrary.",
                "High trust required because the command can touch arbitrary files, services, packages, users, mounts, or firewall state.",
                ["Linux Made Sane cannot infer every affected resource from an arbitrary shell command."]),
            new AiSafeChangeRollbackPlan(
                AiRollbackSupportLevel.NotAvailable,
                "Linux Made Sane cannot promise automatic rollback for an arbitrary shell command.",
                [],
                ["If this command changes the machine, create a dedicated rollback or corrective action after reviewing the result."],
                "Targeted snapshots are not reliable for arbitrary shell commands because the affected state is unknown."),
            new AiSafeChangeVerificationPlan(
                "Verify the command exit code and any resulting output.",
                ["Confirm the command exits successfully and inspect stdout/stderr for the expected effect."]));

    private async Task<AiSafeChangeSnapshot> CaptureSnapshotAsync(
        Guid threadId,
        AiProposedAction action,
        CancellationToken cancellationToken)
    {
        var snapshotId = Guid.NewGuid();
        var host = await ResolveHostForActionAsync(threadId, action, cancellationToken);

        return action.ToolName switch
        {
            AiToolNames.RestartService => await CaptureServiceSnapshotAsync(snapshotId, host, action, cancellationToken),
            AiToolNames.WriteFileWithConfirmation => await CaptureFileSnapshotAsync(snapshotId, host, action, cancellationToken),
            AiToolNames.InstallPackageWithConfirmation => await CapturePackageSnapshotAsync(snapshotId, host, action, cancellationToken),
            _ => new AiSafeChangeSnapshot(
                snapshotId,
                DateTimeOffset.UtcNow,
                "No targeted snapshot captured for the arbitrary command.",
                [],
                ["Linux Made Sane could not infer a safe targeted snapshot for this arbitrary shell command."])
            {
                Notes = "Rollback is unavailable for arbitrary shell commands."
            }
        };
    }

    private async Task<AiSafeChangeSnapshot> CaptureServiceSnapshotAsync(
        Guid snapshotId,
        ManagedHost host,
        AiProposedAction action,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<RestartServiceToolRequest>(action.ToolArgumentsJson);
        var activeState = await ReadServiceActiveStateAsync(host, request.ServiceName.Trim(), cancellationToken);
        var enabledState = await ReadServiceEnabledStateAsync(host, request.ServiceName.Trim(), cancellationToken);
        return new AiSafeChangeSnapshot(
            snapshotId,
            DateTimeOffset.UtcNow,
            $"Captured active/enabled state for {request.ServiceName.Trim()} before restart.",
            [$"Service {request.ServiceName.Trim()} active={activeState} enabled={enabledState}."],
            [])
        {
            Service = new AiSafeChangeServiceSnapshot(request.ServiceName.Trim(), activeState, enabledState)
        };
    }

    private async Task<AiSafeChangeSnapshot> CaptureFileSnapshotAsync(
        Guid snapshotId,
        ManagedHost host,
        AiProposedAction action,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<WriteFileWithConfirmationToolRequest>(action.ToolArgumentsJson);
        var metadata = await TryReadFileMetadataAsync(host, request.Path.Trim(), cancellationToken);
        if (!metadata.Exists)
        {
            return new AiSafeChangeSnapshot(
                snapshotId,
                DateTimeOffset.UtcNow,
                $"Recorded that {request.Path.Trim()} did not exist before the write.",
                [$"{request.Path.Trim()} was absent before the change."],
                [])
            {
                File = new AiSafeChangeFileSnapshot(request.Path.Trim(), false, string.Empty, 0, string.Empty)
            };
        }

        var content = await ReadFileContentsAsync(host, request.Path.Trim(), SnapshotPreviewBytes, cancellationToken);
        var backupPath = BuildBackupPath(host, snapshotId, request.Path.Trim());
        await WriteBackupFileAsync(host, backupPath, content.Content, cancellationToken);
        return new AiSafeChangeSnapshot(
            snapshotId,
            DateTimeOffset.UtcNow,
            $"Captured the previous contents of {request.Path.Trim()} before overwrite.",
            [$"Backed up {request.Path.Trim()} to {backupPath}."],
            content.IsTruncated
                ? ["The original file exceeded the preferred snapshot read limit. Automatic rollback may be incomplete."]
                : [])
        {
            File = new AiSafeChangeFileSnapshot(
                request.Path.Trim(),
                true,
                backupPath,
                content.SizeBytes,
                ComputeSha256(content.Content)),
            Notes = content.IsTruncated
                ? "The source file was truncated while read for backup."
                : string.Empty
        };
    }

    private async Task<AiSafeChangeSnapshot> CapturePackageSnapshotAsync(
        Guid snapshotId,
        ManagedHost host,
        AiProposedAction action,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<InstallPackageWithConfirmationToolRequest>(action.ToolArgumentsJson);
        var packageNames = NormalizePackageNames(request.PackageNames);
        var states = await ReadPackageStatesAsync(host, packageNames, cancellationToken);
        return new AiSafeChangeSnapshot(
            snapshotId,
            DateTimeOffset.UtcNow,
            $"Captured installed/version state for {packageNames.Length} package(s) before apt install.",
            states.Select(state => state.WasInstalled
                ? $"{state.PackageName} installed ({state.Version})."
                : $"{state.PackageName} not installed.").ToArray(),
            [])
        {
            Package = new AiSafeChangePackageSnapshot(states)
        };
    }

    private async Task<AiSafeChangeVerificationResult> VerifyExecutionAsync(
        Guid threadId,
        AiProposedAction action,
        AiToolExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        if (executionResult.PersistedResult.Outcome != AiExecutionOutcome.Succeeded)
        {
            return new AiSafeChangeVerificationResult(
                false,
                "The change did not complete successfully, so post-change verification could not pass.",
                [new AiSafeChangeVerificationStepResult("Execution outcome", false, executionResult.PersistedResult.Summary)],
                DateTimeOffset.UtcNow);
        }

        var host = await ResolveHostForActionAsync(threadId, action, cancellationToken);
        return action.ToolName switch
        {
            AiToolNames.RestartService => await VerifyServiceRestartAsync(host, action, cancellationToken),
            AiToolNames.WriteFileWithConfirmation => await VerifyFileWriteAsync(host, action, cancellationToken),
            AiToolNames.InstallPackageWithConfirmation => await VerifyPackageInstallAsync(host, action, cancellationToken),
            _ => new AiSafeChangeVerificationResult(
                true,
                "Linux Made Sane verified the action at the generic level by checking the command outcome.",
                [new AiSafeChangeVerificationStepResult("Exit code", true, "The command reported success.")],
                DateTimeOffset.UtcNow)
        };
    }

    private async Task<AiSafeChangeVerificationResult> VerifyServiceRestartAsync(
        ManagedHost host,
        AiProposedAction action,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<RestartServiceToolRequest>(action.ToolArgumentsJson);
        var activeState = await ReadServiceActiveStateAsync(host, request.ServiceName.Trim(), cancellationToken);
        return new AiSafeChangeVerificationResult(
            activeState,
            activeState
                ? $"Verified that {request.ServiceName.Trim()} is active after restart."
                : $"Verification failed because {request.ServiceName.Trim()} is not active after restart.",
            [new AiSafeChangeVerificationStepResult(
                $"systemctl is-active {request.ServiceName.Trim()}",
                activeState,
                activeState ? "Service is active." : "Service did not report active.")],
            DateTimeOffset.UtcNow);
    }

    private async Task<AiSafeChangeVerificationResult> VerifyFileWriteAsync(
        ManagedHost host,
        AiProposedAction action,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<WriteFileWithConfirmationToolRequest>(action.ToolArgumentsJson);
        var currentContent = await ReadFileContentsAsync(host, request.Path.Trim(), Math.Max(SnapshotPreviewBytes, Encoding.UTF8.GetByteCount(request.Content)), cancellationToken);
        var expectedHash = ComputeSha256(request.Content);
        var actualHash = ComputeSha256(currentContent.Content);
        var matches = string.Equals(expectedHash, actualHash, StringComparison.Ordinal);

        return new AiSafeChangeVerificationResult(
            matches,
            matches
                ? $"Verified that {request.Path.Trim()} matches the requested content."
                : $"Verification failed because {request.Path.Trim()} does not match the requested content.",
            [new AiSafeChangeVerificationStepResult(
                $"Read back {request.Path.Trim()}",
                matches,
                matches
                    ? $"Content hash {actualHash} matched."
                    : $"Expected hash {expectedHash}, got {actualHash}.")],
            DateTimeOffset.UtcNow);
    }

    private async Task<AiSafeChangeVerificationResult> VerifyPackageInstallAsync(
        ManagedHost host,
        AiProposedAction action,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequest<InstallPackageWithConfirmationToolRequest>(action.ToolArgumentsJson);
        var packageNames = NormalizePackageNames(request.PackageNames);
        var states = await ReadPackageStatesAsync(host, packageNames, cancellationToken);
        var failures = states.Where(state => !state.WasInstalled).ToArray();
        var succeeded = failures.Length == 0;

        return new AiSafeChangeVerificationResult(
            succeeded,
            succeeded
                ? "Verified that all requested packages are installed."
                : "One or more requested packages are still missing after the apt install.",
            states.Select(state => new AiSafeChangeVerificationStepResult(
                $"dpkg-query {state.PackageName}",
                state.WasInstalled,
                state.WasInstalled
                    ? $"Installed version {state.Version}."
                    : "Package is not installed.")).ToArray(),
            DateTimeOffset.UtcNow);
    }

    private async Task<AiSafeChangeVerificationResult> VerifyRollbackAsync(
        Guid threadId,
        AiProposedAction action,
        AiSafeChangeSnapshot snapshot,
        IReadOnlyList<string> restoredItems,
        IReadOnlyList<string> remainingItems,
        CancellationToken cancellationToken)
    {
        var host = await ResolveHostForActionAsync(threadId, action, cancellationToken);
        return action.SafeChange?.OperationKind switch
        {
            AiSafeChangeOperationKind.FileWrite => await VerifyRollbackFileAsync(host, snapshot, cancellationToken),
            AiSafeChangeOperationKind.ServiceRestart => await VerifyRollbackServiceAsync(host, snapshot, cancellationToken),
            AiSafeChangeOperationKind.PackageInstall => await VerifyRollbackPackageAsync(host, snapshot, cancellationToken),
            _ => new AiSafeChangeVerificationResult(
                remainingItems.Count == 0,
                remainingItems.Count == 0 ? "Rollback completed." : "Rollback completed with unresolved items.",
                [new AiSafeChangeVerificationStepResult("Rollback summary", remainingItems.Count == 0, string.Join(Environment.NewLine, restoredItems.Concat(remainingItems)))],
                DateTimeOffset.UtcNow)
        };
    }

    private async Task<AiSafeChangeVerificationResult> VerifyRollbackFileAsync(
        ManagedHost host,
        AiSafeChangeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var file = snapshot.File ?? throw new InvalidOperationException("The snapshot does not contain file data.");
        if (!file.Existed)
        {
            var exists = (await TryReadFileMetadataAsync(host, file.OriginalPath, cancellationToken)).Exists;
            return new AiSafeChangeVerificationResult(
                !exists,
                !exists
                    ? $"Verified that {file.OriginalPath} was removed during rollback."
                    : $"Rollback verification failed because {file.OriginalPath} still exists.",
                [new AiSafeChangeVerificationStepResult(file.OriginalPath, !exists, !exists ? "File is absent." : "File still exists.")],
                DateTimeOffset.UtcNow);
        }

        var restored = await ReadFileContentsAsync(host, file.OriginalPath, SnapshotPreviewBytes, cancellationToken);
        var hash = ComputeSha256(restored.Content);
        var matches = string.Equals(hash, file.ContentHash, StringComparison.Ordinal);
        return new AiSafeChangeVerificationResult(
            matches,
            matches
                ? $"Verified that {file.OriginalPath} matches the captured pre-change contents."
                : $"Rollback verification failed because {file.OriginalPath} does not match the captured pre-change contents.",
            [new AiSafeChangeVerificationStepResult(file.OriginalPath, matches, matches ? "File hash matches the captured snapshot." : $"Expected hash {file.ContentHash}, got {hash}.")],
            DateTimeOffset.UtcNow);
    }

    private async Task<AiSafeChangeVerificationResult> VerifyRollbackServiceAsync(
        ManagedHost host,
        AiSafeChangeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var service = snapshot.Service ?? throw new InvalidOperationException("The snapshot does not contain service data.");
        var activeState = await ReadServiceActiveStateAsync(host, service.ServiceName, cancellationToken);
        var matches = activeState == service.WasActive;
        return new AiSafeChangeVerificationResult(
            matches,
            matches
                ? $"Verified that {service.ServiceName} returned to its previous active state."
                : $"Rollback verification failed because {service.ServiceName} did not return to its previous active state.",
            [new AiSafeChangeVerificationStepResult(
                $"systemctl is-active {service.ServiceName}",
                matches,
                matches ? $"Service active state restored to {service.WasActive}." : $"Expected active={service.WasActive}, got active={activeState}.")],
            DateTimeOffset.UtcNow);
    }

    private async Task<AiSafeChangeVerificationResult> VerifyRollbackPackageAsync(
        ManagedHost host,
        AiSafeChangeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var packageSnapshot = snapshot.Package ?? throw new InvalidOperationException("The snapshot does not contain package data.");
        var states = await ReadPackageStatesAsync(host, packageSnapshot.Packages.Select(package => package.PackageName).ToArray(), cancellationToken);
        var stepResults = packageSnapshot.Packages
            .Select(original =>
            {
                var current = states.First(state => state.PackageName.Equals(original.PackageName, StringComparison.OrdinalIgnoreCase));
                var matches = original.WasInstalled
                    ? current.WasInstalled
                    : !current.WasInstalled;
                return new AiSafeChangeVerificationStepResult(
                    $"dpkg-query {original.PackageName}",
                    matches,
                    original.WasInstalled
                        ? (current.WasInstalled ? "Package is still installed as expected." : "Package is missing after rollback.")
                        : (!current.WasInstalled ? "Package was removed again." : "Package is still installed."));
            })
            .ToArray();
        var succeeded = stepResults.All(step => step.Succeeded);
        return new AiSafeChangeVerificationResult(
            succeeded,
            succeeded
                ? "Verified the package state restored as far as Linux Made Sane can automate."
                : "Rollback verification found package state mismatches.",
            stepResults,
            DateTimeOffset.UtcNow);
    }

    private async Task RollbackFileWriteAsync(
        ManagedHost host,
        AiSafeChangeSnapshot snapshot,
        List<string> restoredItems,
        List<string> remainingItems,
        StringBuilder builder,
        CancellationToken cancellationToken)
    {
        var file = snapshot.File ?? throw new InvalidOperationException("The snapshot does not contain file rollback data.");
        if (!file.Existed)
        {
            await DeleteFileAsync(host, file.OriginalPath, cancellationToken);
            restoredItems.Add($"Removed {file.OriginalPath} because it did not exist before the change.");
            builder.AppendLine($"Removed {file.OriginalPath}.");
            return;
        }

        var backupContent = await ReadFileContentsAsync(host, file.BackupPath, SnapshotPreviewBytes, cancellationToken);
        await WriteFileContentsAsync(host, file.OriginalPath, backupContent.Content, true, cancellationToken);
        restoredItems.Add($"Restored {file.OriginalPath} from {file.BackupPath}.");
        builder.AppendLine($"Restored {file.OriginalPath} from {file.BackupPath}.");
    }

    private async Task RollbackServiceRestartAsync(
        ManagedHost host,
        AiSafeChangeSnapshot snapshot,
        List<string> restoredItems,
        List<string> remainingItems,
        StringBuilder builder,
        CancellationToken cancellationToken)
    {
        var service = snapshot.Service ?? throw new InvalidOperationException("The snapshot does not contain service rollback data.");
        var command = service.WasActive
            ? $"sudo systemctl start {QuoteShellArgument(service.ServiceName)}"
            : $"sudo systemctl stop {QuoteShellArgument(service.ServiceName)}";
        var result = await ExecuteHostCommandAsync(host, WrapShellScript(command), cancellationToken);
        if (result.IsSuccess)
        {
            restoredItems.Add($"{service.ServiceName} active state restored to {service.WasActive}.");
            builder.AppendLine(result.CommandText);
            builder.AppendLine(BuildCommandOutput(result));
            return;
        }

        remainingItems.Add($"Linux Made Sane could not restore the active state for {service.ServiceName}: {result.StandardError}".Trim());
    }

    private async Task RollbackPackageInstallAsync(
        ManagedHost host,
        AiSafeChangeSnapshot snapshot,
        List<string> restoredItems,
        List<string> remainingItems,
        StringBuilder builder,
        CancellationToken cancellationToken)
    {
        var packageSnapshot = snapshot.Package ?? throw new InvalidOperationException("The snapshot does not contain package rollback data.");
        var removablePackages = packageSnapshot.Packages.Where(package => !package.WasInstalled).Select(package => package.PackageName).ToArray();
        if (removablePackages.Length > 0)
        {
            var command = WrapShellScript($"sudo apt-get remove -y -- {string.Join(' ', removablePackages.Select(QuoteShellArgument))}");
            var result = await ExecuteHostCommandAsync(host, command, cancellationToken);
            if (result.IsSuccess)
            {
                restoredItems.Add($"Removed newly installed package(s): {string.Join(", ", removablePackages)}.");
                builder.AppendLine(BuildCommandOutput(result));
            }
            else
            {
                remainingItems.Add($"Linux Made Sane could not remove one or more packages: {result.StandardError}".Trim());
            }
        }

        foreach (var package in packageSnapshot.Packages.Where(package => package.WasInstalled))
        {
            remainingItems.Add($"Package {package.PackageName} was already installed before the change. Restore version {package.Version} manually if apt changed it.");
        }
    }

    private async Task PersistUpdatedActionAsync(AiProposedAction action, CancellationToken cancellationToken)
    {
        var plan = await conversationStore.GetExecutionPlanAsync(action.ExecutionPlanId, cancellationToken)
            ?? throw new InvalidOperationException("The execution plan for this safe change could not be found.");
        var updatedPlan = plan with
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Actions = plan.Actions
                .Select(existing => existing.Id == action.Id ? action : existing)
                .ToArray()
        };
        await conversationStore.SaveExecutionPlanAsync(updatedPlan, cancellationToken);
    }

    private async Task<AiProposedAction> FindActionAsync(Guid threadId, Guid actionId, CancellationToken cancellationToken)
    {
        var plans = await conversationStore.ListExecutionPlansAsync(threadId, cancellationToken);
        return plans
            .SelectMany(plan => plan.Actions)
            .FirstOrDefault(action => action.Id == actionId)
            ?? throw new InvalidOperationException("The requested safe change action could not be found.");
    }

    private async Task<ManagedHost> ResolveHostForActionAsync(Guid threadId, AiProposedAction action, CancellationToken cancellationToken)
    {
        return action.ToolName switch
        {
            AiToolNames.RestartService => await ResolveHostAsync(threadId, DeserializeRequest<RestartServiceToolRequest>(action.ToolArgumentsJson).ServerId, cancellationToken),
            AiToolNames.WriteFileWithConfirmation => await ResolveHostAsync(threadId, DeserializeRequest<WriteFileWithConfirmationToolRequest>(action.ToolArgumentsJson).ServerId, cancellationToken),
            AiToolNames.InstallPackageWithConfirmation => await ResolveHostAsync(threadId, DeserializeRequest<InstallPackageWithConfirmationToolRequest>(action.ToolArgumentsJson).ServerId, cancellationToken),
            AiToolNames.RunCommand => await ResolveHostAsync(threadId, DeserializeRequest<RunCommandToolRequest>(action.ToolArgumentsJson).ServerId, cancellationToken),
            AiToolNames.RollbackSafeChange => await ResolveHostForActionAsync(threadId, await FindActionAsync(threadId, DeserializeRequest<RollbackSafeChangeToolRequest>(action.ToolArgumentsJson).OriginalActionId, cancellationToken), cancellationToken),
            _ => throw new InvalidOperationException("Linux Made Sane cannot resolve a target host for this action.")
        };
    }

    private async Task<ManagedHost> ResolveHostAsync(Guid threadId, Guid serverId, CancellationToken cancellationToken)
    {
        var attachedServers = AiLocalMachine.GetEffectiveAttachedServers(
            threadId,
            await conversationStore.ListAttachedServersAsync(threadId, cancellationToken));

        if (AiLocalMachine.IsLocalMachine(serverId))
        {
            if (attachedServers.All(server => server.ManagedHostId != serverId))
            {
                throw new InvalidOperationException("The local Linux Made Sane host is not attached to this chat.");
            }

            return AiLocalMachine.CreateManagedHost();
        }

        var host = await hostStore.GetAsync(serverId, cancellationToken)
            ?? throw new InvalidOperationException("The target host could not be found.");
        if (attachedServers.Count > 0 && attachedServers.All(server => server.ManagedHostId != serverId))
        {
            throw new InvalidOperationException($"Server {host.Name} is not attached to this chat.");
        }

        return host;
    }

    private async Task<(bool Exists, long SizeBytes)> TryReadFileMetadataAsync(ManagedHost host, string path, CancellationToken cancellationToken)
    {
        if (AiLocalMachine.IsLocalMachine(host.Id))
        {
            var normalizedPath = LocalFileBrowsingSupport.NormalizePath(host.DefaultWorkingDirectory, path);
            var fileInfo = new FileInfo(normalizedPath);
            return fileInfo.Exists
                ? (true, fileInfo.Length)
                : (false, 0);
        }

        var script = WrapShellScript($"""
            if [ -f {QuoteShellArgument(path)} ]; then
              printf 'exists\t1\n'
              printf 'size\t'
              stat -Lc '%s' -- {QuoteShellArgument(path)}
            else
              printf 'exists\t0\n'
              printf 'size\t0\n'
            fi
            """);
        var result = await ExecuteHostCommandAsync(host, script, cancellationToken);
        var exists = false;
        long sizeBytes = 0;
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[0].Equals("exists", StringComparison.OrdinalIgnoreCase))
            {
                exists = parts[1] == "1";
            }
            else if (parts[0].Equals("size", StringComparison.OrdinalIgnoreCase))
            {
                _ = long.TryParse(parts[1], out sizeBytes);
            }
        }

        return (exists, sizeBytes);
    }

    private async Task<IReadOnlyList<AiSafeChangePackageState>> ReadPackageStatesAsync(
        ManagedHost host,
        IReadOnlyList<string> packageNames,
        CancellationToken cancellationToken)
    {
        var packageList = string.Join(' ', packageNames.Select(QuoteShellArgument));
        var command = WrapShellScript(
            $"for pkg in {packageList}; do " +
            "if dpkg-query -W -f='${db:Status-Abbrev}\\t${Version}\\n' -- \"$pkg\" >/tmp/lms-pkg.$$ 2>/dev/null; then " +
            "data=$(cat /tmp/lms-pkg.$$); " +
            "rm -f /tmp/lms-pkg.$$; " +
            "printf '%s\\t1\\t%s\\n' \"$pkg\" \"$data\"; " +
            "else " +
            "rm -f /tmp/lms-pkg.$$; " +
            "printf '%s\\t0\\t\\n' \"$pkg\"; " +
            "fi; " +
            "done");
        var result = await ExecuteHostCommandAsync(host, command, cancellationToken);
        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var parts = line.Split('\t');
                var wasInstalled = parts.Length > 1 && parts[1] == "1";
                var version = wasInstalled && parts.Length > 3 ? parts[3] : string.Empty;
                return new AiSafeChangePackageState(parts[0], wasInstalled, version);
            })
            .ToArray();
    }

    private async Task<bool> ReadServiceActiveStateAsync(ManagedHost host, string serviceName, CancellationToken cancellationToken)
    {
        var result = await ExecuteHostCommandAsync(
            host,
            WrapShellScript($"systemctl is-active {QuoteShellArgument(serviceName)} >/dev/null 2>&1"),
            cancellationToken);
        return result.IsSuccess;
    }

    private async Task<string> ReadServiceEnabledStateAsync(ManagedHost host, string serviceName, CancellationToken cancellationToken)
    {
        var result = await ExecuteHostCommandAsync(
            host,
            WrapShellScript($"systemctl is-enabled {QuoteShellArgument(serviceName)} 2>/dev/null || true"),
            cancellationToken);
        var enabledState = result.StandardOutput.Trim();
        return string.IsNullOrWhiteSpace(enabledState) ? "unknown" : enabledState;
    }

    private async Task<SftpFileContent> ReadFileContentsAsync(
        ManagedHost host,
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (!AiLocalMachine.IsLocalMachine(host.Id))
        {
            return await fileAccessService.ReadFileAsync(
                host,
                path,
                CreateStoredConnectionProfile(host),
                maxBytes,
                cancellationToken);
        }

        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(host.DefaultWorkingDirectory, path);
        if (Directory.Exists(normalizedPath))
        {
            throw new InvalidOperationException("The requested snapshot path is a directory.");
        }

        var fileInfo = new FileInfo(normalizedPath);
        if (!fileInfo.Exists)
        {
            throw new InvalidOperationException($"File {normalizedPath} does not exist.");
        }

        var safeMaxBytes = Math.Clamp(maxBytes, 1, 1_048_576);
        await using var stream = fileInfo.OpenRead();
        var buffer = new byte[safeMaxBytes];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        return new SftpFileContent(
            normalizedPath,
            Encoding.UTF8.GetString(buffer, 0, bytesRead),
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero),
            fileInfo.Length > bytesRead);
    }

    private async Task WriteBackupFileAsync(ManagedHost host, string path, string content, CancellationToken cancellationToken) =>
        await WriteFileContentsAsync(host, path, content, true, cancellationToken);

    private async Task WriteFileContentsAsync(
        ManagedHost host,
        string path,
        string content,
        bool createDirectories,
        CancellationToken cancellationToken)
    {
        if (!AiLocalMachine.IsLocalMachine(host.Id))
        {
            await fileAccessService.WriteFileAsync(
                host,
                path,
                content,
                CreateStoredConnectionProfile(host),
                createDirectories,
                cancellationToken);
            return;
        }

        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(host.DefaultWorkingDirectory, path);
        var directory = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            if (createDirectories)
            {
                Directory.CreateDirectory(directory);
            }
            else if (!Directory.Exists(directory))
            {
                throw new InvalidOperationException($"Directory {directory} does not exist.");
            }
        }

        await File.WriteAllTextAsync(normalizedPath, content, cancellationToken);
    }

    private async Task DeleteFileAsync(ManagedHost host, string path, CancellationToken cancellationToken)
    {
        if (!AiLocalMachine.IsLocalMachine(host.Id))
        {
            await fileAccessService.DeleteAsync(
                host,
                path,
                CreateStoredConnectionProfile(host),
                false,
                cancellationToken);
            return;
        }

        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(host.DefaultWorkingDirectory, path);
        if (File.Exists(normalizedPath))
        {
            File.Delete(normalizedPath);
        }
    }

    private async Task<CommandExecutionResult> ExecuteHostCommandAsync(
        ManagedHost host,
        string commandText,
        CancellationToken cancellationToken)
    {
        return await commandExecutionService.ExecuteAsync(host, commandText, cancellationToken: cancellationToken);
    }

    private static ManagedHostConnectionProfile CreateStoredConnectionProfile(ManagedHost host) =>
        new(host.Username, null, PreferStoredCredentials: true);

    private static string[] NormalizePackageNames(IReadOnlyList<string> packageNames) =>
        packageNames
            .Select(name => name.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static TRequest DeserializeRequest<TRequest>(string json)
        where TRequest : IAiToolRequest
    {
        var request = JsonSerializer.Deserialize<TRequest>(string.IsNullOrWhiteSpace(json) ? "{}" : json, SerializerOptions);
        return request ?? throw new InvalidOperationException($"Linux Made Sane could not deserialize {typeof(TRequest).Name}.");
    }

    private static string BuildBackupPath(ManagedHost host, Guid snapshotId, string originalPath)
    {
        var safeHost = host.Name.Replace(' ', '-').Replace('/', '-');
        var fileName = Path.GetFileName(originalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "snapshot";
        }

        return $"{BackupRootPath}/{safeHost}/{snapshotId:N}/{fileName}.bak";
    }

    private static string BuildChangeSummary(
        AiProposedAction action,
        AiToolExecutionResult result,
        AiSafeChangeVerificationResult verification)
    {
        if (result.PersistedResult.Outcome != AiExecutionOutcome.Succeeded)
        {
            return $"{action.Title} failed before Linux Made Sane could verify the requested change.";
        }

        return verification.Succeeded
            ? $"{action.Title} completed and Linux Made Sane verified the expected result."
            : $"{action.Title} completed, but Linux Made Sane could not fully verify the expected result.";
    }

    private static IReadOnlyList<string> BuildRollbackVerificationSteps(AiProposedAction action) =>
        action.SafeChange?.OperationKind switch
        {
            AiSafeChangeOperationKind.FileWrite => ["Verify the original file contents are restored or the created file is removed."],
            AiSafeChangeOperationKind.ServiceRestart => ["Verify the service returns to its previous active state."],
            AiSafeChangeOperationKind.PackageInstall => ["Verify packages that were absent before are removed again."],
            _ => ["Verify the targeted state is restored."]
        };

    private static string BuildCommandOutput(CommandExecutionResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.CommandText);
        builder.AppendLine($"Exit code: {result.ExitCode}");

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            builder.AppendLine();
            builder.AppendLine(result.StandardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            builder.AppendLine();
            builder.AppendLine("stderr:");
            builder.AppendLine(result.StandardError.TrimEnd());
        }

        return builder.ToString().TrimEnd();
    }

    private static AiToolExecutionResult CreateFailureExecutionResult(
        AiProposedAction action,
        AiToolInvocation invocation,
        string errorMessage)
    {
        var response = new SafeChangeFailureToolResponse(errorMessage);
        return new AiToolExecutionResult(
            new AiToolDefinition(
                action.ToolName,
                action.Description,
                typeof(IAiToolRequest),
                typeof(SafeChangeFailureToolResponse),
                new AiToolApprovalMetadata(
                    action.RiskLevel,
                    action.ApprovalRequirement,
                    !string.IsNullOrWhiteSpace(action.CommandPreview),
                    action.RequiresApproval,
                    false),
                "IAiSafeChangeService",
                false),
            response,
            new AiToolResult(
                Guid.NewGuid(),
                invocation.Id,
                AiExecutionOutcome.Failed,
                $"{action.ToolName} failed before Linux Made Sane could produce a structured response.",
                string.Empty,
                errorMessage,
                JsonSerializer.Serialize(response, SerializerOptions),
                null,
                DateTimeOffset.UtcNow));
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static string QuoteShellArgument(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private static string WrapShellScript(string script) =>
        $"/bin/sh -lc {QuoteShellArgument(script.Trim())}";
}
