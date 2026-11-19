# Leiftur.Client

[![Tests](https://github.com/Vettvangur/Leiftur.Client/actions/workflows/tests.yml/badge.svg)](https://github.com/Vettvangur/Leiftur.Client/actions/workflows/tests.yml)

The official .NET client for the Leiftur SMS service. Send, schedule and track SMS messages from your own
application.

Works in .NET 8 and later. You need a Leiftur account and an API key.

- [Install and set up](#install-and-set-up)
- [Send an SMS](#send-an-sms)
- [Send to many recipients](#send-to-many-recipients)
- [Personal messages and templates](#personal-messages-and-templates)
- [Contact lists](#contact-lists)
- [Verification codes](#verification-codes)
- [Schedule and cancel](#schedule-and-cancel)
- [Tag messages for billing](#tag-messages-for-billing)
- [Never send twice](#never-send-twice)
- [Check what happened](#check-what-happened)
- [Get told when a message is delivered: webhooks](#get-told-when-a-message-is-delivered-webhooks)
- [People who must not be contacted: the suppression list](#people-who-must-not-be-contacted-the-suppression-list)
- [Usage reports](#usage-reports)
- [Monthly statement](#monthly-statement)
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

## Personal messages and templates

A text can contain placeholders that are filled in for each recipient.

| Written as | Means |
|---|---|
| `{{name}}` | The recipient's value for `name`. If there is none, the send is refused. |
| `{{name\|vinur}}` | The same, but use `vinur` when there is no value |

```csharp
var result = await leiftur.SendAsync(new SendSmsRequest
{
    From = "Acme",
    Body = "Hæ {{name|vinur}}, pöntun {{order}} er tilbúin í {{store}}.",
    Data = new Dictionary<string, string> { ["store"] = "Kringlunni" },      // the same for everyone
    Recipients =
    [
        new SendRecipient("7801512", ("name", "Anna"), ("order", "1001")),
        new SendRecipient("7801513", ("order", "1002"), ("store", "Smáralind")),   // a recipient's own value wins
    ],
});
// 780 1512 receives: Hæ Anna, pöntun 1001 er tilbúin í Kringlunni.
// 780 1513 receives: Hæ vinur, pöntun 1002 er tilbúin í Smáralind.
```

Things to know:

- **A missing value stops the whole send.** If any recipient lacks a value that has no fallback, the request is
  refused with a `LeifturApiException` (400) naming the recipient and the placeholder, and nothing is sent.
  Give a fallback (`{{name|vinur}}`) wherever a value might be missing.
- **A plain send is never altered.** Placeholders are only filled in when the request has `Data`,
  `Recipients`, `TemplateId` or `ContactListId`. `SendAsync("Acme", "7801512", "Use {{name}} here")` arrives
  exactly as written.
- **Segments are counted per recipient.** A longer name, or one with an Icelandic letter, can change what a
  message costs. `result.TotalSegments` and `result.Messages[i].Segments` have the real numbers.
- **Personal messages go out more slowly.** Recipients who end up with the same text are sent together.
  Recipients who each get a different text are sent one at a time, so a large personalised send takes much
  longer than the same text to everyone.

### Stored templates

Keep a text on the server and refer to it by id, so it can be changed in one place without a new release of
your application.

```csharp
TemplateDto template = await leiftur.CreateTemplateAsync("Order ready", "Hæ {{name|vinur}}, pöntun {{order}} er tilbúin.");
Console.WriteLine(string.Join(", ", template.Fields));   // name, order

// See it filled in, and what it costs, before sending anything:
TemplatePreview preview = await leiftur.PreviewTemplateAsync(template.Id, new Dictionary<string, string> { ["order"] = "1001" });
Console.WriteLine(preview.Body);       // Hæ vinur, pöntun 1001 er tilbúin.
Console.WriteLine(preview.Segments);   // 1
Console.WriteLine(preview.Missing.Count);   // 0: nothing is left unfilled

await leiftur.SendAsync(new SendSmsRequest
{
    From = "Acme",
    TemplateId = template.Id,
    Recipients = [new SendRecipient("7801512", ("name", "Anna"), ("order", "1001"))],
});
```

```csharp
IReadOnlyList<TemplateDto> all = await leiftur.ListTemplatesAsync();
TemplateDto? one = await leiftur.GetTemplateAsync(template.Id);            // null if it does not exist
await leiftur.UpdateTemplateAsync(template.Id, "Order ready", "Pöntun {{order}} er tilbúin.");
bool deleted = await leiftur.DeleteTemplateAsync(template.Id);
```

Template names are unique within your account. Changing a template does not affect messages already accepted.

## Contact lists

A named list of numbers, each with its own values for placeholders.

```csharp
ContactListDto list = await leiftur.CreateContactListAsync("Newsletter");

AddContactsResponse added = await leiftur.AddContactsAsync(list.Id,
[
    new ContactInput("7801512", ("name", "Anna")),
    new ContactInput("+354 780 1513", ("name", "Jón")),
    new ContactInput("7801514"),
]);
Console.WriteLine($"{added.Added} added, {added.Updated} updated, {added.Invalid.Count} invalid");
```

- Up to 10,000 contacts per call. Valid ones are stored even if others in the same call are not; the rejected
  inputs come back in `Invalid`.
- Adding a number that is already on the list **replaces its data**. It is never listed twice.

Send to everyone on the list:

```csharp
var result = await leiftur.SendAsync(new SendSmsRequest
{
    From = "Acme",
    ContactListId = list.Id,
    Body = "Hæ {{name|vinur}}, útsalan er byrjuð.",
    Reference = "sale-november",
});

Console.WriteLine($"{result.TotalMessages} sent, {result.Suppressed.Count} skipped because they opted out");
```

Each contact's stored values fill the placeholders. People on your suppression list are skipped, as with any
send. A list with more than 10,000 contacts cannot be sent to in one request.

```csharp
PagedResult<ContactDto> page = await leiftur.ListContactsAsync(list.Id, page: 1, pageSize: 100);
IReadOnlyList<ContactListDto> lists = await leiftur.ListContactListsAsync();     // each with its number of contacts
bool removed = await leiftur.RemoveContactAsync(list.Id, "7801512");
bool gone = await leiftur.DeleteContactListAsync(list.Id);                       // the list and all its contacts
```

Taking someone off a contact list is not an opt-out: it only affects that list. To make sure a person is never
contacted, add them to the [suppression list](#people-who-must-not-be-contacted-the-suppression-list).

## Verification codes

Proves that a person holds a phone number, for sign-up, login or confirming a change. One call sends a code,
another checks what the person typed. Your application never sees or stores the code.

```csharp
// 1. The person enters their phone number.
VerificationDto verification = await leiftur.StartVerificationAsync("7801512", from: "Acme");
// They receive: Staðfestingarkóðinn þinn er 482913.
// Keep verification.Id (in the session, for example) for the next step.

// 2. The person types the code they received.
VerificationCheckResult check = await leiftur.CheckVerificationAsync(verification.Id, typedCode);

if (check.Valid)
{
    // The number is theirs.
}
else if (check.Status == VerificationStatus.Pending)
{
    // Wrong code. They may try again: check.AttemptsRemaining attempts are left.
}
else
{
    // Expired, locked after too many wrong codes, or replaced by a newer code. Start again.
}
```

**Act on `check.Valid`.** It is `true` only for the one check that confirmed the code.

| `Status` | Meaning |
|---|---|
| `Pending` | Wrong code; the person can try again |
| `Approved` | The code was right |
| `Failed` | Five wrong codes; this verification is locked |
| `Expired` | The time ran out |
| `Superseded` | A newer code was sent to the same number |

To choose the text, the length or how long the code lasts:

```csharp
await leiftur.StartVerificationAsync(new StartVerificationRequest
{
    To = "7801512",
    From = "Acme",
    Message = "Acme: {{code}} er kóðinn þinn. Ekki deila honum.",   // must contain {{code}}
    CodeLength = 4,              // 4 to 8 digits, default 6
    ExpiresInSeconds = 600,      // 60 to 3600, default 300
});

// Or the standard text in English:
await leiftur.StartVerificationAsync(new StartVerificationRequest { To = "7801512", From = "Acme", Language = "en" });
// Your verification code is 482913.
```

The rules, so you know what to expect:

- **Five attempts per code.** After five wrong codes the verification is locked, even if the next one is right.
- **A code works once,** and only the newest code sent to a number works.
- **Five codes per number per hour.** A sixth request throws `LeifturApiException` with `429 TooManyRequests`.
  Show the person a "try again later" message; do not retry in a loop.
- **Neither call is retried by the client.** Sending again would text the person a second code, and checking
  again would use up another attempt. If either throws because of a network error, let the person press the
  button again.
- **The code is not kept anywhere you can read it.** The message's `Body` comes back empty from
  `GetMessageAsync` and in webhook events.

A verification SMS is billed like any other message and shows in reports and the statement with the tag
`verification`.

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

### Your own data on a message

`Metadata` takes up to 10 key/value pairs of your own (keys up to 40 characters, values up to 200). They come
back with the message and in webhook events, so the code that receives a delivery event knows what the message
was about without a lookup.

```csharp
var sent = await leiftur.SendAsync(new SendSmsRequest
{
    From = "Acme",
    To = ["7801512"],
    Body = "Your order is ready.",
    Metadata = new Dictionary<string, string> { ["orderId"] = "1001", ["store"] = "Kringlan" },
});

var message = await leiftur.GetMessageAsync(sent.Messages[0].Id);
Console.WriteLine(message!.Metadata["orderId"]);   // 1001
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

## Get told when a message is delivered: webhooks

Instead of asking for each message's status, register an address and Leiftur posts the status changes to it.

### Register

```csharp
WebhookCreatedResponse created = await leiftur.CreateWebhookAsync("https://shop.example.is/leiftur-events");

Console.WriteLine(created.Webhook.Id);
Console.WriteLine(created.Secret);   // "whsec_…": shown only now. Store it; you need it to verify requests.
```

By default you get `message.delivered` and `message.failed`. To choose yourself:

```csharp
await leiftur.CreateWebhookAsync("https://shop.example.is/leiftur-events",
    [WebhookEventTypes.MessageSent, WebhookEventTypes.MessageDelivered, WebhookEventTypes.MessageFailed]);
```

The address must be a public `https://` address. An account can have up to 5 webhooks.

### Receive

Each request carries one or more events (up to 100) and a `Leiftur-Signature` header. Check the signature
before trusting the content: anyone who learns the address could post to it.

```csharp
app.MapPost("/leiftur-events", async (HttpRequest request, IConfiguration config) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();   // the raw body, exactly as received

    var genuine = LeifturWebhook.TryRead(
        secret: config["Leiftur:WebhookSecret"]!,
        signatureHeader: request.Headers[LeifturWebhook.SignatureHeaderName],
        body: body,
        out WebhookPayload payload);
    if (!genuine) return Results.Unauthorized();

    foreach (var e in payload.Events)
    {
        switch (e.Type)
        {
            case WebhookEventTypes.MessageDelivered:
                Console.WriteLine($"Order {e.Message!.Metadata.GetValueOrDefault("orderId")}: delivered to {e.Message.To}");
                break;
            case WebhookEventTypes.MessageFailed:
                Console.WriteLine($"Could not reach {e.Message!.To}: {e.Message.Error}");
                break;
        }
    }
    return Results.Ok();
});
```

Three rules for the receiving side:

- **Answer `2xx` within 10 seconds.** Do slow work after answering. If you don't answer in time, the same
  events are sent again: after 30 seconds, 2 minutes, 10 minutes, and so on for about a day and a half.
- **The same event can arrive twice.** `e.Id` stays the same across retries; remember the ids you have handled
  and skip repeats.
- **Verify the raw body.** Parsing the JSON and writing it out again changes the bytes, and the check fails.

`TryRead` also refuses requests older than five minutes, so a captured request cannot be replayed later. If
you only want the check, or the parsing, use `LeifturWebhook.VerifySignature` and `LeifturWebhook.Parse`.

### Test and troubleshoot

```csharp
await leiftur.SendTestWebhookAsync(created.Webhook.Id);   // sends a "webhook.test" event, with no message

foreach (var d in await leiftur.ListWebhookDeliveriesAsync(created.Webhook.Id))
{
    Console.WriteLine($"{d.EventType}: {d.Status} after {d.Attempts} attempt(s) {d.LastError}");
}
// webhook.test: Delivered after 1 attempt(s)
// message.delivered: Pending after 2 attempt(s) The endpoint answered 500.

IReadOnlyList<WebhookDto> all = await leiftur.ListWebhooksAsync();
bool removed = await leiftur.DeleteWebhookAsync(created.Webhook.Id);   // also drops events still waiting for it
```

## People who must not be contacted: the suppression list

A list of numbers your account must not send to, usually people who asked not to be contacted. Every send is
checked against it.

Recipients cannot opt out by replying to an SMS, so offer another way (a link, a phone number) and add the
number when someone uses it:

```csharp
AddSuppressionsResponse added = await leiftur.AddSuppressionsAsync(
    ["7801512", "+354 780 1513"],
    reason: SuppressionReasons.OptOut,
    note: "Unsubscribe link, November newsletter");

Console.WriteLine($"{added.Added} added, {added.AlreadyListed} were already listed");
```

A later send skips those numbers and tells you which:

```csharp
var result = await leiftur.SendAsync(new SendSmsRequest
{
    From = "Acme",
    To = ["7801512", "7801599"],
    Body = "Opið til 22 í kvöld.",
});

Console.WriteLine(result.TotalMessages);              // 1
Console.WriteLine(string.Join(", ", result.Suppressed));   // 3547801512: not sent, not billed
```

If every recipient is suppressed, the send is refused with a `LeifturApiException` (400), because there is
nothing to send.

The list applies to everything your account sends, including one-time codes. If someone opts out of marketing
but must still receive login codes, keep that distinction in your own system and do not add them here.

```csharp
SuppressionDto? entry = await leiftur.GetSuppressionAsync("7801512");    // null if not on the list
PagedResult<SuppressionDto> page = await leiftur.ListSuppressionsAsync(page: 1, pageSize: 100);
bool removed = await leiftur.RemoveSuppressionAsync("7801512");          // sending to it is allowed again
```

| Reason | Use for |
|---|---|
| `SuppressionReasons.OptOut` (default) | The person asked not to be contacted |
| `SuppressionReasons.Complaint` | The person complained |
| `SuppressionReasons.InvalidNumber` | The number does not work |
| `SuppressionReasons.Manual` | Anything else |

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

## Monthly statement

What your account owes for a calendar month: the monthly fee plus what was sent. All amounts are before VAT.

```csharp
BillingStatement statement = await leiftur.GetStatementAsync(2026, 11);

foreach (var line in statement.Usage)
{
    Console.WriteLine($"{line.CountryCode}: {line.Quantity} × {line.UnitPrice} per {line.Unit} = {line.Amount} {line.Currency}");
}
// DK: 44 × 25 per Segment = 1100 ISK
// IS: 1666 × 10 per Segment = 16660 ISK

var total = statement.Totals[0];
Console.WriteLine($"Monthly fee {total.MonthlyFee} + usage {total.Usage} = {total.Total} {total.Currency}");
// Monthly fee 5000 + usage 17760 = 22760 ISK
```

- There is one line per country and price. If your price changed during the month, that country has two lines.
- `Quantity` is what the unit price is multiplied by: segments, or messages when `Unit` is `Message`.
- Failed messages are counted in `FailedMessages` and are not charged. Cancelled messages are left out.
- The current month is a running total. Past months do not change.
- A key that is tied to one project cannot read the statement, because it covers the whole account.

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
| `400 BadRequest` | Invalid field or phone number, a country without a price, or a placeholder with no value |
| `401 Unauthorized` | Wrong or revoked key, or called from an IP address the key does not allow |
| `402 PaymentRequired` | The monthly spending limit would be exceeded |
| `403 Forbidden` | Sender name not approved, country not enabled, or the key lacks permission for this call |
| `409 Conflict` | A template or list with that name already exists |
| `429 TooManyRequests` | Too many requests (thrown after the retries are used up), or too many verification codes to one number |

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
| `GetStatementAsync(year, month)` | `BillingStatement` | What the account owes for a month |
| `CreateTemplateAsync(name, body)` | `TemplateDto` | Store a text with placeholders |
| `ListTemplatesAsync()`, `GetTemplateAsync(id)` | `IReadOnlyList<TemplateDto>`, `TemplateDto?` | Read templates |
| `UpdateTemplateAsync(id, name, body)` | `TemplateDto` | Change a template |
| `DeleteTemplateAsync(id)` | `bool` | Remove a template; `false` if it does not exist |
| `PreviewTemplateAsync(id, data?)` | `TemplatePreview` | See a template filled in, with its segment count |
| `CreateContactListAsync(name)` | `ContactListDto` | Create a list |
| `ListContactListsAsync()` | `IReadOnlyList<ContactListDto>` | Your lists and their sizes |
| `DeleteContactListAsync(id)` | `bool` | Delete a list and its contacts |
| `AddContactsAsync(listId, contacts)` | `AddContactsResponse` | Add contacts, or replace the data of existing ones |
| `ListContactsAsync(listId, page, pageSize)` | `PagedResult<ContactDto>` | The contacts on a list |
| `RemoveContactAsync(listId, number)` | `bool` | Take one number off a list |
| `StartVerificationAsync(to, from)` or `(request)` | `VerificationDto` | Send a verification code |
| `CheckVerificationAsync(id, code)` | `VerificationCheckResult` | Check the code a person typed |
| `GetVerificationAsync(id)` | `VerificationDto?` | Status of a verification, or `null` |
| `AddSuppressionsAsync(numbers, reason?, note?)` | `AddSuppressionsResponse` | Stop sending to numbers |
| `ListSuppressionsAsync(page, pageSize, reason?)` | `PagedResult<SuppressionDto>` | The suppression list |
| `GetSuppressionAsync(number)` | `SuppressionDto?` | One entry, or `null` if not listed |
| `RemoveSuppressionAsync(number)` | `bool` | Allow sending again; `false` if it was not listed |
| `CreateWebhookAsync(url, events?)` | `WebhookCreatedResponse` | Register an address; returns the secret once |
| `ListWebhooksAsync()` | `IReadOnlyList<WebhookDto>` | Your webhooks |
| `DeleteWebhookAsync(id)` | `bool` | Remove a webhook; `false` if it does not exist |
| `SendTestWebhookAsync(id)` | `WebhookDeliveryDto` | Send a test event |
| `ListWebhookDeliveriesAsync(id)` | `IReadOnlyList<WebhookDeliveryDto>` | Recent events and how delivery went |

Every method also takes a `CancellationToken`.

For receiving webhooks: `LeifturWebhook.TryRead`, `LeifturWebhook.VerifySignature` and `LeifturWebhook.Parse`.

Each kind of call needs an API key that is allowed to make it. The permissions are named `messages:send`,
`messages:read`, `reports:read`, `webhooks:manage`, `suppressions:manage`, `templates:manage`,
`contacts:manage` and `verifications:use`; a call without the right one fails with `403 Forbidden`.

## Licence

[MIT](LICENSE)
