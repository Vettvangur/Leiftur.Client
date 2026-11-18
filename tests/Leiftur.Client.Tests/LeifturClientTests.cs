using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Leiftur.Client.Tests;

public class LeifturClientTests
{
    private sealed record Seen(HttpMethod Method, string PathAndQuery, string? Authorization, string? IdempotencyKey, string? Body);

    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(Requests.Count);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (LeifturClient Client, StubHandler Handler) Create(Func<int, HttpResponseMessage> respond, int maxRetries = 2)
    {
        var handler = new StubHandler(respond);
        var client = new LeifturClient(new HttpClient(handler), new LeifturClientOptions
        {
            BaseAddress = new Uri("https://api.example.test/"),
            ApiKey = "lf_test_key",
            MaxRetries = maxRetries,
        });
        return (client, handler);
    }

    private const string Accepted = """
        {
          "batchId": "019a9d2e-6f3b-7c41-9a55-0d1f6c2b8e10",
          "scheduledAt": null,
          "totalMessages": 1,
          "totalSegments": 2,
          "estimatedPrice": 20,
          "currency": "ISK",
          "messages": [
            { "id": "019a9d2e-6f3c-7a02-b1d4-5e7f90ab12cd", "to": "3547801512", "countryCode": "IS", "segments": 2, "status": "Queued" }
          ],
          "replayed": false
        }
        """;

    private const string Message = """
        {
          "id": "019a9d2e-6f3c-7a02-b1d4-5e7f90ab12cd",
          "batchId": "019a9d2e-6f3b-7c41-9a55-0d1f6c2b8e10",
          "projectId": null,
          "from": "ACME",
          "to": "3547801512",
          "countryCode": "IS",
          "body": "Halló",
          "encoding": "Ucs2",
          "segments": 1,
          "status": "Delivered",
          "reference": "customer-42",
          "tags": ["orders"],
          "unitPrice": 10,
          "price": 10,
          "currency": "ISK",
          "createdAt": "2026-11-18T14:02:11.412+00:00",
          "scheduledAt": null,
          "sentAt": "2026-11-18T14:02:11.930+00:00",
          "deliveredAt": "2026-11-18T14:02:15.537+00:00",
          "failedAt": null,
          "error": null
        }
        """;

