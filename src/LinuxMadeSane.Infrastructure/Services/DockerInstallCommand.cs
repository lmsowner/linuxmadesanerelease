// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Interfaces;

namespace LinuxMadeSane.Infrastructure.Services;

public static class DockerInstallCommand
{
    public static ContainerInstall Parse(string input)
    {
        if (input.Length > 32768) throw new InvalidOperationException("Use an install command under 32 KiB.");
        var commands = Tokenize(input);
        string? pulled = null;
        var createdVolumes = new List<string>();
        ContainerInstall? install = null;
        foreach (var words in commands)
        {
            if (words.FirstOrDefault() == "sudo") words.RemoveAt(0);
            if (words.FirstOrDefault() != "docker" || install != null)
                throw new InvalidOperationException("Paste one docker run command. A matching docker pull or docker volume create can precede it.");
            if (words.Count == 3 && words[1] == "pull" && pulled == null) pulled = words[2];
            else if (words.Count == 4 && words[1] == "volume" && words[2] == "create") createdVolumes.Add(words[3]);
            else
            {
                if (words.ElementAtOrDefault(1) == "container") words.RemoveAt(1);
                if (words.ElementAtOrDefault(1) != "run") throw new InvalidOperationException("Expected a docker run command.");
                install = ParseRun(words);
            }
        }
        if (install == null) throw new InvalidOperationException("Paste a docker run command with an image.");
        if ((pulled != null && pulled != install.Image) || createdVolumes.Any(v => v != install.Volume && !install.AdditionalVolumes.Any(m => m.Name == v)))
            throw new InvalidOperationException("The setup commands must use the image and volume from docker run.");
        return install;
    }

    private static ContainerInstall ParseRun(List<string> words)
    {
        string name = "", restart = "no";
        var volumes = new List<ContainerVolume>();
        var ports = new List<DockerPortBinding>();
        var environment = new Dictionary<string,string>(StringComparer.Ordinal);
        var i = 2;
        while (i < words.Count && words[i].StartsWith('-'))
        {
            var word = words[i++];
            if (word == "--") break;
            if (word is "-d" or "--detach") continue;
            var pair = word.Split('=', 2);
            var flag = pair[0];
            string? value = pair.ElementAtOrDefault(1);
            if (word.Length > 2 && word[0] == '-' && word[1] is 'p' or 'e' or 'v')
            {
                flag = word[..2];
                value = word[2..];
            }
            if (flag is not ("--name" or "--restart" or "-p" or "--publish" or "-e" or "--env" or "-v" or "--volume" or "--mount" or "--network"))
                throw new InvalidOperationException(Regex.IsMatch(flag, @"\A--?[a-zA-Z0-9-]+\z")
                    ? $"The installer does not support {flag}." : "The installer could not read a Docker option.");
            value ??= i < words.Count ? words[i++] : throw new InvalidOperationException($"Enter a value for {flag}.");
            switch (flag)
            {
                case "--name": name = value; break;
                case "--restart":
                    if (!ValidRestartPolicy(value)) throw new InvalidOperationException("Unrecognised Docker restart policy.");
                    restart = value; break;
                case "-p": case "--publish":
                    ports.Add(ParsePort(value)); break;
                case "--network":
                    if (value is not ("bridge" or "default")) throw new InvalidOperationException("The installer supports Docker's default bridge network.");
                    break;
                case "--mount": case "-v": case "--volume":
                    var parts = flag == "--mount" ? NamedMount(value) : value.Split(':');
                    if (parts.Length is < 2 or > 3 || parts.Length == 3 && parts[2] is not ("ro" or "rw"))
                        throw new InvalidOperationException("Use a named volume such as web-data:/data or settings:/config:ro.");
                    var volume = new ContainerVolume(parts[0], parts[1], parts.Length == 3 && parts[2] == "ro");
                    LocalContainerService.ValidateVolume(volume);
                    if (volumes.Any(v => v.Path == volume.Path)) throw new InvalidOperationException("Use a different container path for each volume.");
                    volumes.Add(volume); break;
                default:
                    var entry = value.Split('=', 2);
                    if (entry.Length != 2 || !Regex.IsMatch(entry[0], @"\A[A-Za-z_][A-Za-z0-9_]*\z"))
                        throw new InvalidOperationException("Set each environment variable using NAME=value.");
                    environment[entry[0]] = entry[1]; break;
            }
        }
        if (i == words.Count || !LocalContainerService.ValidImage(words[i])) throw new InvalidOperationException("Enter a valid Docker image after the options.");
        var image = words[i++];
        if (name.Length == 0) name = image.Split('@')[0].Split('/')[^1].Split(':')[0];
        LocalContainerService.ValidatePorts(ports);
        var primary = volumes.FirstOrDefault();
        return new(name, image, environment, primary?.Name ?? "", primary?.Path ?? "", ports)
        {
            VolumeReadOnly = primary?.ReadOnly ?? false, AdditionalVolumes = volumes.Skip(1).ToArray(),
            RestartPolicy = restart, Command = words.Skip(i).ToArray()
        };
    }

