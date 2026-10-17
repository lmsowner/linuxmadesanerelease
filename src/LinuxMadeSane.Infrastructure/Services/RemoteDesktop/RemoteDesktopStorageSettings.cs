// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Infrastructure.Services.RemoteDesktop;
public sealed record RemoteDesktopStorageSettings(string RootDirectory)
{
    public string ConfigurationDirectory => Path.Combine(RootDirectory, "config");
    public string RuntimeDirectory => Path.Combine(RootDirectory, "run");
}
