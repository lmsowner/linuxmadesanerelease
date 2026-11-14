// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Diagnostics;
using System.Globalization;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using Renci.SshNet;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed partial class PrivilegedFileBrowsingService
{
    // A streaming receiver: fixed-size reads, destination-side partial file, explicit commit.
    // EOF before the commit byte is cancellation, never a successfully uploaded file.
    internal const string ReceiveUploadScript = """
        import ctypes, errno, os, sys
        partial, destination, expected = sys.argv[1], sys.argv[2], int(sys.argv[3])
        created = False
        try:
            if os.path.lexists(destination):
                raise FileExistsError('The destination already exists. Choose another name or remove it first.')
            fd = os.open(partial, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            created = True
            with os.fdopen(fd, 'wb') as output:
                print('READY', flush=True)
                remaining = expected
                while remaining:
                    chunk = sys.stdin.buffer.read(min(131072, remaining))
                    if not chunk:
                        raise EOFError('Upload cancelled or incomplete.')
                    output.write(chunk)
                    remaining -= len(chunk)
                if sys.stdin.buffer.read(1) != b'\x01':
                    raise EOFError('Upload cancelled or incomplete.')
                output.flush()
                os.fsync(output.fileno())
            libc = ctypes.CDLL(None, use_errno=True)
            rename = getattr(libc, 'renameat2', None)
            if rename is not None:
                result = rename(-100, os.fsencode(partial), -100, os.fsencode(destination), 1)
                if result != 0:
                    code = ctypes.get_errno()
                    if code not in (errno.ENOSYS, errno.EINVAL, errno.EOPNOTSUPP):
                        raise OSError(code, os.strerror(code))
                    os.link(partial, destination)
                    os.unlink(partial)
            else:
                os.link(partial, destination)
                os.unlink(partial)
            created = False
        finally:
            if created:
                os.unlink(partial)
        """;

    public async Task<IManagedHostFileUpload> BeginUploadAsync(ManagedHost host, string destinationPath,
        long totalBytes, ManagedHostConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        var destination = NormalizeMutablePath(destinationPath);
        var partial = Path.GetDirectoryName(destination)!.TrimEnd('/') + $"/.lms-upload-{Guid.NewGuid():N}.partial";
        string[] arguments = ["-c", ReceiveUploadScript, partial, destination, totalBytes.ToString(CultureInfo.InvariantCulture)];
        Process? process = null;
        SshClient? client = null;
        SshCommand? command = null;
        Stream? input = null;
        StreamReader? output = null;
        Task<string>? errors = null;
        Task? execution = null;
        var committed = false;
        var cleaned = false;

        async ValueTask Cleanup()
        {
            if (cleaned) return;
            cleaned = true;
            try
            {
                if (input is not null) await input.DisposeAsync();
                if (execution is not null)
                {
                    try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch
                    {
                        if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
                        command?.CancelAsync();
                    }
                }
                if (!committed)
                {
                    // Also handles abrupt process/channel loss. Only our random partial is removed.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await ExecutePythonAsync(host, profile,
                        "import os,sys\np=sys.argv[1]\nif os.path.lexists(p): os.unlink(p)", [partial], null, timeout.Token);
                }
            }
            finally
            {
                output?.Dispose();
                command?.Dispose();
                client?.Dispose();
                process?.Dispose();
            }
        }

        try
        {
            if (AiLocalMachine.IsLocalMachine(host.Id) && !profile.UseSshTransport)
            {
                var info = new ProcessStartInfo("sudo")
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, WorkingDirectory = "/"
                };
                info.ArgumentList.Add("-n");
                info.ArgumentList.Add("--");
                info.ArgumentList.Add("/usr/bin/python3");
                foreach (var argument in arguments) info.ArgumentList.Add(argument);
                process = Process.Start(info) ?? throw new IOException("Could not start the upload writer.");
                input = process.StandardInput.BaseStream;
                output = process.StandardOutput;
                errors = process.StandardError.ReadToEndAsync();
                execution = process.WaitForExitAsync();
            }
            else
            {
                var request = BuildRemoteRequest(host, profile);
                var credentials = await sshConnectionFactory.ResolveCredentialsAsync(host,
                    new(request.Username, request.Password, request.PrivateKey, request.PrivateKeyPassphrase, request.PreferStoredCredentials), cancellationToken);
                client = sshConnectionFactory.CreateSshClient(host, credentials, ConnectTimeout, KeepAliveInterval);
                await client.ConnectAsync(cancellationToken);
                var invocation = QuoteShellArgument("/usr/bin/python3") + string.Concat(arguments.Select(a => " " + QuoteShellArgument(a)));
                command = client.CreateCommand("if [ \"$(id -u)\" -eq 0 ]; then exec " + invocation + "; else exec sudo -n " + invocation + "; fi");
                // Large browser uploads are not limited by the ordinary 30-minute command timeout.
                command.CommandTimeout = Timeout.InfiniteTimeSpan;
                execution = command.ExecuteAsync();
                input = command.CreateInputStream();
                output = new StreamReader(command.OutputStream);
                errors = new StreamReader(command.ExtendedOutputStream).ReadToEndAsync();
            }

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(ConnectTimeout);
            if (await output.ReadLineAsync(startup.Token) != "READY")
                throw new IOException((await errors).Trim());
            return new DestinationFileUpload(input, async token =>
            {
                await input.WriteAsync(new byte[] { 1 }, token);
                await input.FlushAsync(token);
                await input.DisposeAsync();
                await execution.WaitAsync(token);
                var error = await errors.WaitAsync(token);
                var exitCode = process?.ExitCode ?? command?.ExitStatus ?? -1;
                if (exitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? "The destination could not finish the upload." : error.Trim());
                committed = true;
            }, Cleanup);
        }
        catch
        {
            try { await Cleanup(); } catch { /* Preserve the original upload failure. */ }
            throw;
        }
    }
}
