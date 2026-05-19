// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Infrastructure.Services;

internal sealed class SambaConfigurationShareWriter
{
    private static readonly string[] DefaultMainConfigPaths =
    [
        "/etc/samba/smb.conf",
        "/usr/local/samba/lib/smb.conf"
    ];

    private readonly ILinuxCommandRunner commandRunner;
    private readonly IReadOnlyList<string> mainConfigPaths;
    private readonly string managedConfigPath;

    public SambaConfigurationShareWriter(
        ILinuxCommandRunner commandRunner,
        IReadOnlyList<string>? mainConfigPaths = null,
        string? managedConfigPath = null)
    {
        this.commandRunner = commandRunner;
        this.mainConfigPaths = mainConfigPaths ?? DefaultMainConfigPaths;
        this.managedConfigPath = string.IsNullOrWhiteSpace(managedConfigPath)
            ? "/etc/samba/lms-shares.conf"
            : managedConfigPath.Trim();
    }

    public async Task<IReadOnlyList<SambaShareDefinition>> ListManagedSharesAsync(CancellationToken cancellationToken = default)
    {
        var text = await ReadManagedConfigurationTextAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text)
            ? Array.Empty<SambaShareDefinition>()
            : SambaConfigurationShareReader.ParseShares(text);
    }

    public async Task<bool> IsManagedShareAsync(
        SambaShareDefinition share,
        CancellationToken cancellationToken = default)
    {
        var managedShares = await ListManagedSharesAsync(cancellationToken);
        return managedShares.Any(managedShare => SharesMatch(managedShare, share));
    }

    public async Task SaveManagedShareAsync(
        SambaShareDefinition share,
        SambaShareDefinition? previousShare = null,
        CancellationToken cancellationToken = default)
    {
        var managedShares = (await ListManagedSharesAsync(cancellationToken)).ToList();
        var existingIndex = FindManagedShareIndex(managedShares, share, previousShare);
        if (existingIndex >= 0)
        {
            managedShares[existingIndex] = share;
        }
        else
        {
            managedShares.Add(share);
        }

        await ApplyManagedConfigurationAsync(managedShares, share.SharePath, cancellationToken);
    }

    public async Task DeleteManagedShareAsync(
        SambaShareDefinition share,
        CancellationToken cancellationToken = default)
    {
        var managedShares = (await ListManagedSharesAsync(cancellationToken)).ToList();
        managedShares.RemoveAll(candidate => SharesMatch(candidate, share));
        await ApplyManagedConfigurationAsync(managedShares, null, cancellationToken);
    }

    private async Task<string> ReadManagedConfigurationTextAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(managedConfigPath))
        {
            return await File.ReadAllTextAsync(managedConfigPath, cancellationToken);
        }

        return string.Empty;
    }

    private async Task ApplyManagedConfigurationAsync(
        IReadOnlyList<SambaShareDefinition> managedShares,
        string? sharePathToEnsure,
        CancellationToken cancellationToken)
    {
        var mainConfigPath = ResolvePrimaryMainConfigPath();
        var liveMainText = await ReadTextOrDefaultAsync(mainConfigPath, cancellationToken);
        var liveManagedText = RenderManagedShares(managedShares);

        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var tempMainConfigPath = Path.Combine(tempDirectory, Path.GetFileName(mainConfigPath));
            var tempManagedConfigPath = Path.Combine(tempDirectory, Path.GetFileName(managedConfigPath));

            await File.WriteAllTextAsync(tempManagedConfigPath, liveManagedText, cancellationToken);
            await File.WriteAllTextAsync(
                tempMainConfigPath,
                EnsureManagedInclude(liveMainText, tempManagedConfigPath),
                cancellationToken);

            await ValidateConfigurationAsync(tempMainConfigPath, cancellationToken);

            if (!string.IsNullOrWhiteSpace(sharePathToEnsure))
            {
                await EnsureSharePathExistsAsync(sharePathToEnsure, cancellationToken);
            }

            await WriteTextAsync(managedConfigPath, liveManagedText, cancellationToken);
            await WriteTextAsync(mainConfigPath, EnsureManagedInclude(liveMainText, managedConfigPath), cancellationToken);
            await TryEnableAndRestartSambaAsync(cancellationToken);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private string ResolvePrimaryMainConfigPath()
    {
        foreach (var path in mainConfigPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return mainConfigPaths[0];
    }

    private async Task<string> ReadTextOrDefaultAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }

        return string.Empty;
    }

    private async Task ValidateConfigurationAsync(string mainConfigPath, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "testparm",
                [mainConfigPath, "-s"],
                RequiresSudo: false,
                Timeout: TimeSpan.FromSeconds(15),
                "Validate staged Samba configuration")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0 || IsExecutableMissing(result))
        {
            return;
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result.StandardError)
                ? $"Samba configuration validation failed: {result.StandardOutput.Trim()}"
                : $"Samba configuration validation failed: {result.StandardError.Trim()}");
    }

    private async Task EnsureSharePathExistsAsync(string sharePath, CancellationToken cancellationToken)
    {
        if (Directory.Exists(sharePath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(sharePath);
            return;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        await RunRequiredCommandAsync(
            "mkdir",
            ["-p", sharePath],
            $"Create share path {sharePath}",
            requiresSudo: true,
            cancellationToken);
    }

    private async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            try
            {
                Directory.CreateDirectory(directoryPath);
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        try
        {
            await File.WriteAllTextAsync(path, content, cancellationToken);
            return;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }

        var temporaryFilePath = Path.Combine(CreateTemporaryDirectory(), Path.GetRandomFileName());
        try
        {
            await File.WriteAllTextAsync(temporaryFilePath, content, cancellationToken);
            await RunRequiredCommandAsync(
                "install",
                ["-D", "-m", "0644", temporaryFilePath, path],
                $"Write Samba configuration {path}",
                requiresSudo: true,
                cancellationToken);
        }
        finally
        {
            var tempDirectory = Path.GetDirectoryName(temporaryFilePath);
            if (File.Exists(temporaryFilePath))
            {
                File.Delete(temporaryFilePath);
            }

            if (!string.IsNullOrWhiteSpace(tempDirectory) && Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private async Task TryEnableAndRestartSambaAsync(CancellationToken cancellationToken)
    {
        var enableResult = await RunOptionalCommandAsync(
            "systemctl",
            ["enable", "smbd"],
            "Enable Samba service",
            requiresSudo: true,
            cancellationToken);

        if (IsRuntimeCommandFatal(enableResult))
        {
            return;
        }

        await RunOptionalCommandAsync(
            "systemctl",
            ["restart", "smbd"],
            "Restart Samba service",
            requiresSudo: true,
            cancellationToken);
    }

    private async Task<LinuxCommandResult> RunOptionalCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        bool requiresSudo,
        CancellationToken cancellationToken) =>
        await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, requiresSudo, TimeSpan.FromSeconds(30), description)
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

    private async Task RunRequiredCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        bool requiresSudo,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, requiresSudo, TimeSpan.FromSeconds(30), description),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result.StandardError)
                ? $"{description} failed: {result.StandardOutput.Trim()}"
                : $"{description} failed: {result.StandardError.Trim()}");
    }

    private static bool SharesMatch(SambaShareDefinition left, SambaShareDefinition right) =>
        left.Id == right.Id ||
        (left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) &&
         left.SharePath.Equals(right.SharePath, StringComparison.OrdinalIgnoreCase));

    private static int FindManagedShareIndex(
        IReadOnlyList<SambaShareDefinition> managedShares,
        SambaShareDefinition share,
        SambaShareDefinition? previousShare)
    {
        for (var index = 0; index < managedShares.Count; index++)
        {
            var candidate = managedShares[index];
            if (SharesMatch(candidate, share))
            {
                return index;
            }

            if (previousShare is not null &&
                (SharesMatch(candidate, previousShare) ||
                 candidate.Name.Equals(previousShare.Name, StringComparison.OrdinalIgnoreCase) ||
                 candidate.SharePath.Equals(previousShare.SharePath, StringComparison.OrdinalIgnoreCase)))
            {
                return index;
            }
        }

        return -1;
    }

    private static string RenderManagedShares(IReadOnlyList<SambaShareDefinition> shares)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Linux Made Sane managed Samba shares");
        builder.AppendLine("# Edit these shares from LMS instead of editing this file by hand.");
        builder.AppendLine();

        foreach (var share in shares.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"[{share.Name}]");
            builder.AppendLine($"    path = {share.SharePath}");
            builder.AppendLine($"    comment = {share.Description}");
            builder.AppendLine($"    browseable = {FormatBoolean(share.Browseable)}");
            builder.AppendLine($"    read only = {FormatBoolean(share.ReadOnly)}");
            builder.AppendLine($"    guest ok = {FormatBoolean(share.GuestAccess)}");

            var validUsers = BuildValidUsersEntry(share);
            if (!string.IsNullOrWhiteSpace(validUsers))
            {
                builder.AppendLine($"    valid users = {validUsers}");
            }

            if (share.WriteList.Count > 0)
            {
                builder.AppendLine($"    write list = {string.Join(' ', share.WriteList)}");
            }

            if (share.ReadList.Count > 0)
            {
                builder.AppendLine($"    read list = {string.Join(' ', share.ReadList)}");
            }

            if (!string.IsNullOrWhiteSpace(share.ForceUser))
            {
                builder.AppendLine($"    force user = {share.ForceUser}");
            }

            if (!string.IsNullOrWhiteSpace(share.ForceGroup))
            {
                builder.AppendLine($"    force group = {share.ForceGroup}");
            }

            builder.AppendLine($"    create mask = {share.CreateMask}");
            builder.AppendLine($"    directory mask = {share.DirectoryMask}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildValidUsersEntry(SambaShareDefinition share)
    {
        var entries = new List<string>();
        entries.AddRange(share.ValidUsers);
        entries.AddRange(share.ValidGroups.Select(group => group.StartsWith('@') ? group : $"@{group}"));
        return string.Join(' ', entries.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string EnsureManagedInclude(string mainConfigText, string includePath)
    {
        if (string.IsNullOrWhiteSpace(mainConfigText))
        {
            return $"[global]{Environment.NewLine}    include = {includePath}{Environment.NewLine}";
        }

        var normalizedNewLine = mainConfigText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = mainConfigText.Replace("\r\n", "\n").Split('\n').ToList();

        if (HasIncludeLine(lines, includePath))
        {
            return string.Join(normalizedNewLine, lines);
        }

        var globalSectionStart = FindSectionStart(lines, "global");
        if (globalSectionStart >= 0)
        {
            var insertIndex = lines.Count;
            for (var index = globalSectionStart + 1; index < lines.Count; index++)
            {
                var line = lines[index].Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    insertIndex = index;
                    break;
                }
            }

            lines.Insert(insertIndex, $"    include = {includePath}");
        }
        else
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add("[global]");
            lines.Add($"    include = {includePath}");
        }

        return string.Join(normalizedNewLine, lines);
    }

    private static bool HasIncludeLine(IReadOnlyList<string> lines, string includePath)
    {
        foreach (var line in lines)
        {
            if (!TryParseKeyValue(line, out var key, out var value))
            {
                continue;
            }

            if (key.Equals("include", StringComparison.OrdinalIgnoreCase) &&
                value.Equals(includePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindSectionStart(IReadOnlyList<string> lines, string sectionName)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith('[') &&
                line.EndsWith(']') &&
                line[1..^1].Trim().Equals(sectionName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
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

        key = line[..separatorIndex].Trim();
        value = line[(separatorIndex + 1)..].Trim();
        return key.Length > 0;
    }

    private static bool IsExecutableMissing(LinuxCommandResult result) =>
        result.ExitCode == 127 ||
        result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("No such file", StringComparison.OrdinalIgnoreCase);

    private static bool IsRuntimeCommandFatal(LinuxCommandResult result)
    {
        if (result.ExitCode == 0)
        {
            return false;
        }

        if (IsExecutableMissing(result))
        {
            return true;
        }

        var text = string.Concat(result.StandardOutput, "\n", result.StandardError);
        return text.Contains("could not be found", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not-found", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBoolean(bool value) => value ? "yes" : "no";

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "linuxmadesane-samba", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
