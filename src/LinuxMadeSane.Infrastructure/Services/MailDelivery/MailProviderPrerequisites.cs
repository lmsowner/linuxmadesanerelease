// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class MailProviderPrerequisites
{
    internal const string ManagedPowerShell = "/opt/linuxmadesane/tools/powershell/7.6.9/pwsh";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const string VersionCheck = "if ($PSVersionTable.PSVersion -ge [version]'7.6' -and -not $PSVersionTable.PSVersion.PreReleaseLabel) { exit 0 }; exit 78";
    internal static async Task<string> EnsurePowerShellAsync(IStreamingLinuxCommandRunner runner, Action<MicrosoftMailRegistrationProgress> progress, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            async Task<bool> Ready(string executable) => (await runner.RunStreamingAsync(new(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", VersionCheck], false, TimeSpan.FromSeconds(30), "Check Microsoft registration runtime")
                { IsOptionalExternalTool = true }, false, _ => { }, token)).ExitCode == 0;
            if (await Ready("pwsh")) return "pwsh";
            if (await Ready(ManagedPowerShell)) return ManagedPowerShell;
            progress(new("Preparing Microsoft sign-in tools automatically…"));
            var result = await runner.RunStreamingAsync(new("bash", ["-ceu", InstallPowerShell], true, TimeSpan.FromMinutes(15), "Prepare Microsoft registration runtime"), false, _ => { }, CancellationToken.None);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || !await Ready(ManagedPowerShell))
                throw new InvalidOperationException("LMS could not finish preparing Microsoft sign-in tools. Check LMS has elevated installation access and can reach the package repositories and GitHub, then retry here. Live mail has not changed.");
            return ManagedPowerShell;
        }
        finally { Gate.Release(); }
    }
    // Keep the managed runtime separate from any PowerShell used by other applications.
    // Archive checksums come from Microsoft's PowerShell v7.6.9 release assets.
    internal const string InstallPowerShell = """
        set -eu
        export DEBIAN_FRONTEND=noninteractive
        root=/opt/linuxmadesane/tools/powershell
        target=$root/7.6.9
        install -d -m 755 "$root"
        case "$(uname -m)" in
          x86_64) arch=x64; checksum=1da6d6d6035d651f20e70ddee7c4c15ac6ab8b9fb40ee92240d91263f28a884b ;;
          aarch64|arm64) arch=arm64; checksum=02eb9991af447c7e7fe881acde195961afd397c40b8eb1c2b3856b84ec2d85f2 ;;
          *) exit 65 ;;
        esac
        if command -v apt-get >/dev/null; then
          apt-get -o DPkg::Lock::Timeout=120 update -qq
          icu=$(apt-cache pkgnames | awk '/^libicu[0-9]+$/ {print}' | sort -V | tail -1)
          test -n "$icu"
          apt-get -o DPkg::Lock::Timeout=120 install -y --no-upgrade --no-install-recommends ca-certificates curl tar util-linux "$icu" libgssapi-krb5-2 zlib1g
        elif command -v dnf >/dev/null; then
          dnf install -y ca-certificates curl tar util-linux libicu krb5-libs zlib openssl-libs
        else
          exit 69
        fi
        exec 9>/run/lock/linuxmadesane-powershell.lock
        flock -w 600 9
        if [ -x "$target/pwsh" ] && "$target/pwsh" -NoLogo -NoProfile -NonInteractive -Command 'exit 0' >/dev/null 2>&1; then exit 0; fi
        stage=$(mktemp -d "$root/.prepare.XXXXXX")
        archive=$(mktemp "$root/.download.XXXXXX")
        trap 'rm -rf -- "$stage"; rm -f -- "$archive"' EXIT
        curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 --retry 3 --connect-timeout 30 --max-time 600 -o "$archive" "https://github.com/PowerShell/PowerShell/releases/download/v7.6.9/powershell-7.6.9-linux-$arch.tar.gz"
        printf '%s  %s\n' "$checksum" "$archive" | sha256sum -c -
        tar --no-same-owner --no-same-permissions -xzf "$archive" -C "$stage"
        chmod -R a+rX,go-w "$stage"
        chmod 755 "$stage" "$stage/pwsh"
        "$stage/pwsh" -NoLogo -NoProfile -NonInteractive -Command 'if ($PSVersionTable.PSVersion -lt [version]"7.6") { exit 78 }'
        if [ -e "$target" ]; then mv -- "$target" "$target.replaced.$(date +%s).$$"; fi
        mv -- "$stage" "$target"
        """;
}
