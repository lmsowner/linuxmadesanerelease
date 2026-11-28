// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web.Services;

// Owned by one fleet update run, never by the user account or global terminal state.
public sealed class FleetPackageApprovalScope(IEnumerable<Guid> hostIds)
{
    private readonly HashSet<Guid> hosts=hostIds.ToHashSet();
    private readonly object sync=new();
    private bool approved,ended;
    public int HostCount=>hosts.Count;
    public bool IsApprovedFor(Guid hostId) { lock(sync) return !ended && approved && hosts.Contains(hostId); }
    public bool Approve(Guid hostId)
    {
        lock(sync)
        {
            if(ended || !hosts.Contains(hostId)) return false;
            approved=true;return true;
        }
    }
    public void Revoke() { lock(sync) approved=false; }
    public void End() { lock(sync) { ended=true;approved=false; } }
}
