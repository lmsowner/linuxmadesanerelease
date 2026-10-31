// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Models.Vulnerabilities;
namespace LinuxMadeSane.Application.Vulnerabilities;

public static partial class TrivyInventory
{
    public const int MaximumPackages = 50000;
    public const int MaximumBytes = 8*1024*1024;
    [GeneratedRegex("^[A-Za-z0-9.+:~_-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex Field();
    public static void Validate(OsPackageInventory inventory)
    {
        if (inventory is null || inventory.Packages is null)throw new ArgumentException("An OS package inventory is required.");
        if (inventory.Distribution is not ("ubuntu" or "debian") || !Field().IsMatch(inventory.Release))
            throw new ArgumentException("OS package scanning currently supports the existing Debian/Ubuntu APT hosts.");
        if(inventory.CapturedAtUtc>DateTimeOffset.UtcNow.AddMinutes(5) || inventory.CapturedAtUtc<DateTimeOffset.UtcNow.AddDays(-1))
            throw new ArgumentException("Submit a fresh package inventory.");
        if(inventory.Packages.Count is 0 or >MaximumPackages) throw new ArgumentException("Invalid package inventory size.");
        var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in inventory.Packages)
        {
            if(p is null || new[]{p.Name,p.Version,p.Architecture,p.SourceName,p.SourceVersion}.Any(v=>v is null||!Field().IsMatch(v)) || !keys.Add(p.Name+"\n"+p.Architecture))
                throw new ArgumentException("Invalid or duplicate package metadata.");
        }
        if(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(inventory).Length>MaximumBytes) throw new ArgumentException("Package inventory exceeds 8 MiB.");
    }
    // Only package-manager metadata is materialised. No host paths or user files are accepted.
    public static async Task MaterialiseAsync(OsPackageInventory inventory,string root,CancellationToken token)
    {
        Validate(inventory);
        Directory.CreateDirectory(Path.Combine(root,"etc"));Directory.CreateDirectory(Path.Combine(root,"var/lib/dpkg"));
        await File.WriteAllTextAsync(Path.Combine(root,"etc/os-release"),$"ID={inventory.Distribution}\nVERSION_ID={inventory.Release}\nNAME={inventory.Distribution}\n",token);
        if(inventory.Distribution=="ubuntu")
            await File.WriteAllTextAsync(Path.Combine(root,"etc/lsb-release"),$"DISTRIB_ID=Ubuntu\nDISTRIB_RELEASE={inventory.Release}\n",token);
        else
            await File.WriteAllTextAsync(Path.Combine(root,"etc/debian_version"),inventory.Release+"\n",token);
        var status=new StringBuilder();
        foreach(var p in inventory.Packages.OrderBy(p=>p.Name,StringComparer.Ordinal).ThenBy(p=>p.Architecture,StringComparer.Ordinal))
            status.Append($"Package: {p.Name}\nStatus: install ok installed\nArchitecture: {p.Architecture}\nVersion: {p.Version}\nSource: {p.SourceName} ({p.SourceVersion})\n\n");
        await File.WriteAllTextAsync(Path.Combine(root,"var/lib/dpkg/status"),status.ToString(),token);
    }
}
