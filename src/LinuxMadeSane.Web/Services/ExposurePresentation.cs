// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Net;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Infrastructure;

namespace LinuxMadeSane.Web.Services;

public sealed record ExposureServiceGroup(string Service, IReadOnlyList<ExposureEntry> Entries,
    IReadOnlyList<string> Bindings, IReadOnlyList<string> Routes, string AccessSummary);

// Presentation only: preserve observations and evidence; never infer Internet reachability.
public static class ExposurePresentation
{
    public static IReadOnlyList<ExposureServiceGroup> Group(ExposureSnapshot snapshot)
    {
        var published = snapshot.Entries.Where(x => x.Method == "Docker published port").ToArray();
        string Name(ExposureEntry entry)
        {
            if (entry.Service.Equals("docker-proxy", StringComparison.OrdinalIgnoreCase))
            {
                var candidates = published.Where(x => SameBinding(entry, x)).Select(x => x.Service).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (candidates.Length == 1) return candidates[0];
            }
            return string.IsNullOrWhiteSpace(entry.Service) ? "Unnamed service" : entry.Service.Trim();
        }
        return snapshot.Entries.GroupBy(Name, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToArray();
                var bindings = entries.Select(x => x.Local).Distinct().Order().ToArray();
                var routes = entries.SelectMany(x => x.External.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct().Order().ToArray();
                var listeners = entries.Where(x => x.Method.Contains("listener", StringComparison.OrdinalIgnoreCase) || x.Method == "Docker published port").ToArray();
                var all = listeners.Any(x => Binding(x.Local).Address is "0.0.0.0" or "::" or "*" or "any");
                var loopback = listeners.Length > 0 && listeners.All(x => IPAddress.TryParse(Binding(x.Local).Address, out var address) && IPAddress.IsLoopback(address));
                var summary = all ? "All interfaces · Internet access unverified" : loopback ?
                    routes.Length > 0 ? "Loopback listener + Edge Gateway route" : "Direct access from this host only" :
                    listeners.Length == 0 ? "Edge Gateway route · reachability unverified" : "Listed addresses · Internet access unverified";
                return new ExposureServiceGroup(group.Key, entries, bindings, routes, summary);
            }).ToArray();
    }
    private static (string Address, string Port, string Protocol) Binding(string value)
    {
        var match = Regex.Match(value, @"^(?:\[(?<ip>[^\]]+)\]|(?<ip>[^:]+)):(?<port>\d+)(?:->.*?/(?<protocol>tcp|udp))?$", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups["ip"].Value, match.Groups["port"].Value, match.Groups["protocol"].Value.ToLowerInvariant()) : ("", "", "");
    }
    private static bool SameBinding(ExposureEntry listener, ExposureEntry container)
    {
        var left = Binding(listener.Local); var right = Binding(container.Local);
        var protocol = listener.Reasoning.Any(x => new[] { right.Protocol, right.Protocol + "4", right.Protocol + "6" }
            .Any(protocol => x.StartsWith("Observed " + protocol + " listening socket.", StringComparison.OrdinalIgnoreCase)));
        return left.Port.Length > 0 && left.Port == right.Port && left.Address == right.Address && right.Protocol.Length > 0 && protocol;
    }
}
