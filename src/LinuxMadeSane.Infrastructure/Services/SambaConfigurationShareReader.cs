using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Infrastructure.Services;

internal sealed class SambaConfigurationShareReader
{
    private static readonly string[] DefaultConfigPaths =
    [
        "/etc/samba/smb.conf",
        "/usr/local/samba/lib/smb.conf"
    ];

    private readonly ILinuxCommandRunner commandRunner;
    private readonly IReadOnlyList<string> configPaths;

    public SambaConfigurationShareReader(
        ILinuxCommandRunner commandRunner,
        IReadOnlyList<string>? configPaths = null)
    {
        this.commandRunner = commandRunner;
        this.configPaths = configPaths ?? DefaultConfigPaths;
    }

    public async Task<IReadOnlyList<SambaShareDefinition>> ListSharesAsync(CancellationToken cancellationToken = default)
    {
        var configurationText = await ReadConfigurationTextAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(configurationText)
            ? Array.Empty<SambaShareDefinition>()
            : ParseShares(configurationText);
    }

    internal async Task<string> ReadConfigurationTextAsync(CancellationToken cancellationToken = default)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "testparm",
                ["-s"],
                RequiresSudo: false,
                Timeout: TimeSpan.FromSeconds(10),
                "Read Samba share configuration")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return result.StandardOutput;
        }

        return await ReadConfigurationFilesAsync(cancellationToken);
    }

    internal static IReadOnlyList<SambaShareDefinition> ParseShares(string configurationText)
    {
        var sections = ParseSections(configurationText);
        if (sections.Count == 0)
        {
            return Array.Empty<SambaShareDefinition>();
        }

        var sectionsByName = sections.ToDictionary(section => section.Name, StringComparer.OrdinalIgnoreCase);

        return sections
            .Where(section => !section.Name.Equals("global", StringComparison.OrdinalIgnoreCase))
            .Select(section => BuildShareDefinition(section, sectionsByName))
            .Where(share => share is not null)
            .Select(share => share!)
            .OrderBy(share => share.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<string> ReadConfigurationFilesAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var configPath in configPaths)
        {
            await AppendConfigurationPathAsync(configPath, null, builder, visitedPaths, cancellationToken);
        }

        return builder.ToString();
    }

    private async Task AppendConfigurationPathAsync(
        string pathOrPattern,
        string? baseDirectory,
        StringBuilder builder,
        HashSet<string> visitedPaths,
        CancellationToken cancellationToken)
    {
        foreach (var resolvedPath in ExpandConfigurationPaths(pathOrPattern, baseDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = Path.GetFullPath(resolvedPath);
            if (!File.Exists(fullPath) || !visitedPaths.Add(fullPath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(fullPath);
            var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);

            foreach (var line in lines)
            {
                if (TryParseKeyValue(line, out var key, out var value) &&
                    key.Equals("include", StringComparison.OrdinalIgnoreCase))
                {
                    await AppendConfigurationPathAsync(value, directory, builder, visitedPaths, cancellationToken);
                    continue;
                }

                builder.AppendLine(line);
            }

            builder.AppendLine();
        }
    }

    private static IReadOnlyList<string> ExpandConfigurationPaths(string pathOrPattern, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(pathOrPattern))
        {
            return Array.Empty<string>();
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(pathOrPattern.Trim());
        if (expandedPath.Contains('%'))
        {
            return Array.Empty<string>();
        }

        var resolvedPath = Path.IsPathRooted(expandedPath)
            ? expandedPath
            : string.IsNullOrWhiteSpace(baseDirectory)
                ? expandedPath
                : Path.Combine(baseDirectory, expandedPath);

        if (!resolvedPath.Contains('*') &&
            !resolvedPath.Contains('?'))
        {
            return [resolvedPath];
        }

        var directory = Path.GetDirectoryName(resolvedPath);
        var pattern = Path.GetFileName(resolvedPath);

        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(pattern) ||
            !Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static List<SambaSection> ParseSections(string configurationText)
    {
        var sections = new List<SambaSection>();
        SambaSection? currentSection = null;

        foreach (var rawLine in configurationText.Split('\n', StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                currentSection = new SambaSection(line[1..^1].Trim());
                sections.Add(currentSection);
                continue;
            }

            if (currentSection is null || !TryParseKeyValue(line, out var key, out var value))
            {
                continue;
            }

            currentSection.Options[key] = value;
        }

        return sections;
    }

    private static SambaShareDefinition? BuildShareDefinition(
        SambaSection section,
        IReadOnlyDictionary<string, SambaSection> sectionsByName)
    {
        var options = ResolveOptions(section, sectionsByName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (ParseBoolean(GetValue(options, "printable"), defaultValue: false))
        {
            return null;
        }

        var sharePath = GetValue(options, "path");
        if (string.IsNullOrWhiteSpace(sharePath))
        {
            return null;
        }

        var validEntries = ParsePrincipalList(GetValue(options, "valid users"));
        var (validUsers, validGroups) = SplitUsersAndGroups(validEntries);
        var writeList = ParsePrincipalList(GetValue(options, "write list"));
        var readList = ParsePrincipalList(GetValue(options, "read list"));
        var readOnly = ResolveReadOnly(options);
        var browseable = ResolveBrowseable(options);
        var guestAccess = ResolveGuestAccess(options);
        var createMask = GetMask(options, "create mask", "create mode", "0744");
        var directoryMask = GetMask(options, "directory mask", "directory mode", "0755");

        return new SambaShareDefinition(
            CreateStableId("share", $"{section.Name}|{sharePath}"),
            section.Name,
            sharePath,
            string.IsNullOrWhiteSpace(GetValue(options, "comment"))
                ? $"{section.Name} shared files"
                : GetValue(options, "comment"),
            browseable,
            readOnly,
            guestAccess,
            validUsers,
            validGroups,
            writeList,
            readList,
            NullIfWhiteSpace(GetValue(options, "force user")),
            NullIfWhiteSpace(GetValue(options, "force group")),
            createMask,
            directoryMask,
            BuildMaskExplanation(createMask, directory: false),
            BuildMaskExplanation(directoryMask, directory: true));
    }

    private static Dictionary<string, string> ResolveOptions(
        SambaSection section,
        IReadOnlyDictionary<string, SambaSection> sectionsByName,
        HashSet<string> resolutionChain)
    {
        if (!resolutionChain.Add(section.Name))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (section.Options.TryGetValue("copy", out var baseShareName) &&
            sectionsByName.TryGetValue(baseShareName.Trim(), out var baseSection))
        {
            foreach (var entry in ResolveOptions(baseSection, sectionsByName, resolutionChain))
            {
                resolved[entry.Key] = entry.Value;
            }
        }

        foreach (var entry in section.Options)
        {
            if (!entry.Key.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                resolved[entry.Key] = entry.Value;
            }
        }

        resolutionChain.Remove(section.Name);
        return resolved;
    }

    private static bool ResolveReadOnly(IReadOnlyDictionary<string, string> options)
    {
        var readOnly = GetValue(options, "read only");
        if (!string.IsNullOrWhiteSpace(readOnly))
        {
            return ParseBoolean(readOnly, defaultValue: true);
        }

        var writable = FirstValue(options, "write ok", "writeable", "writable");
        return string.IsNullOrWhiteSpace(writable)
            ? true
            : !ParseBoolean(writable, defaultValue: false);
    }

    private static bool ResolveBrowseable(IReadOnlyDictionary<string, string> options)
    {
        var browseable = FirstValue(options, "browseable", "browsable");
        return string.IsNullOrWhiteSpace(browseable)
            ? true
            : ParseBoolean(browseable, defaultValue: true);
    }

    private static bool ResolveGuestAccess(IReadOnlyDictionary<string, string> options)
    {
        var guestValue = FirstValue(options, "guest ok", "public");
        return string.IsNullOrWhiteSpace(guestValue)
            ? false
            : ParseBoolean(guestValue, defaultValue: false);
    }

    private static string GetMask(
        IReadOnlyDictionary<string, string> options,
        string primaryKey,
        string secondaryKey,
        string fallback)
    {
        var value = FirstValue(options, primaryKey, secondaryKey);
        return NormalizeMask(string.IsNullOrWhiteSpace(value) ? fallback : value);
    }

    private static IReadOnlyList<string> ParsePrincipalList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        var buffer = new StringBuilder();
        var quote = '\0';

        foreach (var character in value)
        {
            if (quote == '\0' && (character == '"' || character == '\''))
            {
                quote = character;
                continue;
            }

            if (quote != '\0' && character == quote)
            {
                quote = '\0';
                continue;
            }

            if (quote == '\0' && (character == ',' || char.IsWhiteSpace(character)))
            {
                FlushBuffer(values, buffer);
                continue;
            }

            buffer.Append(character);
        }

        FlushBuffer(values, buffer);

        return values
            .Select(NormalizePrincipalEntry)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void FlushBuffer(List<string> values, StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        values.Add(buffer.ToString());
        buffer.Clear();
    }

    private static string NormalizePrincipalEntry(string value)
    {
        var normalized = value.Trim().Trim('"', '\'');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return normalized[0] switch
        {
            '@' or '+' or '&' when normalized.Length > 1 => $"@{normalized[1..].Trim()}",
            _ => normalized
        };
    }

    private static (IReadOnlyList<string> Users, IReadOnlyList<string> Groups) SplitUsersAndGroups(
        IReadOnlyList<string> principals)
    {
        var users = new List<string>();
        var groups = new List<string>();

        foreach (var principal in principals)
        {
            if (principal.StartsWith('@') && principal.Length > 1)
            {
                groups.Add(principal[1..]);
            }
            else
            {
                users.Add(principal);
            }
        }

        return (
            users.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            groups.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool TryParseKeyValue(string line, out string key, out string value)
    {
        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = line[..separatorIndex].Trim().ToLowerInvariant();
        value = line[(separatorIndex + 1)..].Trim();
        return key.Length > 0;
    }

    private static string GetValue(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value)
            ? value.Trim()
            : string.Empty;

    private static string FirstValue(IReadOnlyDictionary<string, string> options, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = GetValue(options, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static bool ParseBoolean(string value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "1" or "on" => true,
            "no" or "false" or "0" or "off" => false,
            _ => defaultValue
        };
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeMask(string mask)
    {
        var trimmed = mask.Trim();
        return trimmed.Length switch
        {
            3 => trimmed,
            4 => trimmed,
            _ => trimmed.All(char.IsDigit) && trimmed.Length > 0 ? trimmed : "0755"
        };
    }

    private static string BuildMaskExplanation(string mask, bool directory)
    {
        var target = directory ? "folders" : "files";
        return $"New {target} use mask {mask}.";
    }

    private static Guid CreateStableId(string scope, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}:{value.Trim().ToLowerInvariant()}"));
        return new Guid(bytes[..16]);
    }

    private sealed class SambaSection
    {
        public SambaSection(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
