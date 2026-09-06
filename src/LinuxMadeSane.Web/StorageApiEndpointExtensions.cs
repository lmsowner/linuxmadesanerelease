// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Security.Claims;
using LinuxMadeSane.Application.Contracts.Storage;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web;

public static class StorageApiEndpointExtensions
{
    public static IEndpointRouteBuilder MapStorageApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/storage")
            .AddEndpointFilter(async (context, next) =>
            {
                var httpContext = context.HttpContext;
                return HasAdministratorAccess(httpContext)
                    ? await next(context)
                    : Results.Unauthorized();
            });

        group.MapGet("/topology", async (IStorageDiscoveryService storage, CancellationToken cancellationToken) =>
            Results.Ok(await storage.DiscoverAsync(cancellationToken)));

        group.MapGet("/filesystems", async (IStorageDiscoveryService storage, CancellationToken cancellationToken) =>
            Results.Ok((await storage.DiscoverAsync(cancellationToken)).FileSystems));

        group.MapPost("/rescan", async (IStorageDiscoveryService storage, CancellationToken cancellationToken) =>
            Results.Ok(await storage.RescanAsync(cancellationToken)));

        group.MapPost("/disks/attach/mount/plan", async (
            HttpContext context,
            StorageAttachMountRequest request,
            IStorageDiskAttachmentPlanner planner,
            IStorageOperationRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var plan = await planner.CreateMountPlanAsync(request, cancellationToken);
                var now = timeProvider.GetUtcNow();
                await repository.SaveAsync(new StorageResizeOperation(
                    plan.Id,
                    plan,
                    StorageOperationState.AwaitingConfirmation,
                    ResolveActor(context),
                    Environment.MachineName,
                    false,
                    now,
                    now,
                    null,
                    null,
                    null,
                    null,
                    null), cancellationToken);
                return Results.Ok(plan);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPost("/disks/attach/lvm/plan", async (
            HttpContext context,
            StorageAttachVolumeGroupRequest request,
            IStorageDiskAttachmentPlanner planner,
            IStorageOperationRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var plan = await planner.CreateVolumeGroupPlanAsync(request, cancellationToken);
                var now = timeProvider.GetUtcNow();
                await repository.SaveAsync(new StorageResizeOperation(
                    plan.Id,
                    plan,
                    StorageOperationState.AwaitingConfirmation,
                    ResolveActor(context),
                    Environment.MachineName,
                    false,
                    now,
                    now,
                    null,
                    null,
                    null,
                    null,
                    null), cancellationToken);
                return Results.Ok(plan);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPost("/resize/plan", async (
            HttpContext context,
            StorageResizePlanRequest request,
            IStorageResizePlanner planner,
            IStorageOperationRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var plan = await planner.CreatePlanAsync(request, cancellationToken);
                var now = timeProvider.GetUtcNow();
                await repository.SaveAsync(new StorageResizeOperation(
                    plan.Id,
                    plan,
                    StorageOperationState.AwaitingConfirmation,
                    ResolveActor(context),
                    Environment.MachineName,
                    false,
                    now,
                    now,
                    null,
                    null,
                    null,
                    null,
                    null), cancellationToken);
                return Results.Ok(plan);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPost("/resize/{planId:guid}/validate", async (
            Guid planId,
            IStorageResizePlanner planner,
            IStorageDiskAttachmentPlanner attachmentPlanner,
            IStorageOperationRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var operation = await repository.GetAsync(planId, cancellationToken);
            if (operation is null) return Results.NotFound();
            var validation = operation.Plan.OperationType is StorageResizeDirection.AttachMount or StorageResizeDirection.AttachToVolumeGroup
                ? await attachmentPlanner.ValidatePlanAsync(operation.Plan, cancellationToken)
                : await planner.ValidatePlanAsync(operation.Plan, cancellationToken);
            var hasErrors = validation.Any(result => !result.Passed && result.Severity == StorageValidationSeverity.Error);
            var updated = operation with
            {
                Plan = operation.Plan with { ValidationResults = validation },
                State = hasErrors ? StorageOperationState.AwaitingConfirmation : StorageOperationState.Validated,
                UpdatedUtc = timeProvider.GetUtcNow()
            };
            await repository.SaveAsync(updated, cancellationToken);
            return Results.Ok(updated.Plan);
        });

        group.MapPost("/resize/{planId:guid}/execute", async (
            HttpContext context,
            Guid planId,
            StorageExecuteApiRequest? request,
            IStorageResizeExecutor executor,
            IStorageOperationRepository repository,
            CancellationToken cancellationToken) =>
        {
            var operation = await repository.GetAsync(planId, cancellationToken);
            if (operation is null) return Results.NotFound();
            try
            {
                return Results.Ok(await executor.QueueAsync(
                    operation.Plan,
                    new StorageExecutionRequest(
                        operation.Plan.Id,
                        request?.BackupAcknowledged == true,
                        ResolveActor(context)),
                    cancellationToken));
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPost("/resize/{planId:guid}/cancel", async (
            Guid planId,
            IStorageResizeExecutor executor,
            CancellationToken cancellationToken) =>
            await executor.CancelAsync(planId, cancellationToken)
                ? Results.Ok()
                : Results.Conflict(new { error = "This operation has already started and cannot be cancelled safely." }));

        group.MapGet("/resize/{planId:guid}", async (
            Guid planId,
            IStorageOperationRepository repository,
            CancellationToken cancellationToken) =>
        {
            var operation = await repository.GetAsync(planId, cancellationToken);
            return operation is null ? Results.NotFound() : Results.Ok(operation);
        });

        group.MapGet("/history", async (IStorageOperationRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListAsync(100, cancellationToken)));

        return endpoints;
    }

    private static bool HasAdministratorAccess(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true ||
        context.Items.TryGetValue("LmsTrustedNetworkAccess", out var value) &&
        value is TrustedNetworkAccessResult { IsTrusted: true };

    private static string ResolveActor(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.Email) ??
        context.User.Identity?.Name ??
        "Local LMS administrator";

    public sealed record StorageExecuteApiRequest(bool BackupAcknowledged);
}
