// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web;

using Entry = SftpApiReference.Entry;

internal static class SftpRequestExamples
{
    internal const string JsonInstructions = "Save this body as request.json, replace the placeholders with your values and restrict access to the file.";
    internal sealed record Language(string Label, Func<Entry, string> Render);
    internal static readonly Language[] Languages =
    [
        new("curl", Curl), new("Python", Python),
        new("C#", CSharp), new("Java", Java)
    ];

    private static string Curl(Entry e)
    {
        var body = e.Json is not null ? " --json \"@request.json\"" :
            e.Upload is not null ? $" -F \"file=@{e.Upload}\"" : "";
        var output = e.Download is null ? "" : $" -o \"{e.Download}\"";
        return $"""
            curl --silent --show-error --fail -X {e.Method} -H "Authorization: Bearer YOUR_API_TOKEN"{body}{output} "{e.ExampleUrl}"
            """;
    }

    private static string Python(Entry e)
    {
        var arguments = e.Json is not null ? ", json=json.loads(Path('request.json').read_text())" :
            e.Upload is not null ? $", files={{'file': ('{e.Upload}', upload)}}" : "";
        var call = $"response = requests.{e.Method.ToLowerInvariant()}(url, headers=headers{arguments}, timeout=60)";
        if (e.Upload is not null)
            call = $"with open('{e.Upload}', 'rb') as upload:\n    {call}";
        var result = e.Download is not null ? $"Path('{e.Download}').write_bytes(response.content)" :
            "print(response.text or response.status_code)";
        return $"""
            {(e.Json is not null ? "import json\n" : "")}{(e.Json is not null || e.Download is not null ? "from pathlib import Path\n" : "")}import requests

            url = '{e.ExampleUrl}'
            headers = dict(Authorization="Bearer YOUR_API_TOKEN")
            {call}
            response.raise_for_status()
            {result}
            """;
    }

    private static string CSharp(Entry e)
    {
        var body = e.Json is not null ? """
            request.Content = new StringContent(await File.ReadAllTextAsync("request.json"), System.Text.Encoding.UTF8, "application/json");
            """ : e.Upload is not null ? $"""
            var form = new MultipartFormDataContent();
            form.Add(new StreamContent(File.OpenRead("{e.Upload}")), "file", "{e.Upload}");
            request.Content = form;
            """ : "";
        var result = e.Download is not null ? $"await File.WriteAllBytesAsync(\"{e.Download}\", await response.Content.ReadAsByteArrayAsync());" :
            "Console.WriteLine((int)response.StatusCode);\nConsole.WriteLine(await response.Content.ReadAsStringAsync());";
        return $"""
            using System;
            using System.IO;
            using System.Net.Http;
            using System.Net.Http.Headers;

            using var client = new HttpClient();
            var url = "{e.ExampleUrl}";
            using var request = new HttpRequestMessage(new HttpMethod("{e.Method}"), url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");
            {body}
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            {result}
            """;
    }

    private static string Java(Entry e)
    {
        var body = e.Json is not null ? """
            builder.header("Content-Type", "application/json");
            var body = BodyPublishers.ofFile(Path.of("request.json"));
            """ : e.Upload is not null ? $$"""
            var boundary = java.util.UUID.randomUUID().toString();
            builder.header("Content-Type", "multipart/form-data; boundary=" + boundary);
            var body = BodyPublishers.concat(
                BodyPublishers.ofString("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{{e.Upload}}\"\r\nContent-Type: application/octet-stream\r\n\r\n"),
                BodyPublishers.ofFile(Path.of("{{e.Upload}}")),
                BodyPublishers.ofString("\r\n--" + boundary + "--\r\n"));
            """ : "var body = BodyPublishers.noBody();";
        var handler = e.Download is not null ? "ofByteArray()" : "ofString()";
        var result = e.Download is not null ? $"Files.write(Path.of(\"{e.Download}\"), response.body());" :
            "System.out.println(response.statusCode());\nSystem.out.println(response.body());";
        return $$"""
            import java.net.URI;
            import java.net.http.*;
            import java.net.http.HttpRequest.BodyPublishers;
            import java.nio.file.*;

            class Example {
                public static void main(String[] args) throws Exception {
                    var url = "{{e.ExampleUrl}}";
                    var builder = HttpRequest.newBuilder(URI.create(url))
                        .header("Authorization", "Bearer YOUR_API_TOKEN");
                    {{body.Replace("\n", "\n        ")}}
                    var request = builder.method("{{e.Method}}", body).build();
                    var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.{{handler}});
                    if (response.statusCode() / 100 != 2)
                        throw new java.io.IOException("HTTP " + response.statusCode());
                    {{result.Replace("\n", "\n        ")}}
                }
            }
            """;
    }
}
