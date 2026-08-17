# MillionSend .NET SDK

Official .NET SDK for [MillionSend](https://github.com/MillionSend/millionsend) — a
self-hostable, [Resend](https://resend.com)-wire-compatible email API.

The API is wire-compatible with Resend, so migrating is mostly swapping the
package and pointing the client at your instance. Targets **net8.0**.

## Install

```bash
dotnet add package MillionSend
```

## Quickstart

```csharp
using MillionSend;

var client = new MillionSendClient("ms_123", "https://mail.acme.dev");

var res = await client.EmailSendAsync(new EmailMessage
{
    From = "Acme <onboarding@acme.dev>",
    To = "delivered@resend.dev",
    Subject = "Hello from MillionSend",
    Html = "<strong>It works!</strong>",
});

if (res.Success)
    Console.WriteLine($"sent {res.Content!.Id}");
else
    Console.Error.WriteLine($"{res.Exception!.ErrorName}: {res.Exception.Message}");
```

`To`, `Cc`, `Bcc`, and `ReplyTo` accept a single address or an array — assign a
`string` or a `string[]` directly.

## Configuration

Construct with the convenience constructor, or with `MillionSendClientOptions`:

```csharp
var client = new MillionSendClient(new MillionSendClientOptions
{
    ApiToken = "ms_123",
    ApiUrl = "https://mail.acme.dev",
});
```

- `ApiToken` falls back to the `MILLIONSEND_API_KEY` environment variable. Missing
  key → throws at construction.
- `ApiUrl` falls back to `MILLIONSEND_BASE_URL`, then `http://localhost:3001`.
  MillionSend is self-hosted, so **set this to your deployment in production.**

You may pass your own `HttpClient` as the second argument (for proxies, custom
handlers, or tests): `new MillionSendClient(options, httpClient)`.

## Error handling

No method throws for an API or transport error. Every call returns a
`MillionSendResponse<T>`:

- `Success` — `true` when the call succeeded.
- `Content` — the deserialized response body on success.
- `Exception` — a `MillionSendException` on failure, carrying `StatusCode`
  (`int?`), `ErrorName` (the stable snake_case discriminant, e.g.
  `validation_error`, `not_found`, `sending_paused`), and `Message`.

Transport and client-side failures (the request never reached the API) carry
`StatusCode == null` and `ErrorName == "application_error"`.

```csharp
var res = await client.EmailRetrieveAsync(id);
if (!res.Success && res.Exception!.ErrorName == "not_found")
{
    // ...
}
```

## Resources

### Emails

```csharp
await client.EmailSendAsync(message, idempotencyKey: "unique-key"); // POST /emails
await client.EmailRetrieveAsync(id);                                // GET /emails/{id}
await client.EmailCancelAsync(id);                                  // POST /emails/{id}/cancel (scheduled only)
await client.EmailBatchAsync(new[] { messageA, messageB }, idempotencyKey: "k"); // up to 100
```

`EmailSendAsync` and `EmailBatchAsync` accept an optional `idempotencyKey` — the
only two endpoints that support the `Idempotency-Key` header.

### Contacts

Contacts are team-global: one record per email address (case-insensitive), no
audiences. Creating a duplicate returns a 409 `validation_error`.

```csharp
await client.ContactAddAsync(new ContactCreateOptions
{
    Email = "ada@acme.dev",
    FirstName = "Ada",
    Properties = new() { ["plan"] = "pro" },
});
await client.ContactRetrieveAsync(new ContactAddress { Email = "ada@acme.dev" });
await client.ContactRetrieveAsync(new ContactAddress { Id = contactId });   // by id or email (email wins)
await client.ContactUpdateAsync(new ContactUpdateOptions { Id = contactId, Unsubscribed = true });
await client.ContactDeleteAsync(new ContactAddress { Email = "ada@acme.dev" });
await client.ContactListAsync(new ListOptions { Limit = 50 });

// Topic subscriptions (granular unsubscribe)
await client.ContactTopicsUpdateAsync(new ContactTopicsUpdateOptions
{
    Email = "ada@acme.dev",
    Topics = new() { new ContactTopicUpdate { Id = topicId, Subscription = TopicSubscription.OptOut } },
});
```

A contact is addressable by id **or** email; when both are set, email wins.

### Topics

```csharp
await client.TopicAddAsync(new TopicCreateOptions { Name = "Product updates", DefaultSubscription = TopicSubscription.OptIn });
await client.TopicRetrieveAsync(id);
await client.TopicListAsync();     // unpaginated: bare { data }
await client.TopicDeleteAsync(id);
```

### Broadcasts

Targeting is an optional `SegmentId` and/or `TopicId` — set neither to send to
every contact of the team.

```csharp
var broadcast = await client.BroadcastAddAsync(new BroadcastCreateOptions
{
    From = "Acme <news@acme.dev>",
    Subject = "Launch",
    Html = "<p>Hi {{{FIRST_NAME|there}}}</p>",
    SegmentId = segmentId, // optional
});
await client.BroadcastListAsync();
await client.BroadcastRetrieveAsync(id);
await client.BroadcastUpdateAsync(id, new BroadcastUpdateOptions { Subject = "Launch 🚀" }); // draft only
await client.BroadcastSendAsync(id, scheduledAt: "2026-09-01T09:00:00Z"); // omit to send now
await client.BroadcastCancelAsync(id); // scheduled only
await client.BroadcastDeleteAsync(id); // draft only
```

### Segments (MillionSend extension)

Dynamic segments are a saved filter over the team's contacts — a MillionSend
superset with no Resend equivalent.

```csharp
await client.SegmentAddAsync(new SegmentCreateOptions
{
    Name = "Pro plan",
    Filter = new SegmentFilter
    {
        Match = SegmentMatch.All,
        Conditions = new() { new SegmentCondition { Field = "property:plan", Op = "equals", Value = "pro" } },
    },
});
await client.SegmentRetrieveAsync(id);   // includes a live contact_count
await client.SegmentListAsync();
await client.SegmentUpdateAsync(id, new SegmentUpdateOptions { Name = "Pro tier" });
await client.SegmentDeleteAsync(id);
```

## Migrating from Resend

The `resend-dotnet` SDK exposes resources as `client.Emails.SendAsync(...)` and
throws `ResendException`. MillionSend flattens the surface to
`client.EmailSendAsync(...)` and returns a `MillionSendResponse<T>` (no throw).
Method names and payload shapes otherwise line up. Notes:

- **Domains and API keys** are managed in the MillionSend dashboard, not via the
  API, so there are no domain/api-key methods here.
- **No audiences**: contacts are team-global, one record per email address.
  Resend's audience/segment grouping maps to MillionSend's dynamic **segments**
  (saved filters) above.

## Development

```bash
dotnet test          # unit tests (the e2e tests stay inert without MILLIONSEND_E2E)
```

The e2e tests run only when `MILLIONSEND_E2E=1` opts in (plus the API key):

```bash
MILLIONSEND_E2E=1 MILLIONSEND_API_KEY=ms_... dotnet test --filter Category=e2e
```

## License

MIT
