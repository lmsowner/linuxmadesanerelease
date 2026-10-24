// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Text.Json;

namespace LinuxMadeSane.Web.Services;

public static class AboutCredits
{
    public const string WebsiteUrl = "https://www.linuxmadesane.com";
    public const string PurchaseUrl = WebsiteUrl + "/contact?topic=licensing";
    public const string SupportAgreementUrl = WebsiteUrl + "/contact?topic=support";
    public const string GitHubUrl = "https://github.com/lmsowner/linuxmadesanerelease";

    public static IReadOnlyList<AboutPackage> Packages { get; } =
        JsonSerializer.Deserialize<AboutPackage[]>(ReadResource("about-packages.json"))!
            .Concat(JsonSerializer.Deserialize<AboutPackage[]>(ReadResource("about-browser-packages.json"))!)
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<AboutNotice> Notices { get; } = new[]
    {
        new AboutNotice("LMS licence", "LICENSE"),
        new AboutNotice("Licensing policy", "LICENSING.md"),
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
