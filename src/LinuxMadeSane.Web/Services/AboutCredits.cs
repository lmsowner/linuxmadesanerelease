// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;

namespace LinuxMadeSane.Web.Services;

public static class AboutCredits
{
    public const string WebsiteUrl = "https://www.linuxmadesane.com";
    public const string GitHubUrl = "https://github.com/lmsowner/linuxmadesanerelease";

    public static IReadOnlyList<AboutPackage> Packages { get; } =
        JsonSerializer.Deserialize<AboutPackage[]>(ReadResource("about-packages.json"))!
            .Concat(JsonSerializer.Deserialize<AboutPackage[]>(ReadResource("about-browser-packages.json"))!)
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<AboutNotice> Notices { get; } = new[]
    {
        new AboutNotice("LMS license", "LICENSE"),
        new AboutNotice("Copyright and attribution", "NOTICE"),
        new AboutNotice("Third-party notices", "THIRD-PARTY-NOTICES.md"),
        new AboutNotice("Commercial licensing", "COMMERCIAL-LICENSE.md"),
        new AboutNotice("Trademarks", "TRADEMARKS.md")
    };

    private static string ReadResource(string name)
    {
        using var stream = typeof(AboutCredits).Assembly.GetManifestResourceStream($"Lms.About.{name}")
            ?? throw new InvalidOperationException($"Missing About resource: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public sealed record AboutNotice(string Title, string FileName)
    {
        public string Text { get; } = ReadResource(FileName);
    }
}

public sealed record AboutPackage(
    string Name, string Version, string[] Groups, string License, string LicenseUrl,
    string Url, string PackageUrl, string Authors, string Description);
