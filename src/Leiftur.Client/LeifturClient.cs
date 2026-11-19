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

    /// <summary>What the account owes for a month: the monthly fee plus what was sent. Before VAT.</summary>
    /// <param name="year">The year, e.g. 2026.</param>
    /// <param name="month">The month, 1-12.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<BillingStatement> GetStatementAsync(int year, int month, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops sending to these numbers. Later sends skip them and report them back in
    /// <see cref="SendSmsResponse.Suppressed"/>. Adding a number that is already listed is harmless.
    /// </summary>
    /// <param name="numbers">Up to 10,000 numbers, in any accepted format.</param>
    /// <param name="reason">One of <see cref="SuppressionReasons"/>. Defaults to opt_out.</param>
    /// <param name="note">Free text for your own records, up to 500 characters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<AddSuppressionsResponse> AddSuppressionsAsync(IEnumerable<string> numbers, string? reason = null, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>The suppression list, newest first.</summary>
    Task<PagedResult<SuppressionDto>> ListSuppressionsAsync(int page = 1, int pageSize = 100, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the entry if the number is on the suppression list, otherwise null.</summary>
    Task<SuppressionDto?> GetSuppressionAsync(string number, CancellationToken cancellationToken = default);

    /// <summary>Allows sending to the number again. Returns false if it was not on the list.</summary>
    Task<bool> RemoveSuppressionAsync(string number, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an HTTPS address that receives message status events. The returned secret is shown only
    /// once; keep it to verify incoming requests with <see cref="LeifturWebhook"/>.
    /// </summary>
    /// <param name="url">A public https:// address.</param>
    /// <param name="events">Which of <see cref="WebhookEventTypes"/> to receive. Defaults to delivered and failed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<WebhookCreatedResponse> CreateWebhookAsync(string url, IEnumerable<string>? events = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WebhookDto>> ListWebhooksAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes a webhook and any events still waiting for it. Returns false if it does not exist.</summary>
    Task<bool> DeleteWebhookAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Sends a webhook.test event to the webhook's address.</summary>
    Task<WebhookDeliveryDto> SendTestWebhookAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The 100 most recent events for a webhook and how delivering each went.</summary>
    Task<IReadOnlyList<WebhookDeliveryDto>> ListWebhookDeliveriesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a text for reuse. Placeholders are written {{name}}, or {{name|fallback}} to give a value to
    /// use when a recipient has none. Send with it by setting <see cref="SendSmsRequest.TemplateId"/>.
    /// </summary>
    Task<TemplateDto> CreateTemplateAsync(string name, string body, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns null when the template does not exist.</summary>
    Task<TemplateDto?> GetTemplateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Changes a template. Messages already accepted keep the text they were accepted with.</summary>
    Task<TemplateDto> UpdateTemplateAsync(Guid id, string name, string body, CancellationToken cancellationToken = default);

    /// <summary>Returns false if the template does not exist.</summary>
    Task<bool> DeleteTemplateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Shows a template filled in with the given values, and how many segments it would take. Sends nothing.</summary>
    Task<TemplatePreview> PreviewTemplateAsync(Guid id, IReadOnlyDictionary<string, string>? data = null, CancellationToken cancellationToken = default);

    Task<ContactListDto> CreateContactListAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContactListDto>> ListContactListsAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a list and every contact on it. Returns false if the list does not exist.</summary>
    Task<bool> DeleteContactListAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds up to 10,000 contacts to a list. A number already on the list has its data replaced; it is never
    /// listed twice.
    /// </summary>
    Task<AddContactsResponse> AddContactsAsync(Guid listId, IEnumerable<ContactInput> contacts, CancellationToken cancellationToken = default);

    /// <summary>The contacts on a list, ordered by number.</summary>
    Task<PagedResult<ContactDto>> ListContactsAsync(Guid listId, int page = 1, int pageSize = 100, CancellationToken cancellationToken = default);

    /// <summary>Takes one number off a list. Returns false if it was not on it. This is not an opt-out; use the suppression list for that.</summary>
    Task<bool> RemoveContactAsync(Guid listId, string number, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a verification code to a phone number. The code is not returned: check what the person typed
    /// with <see cref="CheckVerificationAsync"/>. Not retried on failure, since a retry would send a second code.
    /// </summary>
    Task<VerificationDto> StartVerificationAsync(StartVerificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends a six-digit code, valid for five minutes, with the standard Icelandic text.</summary>
    Task<VerificationDto> StartVerificationAsync(string to, string from, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the code a person typed. Act on <see cref="VerificationCheckResult.Valid"/>. Each call uses one
    /// of the five attempts, so it is not retried on failure.
    /// </summary>
    Task<VerificationCheckResult> CheckVerificationAsync(Guid id, string code, CancellationToken cancellationToken = default);

    /// <summary>Returns null when the verification does not exist.</summary>
    Task<VerificationDto?> GetVerificationAsync(Guid id, CancellationToken cancellationToken = default);
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

    public async Task<BillingStatement> GetStatementAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month), "Month must be 1-12.");
        var url = $"v1/reports/statement?month={year.ToString("D4", CultureInfo.InvariantCulture)}-{month.ToString("D2", CultureInfo.InvariantCulture)}";
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<BillingStatement>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AddSuppressionsResponse> AddSuppressionsAsync(IEnumerable<string> numbers, string? reason = null, string? note = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        var body = new { numbers = numbers.ToList(), reason, note };
        // Adding the same numbers twice changes nothing, so this is safe to retry.
        var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "v1/suppressions") { Content = JsonContent.Create(body, options: Json) }, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<AddSuppressionsResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedResult<SuppressionDto>> ListSuppressionsAsync(int page = 1, int pageSize = 100, string? reason = null, CancellationToken cancellationToken = default)
    {
        var url = $"v1/suppressions?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(reason)) url += $"&reason={Uri.EscapeDataString(reason)}";
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<PagedResult<SuppressionDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<SuppressionDto?> GetSuppressionAsync(string number, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        return GetOrNullAsync<SuppressionDto>($"v1/suppressions/{Uri.EscapeDataString(number)}", cancellationToken);
    }

    public Task<bool> RemoveSuppressionAsync(string number, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        return DeleteAsync($"v1/suppressions/{Uri.EscapeDataString(number)}", cancellationToken);
    }

    public async Task<WebhookCreatedResponse> CreateWebhookAsync(string url, IEnumerable<string>? events = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var body = new { url, events = events?.ToList() };
        // Not retried: a second attempt after a lost answer would register the address twice.
        var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "v1/webhooks") { Content = JsonContent.Create(body, options: Json) }, cancellationToken, maxRetries: 0).ConfigureAwait(false);
        return await ReadAsync<WebhookCreatedResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WebhookDto>> ListWebhooksAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, "v1/webhooks"), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<List<WebhookDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteWebhookAsync(Guid id, CancellationToken cancellationToken = default) =>
        DeleteAsync($"v1/webhooks/{id}", cancellationToken);

    public async Task<WebhookDeliveryDto> SendTestWebhookAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Not retried: each call sends one more test event.
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, $"v1/webhooks/{id}/test"), cancellationToken, maxRetries: 0).ConfigureAwait(false);
        return await ReadAsync<WebhookDeliveryDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WebhookDeliveryDto>> ListWebhookDeliveriesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, $"v1/webhooks/{id}/deliveries"), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<List<WebhookDeliveryDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TemplateDto> CreateTemplateAsync(string name, string body, CancellationToken cancellationToken = default)
    {
        // Not retried: after a lost answer the name would already be taken, and the repeat would fail.
        var response = await SendWithRetryAsync(() => Post("v1/templates", new { name, body }), cancellationToken, maxRetries: 0).ConfigureAwait(false);
        return await ReadAsync<TemplateDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, "v1/templates"), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<List<TemplateDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<TemplateDto?> GetTemplateAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<TemplateDto>($"v1/templates/{id}", cancellationToken);

    public async Task<TemplateDto> UpdateTemplateAsync(Guid id, string name, string body, CancellationToken cancellationToken = default)
    {
        // Saving the same content twice ends in the same state, so this is safe to retry.
        var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"v1/templates/{id}") { Content = JsonContent.Create(new { name, body }, options: Json) }, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<TemplateDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteTemplateAsync(Guid id, CancellationToken cancellationToken = default) =>
        DeleteAsync($"v1/templates/{id}", cancellationToken);

    public async Task<TemplatePreview> PreviewTemplateAsync(Guid id, IReadOnlyDictionary<string, string>? data = null, CancellationToken cancellationToken = default)
    {
        var response = await SendWithRetryAsync(() => Post($"v1/templates/{id}/preview", new { data }), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<TemplatePreview>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContactListDto> CreateContactListAsync(string name, CancellationToken cancellationToken = default)
    {
        // Not retried, for the same reason as creating a template.
        var response = await SendWithRetryAsync(() => Post("v1/lists", new { name }), cancellationToken, maxRetries: 0).ConfigureAwait(false);
        return await ReadAsync<ContactListDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContactListDto>> ListContactListsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, "v1/lists"), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<List<ContactListDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteContactListAsync(Guid id, CancellationToken cancellationToken = default) =>
        DeleteAsync($"v1/lists/{id}", cancellationToken);

    public async Task<AddContactsResponse> AddContactsAsync(Guid listId, IEnumerable<ContactInput> contacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        var body = new { contacts = contacts.ToList() };
        // Adding the same contacts twice ends in the same list, so this is safe to retry.
        var response = await SendWithRetryAsync(() => Post($"v1/lists/{listId}/contacts", body), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<AddContactsResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedResult<ContactDto>> ListContactsAsync(Guid listId, int page = 1, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"v1/lists/{listId}/contacts?page={page}&pageSize={pageSize}"), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<PagedResult<ContactDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> RemoveContactAsync(Guid listId, string number, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        return DeleteAsync($"v1/lists/{listId}/contacts/{Uri.EscapeDataString(number)}", cancellationToken);
    }

    public async Task<VerificationDto> StartVerificationAsync(StartVerificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Not retried: a second attempt after a lost answer would send the person a second code.
        var response = await SendWithRetryAsync(() => Post("v1/verifications", request), cancellationToken, maxRetries: 0).ConfigureAwait(false);
        return await ReadAsync<VerificationDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<VerificationDto> StartVerificationAsync(string to, string from, CancellationToken cancellationToken = default) =>
        StartVerificationAsync(new StartVerificationRequest { To = to, From = from }, cancellationToken);

    public async Task<VerificationCheckResult> CheckVerificationAsync(Guid id, string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        // Not retried: each check uses up an attempt, and a repeat of a check that succeeded would come back as not valid.
        var response = await SendWithRetryAsync(() => Post($"v1/verifications/{id}/check", new { code }), cancellationToken, maxRetries: 0).ConfigureAwait(false);
        return await ReadAsync<VerificationCheckResult>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<VerificationDto?> GetVerificationAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<VerificationDto>($"v1/verifications/{id}", cancellationToken);

    private static HttpRequestMessage Post<T>(string url, T body) =>
        new(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };

    /// <summary>True if something was deleted, false if it was not there.</summary>
    private async Task<bool> DeleteAsync(string url, CancellationToken ct)
    {
        // Deleting twice ends in the same state, so this is safe to retry.
        var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return false;
        }
        if (response.IsSuccessStatusCode)
        {
            response.Dispose();
            return true;
        }
        await ReadAsync<object>(response, ct).ConfigureAwait(false);   // throws LeifturApiException
        return false;
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

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct, int? maxRetries = null)
    {
        var retries = maxRetries ?? _options.MaxRetries;
        for (var attempt = 0; ; attempt++)
        {
            var lastAttempt = attempt >= retries;
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
