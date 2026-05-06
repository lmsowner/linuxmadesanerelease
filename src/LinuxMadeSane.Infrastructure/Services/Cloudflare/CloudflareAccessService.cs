using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;
using Microsoft.Extensions.Options;

namespace LinuxMadeSane.Infrastructure.Services.Cloudflare;

public sealed class CloudflareAccessService(
    ICloudflareClient client,
    IOptions<CloudflareIntegrationOptions> options) : ICloudflareAccessService
{
    private readonly CloudflareIntegrationOptions integrationOptions = options.Value;

    public async Task<IReadOnlyList<CloudflareAccessApplication>> ListApplicationsAsync(
        string apiToken,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var results = await client.GetAllPagesAsync<CloudflareAccessApplicationDto>(
            apiToken,
            $"accounts/{accountId}/access/apps",
            cancellationToken: cancellationToken);

        return results
            .Select(item => item.ToModel(accountId))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CloudflareAccessApplication> CreateApplicationAsync(
        string apiToken,
        string accountId,
        CloudflareAccessApplication application,
        CancellationToken cancellationToken = default)
    {
        var result = await client.PostAsync<object, CloudflareAccessApplicationDto>(
            apiToken,
            $"accounts/{accountId}/access/apps",
            BuildApplicationRequest(application),
            cancellationToken);

        return result.ToModel(accountId);
    }

    public async Task<CloudflareAccessApplication> UpdateApplicationAsync(
        string apiToken,
        string accountId,
        CloudflareAccessApplication application,
        CancellationToken cancellationToken = default)
    {
        var result = await client.PutAsync<object, CloudflareAccessApplicationDto>(
            apiToken,
            $"accounts/{accountId}/access/apps/{application.Id}",
            BuildApplicationRequest(application),
            cancellationToken);

        return result.ToModel(accountId);
    }

    public Task DeleteApplicationAsync(
        string apiToken,
        string accountId,
        string applicationId,
        CancellationToken cancellationToken = default) =>
        client.DeleteAsync(apiToken, $"accounts/{accountId}/access/apps/{applicationId}", cancellationToken);

    public async Task<IReadOnlyList<CloudflareAccessPolicy>> ListPoliciesAsync(
        string apiToken,
        string accountId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        var results = await client.GetAllPagesAsync<CloudflareAccessPolicyDto>(
            apiToken,
            $"accounts/{accountId}/access/apps/{applicationId}/policies",
            cancellationToken: cancellationToken);

        return results
            .Select(item => item.ToModel() with { ApplicationId = applicationId })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CloudflareAccessPolicy> CreatePolicyAsync(
        string apiToken,
        string accountId,
        string applicationId,
        CloudflareAccessPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var result = await client.PostAsync<object, CloudflareAccessPolicyDto>(
            apiToken,
            $"accounts/{accountId}/access/apps/{applicationId}/policies",
            BuildPolicyRequest(policy),
            cancellationToken);

        return result.ToModel() with { ApplicationId = applicationId };
    }

    public async Task<CloudflareAccessPolicy> UpdatePolicyAsync(
        string apiToken,
        string accountId,
        string applicationId,
        CloudflareAccessPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var result = await client.PutAsync<object, CloudflareAccessPolicyDto>(
            apiToken,
            $"accounts/{accountId}/access/apps/{applicationId}/policies/{policy.Id}",
            BuildPolicyRequest(policy),
            cancellationToken);

        return result.ToModel() with { ApplicationId = applicationId };
    }

    public Task DeletePolicyAsync(
        string apiToken,
        string accountId,
        string applicationId,
        string policyId,
        CancellationToken cancellationToken = default) =>
        client.DeleteAsync(apiToken, $"accounts/{accountId}/access/apps/{applicationId}/policies/{policyId}", cancellationToken);

    private object BuildApplicationRequest(CloudflareAccessApplication application) => new
    {
        name = application.Name,
        type = "self_hosted",
        domain = application.Domain,
        session_duration = string.IsNullOrWhiteSpace(application.SessionDuration)
            ? integrationOptions.DefaultAccessSessionDuration
            : application.SessionDuration,
        skip_interstitial = true,
        app_launcher_visible = false,
        destinations = new[]
        {
            new
            {
                type = "public",
                uri = application.Domain
            }
        }
    };

    private static object BuildPolicyRequest(CloudflareAccessPolicy policy) => new
    {
        name = policy.Name,
        decision = policy.Decision,
        include = BuildIncludeRules(policy)
    };

    private static object[] BuildIncludeRules(CloudflareAccessPolicy policy)
    {
        var rules = new List<object>();

        foreach (var email in policy.IncludeEmails.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            rules.Add(new
            {
                email = new
                {
                    email = email.Trim()
                }
            });
        }

        foreach (var domain in policy.IncludeEmailDomains.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            rules.Add(new
            {
                email_domain = new
                {
                    domain = domain.Trim().TrimStart('@')
                }
            });
        }

        return rules.ToArray();
    }
}
