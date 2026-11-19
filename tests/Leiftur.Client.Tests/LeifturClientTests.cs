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

public class NewFeatureTests
{
    private sealed record Seen(HttpMethod Method, string PathAndQuery, string? Body);

    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(Requests.Count);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (LeifturClient Client, StubHandler Handler) Create(Func<int, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var client = new LeifturClient(new HttpClient(handler), new LeifturClientOptions { BaseAddress = new Uri("https://api.example.test/"), ApiKey = "lf_test_key", MaxRetries = 2 });
        return (client, handler);
    }

    [Fact]
    public async Task Metadata_is_sent_and_suppressed_recipients_are_read_back()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.Accepted, """
            {
              "batchId": "019a9d2e-6f3b-7c41-9a55-0d1f6c2b8e10", "scheduledAt": null, "totalMessages": 1, "totalSegments": 1,
              "estimatedPrice": 6, "currency": "ISK",
              "messages": [ { "id": "019a9d2e-6f3c-7a02-b1d4-5e7f90ab12cd", "to": "3547801599", "countryCode": "IS", "segments": 1, "status": "Queued" } ],
              "replayed": false, "suppressed": ["3547801512"]
            }
            """));

        var result = await client.SendAsync(new SendSmsRequest
        {
            From = "Acme", To = ["7801512", "7801599"], Body = "Hi",
            Metadata = new Dictionary<string, string> { ["orderId"] = "1001" },
        });

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        Assert.Equal("1001", sent.RootElement.GetProperty("metadata").GetProperty("orderId").GetString());
        Assert.Equal(["3547801512"], result.Suppressed);
        Assert.Equal(1, result.TotalMessages);
    }

    [Fact]
    public async Task An_older_api_answer_without_the_new_fields_still_reads()
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, """
            {
              "id": "019a9d2e-6f3c-7a02-b1d4-5e7f90ab12cd", "batchId": "019a9d2e-6f3b-7c41-9a55-0d1f6c2b8e10", "from": "Acme", "to": "3547801512",
              "countryCode": "IS", "body": "Hi", "encoding": "Gsm7", "segments": 1, "status": "Delivered", "tags": [],
              "unitPrice": 6, "price": 6, "currency": "ISK", "createdAt": "2026-11-18T14:02:11+00:00"
            }
            """));

        var message = await client.GetMessageAsync(Guid.NewGuid());

        Assert.Empty(message!.Metadata);
    }

    [Fact]
    public async Task Suppressions_can_be_added_listed_checked_and_removed()
    {
        var (client, handler) = Create(n => n switch
        {
            1 => Json(HttpStatusCode.OK, """{ "added": 2, "alreadyListed": 1, "invalid": ["12"] }"""),
            2 => Json(HttpStatusCode.OK, """{ "items": [ { "number": "3547801512", "reason": "opt_out", "note": "link", "createdAt": "2026-11-18T16:40:00+00:00" } ], "page": 1, "pageSize": 50, "total": 1 }"""),
            3 => Json(HttpStatusCode.OK, """{ "number": "3547801512", "reason": "opt_out", "note": null, "createdAt": "2026-11-18T16:40:00+00:00" }"""),
            4 => new HttpResponseMessage(HttpStatusCode.NotFound),
            5 => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var added = await client.AddSuppressionsAsync(["7801512", "+354 780 1513", "12"], SuppressionReasons.OptOut, "link");
        var list = await client.ListSuppressionsAsync(pageSize: 50, reason: SuppressionReasons.OptOut);
        var found = await client.GetSuppressionAsync("+354 780 1512");
        var missing = await client.GetSuppressionAsync("7809999");
        var removed = await client.RemoveSuppressionAsync("7801512");
        var removedAgain = await client.RemoveSuppressionAsync("7801512");

        Assert.Equal((2, 1), (added.Added, added.AlreadyListed));
        Assert.Equal(["12"], added.Invalid);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(3, body.RootElement.GetProperty("numbers").GetArrayLength());
        Assert.Equal("opt_out", body.RootElement.GetProperty("reason").GetString());

        Assert.Equal("/v1/suppressions?page=1&pageSize=50&reason=opt_out", handler.Requests[1].PathAndQuery);
        Assert.Equal("3547801512", list.Items.Single().Number);
        Assert.Equal("/v1/suppressions/%2B354%20780%201512", handler.Requests[2].PathAndQuery);
        Assert.Equal("opt_out", found!.Reason);
        Assert.Null(missing);
        Assert.Equal((HttpMethod.Delete, "/v1/suppressions/7801512"), (handler.Requests[4].Method, handler.Requests[4].PathAndQuery));
        Assert.True(removed);
        Assert.False(removedAgain);
    }

    [Fact]
    public async Task Webhooks_can_be_created_listed_tested_and_deleted()
    {
        var id = Guid.Parse("019a9d40-2b7c-7e11-a3f0-9c8d7e6f5a4b");
        var webhook = $$"""{ "id": "{{id}}", "url": "https://shop.example.is/hook", "events": ["message.delivered", "message.failed"], "isActive": true, "createdAt": "2026-11-18T16:10:00+00:00" }""";
        var delivery = """{ "id": "019a9d41-77aa-7c02-8e19-2f3a4b5c6d7e", "eventType": "webhook.test", "messageId": null, "status": "Pending", "attempts": 0, "lastStatusCode": null, "lastError": null, "createdAt": "2026-11-18T16:10:30+00:00", "deliveredAt": null, "nextAttemptAt": "2026-11-18T16:10:30+00:00" }""";
        var (client, handler) = Create(n => n switch
        {
            1 => Json(HttpStatusCode.Created, $$"""{ "webhook": {{webhook}}, "secret": "whsec_abc" }"""),
            2 => Json(HttpStatusCode.OK, $"[{webhook}]"),
            3 => Json(HttpStatusCode.Accepted, delivery),
            4 => Json(HttpStatusCode.OK, $"[{delivery}]"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });

        var created = await client.CreateWebhookAsync("https://shop.example.is/hook", [WebhookEventTypes.MessageDelivered, WebhookEventTypes.MessageFailed]);
        var all = await client.ListWebhooksAsync();
        var test = await client.SendTestWebhookAsync(id);
        var log = await client.ListWebhookDeliveriesAsync(id);
        var deleted = await client.DeleteWebhookAsync(id);

        Assert.Equal("whsec_abc", created.Secret);
        Assert.Equal(id, created.Webhook.Id);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("https://shop.example.is/hook", body.RootElement.GetProperty("url").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("events").GetArrayLength());
        Assert.Equal(id, all.Single().Id);
        Assert.Equal(("webhook.test", "Pending"), (test.EventType, test.Status));
        Assert.Equal($"/v1/webhooks/{id}/deliveries", handler.Requests[3].PathAndQuery);
        Assert.Single(log);
        Assert.True(deleted);
    }

    [Fact]
    public async Task Creating_a_webhook_is_not_retried_because_a_repeat_would_register_it_twice()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<LeifturApiException>(() => client.CreateWebhookAsync("https://shop.example.is/hook"));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_statement_is_asked_for_by_month_and_read_in_full()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, """
            {
              "clientId": "019a9d20-0000-7000-8000-000000000001", "clientName": "Acme ehf.", "month": "2026-11", "from": "2026-11-01", "to": "2026-11-30",
              "usage": [
                { "countryCode": "DK", "unit": "Segment", "unitPrice": 25, "messages": 40, "segments": 44, "quantity": 44, "amount": 1100, "currency": "ISK" },
                { "countryCode": "IS", "unit": "Message", "unitPrice": 10, "messages": 1201, "segments": 1666, "quantity": 1201, "amount": 12010, "currency": "ISK" }
              ],
              "failedMessages": 10,
              "totals": [ { "currency": "ISK", "monthlyFee": 5000, "usage": 13110, "total": 18110 } ],
              "amountsExcludeVat": true
            }
            """));

        var statement = await client.GetStatementAsync(2026, 11);

        Assert.Equal("/v1/reports/statement?month=2026-11", handler.Requests.Single().PathAndQuery);
        Assert.Equal(new DateOnly(2026, 11, 30), statement.To);
        Assert.Equal((BillingUnit.Message, 1201, 12010m), (statement.Usage[1].Unit, statement.Usage[1].Quantity, statement.Usage[1].Amount));
        Assert.Equal(10, statement.FailedMessages);
        var total = statement.Totals.Single();
        Assert.Equal((5000m, 13110m, 18110m), (total.MonthlyFee, total.Usage, total.Total));
        Assert.True(statement.AmountsExcludeVat);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetStatementAsync(2026, 13));
    }
}

