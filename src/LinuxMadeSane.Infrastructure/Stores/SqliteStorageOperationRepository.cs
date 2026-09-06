// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using LinuxMadeSane.Application.Contracts.Storage;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteStorageOperationRepository(LinuxMadeSaneDbContext dbContext)
    : IStorageOperationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SaveAsync(StorageResizeOperation operation, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.StorageResizeOperations
            .SingleOrDefaultAsync(item => item.Id == operation.Id, cancellationToken);
        if (entity is null)
        {
            dbContext.StorageResizeOperations.Add(Map(operation));
        }
        else
        {
            Copy(operation, entity);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<StorageResizeOperation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.StorageResizeOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<StorageResizeOperation>> ListAsync(
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        (await dbContext.StorageResizeOperations
            .AsNoTracking()
            .ToListAsync(cancellationToken))
        .OrderByDescending(item => item.CreatedUtc)
        .Take(Math.Clamp(limit, 1, 500))
        .Select(Map)
        .ToArray();

    public Task<bool> HasActiveOperationForDiskAsync(
        string diskDevicePath,
        CancellationToken cancellationToken = default)
    {
        var activeStates = new[]
        {
            (int)StorageOperationState.Queued,
            (int)StorageOperationState.Running,
            (int)StorageOperationState.Verifying
        };
        return dbContext.StorageResizeOperations.AsNoTracking().AnyAsync(
            item => item.DiskDevicePath == diskDevicePath && activeStates.Contains(item.State),
            cancellationToken);
    }

    private static StorageResizeOperationEntity Map(StorageResizeOperation model)
    {
        var entity = new StorageResizeOperationEntity { Id = model.Id };
        Copy(model, entity);
        return entity;
    }

    private static void Copy(StorageResizeOperation model, StorageResizeOperationEntity entity)
    {
        entity.State = (int)model.State;
        entity.DiskDevicePath = model.Plan.TargetDiskDevicePath;
        entity.MountPoint = model.Plan.TargetMountPoint;
        entity.OperationType = model.Plan.OperationType.ToString();
        entity.BeforeSizeBytes = model.Plan.CurrentSizeBytes;
        entity.AfterSizeBytes = model.Plan.RequestedSizeBytes;
        entity.RequestedBy = model.RequestedBy;
        entity.Hostname = model.Hostname;
        entity.BackupAcknowledged = model.BackupAcknowledged;
        entity.PlanJson = JsonSerializer.Serialize(model.Plan, JsonOptions);
        entity.BeforeTopologyJson = model.BeforeTopology is null ? null : JsonSerializer.Serialize(model.BeforeTopology, JsonOptions);
        entity.AfterTopologyJson = model.AfterTopology is null ? null : JsonSerializer.Serialize(model.AfterTopology, JsonOptions);
        entity.FailureDetail = model.FailureDetail;
        entity.CreatedUtc = model.CreatedUtc;
        entity.UpdatedUtc = model.UpdatedUtc;
        entity.StartedUtc = model.StartedUtc;
        entity.CompletedUtc = model.CompletedUtc;
    }

    private static StorageResizeOperation Map(StorageResizeOperationEntity entity)
    {
        var plan = JsonSerializer.Deserialize<StorageResizePlan>(entity.PlanJson, JsonOptions)
            ?? throw new InvalidOperationException($"Storage operation {entity.Id} has an unreadable plan.");
        var before = string.IsNullOrWhiteSpace(entity.BeforeTopologyJson)
            ? null
            : JsonSerializer.Deserialize<StorageTopologySnapshot>(entity.BeforeTopologyJson, JsonOptions);
        var after = string.IsNullOrWhiteSpace(entity.AfterTopologyJson)
            ? null
            : JsonSerializer.Deserialize<StorageTopologySnapshot>(entity.AfterTopologyJson, JsonOptions);
        return new StorageResizeOperation(
            entity.Id,
            plan,
            (StorageOperationState)entity.State,
            entity.RequestedBy,
            entity.Hostname,
            entity.BackupAcknowledged,
            entity.CreatedUtc,
            entity.UpdatedUtc,
            entity.StartedUtc,
            entity.CompletedUtc,
            entity.FailureDetail,
            before,
            after);
    }
}
