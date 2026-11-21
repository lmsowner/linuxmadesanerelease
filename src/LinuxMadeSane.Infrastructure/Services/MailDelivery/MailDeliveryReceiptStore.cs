// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Services;

// Metadata only: Postfix remains the sole message queue. A persisted claim is never
// automatically released after a crash because the provider may already have accepted it.
public sealed class MailDeliveryReceiptStore(LinuxMadeSaneDbContext database) : IMailDeliveryReceipts
{
    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS mail_delivery_receipts (
          Key TEXT NOT NULL PRIMARY KEY, QueueId TEXT NOT NULL,
          ReceiptJson TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_mail_delivery_receipts_QueueId ON mail_delivery_receipts(QueueId);
        """;
    private SqliteConnection Connection() => new(database.Database.GetConnectionString());
    public async Task<(bool Claimed, MailDeliveryReceipt Receipt)> ClaimAsync(string key, string queueId, CancellationToken token, string[]? envelopeKeys = null)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = new MailDeliveryReceipt(key, queueId, MailDeliveryOutcome.Indeterminate,
            "Submission started but acceptance is not confirmed. Check provider tracing before authorising a retry.", null, now, null) { EnvelopeKeys = envelopeKeys ?? [] };
        using var connection = Connection(); await connection.OpenAsync(token);
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO mail_delivery_receipts(Key,QueueId,ReceiptJson,UpdatedUtc) VALUES($key,$queue,$json,$at)";
        Bind(insert, pending); var inserted = await insert.ExecuteNonQueryAsync(token);
        if (inserted == 1)
        {
            var overlaps = await HasUncertainOverlapAsync(connection, transaction, pending, token);
            if (overlaps) pending = pending with { Detail = "The native queue regrouped recipients from an earlier submission. Acceptance is unconfirmed for this batch; check provider tracing before authorising a retry." };
            using var persist = connection.CreateCommand(); persist.Transaction = transaction;
            persist.CommandText = "UPDATE mail_delivery_receipts SET ReceiptJson=$json WHERE Key=$key"; Bind(persist, pending); await persist.ExecuteNonQueryAsync(token);
            await transaction.CommitAsync(token); return (!overlaps, pending);
        }
        using var select = connection.CreateCommand(); select.Transaction = transaction;
        select.CommandText = "SELECT ReceiptJson FROM mail_delivery_receipts WHERE Key=$key"; select.Parameters.AddWithValue("$key", key);
        var previous = JsonSerializer.Deserialize<MailDeliveryReceipt>((string)(await select.ExecuteScalarAsync(token))!)!;
        if (previous.Outcome is MailDeliveryOutcome.TemporaryFailure or MailDeliveryOutcome.AuthenticationFailure or MailDeliveryOutcome.ConfigurationFailure &&
            (previous.RetryAfterUtc is null || previous.RetryAfterUtc <= now))
        {
            var overlaps = !previous.ManualRetryApproved && await HasUncertainOverlapAsync(connection, transaction, pending, token);
            if (overlaps) pending = pending with { Detail = "The native queue regrouped recipients from an earlier submission. Check provider tracing before resolving this batch." };
            using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE mail_delivery_receipts SET ReceiptJson=$json,UpdatedUtc=$at WHERE Key=$key"; Bind(update, pending);
            await update.ExecuteNonQueryAsync(token); await transaction.CommitAsync(token); return (!overlaps, pending);
        }
        await transaction.CommitAsync(token); return (false, previous);
    }
    public async Task CompleteAsync(MailDeliveryReceipt receipt, MailDeliveryResult result, CancellationToken token)
    {
        var completed = receipt with { Outcome = result.Outcome, Detail = result.Detail, ProviderMessageId = result.ProviderMessageId,
            RetryAfterUtc = result.RetryAfterUtc, UpdatedUtc = DateTimeOffset.UtcNow };
        using var connection = Connection(); await connection.OpenAsync(token);
        using var command = connection.CreateCommand(); command.CommandText = "UPDATE mail_delivery_receipts SET ReceiptJson=$json,UpdatedUtc=$at WHERE Key=$key";
        Bind(command, completed); await command.ExecuteNonQueryAsync(token);
    }
    public async Task<IReadOnlyList<MailDeliveryReceipt>> ListAttentionAsync(CancellationToken token = default)
    {
        using var connection = Connection(); await connection.OpenAsync(token);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT ReceiptJson FROM mail_delivery_receipts WHERE json_extract(ReceiptJson, '$.Outcome') NOT IN (0,2) ORDER BY UpdatedUtc DESC LIMIT 200";
        using var reader = await command.ExecuteReaderAsync(token); var result = new List<MailDeliveryReceipt>();
        while (await reader.ReadAsync(token))
        {
            var receipt = JsonSerializer.Deserialize<MailDeliveryReceipt>(reader.GetString(0))!;
            if (receipt.Outcome != MailDeliveryOutcome.Accepted) result.Add(receipt);
        }
        return result;
    }
    // Explicit operator decision, shown in the main relay screen, never hidden in history.
    public async Task ResolveAsync(string key, bool providerAccepted, CancellationToken token = default)
    {
        using var connection = Connection(); await connection.OpenAsync(token);
        using var transaction = connection.BeginTransaction();
        using var select = connection.CreateCommand(); select.Transaction = transaction;
        select.CommandText = "SELECT ReceiptJson FROM mail_delivery_receipts WHERE Key=$key"; select.Parameters.AddWithValue("$key", key);
        if (await select.ExecuteScalarAsync(token) is not string json) throw new InvalidOperationException("This delivery attempt no longer exists.");
        var receipt = JsonSerializer.Deserialize<MailDeliveryReceipt>(json)!;
        if (receipt.Outcome != MailDeliveryOutcome.Indeterminate || receipt.UpdatedUtc > DateTimeOffset.UtcNow.AddMinutes(-5))
            throw new InvalidOperationException("Wait five minutes for any active submission to finish, then refresh before resolving this attempt.");
        var resolved = receipt with { Outcome = providerAccepted ? MailDeliveryOutcome.Accepted : MailDeliveryOutcome.TemporaryFailure,
            Detail = providerAccepted ? "Administrator confirmed provider acceptance." : "Administrator explicitly authorised another attempt after checking provider tracing.",
            UpdatedUtc = DateTimeOffset.UtcNow, RetryAfterUtc = null, ManualRetryApproved = !providerAccepted };
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE mail_delivery_receipts SET ReceiptJson=$json,UpdatedUtc=$at WHERE Key=$key"; Bind(update, resolved);
        await update.ExecuteNonQueryAsync(token); await transaction.CommitAsync(token);
    }
    private static async Task<bool> HasUncertainOverlapAsync(SqliteConnection connection, SqliteTransaction transaction, MailDeliveryReceipt pending, CancellationToken token)
    {
        if (pending.EnvelopeKeys.Length == 0) return false;
        using var select = connection.CreateCommand(); select.Transaction = transaction;
        select.CommandText = "SELECT ReceiptJson FROM mail_delivery_receipts WHERE QueueId=$queue AND Key<>$key";
        select.Parameters.AddWithValue("$queue", pending.QueueId); select.Parameters.AddWithValue("$key", pending.Key);
        using var reader = await select.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var previous = JsonSerializer.Deserialize<MailDeliveryReceipt>(reader.GetString(0))!;
            if (previous.Outcome is MailDeliveryOutcome.TemporaryFailure or MailDeliveryOutcome.AuthenticationFailure or MailDeliveryOutcome.ConfigurationFailure) continue;
            if (previous.EnvelopeKeys.Intersect(pending.EnvelopeKeys, StringComparer.Ordinal).Any()) return true;
        }
        return false;
    }
    private static void Bind(SqliteCommand command, MailDeliveryReceipt receipt)
    {
        command.Parameters.AddWithValue("$key", receipt.Key); command.Parameters.AddWithValue("$queue", receipt.QueueId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt)); command.Parameters.AddWithValue("$at", receipt.UpdatedUtc.ToString("O"));
    }
}
