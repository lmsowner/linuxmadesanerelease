namespace LinuxMadeSane.Application.Contracts.Sftp;

public sealed class SftpPublicKeyEditor
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label { get; set; } = string.Empty;

    public string PublicKeyText { get; set; } = string.Empty;
}
