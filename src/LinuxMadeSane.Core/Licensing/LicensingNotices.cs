// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Core.Licensing;
public static class LicensingNotices
{
    public static string? Message(LicensingStatus status) => status.Status switch
    {
        "declaration_required" => "Please review your organisation’s licensing declaration. This does not interrupt management access.",
        "evaluation_active" => $"Professional evaluation — {status.DaysRemaining} days remaining. Your organisation’s evaluation ends on {status.EvaluationEndUtc?.ToLocalTime():d}. Purchase a Professional licence to continue licensed commercial use. Hosted Portal is optional.",
        "professional_required" when status.EvaluationEndUtc is null => "Professional is required for your declared use. Start your organisation’s unused evaluation or import an existing licence. Management remains operational.",
        "professional_required" => $"Your Professional evaluation ended on {status.EvaluationEndUtc?.ToLocalTime():d}. A Professional licence is now required for your declared use. LMS management remains operational so you can maintain your systems. Purchase a licence or import an existing signed licence.",
        "professional_expired" =>  $"Your Professional licence expired on {status.PaidExpiresAtUtc?.ToLocalTime():d}. Renew or import a current licence. Your systems and management tools remain operational.",
        "professional_active" when status.Notices.Contains("over_capacity") => $"You are managing {status.ObservedServers} servers with licensed capacity for {status.LicensedCapacity}. Update your Professional licence to cover the additional servers.",
        _ => null
    };
    public static string Version(LicensingStatus status) => $"{status.TermsVersion}:{status.Status}:{status.EvaluationEndUtc:yyyyMMdd}:{(status.DaysRemaining <= 1 ? 1 : status.DaysRemaining <= 7 ? 7 : 30)}";
}
