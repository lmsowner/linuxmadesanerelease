// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed record FleetPackageResult(ManagedHost Host, string State, string Detail);

// Server-owned work survives navigation away from Hosts. Never cancel an APT
// transaction just because its initiating browser component went away.
public sealed class FleetPackageUpdateService(IServiceScopeFactory scopes)
{
    private readonly object sync = new();
    private FleetPackageResult[] results = [];
    private bool running;
    public event Action? Changed;
    public bool IsRunning { get { lock(sync) return running; } }
    public IReadOnlyList<FleetPackageResult> Results { get { lock(sync) return results.ToArray(); } }

    public static bool IsTarget(ManagedHost host) => host.Kind is ManagedHostKind.SshHost or ManagedHostKind.LmsHost;

    public bool Start(IEnumerable<ManagedHost> hosts, bool ai = false)
    {
        lock(sync)
        {
            if(running) return false;
            results = hosts.Where(IsTarget).DistinctBy(h=>h.Id).Select(h=>new FleetPackageResult(h,"Queued", "Waiting for the previous server.")).ToArray();
            if(results.Length==0) return false;
            running=true;
        }
        Notify();
        if(!ai) _ = RunAsync();
        return true;
    }

    public void ReportAi(ManagedHost host,string state,string detail) => Set(host,state,detail);
    public void FinishAi()
    {
        lock(sync)
        {
            results=results.Select(r=>r.State is "Queued" or "AI repair" ? r with { State="Needs attention", Detail="AI queue stopped. Review this server before retrying." } : r).ToArray();
            running=false;
        }
        Notify();
    }

    private async Task RunAsync()
    {
        try
        {
            foreach(var item in Results)
            {
                Set(item.Host,"Updating", "Refreshing repositories and installing package updates…");
                try
                {
                    using var scope=scopes.CreateScope();
                    var command=scope.ServiceProvider.GetRequiredService<ICommandExecutionService>();
                    var result=await command.ExecuteAsync(item.Host, UpdateCommand, cancellationToken: CancellationToken.None);
                    var detail=(result.StandardOutput+"\n"+result.StandardError).Trim();
                    Set(item.Host,result.IsSuccess ? "Completed" : "Needs attention",detail.Length>12000 ? detail[^12000..] : detail);
                }
                catch(Exception ex) { Set(item.Host,"Needs attention",ex.Message); }
            }
        }
        finally { lock(sync) running=false; Notify(); }
    }

    private void Set(ManagedHost host,string state,string detail)
    {
        lock(sync) results=results.Select(r=>r.Host.Id==host.Id ? new(host,state,detail) : r).ToArray();
        Notify();
    }
    private void Notify()
    {
        foreach(var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch(InvalidOperationException) { }
    }

    // Quote a fixed script, with no host/user supplied shell interpolation.
    public static string UpdateCommand => AsRoot(UpdateScript);
    public const string MetadataVerificationCommand = "sudo -n apt-get -o APT::Update::Error-Mode=any -o DPkg::Lock::Timeout=120 update";
    public static string VerificationCommand => "/bin/sh -c 'export LC_ALL=C; set -eu; " + AuditScript.Replace("'", "'\"'\"'") + "'";
    private static string AsRoot(string script) => "sudo -n /bin/sh -c '" + script.Replace("'", "'\"'\"'") + "'";

    private const string RefreshScript = """
set -eu
export LC_ALL=C DEBIAN_FRONTEND=noninteractive
command -v apt-get >/dev/null || { echo "This server does not use APT. Use its terminal for package management."; exit 1; }
log=$(mktemp)
trap 'rm -f "$log"' EXIT
refresh() {
  if apt-get -o DPkg::Lock::Timeout=120 -o Acquire::Retries=2 -o APT::Update::Error-Mode=any update >"$log" 2>&1; then
    cat "$log"
  else
    cat "$log"; return 1
  fi
  if grep -Eq '^(W:|E:|Err:)' "$log"; then echo "Repository warnings need repair before completion."; return 1; fi
}
""";
    private const string AuditScript = """
audit=$(dpkg --audit)
if [ -n "$audit" ]; then printf '%s\n' "$audit"; exit 1; fi
pending=$(apt list --upgradable 2>/dev/null)
printf '%s\n' "$pending"
if printf '%s\n' "$pending" | grep -q 'upgradable from:'; then echo "Packages remain pending; use AI repair to inspect held or dependency-blocked updates."; exit 1; fi
if [ -f /var/run/reboot-required ]; then echo "Reboot required; no reboot performed."; fi
echo "Package updates verified."
""";
    private const string UpdateScript = RefreshScript+"\nrefresh\napt-get -o DPkg::Lock::Timeout=120 -o Dpkg::Options::=--force-confold upgrade -y\nrefresh\n"+AuditScript;

    private const string RequestedCheckGuidance = """
If a question or security check is supplied, answer it explicitly for this connected server, not just with a generic update summary. For a CVE or named vulnerability, identify the installed distribution/release, relevant package and exact installed version, and compare against authoritative vendor security advisories or local package changelogs. Account for vendor backported fixes; an older upstream version alone is not proof of vulnerability. Do not invent a CVE identifier or assume what an ambiguous vulnerability name means. Ask for clarification if needed. Do not claim a CVE is patched merely because APT has no pending updates. If the required evidence is unavailable, report that the check is unverified and explain what is missing. Finish with the requested finding (confirmed fixed, not affected, needs attention, or unverified), supporting evidence, package changes and any remaining action for this server. Retain all existing command approval and administrator authentication requirements.
""";

    public static string BuildAiPrompt(ManagedHost host,string instruction,string previousResult) =>
        PackageUpdateAiPromptBuilder.Build(previousResult,[],[],true) + $"""

Fleet package update task for this connected server: {host.Name}.
Discover pending packages using fresh repository metadata, repair actionable APT errors or warnings, then install ALL available package updates. An empty package list above means no inventory was captured, not that the server is already up to date. Continue through a non-interactive upgrade, preserve local configuration, and verify no packages remain upgradable. Do not reboot automatically. Report whether a reboot is required. Do not weaken repository signature checks, remove packages to suppress errors, change SSH/sudo access, perform a distribution upgrade or update LMS itself. Use the connected server's actual repository configuration; do not assume a Cloudflare fault exists on every server.

Administrator's question or requested security check (apply on this server):
{instruction}

{(string.IsNullOrWhiteSpace(instruction) ? string.Empty : RequestedCheckGuidance)}
""";
}
