using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Leiftur.Client;

public sealed class LeifturClientOptions
{
    /// <summary>Base address of the Leiftur API, e.g. https://api.leiftur.is.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>Your Leiftur API key.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>How many times a request is retried after a network error, a timeout, or a 429/5xx response.</summary>
    public int MaxRetries { get; set; } = 3;
}

public interface ILeifturClient
{
    /// <summary>
    /// Sends (or schedules) an SMS. Safe to call again after a failure: every call carries an idempotency key,
    /// and retries inside this method reuse it, so the same request is never sent or billed twice.
    /// </summary>
    /// <param name="request">What to send.</param>
    /// <param name="idempotencyKey">
    /// Supply your own stable key (e.g. your order id) to also protect against your application repeating
    /// the call. Left null, a new key is generated per call.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<SendSmsResponse> SendAsync(SendSmsRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default);

    /// <summary>Convenience overload for a single recipient.</summary>
    Task<SendSmsResponse> SendAsync(string from, string to, string body, string? reference = null, DateTimeOffset? scheduledAt = null, CancellationToken cancellationToken = default);

    /// <summary>Returns null when the message does not exist.</summary>
    Task<MessageDto?> GetMessageAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns null when the batch does not exist.</summary>
    Task<BatchDto?> GetBatchAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Cancels every message in the batch that has not been sent yet.</summary>
    Task<CancelBatchResponse> CancelBatchAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<MessageDto>> ListMessagesAsync(MessageQuery? query = null, CancellationToken cancellationToken = default);

    Task<UsageReport> GetUsageAsync(DateOnly from, DateOnly to, UsageGroupBy groupBy = UsageGroupBy.Day, Guid? projectId = null, CancellationToken cancellationToken = default);
}

public sealed record MessageQuery
{
    public Guid? BatchId { get; init; }
    public Guid? ProjectId { get; init; }
    public string? Reference { get; init; }
    public MessageStatus? Status { get; init; }
    public string? Recipient { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>The API answered with an error.</summary>
public sealed class LeifturApiException(HttpStatusCode statusCode, string message, IReadOnlyList<string> errors) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>Individual problems, e.g. one entry per invalid phone number.</summary>
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class LeifturClient : ILeifturClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly LeifturClientOptions _options;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public LeifturClient(HttpClient http, IOptions<LeifturClientOptions> options) : this(http, options.Value)
    {
    }

    public LeifturClient(HttpClient http, LeifturClientOptions options)
    {
        _http = http;
        _options = options;
        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress ?? throw new ArgumentException("LeifturClientOptions.BaseAddress is required.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("LeifturClientOptions.ApiKey is required.", nameof(options));
        }
    }

    public async Task<SendSmsResponse> SendAsync(SendSmsRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = idempotencyKey ?? Guid.NewGuid().ToString("N");
        var response = await SendWithRetryAsync(() =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, "v1/messages") { Content = JsonContent.Create(request, options: Json) };
            message.Headers.Add("Idempotency-Key", key);
            return message;
        }, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<SendSmsResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<SendSmsResponse> SendAsync(string from, string to, string body, string? reference = null, DateTimeOffset? scheduledAt = null, CancellationToken cancellationToken = default) =>
        SendAsync(new SendSmsRequest { From = from, To = [to], Body = body, Reference = reference, ScheduledAt = scheduledAt }, null, cancellationToken);

    public Task<MessageDto?> GetMessageAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<MessageDto>($"v1/messages/{id}", cancellationToken);

    public Task<BatchDto?> GetBatchAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<BatchDto>($"v1/batches/{id}", cancellationToken);

    public async Task<CancelBatchResponse> CancelBatchAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Cancelling twice is harmless, so this is safe to retry.
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, $"v1/batches/{id}/cancel"), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CancelBatchResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedResult<MessageDto>> ListMessagesAsync(MessageQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new MessageQuery();
        var parts = new List<string> { $"page={query.Page}", $"pageSize={query.PageSize}" };
        if (query.BatchId is { } batchId) parts.Add($"batchId={batchId}");
        if (query.ProjectId is { } projectId) parts.Add($"projectId={projectId}");
        if (!string.IsNullOrEmpty(query.Reference)) parts.Add($"reference={Uri.EscapeDataString(query.Reference)}");
        if (query.Status is { } status) parts.Add($"status={status}");
        if (!string.IsNullOrEmpty(query.Recipient)) parts.Add($"recipient={Uri.EscapeDataString(query.Recipient)}");
        if (query.From is { } from) parts.Add($"from={Date(from)}");
        if (query.To is { } to) parts.Add($"to={Date(to)}");

        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, "v1/messages?" + string.Join('&', parts)), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<PagedResult<MessageDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsageReport> GetUsageAsync(DateOnly from, DateOnly to, UsageGroupBy groupBy = UsageGroupBy.Day, Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        var url = $"v1/reports/usage?from={Date(from)}&to={Date(to)}&groupBy={groupBy}";
        if (projectId is { } pid) url += $"&projectId={pid}";
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<UsageReport>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct) where T : class
    {
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var lastAttempt = attempt >= _options.MaxRetries;
            TimeSpan? retryAfter = null;
            try
            {
                using var request = createRequest();
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

                var transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!transient || lastAttempt) return response;

                retryAfter = response.Headers.RetryAfter?.Delta;
                response.Dispose();
            }
            catch (HttpRequestException) when (!lastAttempt)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && !lastAttempt)
            {
                // HttpClient timeout, not the caller cancelling.
            }

            var backoff = TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt) + Random.Shared.Next(0, 200));
            var delay = retryAfter is { } r && r > backoff ? r : backoff;
            await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, ct).ConfigureAwait(false);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
                       ?? throw new LeifturApiException(response.StatusCode, "The API returned an empty response.", []);
            }

            var message = $"The Leiftur API returned {(int)response.StatusCode}.";
            var errors = new List<string>();
            try
            {
                using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                if (problem.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (problem.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String) message = detail.GetString()!;
                    else if (problem.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String) message = title.GetString()!;

                    if (problem.RootElement.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        errors.AddRange(list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                    }
                }
            }
            catch (JsonException)
            {
                // Not a JSON error body; keep the generic message.
            }
            throw new LeifturApiException(response.StatusCode, message, errors);
        }
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
