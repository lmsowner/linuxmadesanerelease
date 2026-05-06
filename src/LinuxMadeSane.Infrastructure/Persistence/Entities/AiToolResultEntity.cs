namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiToolResultEntity
{
    public Guid Id { get; set; }
    public Guid InvocationId { get; set; }
    public int Outcome { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string OutputText { get; set; } = string.Empty;
    public string ErrorText { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }

    public AiToolInvocationEntity? Invocation { get; set; }
}
