// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text.Json;
namespace LinuxMadeSane.Core.Licensing;
public sealed record PriceTier(int Through, int AnnualMinorUnits);
public sealed record PriceBook(string Version, string Currency, bool Approved, int QuoteThreshold, PriceTier[] Tiers, string[] PortalPlans)
{
    public static PriceBook Proposed { get; } = Load();
    private static PriceBook Load()
    {
        using var stream = typeof(PriceBook).Assembly.GetManifestResourceStream("Lms.Licensing.PriceBook")!;
        return JsonSerializer.Deserialize<PriceBook>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
public sealed record PricingLine(int From, int Through, int Quantity, decimal AnnualGbp);
public sealed record PricingQuote(string PriceVersion, string Currency, bool Proposed, bool QuoteRequired,
    int Servers, decimal? AnnualGbp, decimal? MonthlyGbp, PricingLine[] Lines);
public static class ProfessionalPricing
{
    public static PricingQuote Quote(int servers, PriceBook? book = null)
    {
        book ??= PriceBook.Proposed;
        if (servers < 0) throw new ArgumentException("Server count must be a non-negative integer.");
        if (servers >= book.QuoteThreshold) return new(book.Version, book.Currency, !book.Approved, true, servers, null, null, []);
        var from = 1; var lines = new List<PricingLine>();
        foreach (var tier in book.Tiers)
        {
            var quantity = Math.Max(0, Math.Min(servers, tier.Through) - from + 1);
            if (quantity > 0) lines.Add(new(from, tier.Through, quantity, quantity * tier.AnnualMinorUnits / 100m));
            from = tier.Through + 1;
        }
        var total = lines.Sum(l => l.AnnualGbp);
        return new(book.Version, book.Currency, !book.Approved, false, servers, total, total / 12m, lines.ToArray());
    }
}
public sealed record CapacityEvent(Guid Id, DateTimeOffset EffectiveAtUtc, int Capacity, string AuthorisedActor);
public sealed record InvoiceAccrual(decimal UnroundedGbp, long MinorUnits, decimal ResidualGbp);
public static class MonthlyAccrual
{
    public static InvoiceAccrual Calculate(DateTimeOffset month, DateTimeOffset commercialStart,
        IReadOnlyList<CapacityEvent> events, decimal priorResidual = 0, PriceBook? book = null)
    {
        month = month.ToUniversalTime();
        var start = new DateTimeOffset(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMonths(1);
        var timeline = events.GroupBy(e => e.Id).Select(g => g.Distinct().Single()).OrderBy(e => e.EffectiveAtUtc).ToArray();
        if (timeline.Any(e => string.IsNullOrWhiteSpace(e.AuthorisedActor) || e.Capacity < 0) || timeline.GroupBy(e => e.EffectiveAtUtc).Any(g => g.Count() > 1))
            throw new ArgumentException("Capacity events must be authorised and unambiguous.");
        decimal amount = 0;
        var boundary = commercialStart > start ? commercialStart : start;
        foreach (var change in timeline.Where(e => e.EffectiveAtUtc > boundary && e.EffectiveAtUtc < end)
                     .Select(e => e.EffectiveAtUtc).Append(end))
        {
            if (boundary >= end) break;
            var capacity = timeline.LastOrDefault(e => e.EffectiveAtUtc <= boundary)?.Capacity ?? 0;
            var quote = ProfessionalPricing.Quote(capacity, book);
            if (quote.QuoteRequired) throw new ArgumentException("An approved deployment quote is required; no amount can be accrued.");
            amount += quote.AnnualGbp!.Value / 12m * (change - boundary).Ticks / (end - start).Ticks;
            boundary = change;
        }
        var payable = amount + priorResidual;
        var pennies = checked((long)decimal.Round(payable * 100m, 0, MidpointRounding.AwayFromZero));
        return new(amount, pennies, payable - pennies / 100m);
    }
}
