// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace LinuxMadeSane.Web.Services;

public sealed class ApplicationReleaseChannel(string path)
{
    public string Read(string fallback)
    {
        if (!File.Exists(path)) return Normalize(fallback);
        try { return Normalize(JsonSerializer.Deserialize<ChannelState>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Channel); }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return "stable"; }
    }
    public async Task SaveAsync(string channel, CancellationToken token)
    {
        channel = Normalize(channel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new ChannelState(channel), new JsonSerializerOptions(JsonSerializerDefaults.Web)), token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string Normalize(string? channel) => channel switch
    {
        null or "" or "stable" => "stable",
        "development" => "development",
        _ => throw new ArgumentException("Choose stable or development.")
    };
    public static string Url(string url, string channel, string? version = null)
    {
        var uri = new Uri(url);
        var values = QueryHelpers.ParseQuery(uri.Query);
        values["channel"] = Normalize(channel);
        if (version is not null) values["version"] = version;
        return QueryHelpers.AddQueryString(uri.GetLeftPart(UriPartial.Path), values) + uri.Fragment;
    }
    private sealed record ChannelState(string Channel);
}
