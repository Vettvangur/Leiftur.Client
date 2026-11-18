# Leiftur.Client

[![Tests](https://github.com/Vettvangur/Leiftur.Client/actions/workflows/tests.yml/badge.svg)](https://github.com/Vettvangur/Leiftur.Client/actions/workflows/tests.yml)

The official .NET client for the Leiftur SMS service. Send, schedule and track SMS messages from your own
application.

Works in .NET 8 and later. You need a Leiftur account and an API key.

- [Install and set up](#install-and-set-up)
- [Send an SMS](#send-an-sms)
- [Send to many recipients](#send-to-many-recipients)
- [Schedule and cancel](#schedule-and-cancel)
- [Tag messages for billing](#tag-messages-for-billing)
- [Never send twice](#never-send-twice)
- [Check what happened](#check-what-happened)
- [Usage reports](#usage-reports)
- [Handle errors](#handle-errors)
- [Use without dependency injection](#use-without-dependency-injection)
- [Method overview](#method-overview)
- [Licence](#licence)

## Install and set up

```bash
dotnet add package Leiftur.Client
```

Register the client once, in `Program.cs`:

```csharp
builder.Services.AddLeifturClient(options =>
{
    options.BaseAddress = new Uri("https://api.leiftur.is");
    options.ApiKey = builder.Configuration["Leiftur:ApiKey"]!;
});
```

Keep the key out of source control. In development, store it with user secrets:

```bash
dotnet user-secrets set "Leiftur:ApiKey" "lf_3f9a1c0b7d2e_…"
```

Then ask for `ILeifturClient` wherever you need it. All types are in the `Leiftur.Client` namespace.

```csharp
using Leiftur.Client;

public class OrderNotifier(ILeifturClient leiftur)
{
    public Task NotifyReadyAsync(string phone) =>
        leiftur.SendAsync("Acme", phone, "Your order is ready for pickup.");
}
```

| Option | Default | Meaning |
|---|---|---|
| `BaseAddress` | required | Address of the Leiftur API |
| `ApiKey` | required | Your API key |
| `MaxRetries` | `3` | Retries after a network error, a timeout, or a `429`/`5xx` answer |

## Send an SMS

```csharp
SendSmsResponse result = await leiftur.SendAsync(
    from: "Acme",          // a sender name approved for your account
    to: "7801512",         // local Icelandic number, or +45… / 0045… for other countries
    body: "Your order is ready for pickup.");

Console.WriteLine(result.Messages[0].Id);       // keep this to look the message up later
Console.WriteLine(result.Messages[0].Status);   // Queued
Console.WriteLine(result.TotalSegments);        // 1
Console.WriteLine($"{result.EstimatedPrice} {result.Currency}");   // 10 ISK
```

A successful call means the message is accepted and queued. Delivery happens afterwards; see
[Check what happened](#check-what-happened).

Icelandic letters such as `þ`, `ð` and `á` shorten a single SMS from 160 to 70 characters, so a message can cost
more segments than it looks. `TotalSegments` and `EstimatedPrice` tell you what a send will cost.

## Send to many recipients

One request takes up to 10,000 numbers. A number that appears twice is sent once.

```csharp
var result = await leiftur.SendAsync(new SendSmsRequest
{
    From = "Acme",
    To = ["7801512", "7801513", "+4520123456"],
    Body = "Opið til 22 í kvöld.",
});

Console.WriteLine($"{result.TotalMessages} messages, {result.TotalSegments} segments");

foreach (var message in result.Messages)
{
    Console.WriteLine($"{message.To} ({message.CountryCode}): {message.Segments} segment(s)");
}
// 3547801512 (IS): 1 segment(s)
// 3547801513 (IS): 1 segment(s)
// 4520123456 (DK): 1 segment(s)
```

If any number is invalid, or a country is not enabled for your account, the whole request is refused and
nothing is sent. See [Handle errors](#handle-errors).

For more than 10,000 recipients, split the list:

```csharp
foreach (var chunk in allNumbers.Chunk(10_000))
{
    await leiftur.SendAsync(new SendSmsRequest { From = "Acme", To = chunk, Body = text, Reference = "newsletter-47" });
}
```

## Schedule and cancel

```csharp
var reminder = await leiftur.SendAsync(
    "Acme", "7801512", "Reminder: your appointment is tomorrow at 10:00.",
    scheduledAt: new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero));

// Changed your mind before it went out:
CancelBatchResponse cancelled = await leiftur.CancelBatchAsync(reminder.BatchId);
Console.WriteLine(cancelled.Cancelled);   // 1
```

Cancelling only affects messages that have not been sent. Cancelled messages are not billed. Calling cancel
twice is harmless.

## Tag messages for billing

`Reference` is your own identifier, for example the customer you will re-invoice. `ProjectId` bills one of your
projects. Both can be used to group the usage report.

```csharp
await leiftur.SendAsync(new SendSmsRequest
{
    From = "Acme",
    To = ["7801512"],
    Body = "Your table is booked for 19:00.",
    Reference = "restaurant-harbour",          // who to bill
    Tags = ["booking-confirmation"],           // free labels
    ProjectId = Guid.Parse("019a9d2a-11aa-7b3c-8d4e-5f60718293a4"),
});
```

## Never send twice

A network failure can leave you not knowing whether a send arrived. The client handles this for you: each
`SendAsync` call carries a unique key, and its own retries reuse that key, so the API recognises the repeat and
sends nothing new.

To also protect against *your application* repeating the call (a retried job, a double click), pass a key of
your own that identifies the thing being sent:

```csharp
var request = new SendSmsRequest { From = "Acme", To = [order.Phone], Body = "Your order is ready." };

var first  = await leiftur.SendAsync(request, idempotencyKey: $"order-{order.Id}-ready");
var second = await leiftur.SendAsync(request, idempotencyKey: $"order-{order.Id}-ready");

Console.WriteLine(first.BatchId == second.BatchId);   // True
Console.WriteLine(second.Replayed);                   // True: nothing was sent the second time
```

## Check what happened

```csharp
MessageDto? message = await leiftur.GetMessageAsync(messageId);

if (message is null)
{
    // No such message (or it belongs to another account).
}
else if (message.Status == MessageStatus.Delivered)
{
    Console.WriteLine($"Delivered at {message.DeliveredAt}");
}
else if (message.Status == MessageStatus.Failed)
{
    Console.WriteLine($"Failed: {message.Error}");
}
```

| Status | Meaning |
|---|---|
| `Scheduled` | Waiting for its send time |
| `Queued` | Waiting to be handed to the carrier |
| `Sending` | Being handed to the carrier right now |
| `Sent` | With the carrier; no delivery receipt yet |
| `Delivered` | Arrived on the handset |
| `Failed` | Permanently failed; `Error` says why |
| `Cancelled` | Cancelled before sending |
| `Unknown` | The carrier did not answer, so it is not known whether it went out. It is not retried automatically. |

Progress of a whole send:

```csharp
BatchDto? batch = await leiftur.GetBatchAsync(result.BatchId);

var delivered = batch!.Counts.GetValueOrDefault(MessageStatus.Delivered);
var failed    = batch.Counts.GetValueOrDefault(MessageStatus.Failed);
Console.WriteLine($"{delivered} of {batch.TotalMessages} delivered, {failed} failed");
```

Search:

```csharp
PagedResult<MessageDto> failedThisMonth = await leiftur.ListMessagesAsync(new MessageQuery
{
    Reference = "newsletter-47",
    Status = MessageStatus.Failed,
    From = new DateOnly(2026, 11, 1),
    To = new DateOnly(2026, 11, 30),
    PageSize = 200,
});

Console.WriteLine($"{failedThisMonth.Total} failed");
foreach (var m in failedThisMonth.Items)
{
    Console.WriteLine($"{m.To}: {m.Error}");
}
```

`MessageQuery` can also filter on `BatchId`, `ProjectId` and `Recipient`. Results are newest first; use `Page`
to read further.

## Usage reports

```csharp
UsageReport report = await leiftur.GetUsageAsync(
    from: new DateOnly(2026, 11, 1),
    to: new DateOnly(2026, 11, 30),
    groupBy: UsageGroupBy.Reference);

foreach (var row in report.Rows)
{
    Console.WriteLine($"{row.Key}: {row.Messages} messages, {row.Segments} segments, {row.Amount} {row.Currency}");
}
// newsletter-47: 1210 messages, 1675 segments, 16750 ISK
// restaurant-harbour: 86 messages, 86 segments, 860 ISK
```

`groupBy` can be `Day`, `Month`, `Country`, `Project` or `Reference`. Pass `projectId` to limit the report to
one project. Failed messages are counted but cost nothing; cancelled ones are left out.

## Handle errors

When the API refuses a request, the client throws `LeifturApiException`:

```csharp
try
{
    await leiftur.SendAsync(new SendSmsRequest { From = "Acme", To = ["7801512", "12"], Body = "Hi" });
}
catch (LeifturApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
{
    foreach (var problem in ex.Errors)
    {
        Console.WriteLine(problem);   // Invalid phone number: 12
    }
}
catch (LeifturApiException ex) when (ex.StatusCode == HttpStatusCode.PaymentRequired)
{
    // The monthly spending limit would be exceeded.
    Console.WriteLine(ex.Message);
}
```

| `StatusCode` | Meaning |
|---|---|
| `400 BadRequest` | Invalid field or phone number, or a country without a price |
| `401 Unauthorized` | Wrong or revoked key, or called from an IP address the key does not allow |
| `402 PaymentRequired` | The monthly spending limit would be exceeded |
| `403 Forbidden` | Sender name not approved, country not enabled, or the key lacks permission |
| `429 TooManyRequests` | Too many requests; only thrown after the retries are used up |

Network errors and timeouts are retried `MaxRetries` times with increasing pauses. If they all fail, the
original `HttpRequestException` or `TaskCanceledException` is thrown. It is safe to call `SendAsync` again after
that if you pass your own `idempotencyKey`.

## Use without dependency injection

```csharp
using var http = new HttpClient();
var leiftur = new LeifturClient(http, new LeifturClientOptions
{
    BaseAddress = new Uri("https://api.leiftur.is"),
    ApiKey = Environment.GetEnvironmentVariable("LEIFTUR_API_KEY")!,
});

await leiftur.SendAsync("Acme", "7801512", "Hello");
```

Create one `LeifturClient` and reuse it; do not create one per message.

## Method overview

| Method | Returns | Purpose |
|---|---|---|
| `SendAsync(from, to, body, reference?, scheduledAt?)` | `SendSmsResponse` | One recipient |
| `SendAsync(SendSmsRequest, idempotencyKey?)` | `SendSmsResponse` | Any number of recipients, all options |
| `GetMessageAsync(id)` | `MessageDto?` | One message, or `null` if not found |
| `GetBatchAsync(id)` | `BatchDto?` | One send request with status counts, or `null` |
| `CancelBatchAsync(id)` | `CancelBatchResponse` | Cancel what has not been sent |
| `ListMessagesAsync(MessageQuery?)` | `PagedResult<MessageDto>` | Search |
| `GetUsageAsync(from, to, groupBy, projectId?)` | `UsageReport` | Billable usage |

Every method also takes a `CancellationToken`.

## Licence

[MIT](LICENSE)
