// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web;

internal static class SftpCSharpExamples
{
    internal const string Connect = """
        using System.Net;
        using System.Net.Http.Headers;
        using System.Net.Http.Json;
        using System.Text.Json.Nodes;

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", File.ReadAllText("token.txt").TrimEnd('\r', '\n'));

        async Task<byte[]> Call(HttpMethod method, string url, JsonObject? body = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body != null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }
        async Task<JsonNode> Get(string url) =>
            JsonNode.Parse(await Call(HttpMethod.Get, url)) ?? throw new InvalidDataException("Expected JSON");

        Console.WriteLine(await Get("https://sftp.example.net:5443/api/sftpserver/"));
        var status = await Get("https://sftp.example.net:5443/api/sftpserver/server/status");
        if (!(bool)status["managed"]! || !(bool)status["sshActive"]! || !(bool)status["configurationValid"]!)
            throw new InvalidOperationException("Check managed SFTP setup and SSH in LMS.");
        """;

    internal const string Provision = """
        async Task<bool> Owned() =>
            (bool)(await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user/exists"))["exists"]!;
        if (!await Owned())
        {
            var mode = "PublicKeyOnly"; // Or PasswordOnly or PasswordAndPublicKey
            var body = new JsonObject { ["username"] = "import_user", ["authenticationMode"] = mode };
            if (mode != "PublicKeyOnly") body["password"] = File.ReadAllText("customer-password.txt").TrimEnd('\r', '\n');
            if (mode != "PasswordOnly") body["publicKey"] = File.ReadAllText("customer.pub").Trim();
            try { await Call(HttpMethod.Post, "https://sftp.example.net:5443/api/sftpserver/users", body); }
            catch (HttpRequestException e) when (e.StatusCode is null or HttpStatusCode.Conflict)
            {
                if (!await Owned()) throw;
            }
            catch (TaskCanceledException) { if (!await Owned()) throw; }
        }
        Console.WriteLine(await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user"));
        """;

    internal const string RegisterKey = """
        var publicKey = File.ReadAllText("customer.pub").Trim();
        await Call(HttpMethod.Put, "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key",
            new JsonObject { ["publicKey"] = publicKey });
        Console.WriteLine(await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user"));
        """;

    internal const string ChangeMode = """
        var target = "PasswordAndPublicKey"; // Or PublicKeyOnly or PasswordOnly
        var account = await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user");
        var current = (string)account["authenticationMode"]!;
        if (current != target)
        {
            if (target != "PasswordOnly" && !(bool)account["hasPublicKey"]!)
                await Call(HttpMethod.Put, "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key",
                    new JsonObject { ["publicKey"] = File.ReadAllText("customer.pub").Trim() });
            var settings = new JsonObject { ["authenticationMode"] = target };
            if (current == "PublicKeyOnly")
                settings["password"] = File.ReadAllText("customer-password.txt").TrimEnd('\r', '\n');
            await Call(HttpMethod.Put, "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth", settings);
        }
        account = await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user");
        if ((string)account["authenticationMode"]! != target || !(bool)account["enabled"]!)
            throw new InvalidOperationException("Review the account before sending login instructions.");
        Console.WriteLine(await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user/auth-guide"));
        """;

    internal const string Import = """
        bool deleteAfterDownload = false;
        var entries = (await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user/files")).AsArray();
        if (entries.Any(e => (string?)e!["name"] == "orders-1042.csv" && !(bool)e["isDirectory"]!))
        {
            var bytes = await Call(HttpMethod.Get, "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=orders-1042.csv");
            if (!File.Exists("orders-1042.csv"))
            {
                using var output = new FileStream("orders-1042.csv", FileMode.CreateNew);
                output.Write(bytes);
            }
            if (!bytes.SequenceEqual(File.ReadAllBytes("orders-1042.csv")))
                throw new IOException("Local copy differs. Source retained.");
            if (deleteAfterDownload)
                await Call(HttpMethod.Delete, "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=orders-1042.csv");
        }
        """;

    internal const string Offboard = """
        Console.Write("Action for import_user (disable, enable or DELETE import_user): ");
        switch (Console.ReadLine())
        {
            case "disable": await Call(HttpMethod.Put, "https://sftp.example.net:5443/api/sftpserver/users/import_user/disable"); break;
            case "enable": await Call(HttpMethod.Put, "https://sftp.example.net:5443/api/sftpserver/users/import_user/enable"); break;
            case "DELETE import_user": await Call(HttpMethod.Delete, "https://sftp.example.net:5443/api/sftpserver/users/import_user"); break;
            default: Console.WriteLine("No change made."); break;
        }
        """;
}