    private static string[] NamedMount(string value)
    {
        string? source = null, target = null;
        bool readOnly = false;
        foreach (var part in value.Split(','))
        {
            var field = part.Split('=', 2);
            switch (field)
            {
                case ["type", "volume"]: break;
                case ["src" or "source", var name]: source = name; break;
                case ["target" or "dst" or "destination", var path]: target = path; break;
                case ["readonly" or "ro"]: readOnly = true; break;
                default: throw new InvalidOperationException("Use a named volume with source, target and optional readonly.");
            }
        }
        if (source == null || target == null) throw new InvalidOperationException("A named volume mount needs a source and target.");
        return [source, target, readOnly ? "ro" : "rw"];
    }

    internal static bool ValidRestartPolicy(string value) => value is "no" or "always" or "unless-stopped" or "on-failure" ||
        value.StartsWith("on-failure:", StringComparison.Ordinal) && int.TryParse(value.AsSpan(11), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    private static DockerPortBinding ParsePort(string value)
    {
        var m = Regex.Match(value, @"\A(?:(?<address>\[[0-9A-Fa-f:.]+\]|[0-9.]+):)?(?<host>[0-9]{1,5}):(?<target>[0-9]{1,5})(?:/(?<protocol>tcp|udp))?\z");
        if (!m.Success) throw new InvalidOperationException("Use a published port such as 8080:80 or 127.0.0.1:5353:53/udp.");
        return new(int.Parse(m.Groups["host"].Value), int.Parse(m.Groups["target"].Value),
            m.Groups["protocol"].Success ? m.Groups["protocol"].Value : "tcp",
            m.Groups["address"].Success ? m.Groups["address"].Value.Trim('[', ']') : "0.0.0.0");
    }

    // Recognise quoting and continuations without running a shell or expanding host variables.
    private static List<List<string>> Tokenize(string input)
    {
        var lines = new List<List<string>>();
        var words = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        bool started = false;
        input = input.Replace("\r\n", "\n");
        void EndWord()
        {
            if (!started) return;
            words.Add(token.ToString()); token.Clear(); started = false;
        }
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '\0') throw new InvalidOperationException("Paste plain text.");
            if (quote != '\'' && c == '\\')
            {
                if (++i >= input.Length) throw new InvalidOperationException("The final continuation is incomplete.");
                var next = input[i];
                if (next == '\n') continue;
                if (quote == '"' && next is not ('"' or '\\' or '$' or '`')) token.Append('\\');
                token.Append(next); started = true; continue;
            }
            if (quote == '\0' && c == '#' && !started)
            {
                while (i+1 < input.Length && input[i+1] != '\n') i++;
                continue;
            }
            if (quote == '\0' && char.IsWhiteSpace(c))
            {
                EndWord();
                if (c == '\n' && words.Count != 0) { lines.Add(words); words = []; }
                continue;
            }
            if (quote == '\0' && c is '\'' or '"') { quote = c; started = true; continue; }
            if (quote != '\0' && c == quote) { quote = '\0'; continue; }
            if ((quote != '\'' && c is '$' or '`') || (quote == '\0' && c is ';' or '&' or '|' or '<' or '>' or '(' or ')' or '*' or '?'))
                throw new InvalidOperationException("Use literal Docker arguments. Shell expansion, command chaining and redirection are unsupported.");
            token.Append(c); started = true;
        }
        if (quote != '\0') throw new InvalidOperationException("Close the quoted Docker argument.");
        EndWord();
        if (words.Count != 0) lines.Add(words);
        return lines;
    }
}