    [Fact]
    public async Task Send_posts_the_request_with_the_key_and_reads_the_answer()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.Accepted, Accepted));

        var result = await client.SendAsync(new SendSmsRequest
        {
            From = "Acme",
            To = ["7801512"],
            Body = "Halló",
            Reference = "customer-42",
            Tags = ["orders"],
            ScheduledAt = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero),
        });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/messages", request.PathAndQuery);
        Assert.Equal("Bearer lf_test_key", request.Authorization);
        Assert.False(string.IsNullOrEmpty(request.IdempotencyKey));

        using var sent = JsonDocument.Parse(request.Body!);
        Assert.Equal("Acme", sent.RootElement.GetProperty("from").GetString());
        Assert.Equal("7801512", sent.RootElement.GetProperty("to")[0].GetString());
        Assert.Equal("Halló", sent.RootElement.GetProperty("body").GetString());
        Assert.Equal("customer-42", sent.RootElement.GetProperty("reference").GetString());
        Assert.Equal("orders", sent.RootElement.GetProperty("tags")[0].GetString());
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero), sent.RootElement.GetProperty("scheduledAt").GetDateTimeOffset());

        Assert.Equal(Guid.Parse("019a9d2e-6f3b-7c41-9a55-0d1f6c2b8e10"), result.BatchId);
        Assert.Equal(2, result.TotalSegments);
        Assert.Equal(20m, result.EstimatedPrice);
        Assert.Equal("ISK", result.Currency);
        Assert.Equal(MessageStatus.Queued, result.Messages[0].Status);
        Assert.Equal("3547801512", result.Messages[0].To);
    }

    [Fact]
    public async Task Each_send_gets_its_own_idempotency_key_unless_one_is_supplied()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.Accepted, Accepted));

        await client.SendAsync("Acme", "7801512", "one");
        await client.SendAsync("Acme", "7801512", "two");
        await client.SendAsync(new SendSmsRequest { From = "Acme", To = ["7801512"], Body = "three" }, idempotencyKey: "order-1001");

        Assert.NotEqual(handler.Requests[0].IdempotencyKey, handler.Requests[1].IdempotencyKey);
        Assert.Equal("order-1001", handler.Requests[2].IdempotencyKey);
    }

    [Fact]
    public async Task A_failed_send_is_retried_with_the_same_idempotency_key()
    {
        var (client, handler) = Create(attempt => attempt switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            2 => throw new HttpRequestException("connection reset"),
            _ => Json(HttpStatusCode.Accepted, Accepted),
        });

        var result = await client.SendAsync("Acme", "7801512", "Halló");

        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests.Select(r => r.IdempotencyKey).Distinct());
        Assert.Equal(1, result.TotalMessages);
    }

    [Fact]
    public async Task Retries_stop_after_the_configured_number()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), maxRetries: 1);

        var error = await Assert.ThrowsAsync<LeifturApiException>(() => client.SendAsync("Acme", "7801512", "Halló"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_refused_request_is_not_retried_and_carries_each_problem()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.BadRequest, """
            { "title": "Invalid request", "status": 400, "detail": "Invalid phone number: 12", "errors": ["Invalid phone number: 12"] }
            """));

        var error = await Assert.ThrowsAsync<LeifturApiException>(() => client.SendAsync("Acme", "12", "Halló"));

        Assert.Single(handler.Requests);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("Invalid phone number: 12", error.Message);
        Assert.Equal(["Invalid phone number: 12"], error.Errors);
    }

    [Fact]
    public async Task An_error_without_a_json_body_still_gives_a_usable_exception()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<LeifturApiException>(() => client.GetBatchAsync(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Contains("401", error.Message);
    }

    [Fact]
    public async Task Get_message_reads_every_field_and_returns_null_when_not_found()
    {
        var id = Guid.Parse("019a9d2e-6f3c-7a02-b1d4-5e7f90ab12cd");
        var (client, handler) = Create(attempt => attempt == 1 ? Json(HttpStatusCode.OK, Message) : new HttpResponseMessage(HttpStatusCode.NotFound));

        var message = await client.GetMessageAsync(id);
        var missing = await client.GetMessageAsync(Guid.NewGuid());

        Assert.Equal($"/v1/messages/{id}", handler.Requests[0].PathAndQuery);
        Assert.Equal(MessageStatus.Delivered, message!.Status);
        Assert.Equal(SmsEncoding.Ucs2, message.Encoding);
        Assert.Equal("customer-42", message.Reference);
        Assert.Equal(["orders"], message.Tags);
        Assert.Equal(10m, message.Price);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 14, 2, 15, 537, TimeSpan.Zero), message.DeliveredAt);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Get_batch_reads_the_status_counts()
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, """
            {
              "id": "019a9d2e-6f3b-7c41-9a55-0d1f6c2b8e10", "projectId": null, "from": "ACME", "reference": "campaign",
              "createdAt": "2026-11-18T14:02:11.412+00:00", "scheduledAt": null, "totalMessages": 250,
              "counts": { "Delivered": 243, "Sent": 5, "Failed": 2 }
            }
            """));

        var batch = await client.GetBatchAsync(Guid.Parse("019a9d2e-6f3b-7c41-9a55-0d1f6c2b8e10"));

        Assert.Equal(250, batch!.TotalMessages);
        Assert.Equal(243, batch.Counts[MessageStatus.Delivered]);
        Assert.Equal(2, batch.Counts[MessageStatus.Failed]);
        Assert.Equal(0, batch.Counts.GetValueOrDefault(MessageStatus.Cancelled));
    }

    [Fact]
    public async Task Cancel_posts_to_the_batch()
    {
        var id = Guid.Parse("019a9d2e-6f3b-7c41-9a55-0d1f6c2b8e10");
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, $$"""{ "batchId": "{{id}}", "cancelled": 3 }"""));

        var result = await client.CancelBatchAsync(id);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal($"/v1/batches/{id}/cancel", handler.Requests[0].PathAndQuery);
        Assert.Equal(3, result.Cancelled);
    }

    [Fact]
    public async Task List_messages_sends_the_filters_as_a_query_string()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, $$"""{ "items": [{{Message}}], "page": 2, "pageSize": 200, "total": 201 }"""));

        var page = await client.ListMessagesAsync(new MessageQuery
        {
            Reference = "newsletter 47",
            Status = MessageStatus.Failed,
            Recipient = "+354 780 1512",
            From = new DateOnly(2026, 11, 1),
            To = new DateOnly(2026, 11, 30),
            Page = 2,
            PageSize = 200,
        });

        Assert.Equal(
            "/v1/messages?page=2&pageSize=200&reference=newsletter%2047&status=Failed&recipient=%2B354%20780%201512&from=2026-11-01&to=2026-11-30",
            handler.Requests[0].PathAndQuery);
        Assert.Equal(201, page.Total);
        Assert.Equal("3547801512", Assert.Single(page.Items).To);
    }

    [Fact]
    public async Task Usage_report_sends_the_range_and_grouping()
    {
        var project = Guid.Parse("019a9d2a-11aa-7b3c-8d4e-5f60718293a4");
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, """
            {
              "from": "2026-11-01T00:00:00+00:00", "to": "2026-11-30T23:59:59.9999999+00:00", "groupBy": "Country",
              "rows": [ { "key": "IS", "currency": "ISK", "messages": 1210, "segments": 1675, "delivered": 1196, "failed": 9, "amount": 16750 } ]
            }
            """));

        var report = await client.GetUsageAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), UsageGroupBy.Country, project);

        Assert.Equal($"/v1/reports/usage?from=2026-11-01&to=2026-11-30&groupBy=Country&projectId={project}", handler.Requests[0].PathAndQuery);
        Assert.Equal(UsageGroupBy.Country, report.GroupBy);
        var row = Assert.Single(report.Rows);
        Assert.Equal(("IS", 1675, 16750m), (row.Key, row.Segments, row.Amount));
    }

    [Fact]
    public void Missing_settings_are_reported_when_the_client_is_created()
    {
        Assert.Throws<ArgumentException>(() => new LeifturClient(new HttpClient(), new LeifturClientOptions { ApiKey = "lf_test_key" }));
        Assert.Throws<ArgumentException>(() => new LeifturClient(new HttpClient(), new LeifturClientOptions { BaseAddress = new Uri("https://api.example.test") }));
    }

    [Fact]
    public void Registers_with_dependency_injection()
    {
        var services = new ServiceCollection();
        services.AddLeifturClient(o =>
        {
            o.BaseAddress = new Uri("https://api.example.test");
            o.ApiKey = "lf_test_key";
        });

        using var provider = services.BuildServiceProvider();
        Assert.IsType<LeifturClient>(provider.GetRequiredService<ILeifturClient>());
    }
}