public class LeifturWebhookTests
{
    private const string Secret = "whsec_4f1c0d";

    private const string Body = """
        {"events":[{"id":"019a9d41-77aa-7c02-8e19-2f3a4b5c6d7e","type":"message.delivered","createdAt":"2026-11-18T16:12:15.537+00:00",
          "message":{"id":"019a9d2e-6f3c-7a02-b1d4-5e7f90ab12cd","batchId":"019a9d2e-6f3b-7c41-9a55-0d1f6c2b8e10","from":"Acme","to":"3547801512",
            "countryCode":"IS","body":"Hi","encoding":"Gsm7","segments":1,"status":"Delivered","reference":"customer-42","tags":[],
            "metadata":{"orderId":"1001"},"unitPrice":6,"price":6,"currency":"ISK","createdAt":"2026-11-18T16:12:11+00:00",
            "deliveredAt":"2026-11-18T16:12:15.537+00:00"}},
         {"id":"019a9d41-77aa-7c02-8e19-2f3a4b5c6d7f","type":"webhook.test","createdAt":"2026-11-18T16:12:16+00:00","message":null}]}
        """;

    private static readonly DateTimeOffset SentAt = new(2026, 11, 18, 16, 12, 16, TimeSpan.Zero);

    private static string Header(string secret = Secret, string? body = null) =>
        $"t={SentAt.ToUnixTimeSeconds()},v1={LeifturWebhook.ComputeSignature(secret, SentAt.ToUnixTimeSeconds(), body ?? Body)}";

