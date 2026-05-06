namespace LinuxMadeSane.Application.Services;

internal static class ManagedHostPathSupport
{
    public static string NormalizeWorkingDirectory(string? workingDirectory, string username)
    {
        var defaultWorkingDirectory = GetDefaultWorkingDirectory(username);
        var trimmedDirectory = workingDirectory?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedDirectory))
        {
            return defaultWorkingDirectory;
        }

        if (trimmedDirectory is "~" or "~/")
        {
            return defaultWorkingDirectory;
        }

        if (trimmedDirectory.StartsWith("~/", StringComparison.Ordinal))
        {
            return defaultWorkingDirectory.TrimEnd('/') + "/" + trimmedDirectory[2..].TrimStart('/');
        }

        return trimmedDirectory;
    }

    public static string QuoteShellArgument(string value) =>
        value.Length == 0
            ? "''"
            : $"'{value.Replace("'", "'\"'\"'")}'";

    public static string? FirstNonEmptyLine(params string[] values) =>
        values
            .SelectMany(value => (value ?? string.Empty).Split('\n'))
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

    private static string GetDefaultWorkingDirectory(string username)
    {
        if (string.Equals(username, "root", StringComparison.OrdinalIgnoreCase))
        {
            return "/root";
        }

        return string.IsNullOrWhiteSpace(username)
            ? "/home"
            : $"/home/{username}";
    }
}
