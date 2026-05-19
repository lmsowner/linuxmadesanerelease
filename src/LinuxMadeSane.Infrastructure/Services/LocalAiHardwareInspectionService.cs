// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.LocalAi;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalAiHardwareInspectionService(
    ILinuxCommandRunner commandRunner,
    ILocalAiEngineStore store) : ILocalAiHardwareInspectionService
{
    public async Task<LocalAiHardwareProfile> InspectAsync(CancellationToken cancellationToken = default)
    {
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var cpuTask = RunOptionalAsync(
            new LinuxCommandRequest("lscpu", [], false, TimeSpan.FromSeconds(10), "Inspect AI engine CPU"));
        var memoryTask = RunRequiredAsync(
            new LinuxCommandRequest("free", ["-b"], false, TimeSpan.FromSeconds(10), "Inspect AI engine memory"));
        var diskTask = RunRequiredAsync(
            new LinuxCommandRequest("df", ["-B1", "--output=avail", "/"], false, TimeSpan.FromSeconds(10), "Inspect AI engine disk"));
        var pciTask = RunOptionalAsync(
            new LinuxCommandRequest("lspci", ["-nn"], false, TimeSpan.FromSeconds(10), "Inspect AI engine PCI devices")
            {
                IsOptionalExternalTool = true
            });
        var nvidiaTask = RunOptionalAsync(
            new LinuxCommandRequest(
                "nvidia-smi",
                ["--query-gpu=name,memory.total,driver_version", "--format=csv,noheader,nounits"],
                false,
                TimeSpan.FromSeconds(10),
                "Inspect NVIDIA GPU availability")
            {
                IsOptionalExternalTool = true
            });
        var rocmTask = RunOptionalAsync(
            new LinuxCommandRequest("rocminfo", [], false, TimeSpan.FromSeconds(10), "Inspect ROCm GPU availability")
            {
                IsOptionalExternalTool = true
            });

        await Task.WhenAll(cpuTask, memoryTask, diskTask, pciTask, nvidiaTask, rocmTask);

        var cpuOutput = cpuTask.Result;
        var memoryOutput = memoryTask.Result;
        var diskOutput = diskTask.Result;
        var pciOutput = pciTask.Result;
        var nvidiaOutput = nvidiaTask.Result;
        var rocmOutput = rocmTask.Result;

        var (cpuModel, physicalCoreCount, logicalCoreCount) = ParseCpu(cpuOutput.StandardOutput);
        var (totalMemoryBytes, availableMemoryBytes) = ParseMemory(memoryOutput.StandardOutput);
        var availableDiskBytes = ParseAvailableDisk(diskOutput.StandardOutput);
        var gpus = ParseGpus(pciOutput.StandardOutput, nvidiaOutput.StandardOutput, rocmOutput.StandardOutput);

        var gpuState = gpus.Count == 0
            ? LocalAiGpuAccelerationState.NotDetected
            : gpus.Any(gpu => gpu.IsCudaAvailable || gpu.IsRocmAvailable)
                ? LocalAiGpuAccelerationState.Confirmed
                : LocalAiGpuAccelerationState.Available;

        var summary = BuildSummary(cpuModel, totalMemoryBytes, gpus, gpuState);
        var profile = new LocalAiHardwareProfile(
            cpuModel,
            physicalCoreCount,
            logicalCoreCount,
            totalMemoryBytes,
            availableMemoryBytes,
            availableDiskBytes,
            gpuState,
            gpus,
            summary,
            capturedAtUtc);

        await store.SaveHardwareProfileAsync(profile, cancellationToken);
        return profile;
    }

    private Task<LinuxCommandResult> RunRequiredAsync(LinuxCommandRequest request) =>
        commandRunner.RunAsync(request, dryRun: false);

    private Task<LinuxCommandResult> RunOptionalAsync(LinuxCommandRequest request) =>
        commandRunner.RunAsync(request, dryRun: false);

    private static (string CpuModel, int PhysicalCoreCount, int LogicalCoreCount) ParseCpu(string stdout)
    {
        var cpuModel = "Unknown CPU";
        var physicalCores = 0;
        var logicalCores = 0;

        foreach (var line in SplitLines(stdout))
        {
            if (line.StartsWith("Model name:", StringComparison.OrdinalIgnoreCase))
            {
                cpuModel = line[(line.IndexOf(':') + 1)..].Trim();
            }
            else if (line.StartsWith("Core(s) per socket:", StringComparison.OrdinalIgnoreCase))
            {
                physicalCores = ParseIntValue(line);
            }
            else if (line.StartsWith("Socket(s):", StringComparison.OrdinalIgnoreCase))
            {
                physicalCores = Math.Max(physicalCores, 1) * Math.Max(ParseIntValue(line), 1);
            }
            else if (line.StartsWith("CPU(s):", StringComparison.OrdinalIgnoreCase))
            {
                logicalCores = ParseIntValue(line);
            }
        }

        return (cpuModel, Math.Max(physicalCores, logicalCores > 0 ? logicalCores : 1), Math.Max(logicalCores, 1));
    }

    private static (long TotalMemoryBytes, long AvailableMemoryBytes) ParseMemory(string stdout)
    {
        foreach (var line in SplitLines(stdout))
        {
            if (!line.StartsWith("Mem:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 7)
            {
                break;
            }

            if (long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) &&
                long.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var available))
            {
                return (total, available);
            }
        }

        return (0, 0);
    }

    private static long ParseAvailableDisk(string stdout)
    {
        var line = SplitLines(stdout).Skip(1).FirstOrDefault();
        return line is not null && long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var available)
            ? available
            : 0;
    }

    private static IReadOnlyList<LocalAiGpuAdapter> ParseGpus(string lspciOutput, string nvidiaOutput, string rocmOutput)
    {
        var adapters = new List<LocalAiGpuAdapter>();
        var nvidiaInfo = ParseNvidia(nvidiaOutput);
        var amdNames = ParseRocm(rocmOutput);

        foreach (var line in SplitLines(lspciOutput))
        {
            if (!line.Contains("VGA compatible controller", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("3D controller", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isNvidia = line.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            var isAmd = line.Contains("AMD", StringComparison.OrdinalIgnoreCase) || line.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase);
            var normalizedName = line[(line.IndexOf(':') + 1)..].Trim();
            var key = normalizedName.ToUpperInvariant();
            nvidiaInfo.TryGetValue(key, out var matchedNvidia);
            var rocmMatch = amdNames.FirstOrDefault(item => normalizedName.Contains(item, StringComparison.OrdinalIgnoreCase));

            adapters.Add(new LocalAiGpuAdapter(
                isNvidia ? "NVIDIA" : isAmd ? "AMD" : "Unknown",
                normalizedName,
                isNvidia,
                isAmd,
                matchedNvidia.TotalVramBytes,
                matchedNvidia.IsDetected,
                !string.IsNullOrWhiteSpace(rocmMatch),
                matchedNvidia.DriverVersion,
                isNvidia
                    ? (matchedNvidia.IsDetected ? "NVIDIA GPU detected and nvidia-smi is available." : "NVIDIA GPU detected but nvidia-smi did not confirm acceleration.")
                    : isAmd
                        ? (!string.IsNullOrWhiteSpace(rocmMatch) ? "AMD GPU detected and ROCm tooling is available." : "AMD GPU detected but ROCm is not confirmed.")
                        : "GPU detected, but vendor-specific acceleration tooling was not confirmed."));
        }

        if (adapters.Count > 0)
        {
            return adapters;
        }

        foreach (var line in SplitLines(nvidiaOutput))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            adapters.Add(new LocalAiGpuAdapter(
                "NVIDIA",
                parts[0],
                true,
                false,
                TryParseMegabytes(parts[1]),
                true,
                false,
                parts[2],
                "NVIDIA GPU detected from nvidia-smi."));
        }

        return adapters;
    }

    private static Dictionary<string, (bool IsDetected, long? TotalVramBytes, string DriverVersion)> ParseNvidia(string stdout)
    {
        var result = new Dictionary<string, (bool, long?, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SplitLines(stdout))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            result[parts[0].ToUpperInvariant()] = (true, TryParseMegabytes(parts[1]), parts[2]);
        }

        return result;
    }

    private static IReadOnlyList<string> ParseRocm(string stdout) =>
        SplitLines(stdout)
            .Where(line => line.Contains("Name:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(line.IndexOf(':') + 1)..].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string BuildSummary(
        string cpuModel,
        long totalMemoryBytes,
        IReadOnlyList<LocalAiGpuAdapter> gpus,
        LocalAiGpuAccelerationState gpuState)
    {
        var ramGb = totalMemoryBytes <= 0 ? "unknown RAM" : $"{Math.Round(totalMemoryBytes / 1024d / 1024d / 1024d, 1):0.#} GB RAM";
        var gpuSummary = gpus.Count == 0
            ? "no GPU acceleration detected"
            : gpuState == LocalAiGpuAccelerationState.Confirmed
                ? $"{gpus.Count} GPU(s) with acceleration confirmed"
                : $"{gpus.Count} GPU(s) detected, but acceleration is not fully confirmed";

        return $"{cpuModel}, {ramGb}, {gpuSummary}.";
    }

    private static IReadOnlyList<string> SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int ParseIntValue(string line)
    {
        var value = line[(line.IndexOf(':') + 1)..].Trim();
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static long? TryParseMegabytes(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? (long?)(parsed * 1024 * 1024)
            : null;
}