    [Fact]
    public void A_request_signed_with_the_secret_is_accepted()
    {
        Assert.True(LeifturWebhook.VerifySignature(Secret, Header(), Body, now: SentAt.AddSeconds(3)));
    }

    [Fact]
    public void The_signature_is_hmac_sha256_over_timestamp_dot_body_in_lowercase_hex()
    {
        // Worked out independently, so the client and the server cannot drift apart unnoticed.
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes("whsec_test"));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("1795017600.{\"events\":[]}"))).ToLowerInvariant();

        Assert.Equal(expected, LeifturWebhook.ComputeSignature("whsec_test", 1795017600, "{\"events\":[]}"));
        Assert.Equal(64, expected.Length);
    }

    [Fact]
    public void Anything_that_does_not_match_is_refused()
    {
        var now = SentAt.AddSeconds(3);
        Assert.False(LeifturWebhook.VerifySignature("whsec_other", Header(), Body, now: now));                       // wrong secret
        Assert.False(LeifturWebhook.VerifySignature(Secret, Header(), Body + " ", now: now));                         // body changed
        Assert.False(LeifturWebhook.VerifySignature(Secret, Header(), Body, now: SentAt.AddMinutes(6)));              // too old: a replay
        Assert.False(LeifturWebhook.VerifySignature(Secret, Header(), Body, now: SentAt.AddMinutes(-6)));             // from the future
        Assert.True(LeifturWebhook.VerifySignature(Secret, Header(), Body, tolerance: TimeSpan.FromHours(1), now: SentAt.AddMinutes(30)));
        Assert.False(LeifturWebhook.VerifySignature(Secret, null, Body, now: now));
        Assert.False(LeifturWebhook.VerifySignature(Secret, "", Body, now: now));
        Assert.False(LeifturWebhook.VerifySignature(Secret, "garbage", Body, now: now));
        Assert.False(LeifturWebhook.VerifySignature(Secret, $"t={SentAt.ToUnixTimeSeconds()}", Body, now: now));     // no signature
        Assert.False(LeifturWebhook.VerifySignature(Secret, "t=99999999999999999,v1=abc", Body, now: now));           // absurd time
    }

    [Fact]
    public void The_events_are_read_with_their_messages()
    {
        var payload = LeifturWebhook.Parse(Body);

        Assert.Equal(2, payload.Events.Count);
        var delivered = payload.Events[0];
        Assert.Equal(WebhookEventTypes.MessageDelivered, delivered.Type);
        Assert.Equal(MessageStatus.Delivered, delivered.Message!.Status);
        Assert.Equal("customer-42", delivered.Message.Reference);
        Assert.Equal("1001", delivered.Message.Metadata["orderId"]);
        Assert.Equal(WebhookEventTypes.Test, payload.Events[1].Type);
        Assert.Null(payload.Events[1].Message);
    }

    [Fact]
    public void Reading_and_verifying_in_one_step_gives_no_events_for_a_bad_signature()
    {
        var recent = $"t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()},v1={LeifturWebhook.ComputeSignature(Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Body)}";

        Assert.True(LeifturWebhook.TryRead(Secret, recent, Body, out var payload));
        Assert.Equal(2, payload.Events.Count);

        Assert.False(LeifturWebhook.TryRead("whsec_other", recent, Body, out var none));
        Assert.Empty(none.Events);
    }
}
