namespace Leiftur.Client;

public static class SuppressionReasons
{
    /// <summary>The person asked not to be contacted. The default.</summary>
    public const string OptOut = "opt_out";
    public const string Manual = "manual";
    public const string InvalidNumber = "invalid_number";
    public const string Complaint = "complaint";
}

/// <param name="Added">Numbers newly put on the list.</param>
/// <param name="AlreadyListed">Numbers that were on the list before.</param>
/// <param name="Invalid">Inputs that are not valid phone numbers. Nothing was stored for these.</param>
public sealed record AddSuppressionsResponse(int Added, int AlreadyListed, IReadOnlyList<string> Invalid);

/// <summary>One number on the suppression list.</summary>
/// <param name="Number">Digits only, with country code, e.g. 3547801512.</param>
public sealed record SuppressionDto(string Number, string Reason, string? Note, DateTimeOffset CreatedAt);
