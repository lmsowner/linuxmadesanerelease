using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Application.Services.Cloudflare;

public static class CloudflareDnsConflictDetector
{
    public static CloudflareDnsConflictResult Detect(
        string hostname,
        string? expectedTarget,
        IReadOnlyList<CloudflareDnsRecord> existingRecords,
        string? managedRecordId = null,
        string? managedCommentMarker = null)
    {
        var record = existingRecords.FirstOrDefault(item =>
            item.Name.Equals(hostname, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            return new CloudflareDnsConflictResult(CloudflareDnsConflictKind.None, null, "No existing DNS record uses this hostname.");
        }

        if (!string.IsNullOrWhiteSpace(expectedTarget) &&
            record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) &&
            record.Content.Equals(expectedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return new CloudflareDnsConflictResult(CloudflareDnsConflictKind.Reuse, record, "The hostname already points at the selected tunnel.");
        }

        var isManagedRecord =
            (!string.IsNullOrWhiteSpace(managedRecordId) && record.Id.Equals(managedRecordId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(managedCommentMarker) &&
             record.Comment.Contains(managedCommentMarker, StringComparison.OrdinalIgnoreCase));

        if (isManagedRecord)
        {
            return new CloudflareDnsConflictResult(CloudflareDnsConflictKind.Update, record, "The hostname exists and is managed by Linux Made Sane, so it can be updated.");
        }

        return new CloudflareDnsConflictResult(
            CloudflareDnsConflictKind.Conflict,
            record,
            $"{record.Name} already exists as {record.Type} -> {record.Content}.");
    }
}

public sealed record CloudflareDnsConflictResult(
    CloudflareDnsConflictKind Kind,
    CloudflareDnsRecord? ExistingRecord,
    string Reason);
