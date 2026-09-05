// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Monitoring;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalProcSystemMonitorService(ILinuxCommandRunner? commandRunner = null) : ILocalSystemMonitorService
{
    private const int ScClockTicks = 2;
    private const int PrivilegedSocketOwnershipFailureTolerance = 1;
    private static readonly TimeSpan MinimumSampleSpacing = TimeSpan.FromMilliseconds(350);
    private static readonly long ClockTicksPerSecond = ResolveClockTicksPerSecond();
    private static readonly string HostName = Dns.GetHostName();
    private static readonly string OperatingSystemName = ReadOperatingSystemName();
    private static readonly string KernelVersion =
        ReadTrimmedText("/proc/sys/kernel/osrelease") ?? RuntimeInformation.OSDescription;
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private RawSample? previousSample;
    private LocalSystemMonitorSnapshot? lastSnapshot;
    private PrivilegedSocketOwnership? lastSuccessfulPrivilegedSocketOwnership;
    private int consecutivePrivilegedSocketOwnershipFailures;

    public async Task<LocalSystemMonitorSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await captureGate.WaitAsync(cancellationToken);
        try
        {
            if (lastSnapshot is not null &&
                DateTimeOffset.UtcNow - lastSnapshot.CapturedAtUtc < MinimumSampleSpacing)
            {
                return lastSnapshot;
            }

            var privilegedSocketOwnership = await ReadPrivilegedSocketOwnershipAsync(cancellationToken);
            var current = await Task.Run(
                () => CaptureRawSample(privilegedSocketOwnership),
                cancellationToken);
            var snapshot = BuildSnapshot(current, previousSample);
            previousSample = current;
            lastSnapshot = snapshot;
            return snapshot;
        }
        finally
        {
            captureGate.Release();
        }
    }

    private static RawSample CaptureRawSample(PrivilegedSocketOwnership privilegedSocketOwnership)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Live system analysis requires the Linux /proc filesystem.");
        }

        var capturedAtUtc = DateTimeOffset.UtcNow;
        var users = ReadUserNames();
        var socketSnapshot = ReadSocketSnapshot();
        return new RawSample(
            capturedAtUtc,
            ReadCpuCounters(),
            ReadLoad(),
            ReadMemoryCounters(),
            ReadRootFileSystem(),
            ReadUptime(),
            ReadProcesses(users, socketSnapshot.ListeningSockets, privilegedSocketOwnership),
            ReadNetworkInterfaces(),
            ReadDisks(),
            socketSnapshot.TcpEstablishedConnectionCount,
            privilegedSocketOwnership.IsAvailable);
    }

    private static LocalSystemMonitorSnapshot BuildSnapshot(RawSample current, RawSample? previous)
    {
        var elapsedSeconds = previous is null
            ? (double?)null
            : Math.Max(0.001, (current.CapturedAtUtc - previous.CapturedAtUtc).TotalSeconds);
        var cpu = BuildCpuMetric(current.Cpu, previous?.Cpu);
        var previousProcesses = previous?.Processes.ToDictionary(
            process => (process.ProcessId, process.StartTimeTicks)) ?? [];
        var processes = current.Processes
            .Select(process => BuildProcessMetric(
                process,
                previousProcesses.GetValueOrDefault((process.ProcessId, process.StartTimeTicks)),
                elapsedSeconds))
            .OrderBy(process => process.ProcessId)
            .ToArray();
        var previousNetwork = previous?.NetworkInterfaces.ToDictionary(item => item.Name, StringComparer.Ordinal) ?? [];
        var network = current.NetworkInterfaces
            .Select(item => BuildNetworkMetric(item, previousNetwork.GetValueOrDefault(item.Name), elapsedSeconds))
            .OrderBy(item => item.IsLoopback)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        var previousDisks = previous?.Disks.ToDictionary(item => item.Name, StringComparer.Ordinal) ?? [];
        var disks = current.Disks
            .Select(item => BuildDiskMetric(item, previousDisks.GetValueOrDefault(item.Name), elapsedSeconds))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();

        return new LocalSystemMonitorSnapshot(
            HostName,
            OperatingSystemName,
            KernelVersion,
            current.CapturedAtUtc,
            elapsedSeconds,
            current.Uptime,
            Environment.ProcessorCount,
            cpu,
            current.Load,
            current.Memory,
            current.RootFileSystem,
            processes.Length,
            processes.Count(process => process.State == "Running"),
            processes.Count(process => process.State == "Zombie"),
            current.TcpEstablishedConnectionCount,
            current.ListeningPortOwnershipIsPrivileged,
            processes,
            network,
            disks);
    }

    internal static LocalCpuMetric BuildCpuMetric(CpuCounters current, CpuCounters? previous)
    {
        if (previous is null)
        {
            return new LocalCpuMetric(null, null, null, null, null);
        }

        var total = current.Total - previous.Value.Total;
        if (total <= 0)
        {
            return new LocalCpuMetric(null, null, null, null, null);
        }

        var idle = NonNegativeDelta(current.Idle, previous.Value.Idle);
        var ioWait = NonNegativeDelta(current.IoWait, previous.Value.IoWait);
        var user = NonNegativeDelta(current.User + current.Nice, previous.Value.User + previous.Value.Nice);
        var system = NonNegativeDelta(
            current.System + current.Irq + current.SoftIrq,
            previous.Value.System + previous.Value.Irq + previous.Value.SoftIrq);
        var steal = NonNegativeDelta(current.Steal, previous.Value.Steal);

        return new LocalCpuMetric(
            Percent(Math.Max(0, total - idle - ioWait), total),
            Percent(user, total),
            Percent(system, total),
            Percent(ioWait, total),
            Percent(steal, total));
    }

    private static LocalProcessMetric BuildProcessMetric(
        RawProcess current,
        RawProcess? previous,
        double? elapsedSeconds)
    {
        double? cpuPercent = null;
        double? readBytesPerSecond = null;
        double? writeBytesPerSecond = null;

        if (previous is not null && elapsedSeconds.HasValue)
        {
            var cpuDelta = current.CpuTicks - previous.CpuTicks;
            if (cpuDelta >= 0 && ClockTicksPerSecond > 0)
            {
                cpuPercent = cpuDelta / (double)ClockTicksPerSecond / elapsedSeconds.Value * 100d;
            }

            readBytesPerSecond = Rate(current.ReadBytes, previous.ReadBytes, elapsedSeconds.Value);
            writeBytesPerSecond = Rate(current.WriteBytes, previous.WriteBytes, elapsedSeconds.Value);
        }

        return new LocalProcessMetric(
            current.ProcessId,
            current.ParentProcessId,
            current.Name,
            current.User,
            MapProcessState(current.State),
            cpuPercent,
            current.ResidentMemoryBytes,
            current.ThreadCount,
            readBytesPerSecond,
            writeBytesPerSecond,
            current.ListeningPortsAccessible,
            current.ListeningPorts,
            current.CommandLine,
            current.StartTimeTicks);
    }

    private static LocalNetworkInterfaceMetric BuildNetworkMetric(
        NetworkCounters current,
        NetworkCounters? previous,
        double? elapsedSeconds) =>
        new(
            current.Name,
            current.Name.Equals("lo", StringComparison.Ordinal),
            Rate(current.ReceiveBytes, previous?.ReceiveBytes, elapsedSeconds),
            Rate(current.TransmitBytes, previous?.TransmitBytes, elapsedSeconds),
            Rate(current.ReceivePackets, previous?.ReceivePackets, elapsedSeconds),
            Rate(current.TransmitPackets, previous?.TransmitPackets, elapsedSeconds),
            current.ReceiveErrors,
            current.TransmitErrors,
            current.ReceiveDrops,
            current.TransmitDrops);

    private static LocalDiskMetric BuildDiskMetric(
        DiskCounters current,
        DiskCounters? previous,
        double? elapsedSeconds)
    {
        double? busyPercent = previous is null || !elapsedSeconds.HasValue
            ? null
            : Math.Clamp(
                NonNegativeDelta(current.IoMilliseconds, previous.IoMilliseconds) /
                (elapsedSeconds.Value * 1000d) * 100d,
                0d,
                100d);

        return new LocalDiskMetric(
            current.Name,
            SectorRate(current.ReadSectors, previous?.ReadSectors, elapsedSeconds),
            SectorRate(current.WrittenSectors, previous?.WrittenSectors, elapsedSeconds),
            Rate(current.Reads, previous?.Reads, elapsedSeconds),
            Rate(current.Writes, previous?.Writes, elapsedSeconds),
            busyPercent);
    }

    internal static CpuCounters ParseCpuCounters(string line)
    {
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 9 || !fields[0].Equals("cpu", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The aggregate /proc/stat CPU row is invalid.");
        }

        return new CpuCounters(
            ParseLong(fields[1]),
            ParseLong(fields[2]),
            ParseLong(fields[3]),
            ParseLong(fields[4]),
            ParseLong(fields[5]),
            ParseLong(fields[6]),
            ParseLong(fields[7]),
            ParseLong(fields[8]));
    }

    internal static NetworkCounters? ParseNetworkCounters(string line)
    {
        var separator = line.IndexOf(':');
        if (separator <= 0)
        {
            return null;
        }

        var name = line[..separator].Trim();
        var fields = line[(separator + 1)..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(name) || fields.Length < 16)
        {
            return null;
        }

        return new NetworkCounters(
            name,
            ParseLong(fields[0]),
            ParseLong(fields[1]),
            ParseLong(fields[2]),
            ParseLong(fields[3]),
            ParseLong(fields[8]),
            ParseLong(fields[9]),
            ParseLong(fields[10]),
            ParseLong(fields[11]));
    }

    internal static DiskCounters? ParseDiskCounters(string line)
    {
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 14)
        {
            return null;
        }

        var name = fields[2];
        if (!Directory.Exists($"/sys/block/{name}") ||
            name.StartsWith("loop", StringComparison.Ordinal) ||
            name.StartsWith("ram", StringComparison.Ordinal))
        {
            return null;
        }

        return new DiskCounters(
            name,
            ParseLong(fields[3]),
            ParseLong(fields[5]),
            ParseLong(fields[7]),
            ParseLong(fields[9]),
            ParseLong(fields[12]));
    }

    private static CpuCounters ReadCpuCounters()
    {
        using var reader = File.OpenText("/proc/stat");
        return ParseCpuCounters(reader.ReadLine() ?? string.Empty);
    }

    private static LocalLoadMetric ReadLoad()
    {
        var fields = (ReadTrimmedText("/proc/loadavg") ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return new LocalLoadMetric(
            ParseDouble(fields.ElementAtOrDefault(0)),
            ParseDouble(fields.ElementAtOrDefault(1)),
            ParseDouble(fields.ElementAtOrDefault(2)));
    }

    private static LocalMemoryMetric ReadMemoryCounters()
    {
        var values = File.ReadLines("/proc/meminfo")
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => parts[0],
                parts => ParseLong(parts[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()) * 1024,
                StringComparer.Ordinal);
        var total = values.GetValueOrDefault("MemTotal");
        var available = values.GetValueOrDefault("MemAvailable");
        var cache = values.GetValueOrDefault("Cached") + values.GetValueOrDefault("SReclaimable");
        var swapTotal = values.GetValueOrDefault("SwapTotal");
        var swapFree = values.GetValueOrDefault("SwapFree");
        return new LocalMemoryMetric(
            total,
            Math.Max(0, total - available),
            available,
            cache,
            swapTotal,
            Math.Max(0, swapTotal - swapFree));
    }

    private static LocalFileSystemMetric ReadRootFileSystem()
    {
        try
        {
            var root = new DriveInfo("/");
            return new LocalFileSystemMetric(
                root.TotalSize,
                Math.Max(0, root.TotalSize - root.AvailableFreeSpace),
                root.AvailableFreeSpace);
        }
        catch
        {
            return new LocalFileSystemMetric(0, 0, 0);
        }
    }

    private static TimeSpan ReadUptime()
    {
        var value = (ReadTrimmedText("/proc/uptime") ?? "0")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return TimeSpan.FromSeconds(Math.Max(0, ParseDouble(value)));
    }

    private static IReadOnlyList<RawProcess> ReadProcesses(
        IReadOnlyDictionary<int, string> users,
        IReadOnlyDictionary<long, LocalProcessListeningPortMetric> listeningSockets,
        PrivilegedSocketOwnership privilegedSocketOwnership)
    {
        var processes = new List<RawProcess>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
            {
                continue;
            }

            try
            {
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var closeName = stat.LastIndexOf(')');
                var openName = stat.IndexOf('(');
                if (openName < 0 || closeName <= openName || closeName + 2 >= stat.Length)
                {
                    continue;
                }

                var name = stat[(openName + 1)..closeName];
                var fields = stat[(closeName + 2)..]
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 24)
                {
                    continue;
                }

                var userId = ReadProcessUserId(directory);
                var io = ReadProcessIo(directory);
                var commandLine = ReadProcessCommandLine(directory, name);
                var processPorts = privilegedSocketOwnership.IsAvailable
                    ? ReadPrivilegedProcessListeningPorts(
                        processId,
                        listeningSockets,
                        privilegedSocketOwnership.SocketInodesByProcessId)
                    : ReadProcessListeningPorts(directory, listeningSockets);
                processes.Add(new RawProcess(
                    processId,
                    (int)ParseLong(fields[1]),
                    name,
                    users.GetValueOrDefault(userId, userId.ToString(CultureInfo.InvariantCulture)),
                    fields[0].Length == 0 ? '?' : fields[0][0],
                    ParseLong(fields[11]) + ParseLong(fields[12]),
                    ParseLong(fields[19]),
                    Math.Max(0, ParseLong(fields[21])) * Environment.SystemPageSize,
                    (int)Math.Max(0, ParseLong(fields[17])),
                    io.ReadBytes,
                    io.WriteBytes,
                    processPorts.IsAccessible,
                    processPorts.Ports,
                    commandLine));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return processes;
    }

    private static IReadOnlyDictionary<int, string> ReadUserNames()
    {
        try
        {
            return File.ReadLines("/etc/passwd")
                .Select(line => line.Split(':'))
                .Where(parts => parts.Length > 2 && int.TryParse(parts[2], out _))
                .GroupBy(parts => int.Parse(parts[2], CultureInfo.InvariantCulture))
                .ToDictionary(group => group.Key, group => group.First()[0]);
        }
        catch
        {
            return new Dictionary<int, string>();
        }
    }

    private static int ReadProcessUserId(string directory)
    {
        foreach (var line in File.ReadLines(Path.Combine(directory, "status")))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                continue;
            }

            return (int)ParseLong(line[4..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault());
        }

        return -1;
    }

    private static (long? ReadBytes, long? WriteBytes) ReadProcessIo(string directory)
    {
        try
        {
            long? readBytes = null;
            long? writeBytes = null;
            foreach (var line in File.ReadLines(Path.Combine(directory, "io")))
            {
                if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
                {
                    readBytes = ParseLong(line["read_bytes:".Length..]);
                }
                else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
                {
                    writeBytes = ParseLong(line["write_bytes:".Length..]);
                }
            }

            return (readBytes, writeBytes);
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static string ReadProcessCommandLine(string directory, string fallback)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, "cmdline"));
            var commandLine = Encoding.UTF8.GetString(bytes).Replace('\0', ' ').Trim();
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return fallback;
            }

            return commandLine.Length <= 320 ? commandLine : $"{commandLine[..317]}...";
        }
        catch
        {
            return fallback;
        }
    }

    private static IReadOnlyList<NetworkCounters> ReadNetworkInterfaces() =>
        File.ReadLines("/proc/net/dev")
            .Select(ParseNetworkCounters)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();

    private static IReadOnlyList<DiskCounters> ReadDisks() =>
        File.ReadLines("/proc/diskstats")
            .Select(ParseDiskCounters)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();

    private async Task<PrivilegedSocketOwnership> ReadPrivilegedSocketOwnershipAsync(
        CancellationToken cancellationToken)
    {
        if (commandRunner is null)
        {
            return PrivilegedSocketOwnership.Unavailable;
        }

        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "find",
                [
                    "/proc",
                    "-ignore_readdir_race",
                    "-regextype", "posix-extended",
                    "-maxdepth", "3",
                    "-path", "/proc/[!0-9]*",
                    "-prune",
                    "-o",
                    "-regex", "/proc/[0-9]+/[^/]+",
                    "!", "-path", "/proc/[0-9]*/fd",
                    "-prune",
                    "-o",
                    "-type", "l",
                    "-path", "/proc/[0-9]*/fd/*",
                    "-lname", "socket:*",
                    "-printf", "%p\t%l\n"
                ],
                RequiresSudo: true,
                Timeout: TimeSpan.FromSeconds(10),
                Description: "Read privileged process socket ownership"),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            var ownership = new PrivilegedSocketOwnership(
                true,
                ParsePrivilegedSocketOwnership(result.StandardOutput));
            lastSuccessfulPrivilegedSocketOwnership = ownership;
            consecutivePrivilegedSocketOwnershipFailures = 0;
            return ownership;
        }

        consecutivePrivilegedSocketOwnershipFailures++;
        return lastSuccessfulPrivilegedSocketOwnership is not null &&
               consecutivePrivilegedSocketOwnershipFailures <= PrivilegedSocketOwnershipFailureTolerance
            ? lastSuccessfulPrivilegedSocketOwnership
            : PrivilegedSocketOwnership.Unavailable;
    }

    internal static IReadOnlyDictionary<int, IReadOnlySet<long>> ParsePrivilegedSocketOwnership(string output)
    {
        var result = new Dictionary<int, HashSet<long>>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\t', 2, StringSplitOptions.TrimEntries);
            if (fields.Length != 2 ||
                !TryParseProcessDescriptorPath(fields[0], out var processId) ||
                !TryParseSocketInode(fields[1], out var inode))
            {
                continue;
            }

            if (!result.TryGetValue(processId, out var processInodes))
            {
                processInodes = [];
                result.Add(processId, processInodes);
            }

            processInodes.Add(inode);
        }

        return result.ToDictionary(
            item => item.Key,
            item => (IReadOnlySet<long>)item.Value);
    }

    private static bool TryParseProcessDescriptorPath(string path, out int processId)
    {
        processId = 0;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 4 &&
               segments[0].Equals("proc", StringComparison.Ordinal) &&
               segments[2].Equals("fd", StringComparison.Ordinal) &&
               int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out processId) &&
               processId > 0 &&
               int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static SocketSnapshot ReadSocketSnapshot()
    {
        var interfaceNamesByAddress = ReadInterfaceNamesByAddress();
        var listeningSockets = new Dictionary<long, LocalProcessListeningPortMetric>();
        var establishedTcpConnections = 0;

        ReadSocketTable(
            "/proc/net/tcp",
            "TCP4",
            isIpv6: false,
            isTcp: true,
            interfaceNamesByAddress,
            listeningSockets,
            ref establishedTcpConnections);
        ReadSocketTable(
            "/proc/net/tcp6",
            "TCP6",
            isIpv6: true,
            isTcp: true,
            interfaceNamesByAddress,
            listeningSockets,
            ref establishedTcpConnections);
        ReadSocketTable(
            "/proc/net/udp",
            "UDP4",
            isIpv6: false,
            isTcp: false,
            interfaceNamesByAddress,
            listeningSockets,
            ref establishedTcpConnections);
        ReadSocketTable(
            "/proc/net/udp6",
            "UDP6",
            isIpv6: true,
            isTcp: false,
            interfaceNamesByAddress,
            listeningSockets,
            ref establishedTcpConnections);

        return new SocketSnapshot(listeningSockets, establishedTcpConnections);
    }

    private static void ReadSocketTable(
        string path,
        string protocol,
        bool isIpv6,
        bool isTcp,
        IReadOnlyDictionary<string, string> interfaceNamesByAddress,
        IDictionary<long, LocalProcessListeningPortMetric> listeningSockets,
        ref int establishedTcpConnections)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (isTcp && fields.Length > 3 && fields[3].Equals("01", StringComparison.Ordinal))
                {
                    establishedTcpConnections++;
                }

                var socket = ParseListeningSocket(fields, protocol, isIpv6, isTcp);
                if (socket is null)
                {
                    continue;
                }

                listeningSockets.TryAdd(
                    socket.Inode,
                    new LocalProcessListeningPortMetric(
                        socket.Protocol,
                        socket.Address.ToString(),
                        socket.Port,
                        ResolveInterfaceName(socket.Address, interfaceNamesByAddress)));
            }
        }
        catch
        {
        }
    }

    internal static ListeningSocket? ParseListeningSocket(
        string line,
        string protocol,
        bool isIpv6,
        bool isTcp) =>
        ParseListeningSocket(
            line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
            protocol,
            isIpv6,
            isTcp);

    private static ListeningSocket? ParseListeningSocket(
        IReadOnlyList<string> fields,
        string protocol,
        bool isIpv6,
        bool isTcp)
    {
        if (fields.Count <= 9 ||
            (isTcp && !fields[3].Equals("0A", StringComparison.Ordinal)) ||
            (!isTcp && !fields[3].Equals("07", StringComparison.Ordinal)))
        {
            return null;
        }

        var localAddress = fields[1].Split(':', 2);
        if (localAddress.Length != 2 ||
            !int.TryParse(localAddress[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port) ||
            port is <= 0 or > 65535 ||
            !long.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inode) ||
            inode <= 0)
        {
            return null;
        }

        var address = ParseProcNetworkAddress(localAddress[0], isIpv6);
        return address is null ? null : new ListeningSocket(inode, protocol, address, port);
    }

    private static IPAddress? ParseProcNetworkAddress(string value, bool isIpv6)
    {
        var expectedLength = isIpv6 ? 32 : 8;
        if (value.Length != expectedLength)
        {
            return null;
        }

        try
        {
            var source = Convert.FromHexString(value);
            if (!isIpv6)
            {
                Array.Reverse(source);
                return new IPAddress(source);
            }

            var address = new byte[16];
            for (var word = 0; word < 4; word++)
            {
                for (var index = 0; index < 4; index++)
                {
                    address[(word * 4) + index] = source[(word * 4) + (3 - index)];
                }
            }

            return new IPAddress(address);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadInterfaceNamesByAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(networkInterface => networkInterface
                    .GetIPProperties()
                    .UnicastAddresses
                    .Select(address => new
                    {
                        Address = AddressKey(address.Address),
                        networkInterface.Name
                    }))
                .GroupBy(item => item.Address, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => string.Join(", ", group
                        .Select(item => item.Name)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal)),
                    StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    internal static string ResolveInterfaceName(
        IPAddress address,
        IReadOnlyDictionary<string, string> interfaceNamesByAddress)
    {
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return "all interfaces";
        }

        if (IPAddress.IsLoopback(address))
        {
            return "lo";
        }

        return interfaceNamesByAddress.GetValueOrDefault(AddressKey(address), "specific address");
    }

    private static string AddressKey(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return Convert.ToHexString(normalized.GetAddressBytes());
    }

    private static ProcessPortReadResult ReadPrivilegedProcessListeningPorts(
        int processId,
        IReadOnlyDictionary<long, LocalProcessListeningPortMetric> listeningSockets,
        IReadOnlyDictionary<int, IReadOnlySet<long>> socketInodesByProcessId)
    {
        if (!socketInodesByProcessId.TryGetValue(processId, out var socketInodes))
        {
            return new ProcessPortReadResult(true, []);
        }

        var ports = socketInodes
            .Where(listeningSockets.ContainsKey)
            .Select(inode => listeningSockets[inode])
            .Distinct()
            .OrderBy(port => port.Port)
            .ThenBy(port => port.Protocol, StringComparer.Ordinal)
            .ThenBy(port => port.Address, StringComparer.Ordinal)
            .ToArray();
        return new ProcessPortReadResult(true, ports);
    }

    private static ProcessPortReadResult ReadProcessListeningPorts(
        string processDirectory,
        IReadOnlyDictionary<long, LocalProcessListeningPortMetric> listeningSockets)
    {
        if (listeningSockets.Count == 0)
        {
            return new ProcessPortReadResult(true, []);
        }

        try
        {
            var socketInodes = new HashSet<long>();
            foreach (var descriptorPath in Directory.EnumerateFileSystemEntries(Path.Combine(processDirectory, "fd")))
            {
                string? target;
                try
                {
                    target = new FileInfo(descriptorPath).LinkTarget;
                }
                catch
                {
                    continue;
                }

                if (TryParseSocketInode(target, out var inode) && listeningSockets.ContainsKey(inode))
                {
                    socketInodes.Add(inode);
                }
            }

            var ports = socketInodes
                .Select(inode => listeningSockets[inode])
                .Distinct()
                .OrderBy(port => port.Port)
                .ThenBy(port => port.Protocol, StringComparer.Ordinal)
                .ThenBy(port => port.Address, StringComparer.Ordinal)
                .ToArray();
            return new ProcessPortReadResult(true, ports);
        }
        catch (IOException)
        {
            return new ProcessPortReadResult(false, []);
        }
        catch (UnauthorizedAccessException)
        {
            return new ProcessPortReadResult(false, []);
        }
    }

    internal static bool TryParseSocketInode(string? target, out long inode)
    {
        const string prefix = "socket:[";
        inode = 0;
        return target is not null &&
               target.StartsWith(prefix, StringComparison.Ordinal) &&
               target.EndsWith(']') &&
               long.TryParse(
                   target.AsSpan(prefix.Length, target.Length - prefix.Length - 1),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out inode) &&
               inode > 0;
    }

    private static string ReadOperatingSystemName()
    {
        try
        {
            var line = File.ReadLines("/etc/os-release")
                .FirstOrDefault(item => item.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
            return line is null
                ? RuntimeInformation.OSDescription
                : line["PRETTY_NAME=".Length..].Trim().Trim('"');
        }
        catch
        {
            return RuntimeInformation.OSDescription;
        }
    }

    private static string? ReadTrimmedText(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string MapProcessState(char value) => value switch
    {
        'R' => "Running",
        'S' => "Sleeping",
        'D' => "Disk wait",
        'Z' => "Zombie",
        'T' or 't' => "Stopped",
        'I' => "Idle",
        'X' or 'x' => "Dead",
        _ => value.ToString()
    };

    private static double? SectorRate(long current, long? previous, double? elapsedSeconds)
    {
        var sectorsPerSecond = Rate(current, previous, elapsedSeconds);
        return sectorsPerSecond * 512d;
    }

    private static double? Rate(long? current, long? previous, double? elapsedSeconds)
    {
        if (!current.HasValue || !previous.HasValue || !elapsedSeconds.HasValue || elapsedSeconds <= 0)
        {
            return null;
        }

        var delta = current.Value - previous.Value;
        return delta < 0 ? null : delta / elapsedSeconds.Value;
    }

    private static long NonNegativeDelta(long current, long previous) =>
        Math.Max(0, current - previous);

    private static double Percent(long part, long total) =>
        total <= 0 ? 0 : Math.Clamp(part / (double)total * 100d, 0d, 100d);

    private static long ParseLong(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static double ParseDouble(string? value) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static long ResolveClockTicksPerSecond()
    {
        try
        {
            var value = sysconf(ScClockTicks);
            return value > 0 ? value : 100;
        }
        catch
        {
            return 100;
        }
    }

    [DllImport("libc")]
    private static extern long sysconf(int name);

    internal readonly record struct CpuCounters(
        long User,
        long Nice,
        long System,
        long Idle,
        long IoWait,
        long Irq,
        long SoftIrq,
        long Steal)
    {
        public long Total => User + Nice + System + Idle + IoWait + Irq + SoftIrq + Steal;
    }

    internal sealed record NetworkCounters(
        string Name,
        long ReceiveBytes,
        long ReceivePackets,
        long ReceiveErrors,
        long ReceiveDrops,
        long TransmitBytes,
        long TransmitPackets,
        long TransmitErrors,
        long TransmitDrops);

    internal sealed record DiskCounters(
        string Name,
        long Reads,
        long ReadSectors,
        long Writes,
        long WrittenSectors,
        long IoMilliseconds);

    internal sealed record ListeningSocket(
        long Inode,
        string Protocol,
        IPAddress Address,
        int Port);

    private sealed record SocketSnapshot(
        IReadOnlyDictionary<long, LocalProcessListeningPortMetric> ListeningSockets,
        int TcpEstablishedConnectionCount);

    private sealed record PrivilegedSocketOwnership(
        bool IsAvailable,
        IReadOnlyDictionary<int, IReadOnlySet<long>> SocketInodesByProcessId)
    {
        public static PrivilegedSocketOwnership Unavailable { get; } = new(
            false,
            new Dictionary<int, IReadOnlySet<long>>());
    }

    private sealed record ProcessPortReadResult(
        bool IsAccessible,
        IReadOnlyList<LocalProcessListeningPortMetric> Ports);

    private sealed record RawProcess(
        int ProcessId,
        int ParentProcessId,
        string Name,
        string User,
        char State,
        long CpuTicks,
        long StartTimeTicks,
        long ResidentMemoryBytes,
        int ThreadCount,
        long? ReadBytes,
        long? WriteBytes,
        bool ListeningPortsAccessible,
        IReadOnlyList<LocalProcessListeningPortMetric> ListeningPorts,
        string CommandLine);

    private sealed record RawSample(
        DateTimeOffset CapturedAtUtc,
        CpuCounters Cpu,
        LocalLoadMetric Load,
        LocalMemoryMetric Memory,
        LocalFileSystemMetric RootFileSystem,
        TimeSpan Uptime,
        IReadOnlyList<RawProcess> Processes,
        IReadOnlyList<NetworkCounters> NetworkInterfaces,
        IReadOnlyList<DiskCounters> Disks,
        int TcpEstablishedConnectionCount,
        bool ListeningPortOwnershipIsPrivileged);
}
