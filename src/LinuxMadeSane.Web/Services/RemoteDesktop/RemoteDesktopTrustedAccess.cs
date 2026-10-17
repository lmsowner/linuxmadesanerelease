// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web.Services.RemoteDesktop;
// Only the internal bridge enters this scope, after authorising a live LMS session.
public sealed class RemoteDesktopTrustedAccess
{
    private readonly AsyncLocal<Guid?> user = new();
    internal Guid? User => user.Value;
    internal IDisposable Enter(Guid id) { var previous = user.Value; user.Value = id; return new Exit(() => user.Value = previous); }
    private sealed class Exit(Action action) : IDisposable { public void Dispose() => action(); }
}
