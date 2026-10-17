// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Core.Licensing;

public sealed record Organisation(Guid Id, string Name, string GroupIdentity);
public sealed record UsageDeclaration(Guid Id, Guid OrganisationId, string Category, string RevenueBand,
    bool ThirdPartyCommercialUse, string Actor, DateTimeOffset DeclaredAtUtc, DateTimeOffset ReviewDueAtUtc,
    string PolicyVersion, string TermsHash, string? FinancialYearEnd, string? ExchangeRateBasis);
public sealed record Evaluation(Guid OrganisationId, DateTimeOffset StartedAtUtc, string Provenance)
{
    public DateTimeOffset EndsAtUtc => StartedAtUtc.AddDays(30);
}
public sealed record ReleaseLicence(string Release, string LegalRegime, string TermsHash, string TermsVersion,
    bool Approved, string? FirstPublicationUtc, string? ChangeDate, string? ChangeLicense);
public sealed record Entitlement(int SchemaVersion, Guid EntitlementId, Guid OrganisationId, string Product,
    string RightsVersion, DateTimeOffset IssuedAtUtc, DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresAtUtc,
    int ServerCapacity, string[] Rights, string Issuer, string KeyId, string? AgreementReference);
public sealed record SignedEntitlement(string Algorithm, string KeyId, string Payload, string Signature);
public sealed record AuditEvent(Guid Id, string Kind, string Actor, DateTimeOffset AtUtc, string Reason);
public sealed record ServerIdentity(Guid Id, string ExternalIdentity, string? Customer, bool Enrolled);
public sealed record LicensingState
{
    public int SchemaVersion { get; init; } = 1;
    public Organisation? Organisation { get; init; }
    public List<UsageDeclaration> Declarations { get; init; } = [];
    public Evaluation? Evaluation { get; init; }
    public SignedEntitlement? Licence { get; init; }
    public List<ServerIdentity> Servers { get; init; } = [];
    public List<AuditEvent> Audit { get; init; } = [];
    public DateTimeOffset? LastSeenUtc { get; init; }
    public bool InvalidImport { get; init; }
    public List<TermsAcceptance> Acceptances { get; init; } = [];
    public List<NoticeDismissal> Dismissals { get; init; } = [];
}
public sealed record TermsAcceptance(string Actor, string TermsVersion, string TermsHash, DateTimeOffset AcceptedAtUtc);
public sealed record NoticeDismissal(string Actor, string NoticeVersion);
public sealed record LicensingStatus(string LegalRegime, string Status, string RequiredLicence,
    string TermsVersion, bool ManagementAllowed, int ObservedServers, int? LicensedCapacity,
    bool QuoteRequired, int DaysRemaining, DateTimeOffset? EvaluationEndUtc, string PortalStatus,
    string BillingStatus, string[] Notices)
{
    public DateTimeOffset? PaidExpiresAtUtc { get; init; }
    public DateTimeOffset? DeclarationReviewDueAtUtc { get; init; }
}
public sealed record DeclarationInput(Guid OrganisationId, string OrganisationName, string GroupIdentity,
    string Category, string RevenueBand, bool ThirdPartyCommercialUse, bool Confirmed,
    string? FinancialYearEnd = null, string? ExchangeRateBasis = null, string? CorrectionReason = null);
public sealed record OrganisationTransfer(int SchemaVersion, Organisation Organisation, UsageDeclaration[] Declarations,
    Evaluation? Evaluation, SignedEntitlement? Licence);
