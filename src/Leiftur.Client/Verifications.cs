using System.Text.Json.Serialization;

namespace Leiftur.Client;

[JsonConverter(typeof(JsonStringEnumConverter<VerificationStatus>))]
public enum VerificationStatus
{
    /// <summary>The code was sent and has not been confirmed yet.</summary>
    Pending = 0,
    /// <summary>The right code was given in time.</summary>
    Approved = 1,
    /// <summary>The time ran out.</summary>
    Expired = 2,
    /// <summary>Too many wrong codes were tried.</summary>
    Failed = 3,
    /// <summary>A newer code was requested for the same number.</summary>
    Superseded = 4,
}

public sealed record StartVerificationRequest
{
    /// <summary>The number to verify.</summary>
    public required string To { get; init; }

    /// <summary>Approved sender name.</summary>
    public required string From { get; init; }

    /// <summary>Your own text, up to 300 characters. It must contain {{code}}. Leave out for a standard text.</summary>
    public string? Message { get; init; }

    /// <summary>Language of the standard text: "is" (default) or "en". Ignored when Message is given.</summary>
    public string? Language { get; init; }

    /// <summary>Digits in the code, 4 to 8. Default 6.</summary>
    public int? CodeLength { get; init; }

    /// <summary>How long the code is valid, 60 to 3600 seconds. Default 300.</summary>
    public int? ExpiresInSeconds { get; init; }

    /// <summary>Your own billing reference for the SMS.</summary>
    public string? Reference { get; init; }
}

/// <summary>A verification. The code itself is never returned.</summary>
public sealed record VerificationDto(
    Guid Id,
    string To,
    VerificationStatus Status,
    DateTimeOffset ExpiresAt,
    int AttemptsRemaining,
    Guid? MessageId,
    DateTimeOffset CreatedAt);

/// <summary>The outcome of checking a code.</summary>
/// <param name="Valid">True only for the one check that confirmed the code. This is the value to act on.</param>
public sealed record VerificationCheckResult(Guid Id, bool Valid, VerificationStatus Status, int AttemptsRemaining);
