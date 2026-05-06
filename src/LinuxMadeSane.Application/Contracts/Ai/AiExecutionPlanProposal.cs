namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class AiExecutionPlanProposal
{
    public Guid? MessageId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<AiProposedActionProposal> Actions { get; set; } = [];
}
