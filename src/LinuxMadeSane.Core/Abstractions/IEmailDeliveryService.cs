namespace LinuxMadeSane.Core.Abstractions;

public interface IEmailDeliveryService
{
    Task<EmailDeliveryResult> SendHtmlAsync(
        string recipientAddress,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default);
}

public sealed record EmailDeliveryResult(
    bool Succeeded,
    bool Attempted,
    string Message);
