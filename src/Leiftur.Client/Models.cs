using System.Text.Json.Serialization;

namespace Leiftur.Client;

/// <summary>Lifecycle state of a single SMS.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MessageStatus>))]
public enum MessageStatus
{
    /// <summary>Accepted, waiting for its scheduled send time.</summary>
    Scheduled = 0,
    /// <summary>Accepted, waiting to be handed to the carrier.</summary>
    Queued = 1,
    /// <summary>Currently being submitted to the carrier.</summary>
    Sending = 2,
    /// <summary>Handed to the carrier; no delivery receipt yet.</summary>
    Sent = 3,
    /// <summary>Delivered to the handset.</summary>
    Delivered = 4,
    /// <summary>Permanently failed.</summary>
    Failed = 5,
    /// <summary>Cancelled before it was sent.</summary>
    Cancelled = 6,
    /// <summary>Submission outcome could not be determined (e.g. carrier timeout). Not retried automatically.</summary>
    Unknown = 7,
}

/// <summary>What a price is charged per.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BillingUnit>))]
public enum BillingUnit
{
    /// <summary>Per segment: a long message, or one with Icelandic letters, counts as several.</summary>
    Segment = 0,
    /// <summary>Per message, however many segments it takes.</summary>
    Message = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter<SmsEncoding>))]
public enum SmsEncoding
{
    Gsm7 = 0,
    Ucs2 = 1,
}

/// <summary>Request to send one SMS body to one or more recipients, now or at a scheduled time.</summary>
public sealed record SendSmsRequest
{
    /// <summary>Approved sender name (max 11 letters/digits) or number.</summary>
    public required string From { get; init; }

    /// <summary>Recipient numbers. Icelandic local numbers or international format. Max 10,000 per request.</summary>
    public required IReadOnlyList<string> To { get; init; }

    public required string Body { get; init; }

    /// <summary>Free-form billing reference (e.g. customer or campaign id). Shown in reports and usable as a grouping key.</summary>
    public string? Reference { get; init; }

    /// <summary>Optional labels for filtering and reporting.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>When to send. Null or a time in the past sends immediately.</summary>
    public DateTimeOffset? ScheduledAt { get; init; }

    /// <summary>Project (sub-account) to bill. Defaults to the project bound to the API key.</summary>
    public Guid? ProjectId { get; init; }

    /// <summary>
    /// Up to 10 key/value pairs of your own that are stored with each message and returned with it, also in
    /// webhook events. Keys up to 40 characters, values up to 200.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record SendSmsResponse
{
    public required Guid BatchId { get; init; }
    public required DateTimeOffset? ScheduledAt { get; init; }
    public required int TotalMessages { get; init; }
    public required int TotalSegments { get; init; }
    public required decimal EstimatedPrice { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<AcceptedMessage> Messages { get; init; }

    /// <summary>True when this response was replayed for a repeated Idempotency-Key.</summary>
    public bool Replayed { get; init; }

    /// <summary>Recipients that were left out because they are on your suppression list. Not sent, not billed.</summary>
    public IReadOnlyList<string> Suppressed { get; init; } = [];
}

public sealed record AcceptedMessage(Guid Id, string To, string CountryCode, int Segments, MessageStatus Status);

public sealed record MessageDto
{
    public required Guid Id { get; init; }
    public required Guid BatchId { get; init; }
    public Guid? ProjectId { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required string CountryCode { get; init; }
    public required string Body { get; init; }
    public required SmsEncoding Encoding { get; init; }
    public required int Segments { get; init; }
    public required MessageStatus Status { get; init; }
    public string? Reference { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>The key/value pairs given when the message was sent.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public required decimal UnitPrice { get; init; }
    public required decimal Price { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
    public string? Error { get; init; }
}

public sealed record BatchDto
{
    public required Guid Id { get; init; }
    public Guid? ProjectId { get; init; }
    public required string From { get; init; }
    public string? Reference { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    public required int TotalMessages { get; init; }
    /// <summary>Number of messages per status.</summary>
    public required IReadOnlyDictionary<MessageStatus, int> Counts { get; init; }
}

public sealed record CancelBatchResponse(Guid BatchId, int Cancelled);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

[JsonConverter(typeof(JsonStringEnumConverter<UsageGroupBy>))]
public enum UsageGroupBy
{
    Country,
    Project,
    Reference,
    Day,
    Month,
}

public sealed record UsageRow
{
    /// <summary>Country code, project id, reference, or date depending on the grouping.</summary>
    public required string Key { get; init; }
    public required string Currency { get; init; }
    public required int Messages { get; init; }
    public required int Segments { get; init; }
    public required int Delivered { get; init; }
    public required int Failed { get; init; }
    public required decimal Amount { get; init; }
}

public sealed record UsageReport(DateTimeOffset From, DateTimeOffset To, UsageGroupBy GroupBy, IReadOnlyList<UsageRow> Rows);
