using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Leiftur.Client;

public static class WebhookEventTypes
{
    /// <summary>The message was handed to the carrier. Not sent to a webhook unless asked for.</summary>
    public const string MessageSent = "message.sent";
    /// <summary>The message arrived on the handset.</summary>
    public const string MessageDelivered = "message.delivered";
    /// <summary>The message permanently failed.</summary>
    public const string MessageFailed = "message.failed";
    /// <summary>Sent when you ask for a test event. Carries no message.</summary>
    public const string Test = "webhook.test";
}

public sealed record WebhookDto(Guid Id, string Url, IReadOnlyList<string> Events, bool IsActive, DateTimeOffset CreatedAt);

/// <summary>Returned once when a webhook is created. The secret cannot be retrieved again.</summary>
public sealed record WebhookCreatedResponse(WebhookDto Webhook, string Secret);

/// <summary>One event for a webhook and how delivering it went.</summary>
/// <param name="Status">Pending (waiting or being retried), Delivered, or Failed (given up).</param>
public sealed record WebhookDeliveryDto(
    Guid Id,
    string EventType,
    Guid? MessageId,
    string Status,
    int Attempts,
    int? LastStatusCode,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? NextAttemptAt);

/// <summary>The body Leiftur posts to your webhook address: one or more events.</summary>
public sealed record WebhookPayload(IReadOnlyList<WebhookEvent> Events);

public sealed record WebhookEvent
{
    /// <summary>Unique per event. A retried request repeats the same ids, so use it to ignore duplicates.</summary>
    public required Guid Id { get; init; }
    /// <summary>One of <see cref="WebhookEventTypes"/>.</summary>
    public required string Type { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>The message as it was when the event happened. Null for webhook.test.</summary>
    public MessageDto? Message { get; init; }
}

/// <summary>
/// For the code that receives Leiftur's webhooks: checks that a request really came from Leiftur and reads it.
/// </summary>
public static class LeifturWebhook
{
    /// <summary>The request header that carries the signature.</summary>
    public const string SignatureHeaderName = "Leiftur-Signature";

    /// <summary>How old a request may be before it is refused, unless you pass another value.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// True if the request was signed with your webhook's secret and is recent.
    /// </summary>
    /// <param name="secret">The secret you were given when the webhook was created ("whsec_…").</param>
    /// <param name="signatureHeader">The value of the Leiftur-Signature header.</param>
    /// <param name="body">The request body exactly as received. Do not parse and re-serialise it first.</param>
    /// <param name="tolerance">How old the request may be. Guards against a captured request being replayed.</param>
    /// <param name="now">The current time; only for tests.</param>
    public static bool VerifySignature(string secret, string? signatureHeader, string body, TimeSpan? tolerance = null, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrEmpty(signatureHeader)) return false;

        long? timestamp = null;
        string? signature = null;
        foreach (var part in signatureHeader.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;
            if (pair[0].Trim() == "t" && long.TryParse(pair[1], out var t)) timestamp = t;
            if (pair[0].Trim() == "v1") signature = pair[1].Trim();
        }
        if (timestamp is null || signature is null) return false;

        DateTimeOffset sentAt;
        try
        {
            sentAt = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        if (((now ?? DateTimeOffset.UtcNow) - sentAt).Duration() > (tolerance ?? DefaultTolerance)) return false;

        var expected = ComputeSignature(secret, timestamp.Value, body);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
    }

    /// <summary>Reads the events from a request body. Verify the signature first.</summary>
    public static WebhookPayload Parse(string body) =>
        JsonSerializer.Deserialize<WebhookPayload>(body, Json) ?? throw new JsonException("The webhook body is empty.");

    /// <summary>Verifies and reads in one step. Returns false, and no events, if the signature does not hold.</summary>
    public static bool TryRead(string secret, string? signatureHeader, string body, out WebhookPayload payload, TimeSpan? tolerance = null)
    {
        payload = new WebhookPayload([]);
        if (!VerifySignature(secret, signatureHeader, body, tolerance)) return false;
        try
        {
            payload = Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>HMAC-SHA256 over "timestamp.body", keyed with the secret, as lowercase hex.</summary>
    public static string ComputeSignature(string secret, long timestamp, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
