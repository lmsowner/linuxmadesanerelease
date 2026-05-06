namespace LinuxMadeSane.Application.Interfaces;

public interface IAiExecutionPlanExecutionService
{
    Task ExecuteApprovedPlanAsync(Guid executionPlanId, CancellationToken cancellationToken = default);
}
