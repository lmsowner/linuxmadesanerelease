// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Vulnerabilities;
using LinuxMadeSane.Infrastructure.Services.Infrastructure;
namespace LinuxMadeSane.Infrastructure.Services.Vulnerabilities;
public sealed class OsPackageInventorySource(IPackageManagementService packages,InfrastructureHostPaths paths):IOsPackageInventorySource
{
    public async Task<OsPackageInventory> CollectAsync(CancellationToken token=default)
    {
        var path=Path.Combine(paths.HostConfigurationDirectory,"os-release");
        if(new FileInfo(path).Length>65536)throw new InvalidDataException("Invalid OS release metadata.");
        var lines=await File.ReadAllLinesAsync(path,token);
        string Value(string key)=>lines.FirstOrDefault(l=>l.StartsWith(key+"=",StringComparison.Ordinal))?[(key.Length+1)..].Trim((char)34,(char)39)??"";
        var distribution=Value("ID");var release=Value("VERSION_ID");
        // Derivatives must declare their actual base release. Never treat Mint's
        // own version number as an Ubuntu release or guess from a codename.
        if(distribution is not ("ubuntu" or "debian") && Value("ID_LIKE").Split(' ',StringSplitOptions.RemoveEmptyEntries).Contains("ubuntu",StringComparer.Ordinal))
        {
            var upstream=Path.Combine(paths.HostConfigurationDirectory,"upstream-release/lsb-release");
            if(!File.Exists(upstream)||new FileInfo(upstream).Length>65536)throw new InvalidDataException("This Ubuntu-based system must report its base release in /etc/upstream-release/lsb-release before its packages can be assessed.");
            var metadata=await File.ReadAllLinesAsync(upstream,token);
            string Base(string key)=>metadata.FirstOrDefault(l=>l.StartsWith(key+"=",StringComparison.Ordinal))?[(key.Length+1)..].Trim((char)34,(char)39)??"";
            if(Base("DISTRIB_ID")!="Ubuntu")throw new InvalidDataException("The declared upstream distribution is not supported for this Ubuntu-based package inventory.");
            distribution="ubuntu";release=Base("DISTRIB_RELEASE");
        }
        var inventory=new OsPackageInventory(distribution,release,DateTimeOffset.UtcNow,await packages.ListInstalledAsync(token));
        LinuxMadeSane.Application.Vulnerabilities.TrivyInventory.Validate(inventory);return inventory;
    }
}
