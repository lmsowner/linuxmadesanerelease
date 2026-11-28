// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web;

internal sealed record SftpIntegrationExamples(string Language, string Setup, string Connect, string Provision, string RegisterKey, string ChangeMode, string Import, string Offboard)
{
    internal static readonly SftpIntegrationExamples[] All =
    [
        new("C#", "Use a .NET console project with implicit usings enabled. Place the connection code in Program.cs, followed by the workflow you want to run. The examples read token.txt and, when needed, customer-password.txt from a private working directory. In an application, obtain those secrets from its secret store.",
            SftpCSharpExamples.Connect, SftpCSharpExamples.Provision, SftpCSharpExamples.RegisterKey,
            SftpCSharpExamples.ChangeMode, SftpCSharpExamples.Import, SftpCSharpExamples.Offboard),
        new("Java", "Use Java 17 or later and Gson (com.google.code.gson:gson). Save SftpClient.java and the selected workflow class in the same project and package. Run SftpClient to check the connection, then run the workflow’s main method. Supply token.txt and, when needed, customer-password.txt in a private working directory or read them from your application’s secret store.",
            SftpJavaExamples.Connect, SftpJavaExamples.Provision, SftpJavaExamples.RegisterKey,
            SftpJavaExamples.ChangeMode, SftpJavaExamples.Import, SftpJavaExamples.Offboard),
        new("curl", "Use Bash, curl 7.82 or later, and jq. Put the connection block first in a script, followed by the workflow to run. Start it with Bash. The token prompt hides your input.",
            SftpBashExamples.Connect, SftpBashExamples.Provision, SftpBashExamples.RegisterKey,
            SftpBashExamples.ChangeMode, SftpBashExamples.Import, SftpBashExamples.Offboard),
        new("Python", "Use Python 3 with the requests package. Put the connection code first in your script, followed by the workflow to run. The token prompt hides your input.",
            SftpPythonExamples.Connect, SftpPythonExamples.Provision, SftpPythonExamples.RegisterKey,
            SftpPythonExamples.ChangeMode, SftpPythonExamples.Import, SftpPythonExamples.Offboard),
    ];
}
