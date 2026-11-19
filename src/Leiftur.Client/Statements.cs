namespace Leiftur.Client;

/// <summary>What the account owes for one calendar month: the monthly fee plus what was sent.</summary>
public sealed record BillingStatement
{
    public required Guid ClientId { get; init; }
    public required string ClientName { get; init; }
    /// <summary>The month, as yyyy-MM.</summary>
    public required string Month { get; init; }
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    /// <summary>One line per country and price. A price change during the month shows as two lines.</summary>
    public required IReadOnlyList<StatementLine> Usage { get; init; }
    /// <summary>Messages that failed. Listed for information; they are not charged.</summary>
    public required int FailedMessages { get; init; }
    /// <summary>One entry per currency. Normally exactly one.</summary>
    public required IReadOnlyList<StatementTotal> Totals { get; init; }
    /// <summary>All amounts are before VAT.</summary>
    public bool AmountsExcludeVat { get; init; } = true;
}

/// <param name="Messages">Messages charged for (failed ones are left out).</param>
/// <param name="Segments">Segments those messages took.</param>
/// <param name="Quantity">What the unit price is multiplied by: segments or messages, depending on <paramref name="Unit"/>.</param>
public sealed record StatementLine(
    string CountryCode,
    BillingUnit Unit,
    decimal UnitPrice,
    int Messages,
    int Segments,
    int Quantity,
    decimal Amount,
    string Currency);

/// <param name="MonthlyFee">The fixed monthly fee.</param>
/// <param name="Usage">The sum of the usage lines.</param>
/// <param name="Total">Monthly fee plus usage.</param>
public sealed record StatementTotal(string Currency, decimal MonthlyFee, decimal Usage, decimal Total);
