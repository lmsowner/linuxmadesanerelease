// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Monitoring;

public sealed record LocalSystemMonitorSnapshot(
    string HostName,
    string OperatingSystem,
    string KernelVersion,
    DateTimeOffset CapturedAtUtc,
    double? SampleDurationSeconds,
    TimeSpan Uptime,
    int LogicalProcessorCount,
    LocalCpuMetric Cpu,
    LocalLoadMetric Load,
    LocalMemoryMetric Memory,
    LocalFileSystemMetric RootFileSystem,
    int ProcessCount,
    int RunningProcessCount,
    int ZombieProcessCount,
    int TcpEstablishedConnectionCount,
    bool ListeningPortOwnershipIsPrivileged,
    IReadOnlyList<LocalProcessMetric> Processes,
    IReadOnlyList<LocalNetworkInterfaceMetric> NetworkInterfaces,
    IReadOnlyList<LocalDiskMetric> Disks);

public sealed record LocalCpuMetric(
    double? UsagePercent,
    double? UserPercent,
    double? SystemPercent,
    double? IoWaitPercent,
    double? StealPercent);

public sealed record LocalLoadMetric(
    double OneMinute,
    double FiveMinutes,
    double FifteenMinutes);

public sealed record LocalMemoryMetric(
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes,
    long CacheBytes,
    long SwapTotalBytes,
    long SwapUsedBytes);

public sealed record LocalFileSystemMetric(
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes);

public sealed record LocalProcessMetric(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string User,
    string State,
    double? CpuPercent,
    long ResidentMemoryBytes,
    int ThreadCount,
    double? ReadBytesPerSecond,
    double? WriteBytesPerSecond,
    bool ListeningPortsAccessible,
    IReadOnlyList<LocalProcessListeningPortMetric> ListeningPorts,
    string CommandLine,
    long ProcessStartTimeTicks);

public sealed record LocalProcessListeningPortMetric(
    string Protocol,
    string Address,
    int Port,
    string InterfaceName);

public sealed record LocalNetworkInterfaceMetric(
    string Name,
    bool IsLoopback,
    double? ReceiveBytesPerSecond,
    double? TransmitBytesPerSecond,
    double? ReceivePacketsPerSecond,
    double? TransmitPacketsPerSecond,
    long ReceiveErrors,
    long TransmitErrors,
    long ReceiveDrops,
    long TransmitDrops);

public sealed record LocalDiskMetric(
    string Name,
    double? ReadBytesPerSecond,
    double? WriteBytesPerSecond,
    double? ReadsPerSecond,
    double? WritesPerSecond,
    double? BusyPercent);
