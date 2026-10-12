using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MillionSend;

// Inputs are PascalCase and mapped to the wire's snake_case by the shared
// JsonSerializerOptions (JsonNamingPolicy.SnakeCaseLower). Responses are the wire
// shape verbatim, deserialized by the same policy.

// ---- shared --------------------------------------------------------------

/// <summary>One or more email addresses. Assign a <see cref="string"/> or a
/// <c>string[]</c>/<c>List&lt;string&gt;</c> directly.</summary>
[JsonConverter(typeof(RecipientsConverter))]
public sealed class Recipients
{
    private readonly string? _single;
    private readonly IReadOnlyList<string>? _many;

    private Recipients(string single) => _single = single;
    private Recipients(IReadOnlyList<string> many) => _many = many;

    public static implicit operator Recipients(string value) => new(value);
    public static implicit operator Recipients(string[] values) => new(values);
    public static implicit operator Recipients(List<string> values) => new(values);

    internal void Write(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        if (_single is not null) writer.WriteStringValue(_single);
        else JsonSerializer.Serialize(writer, _many, options);
    }
}

internal sealed class RecipientsConverter : JsonConverter<Recipients>
{
    public override Recipients Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString()!;
        var list = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? new List<string>();
        return list;
    }

    public override void Write(Utf8JsonWriter writer, Recipients value, JsonSerializerOptions options)
        => value.Write(writer, options);
}

/// <summary>
/// Tri-state PATCH field. Left unset (or assigned a C# <c>null</c>) the field is
/// omitted from the body and the server leaves it unchanged; assigned a value it is
/// sent as-is; assigned <see cref="Null"/> it is sent as JSON <c>null</c>, which
/// clears the stored value. Plain values convert implicitly.
/// </summary>
[JsonConverter(typeof(OptionalConverterFactory))]
public sealed class Optional<T>
{
    public T? Value { get; }

    internal Optional(T? value) => Value = value;

    /// <summary>Explicit JSON <c>null</c>: clears the field on the server.</summary>
    public static Optional<T> Null { get; } = new(default);

    public static implicit operator Optional<T>?(T? value) => value is null ? null : new(value);
}

internal sealed class OptionalConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(OptionalConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

internal sealed class OptionalConverter<T> : JsonConverter<Optional<T>>
{
    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(JsonSerializer.Deserialize<T>(ref reader, options));

    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        if (value.Value is null) writer.WriteNullValue();
        else JsonSerializer.Serialize(writer, value.Value, options);
    }
}

public sealed class Tag
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

/// <summary>Keyset list options. <see cref="After"/> and <see cref="Before"/> are
/// mutually exclusive cursors.</summary>
public class ListOptions
{
    /// <summary>1–100; the API defaults to 20.</summary>
    public int? Limit { get; init; }
    public Guid? After { get; init; }
    public Guid? Before { get; init; }
}

public enum TopicSubscription { OptIn, OptOut }
public enum SegmentMatch { All, Any }

/// <summary>The <c>x-batch-validation</c> header on batch endpoints. <c>Strict</c>
/// (the server default) rejects the whole batch on one invalid item; <c>Permissive</c>
/// writes the valid items and reports the rest in <c>errors</c>.</summary>
public enum BatchValidationMode { Strict, Permissive }

public enum ContactBatchOnConflict { Error, Skip, Upsert }
/// <summary>Facets a contact list row or batch read can carry (<c>include=properties,topics</c>).</summary>
public enum ContactInclude { Properties, Topics }
public enum SuppressionOrigin { Bounce, Complaint, Manual, Unsubscribe }
public enum ApiKeyPermission { FullAccess, SendingAccess }
public enum WebhookStatus { Enabled, Disabled }
public enum ContactPropertyType { String, Number }
public enum TopicVisibility { Private, Public }

/// <summary><c>{ object, id }</c> acknowledgement returned by create/update actions.</summary>
public sealed class ObjectId
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
}

/// <summary><c>{ object, id, deleted }</c> acknowledgement returned by delete actions.</summary>
public sealed class DeletedResponse
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public bool Deleted { get; init; }
}

/// <summary>One rejected batch item (permissive validation only).</summary>
public sealed class BatchError
{
    /// <summary>Position of the item in the request array.</summary>
    public int Index { get; init; }
    public string? Message { get; init; }
}

/// <summary>Envelope for endpoints that return a bare <c>{ "data": [...] }</c>
/// (batch send, topics list, suppression batches).</summary>
public sealed class DataResponse<T>
{
    public List<T> Data { get; init; } = new();
    /// <summary>Rejected items; present only for a batch sent with
    /// <see cref="BatchValidationMode.Permissive"/>.</summary>
    public List<BatchError>? Errors { get; init; }
}

/// <summary>Paginated list envelope: <c>{ object:"list", data:[], has_more }</c>.</summary>
public class ListResponse<T>
{
    public string? Object { get; init; }
    public List<T> Data { get; init; } = new();
    public bool HasMore { get; init; }
}

// ---- emails --------------------------------------------------------------

public sealed class EmailAttachment
{
    public string Filename { get; init; } = string.Empty;
    /// <summary>Base64-encoded file content.</summary>
    public string? Content { get; init; }
    public string? ContentType { get; init; }
    /// <summary>Content-ID for inline images (<c>cid:</c> references in the HTML).</summary>
    public string? ContentId { get; init; }
    public string? Path { get; init; }
}

/// <summary>Resend's template reference. Passed through verbatim; the server answers
/// 422 until template-based sending ships.</summary>
public sealed class EmailMessageTemplate
{
    [JsonPropertyName("id")] public string TemplateId { get; init; } = string.Empty;
    public Dictionary<string, object?>? Variables { get; init; }
}

public sealed class EmailMessage
{
    public string From { get; init; } = string.Empty;
    public Recipients To { get; init; } = null!;
    public string Subject { get; init; } = string.Empty;
    public string? Html { get; init; }
    public string? Text { get; init; }
    public Recipients? Cc { get; init; }
    public Recipients? Bcc { get; init; }
    public Recipients? ReplyTo { get; init; }
    /// <summary>ISO 8601 with offset; up to 30 days ahead.</summary>
    public string? ScheduledAt { get; init; }
    public List<Tag>? Tags { get; init; }
    /// <summary>Topic-scoped send: recipients opted out of the topic are skipped.</summary>
    public Guid? TopicId { get; init; }
    public List<EmailAttachment>? Attachments { get; init; }
    /// <summary>Extra message headers; transport headers are rejected by the API.</summary>
    public Dictionary<string, string>? Headers { get; init; }
    public EmailMessageTemplate? Template { get; init; }
}

public sealed class CreateEmailResponse
{
    public Guid Id { get; init; }
}

public sealed class Email
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? From { get; init; }
    public List<string>? To { get; init; }
    public List<string>? Cc { get; init; }
    public List<string>? Bcc { get; init; }
    public List<string>? ReplyTo { get; init; }
    public string? Subject { get; init; }
    public string? Html { get; init; }
    public string? Text { get; init; }
    public string? CreatedAt { get; init; }
    public string? ScheduledAt { get; init; }
    public string? MessageId { get; init; }
    public string? LastEvent { get; init; }
    /// <summary>Best-practice score (0–10, one decimal); null when the email has no insights.</summary>
    public double? Score { get; init; }
}

// Band, severity, status, and guardrail_status are open string sets on the
// wire (new values arrive with new score versions); strict enum binding would
// make a future value throw, so they stay strings.

public sealed class InsightCheck
{
    /// <summary>Check id from the server's check catalog (open set).</summary>
    public string? Id { get; init; }
    public string? Severity { get; init; }
    public string? Status { get; init; }
    /// <summary>Points deducted from the score; 0 unless status is <c>fail</c>.</summary>
    public double Penalty { get; init; }
    /// <summary>Free-form JSON with check-specific context; absent for most checks.</summary>
    public Dictionary<string, object?>? Detail { get; init; }
}

/// <summary>The pre-send best-practice report computed when the email was sent.</summary>
public sealed class EmailInsights
{
    public string? Object { get; init; }
    public Guid EmailId { get; init; }
    /// <summary>Best-practice score, 0–10, one decimal.</summary>
    public double Score { get; init; }
    public int ScoreVersion { get; init; }
    public string? Band { get; init; }
    public bool Marketing { get; init; }
    public int? HtmlSizeBytes { get; init; }
    public string? ComputedAt { get; init; }
    public List<InsightCheck> Checks { get; init; } = new();
}

/// <summary>Account deliverability over the trailing window. Scores are 0–10
/// with one decimal; null means not enough data to compute.</summary>
public sealed class Deliverability
{
    public string? Object { get; init; }
    public double? Score { get; init; }
    public string? Band { get; init; }
    public double? ContentScore { get; init; }
    public double? OutcomeScore { get; init; }
    public double ComplaintRate { get; init; }
    public double HardBounceRate { get; init; }
    public long EmailsSent { get; init; }
    public long ScoredRecipients { get; init; }
    public int WindowDays { get; init; }
    public bool InsufficientOutcomeData { get; init; }
    public string? GuardrailStatus { get; init; }
    public int ScoreVersion { get; init; }
}

public sealed class CancelEmailResponse
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
}

// ---- contacts (team-global) ----------------------------------------------

public sealed class SegmentRef
{
    public Guid Id { get; init; }
}

public sealed class ContactCreateOptions
{
    public string Email { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public bool? Unsubscribed { get; init; }
    public Dictionary<string, object?>? Properties { get; init; }
    public List<SegmentRef>? Segments { get; init; }
    public List<ContactTopicUpdate>? Topics { get; init; }
}

/// <summary>Addresses a contact by id or email (email wins when both are set).</summary>
public sealed class ContactAddress
{
    public Guid? Id { get; init; }
    public string? Email { get; init; }
}

/// <summary>Options for <c>DELETE /contacts/{id}</c> and <c>POST /contacts/batch/remove</c>.</summary>
public sealed class ContactDeleteOptions
{
    /// <summary>
    /// A plain delete keeps the contact's emails in the send log. With <c>true</c> the address is also
    /// scrubbed from email history, event payloads and API logs (a GDPR/LGPD erasure).
    /// </summary>
    public bool? Erase { get; init; }
}

/// <summary>List options for <c>GET /contacts</c> and <c>GET /segments/{id}/contacts</c>.</summary>
public sealed class ContactListOptions : ListOptions
{
    /// <summary>Attach <see cref="ContactListItem.Properties"/> and/or <see cref="ContactListItem.Topics"/> to every row.</summary>
    public List<ContactInclude>? Include { get; init; }
}

public sealed class ContactUpdateOptions
{
    [JsonIgnore] public Guid? Id { get; init; }
    [JsonIgnore] public string? Email { get; init; }
    /// <summary>Assign a string, or <c>Optional&lt;string&gt;.Null</c> to clear.</summary>
    public Optional<string>? FirstName { get; init; }
    public Optional<string>? LastName { get; init; }
    public bool? Unsubscribed { get; init; }
    /// <summary>Merged into the contact's properties; a <c>null</c> value clears that key.</summary>
    public Dictionary<string, object?>? Properties { get; init; }
}

/// <summary>Query and header options for <c>POST /contacts/batch</c>.</summary>
public sealed class ContactBatchOptions
{
    /// <summary>What to do with an email that already belongs to a contact (server default: error).</summary>
    public ContactBatchOnConflict? OnConflict { get; init; }
    public BatchValidationMode? Validation { get; init; }
}

/// <summary>Body options for <c>POST /contacts/batch/get</c>.</summary>
public sealed class ContactBatchGetOptions
{
    /// <summary>Attach <see cref="ContactListItem.Properties"/> and/or <see cref="ContactListItem.Topics"/> to every contact.</summary>
    public List<ContactInclude>? Include { get; init; }
}

public sealed class ContactBatchItem
{
    public string? Object { get; init; }
    /// <summary>Position of the item in the request array.</summary>
    public int Index { get; init; }
    /// <summary>The contact's id (the existing one for skipped/updated).</summary>
    public Guid Id { get; init; }
    /// <summary><c>created</c>, <c>updated</c> or <c>skipped</c>.</summary>
    public string? Status { get; init; }
}

public sealed class ContactBatchCounts
{
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
}

public sealed class ContactBatchResponse
{
    public List<ContactBatchItem> Data { get; init; } = new();
    public ContactBatchCounts Counts { get; init; } = new();
    /// <summary>Rejected items; present only with <see cref="BatchValidationMode.Permissive"/>.</summary>
    public List<BatchError>? Errors { get; init; }
}

public sealed class ContactId
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
}

public sealed class Contact
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? CreatedAt { get; init; }
    public bool Unsubscribed { get; init; }
    /// <summary>Typed values as returned by the API: <c>{ "type": "string"|"number", "value": ... }</c> per key.</summary>
    public Dictionary<string, object?>? Properties { get; init; }
}

public class ContactListItem
{
    public Guid Id { get; init; }
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? CreatedAt { get; init; }
    public bool Unsubscribed { get; init; }
    /// <summary>Only with <see cref="ContactInclude.Properties"/>; the same typed map as <see cref="Contact.Properties"/>.</summary>
    public Dictionary<string, object?>? Properties { get; init; }
    /// <summary>Only with <see cref="ContactInclude.Topics"/>; the same rows as <c>ContactListTopicsAsync</c>.</summary>
    public List<ContactTopic>? Topics { get; init; }
}

/// <summary>One contact as <c>ContactBatchGetAsync</c> returns it: the list row plus <c>object</c>.</summary>
public sealed class ContactBatchGetItem : ContactListItem
{
    public string? Object { get; init; }
}

/// <summary>A batch-get request entry that matched no contact.</summary>
public sealed class ContactBatchGetMissing
{
    /// <summary>Position of the entry in the request array.</summary>
    public int Index { get; init; }
    public Guid? Id { get; init; }
    public string? Email { get; init; }
}

public sealed class ContactBatchGetResponse
{
    public string? Object { get; init; }
    /// <summary>The contacts found, in request order.</summary>
    public List<ContactBatchGetItem> Data { get; init; } = new();
    public List<ContactBatchGetMissing> Missing { get; init; } = new();
}

public sealed class RemoveContactResponse
{
    public string? Object { get; init; }
    public string? Contact { get; init; }
    public bool Deleted { get; init; }
}

public sealed class ContactTopicUpdate
{
    public Guid Id { get; init; }
    public TopicSubscription Subscription { get; init; }
}

public sealed class ContactTopicsUpdateOptions
{
    public Guid? Id { get; init; }
    public string? Email { get; init; }
    public List<ContactTopicUpdate> Topics { get; init; } = new();
}

/// <summary>A topic as one contact sees it. <see cref="Subscription"/> is the
/// effective choice: the contact's own when <see cref="Explicit"/> is true,
/// otherwise the topic's default.</summary>
public sealed class ContactTopic
{
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public TopicSubscription Subscription { get; init; }
    public bool Explicit { get; init; }
    /// <summary>The hosted preference page lists public topics only.</summary>
    public TopicVisibility? Visibility { get; init; }
}

/// <summary>A contact's hosted preference page. The URL is a signed, contact-scoped
/// capability with no expiry: hand it only to the contact.</summary>
public sealed class ContactPreferencesLink
{
    public string? Object { get; init; }
    public Guid Contact { get; init; }
    public string? Url { get; init; }
}

// ---- contact properties --------------------------------------------------

public sealed class ContactPropertyCreateOptions
{
    public string Key { get; init; } = string.Empty;
    public ContactPropertyType Type { get; init; }
    /// <summary>A string or a number, substituted when a contact has no value.</summary>
    public object? FallbackValue { get; init; }
}

public sealed class ContactPropertyUpdateOptions
{
    /// <summary>A string or number, or <c>Optional&lt;object&gt;.Null</c> to clear.</summary>
    public Optional<object>? FallbackValue { get; init; }
}

public sealed class ContactProperty
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? Key { get; init; }
    public ContactPropertyType Type { get; init; }
    /// <summary>A string, a number or <c>null</c> (a <see cref="JsonElement"/> after deserialization).</summary>
    public object? FallbackValue { get; init; }
    public string? CreatedAt { get; init; }
}

// ---- topics --------------------------------------------------------------

public sealed class TopicCreateOptions
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public TopicSubscription DefaultSubscription { get; init; }
    /// <summary>Public topics always appear on the unsubscribe page; private ones (the server default) only when reached through their own topic link.</summary>
    public TopicVisibility? Visibility { get; init; }
}

public sealed class TopicUpdateOptions
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public TopicVisibility? Visibility { get; init; }
}

public sealed class Topic
{
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public TopicSubscription DefaultSubscription { get; init; }
    public TopicVisibility? Visibility { get; init; }
    public string? CreatedAt { get; init; }
}

public sealed class TopicId
{
    public Guid Id { get; init; }
}

public sealed class RemoveTopicResponse
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public bool Deleted { get; init; }
}

// ---- broadcasts ----------------------------------------------------------

/// <summary>Targeting is an optional <see cref="SegmentId"/> and/or
/// <see cref="TopicId"/>; neither set sends to every contact of the team.</summary>
public sealed class BroadcastCreateOptions
{
    public string? Name { get; init; }
    public Guid? SegmentId { get; init; }
    public string From { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string? Html { get; init; }
    public string? Text { get; init; }
    public Recipients? ReplyTo { get; init; }
    public string? PreviewText { get; init; }
    public Guid? TopicId { get; init; }
    /// <summary>true sends (or schedules) immediately instead of saving a draft.</summary>
    public bool? Send { get; init; }
    /// <summary>Deliver later; requires <see cref="Send"/> = true.</summary>
    public string? ScheduledAt { get; init; }
}

public sealed class BroadcastUpdateOptions
{
    public string? Name { get; init; }
    public Guid? SegmentId { get; init; }
    public string? From { get; init; }
    public string? Subject { get; init; }
    public string? Html { get; init; }
    public string? Text { get; init; }
    public Recipients? ReplyTo { get; init; }
    public string? PreviewText { get; init; }
    /// <summary>Assign a Guid, or <c>Optional&lt;Guid?&gt;.Null</c> to remove the topic.</summary>
    public Optional<Guid?>? TopicId { get; init; }
}

public sealed class BroadcastId
{
    public Guid Id { get; init; }
}

public class BroadcastListItem
{
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public Guid? SegmentId { get; init; }
    public string? Status { get; init; }
    public string? CreatedAt { get; init; }
    public string? ScheduledAt { get; init; }
    public string? SentAt { get; init; }
}

public sealed class Broadcast : BroadcastListItem
{
    public string? Object { get; init; }
    public string? From { get; init; }
    public string? Subject { get; init; }
    public List<string>? ReplyTo { get; init; }
    public string? PreviewText { get; init; }
    public Guid? TopicId { get; init; }
    public string? Html { get; init; }
    public string? Text { get; init; }
}

public sealed class CancelBroadcastResponse
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
}

public sealed class RemoveBroadcastResponse
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public bool Deleted { get; init; }
}

// ---- segments (saved filters over the team's contacts) -------------------

public sealed class SegmentCondition
{
    public string Field { get; init; } = string.Empty;
    public string Op { get; init; } = string.Empty;
    public string? Value { get; init; }
}

public sealed class SegmentFilter
{
    public SegmentMatch Match { get; init; }
    public List<SegmentCondition> Conditions { get; init; } = new();
}

public sealed class SegmentCreateOptions
{
    public string Name { get; init; } = string.Empty;
    public SegmentFilter Filter { get; init; } = new();
}

public sealed class SegmentUpdateOptions
{
    public string? Name { get; init; }
    public SegmentFilter? Filter { get; init; }
}

public sealed class Segment
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public SegmentFilter? Filter { get; init; }
    public string? CreatedAt { get; init; }
    public int? ContactCount { get; init; }
}

public sealed class RemoveSegmentResponse
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public bool Deleted { get; init; }
}

// ---- suppressions --------------------------------------------------------

public sealed class SuppressionListOptions : ListOptions
{
    public SuppressionOrigin? Origin { get; init; }
}

public sealed class Suppression
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? Email { get; init; }
    public SuppressionOrigin Origin { get; init; }
    /// <summary>The email or broadcast that caused the suppression, when known.</summary>
    public Guid? SourceId { get; init; }
    public string? CreatedAt { get; init; }
}

// ---- domains -------------------------------------------------------------

public sealed class DomainCreateOptions
{
    public string Name { get; init; } = string.Empty;
    /// <summary>Sending region, e.g. <c>us-east-1</c>; the instance default when omitted.</summary>
    public string? Region { get; init; }
    /// <summary>Subdomain for the Return-Path (server default: <c>send</c>).</summary>
    public string? CustomReturnPath { get; init; }
    public bool? OpenTracking { get; init; }
    public bool? ClickTracking { get; init; }
    public string? TrackingSubdomain { get; init; }
}

public sealed class DomainUpdateOptions
{
    public bool? OpenTracking { get; init; }
    public bool? ClickTracking { get; init; }
    /// <summary>Assign a string, or <c>Optional&lt;string&gt;.Null</c> to clear.</summary>
    public Optional<string>? TrackingSubdomain { get; init; }
    /// <summary>Accept mail to addresses without an inbox (a paid plan on MillionSend Cloud).</summary>
    public bool? ReceivingCatchAll { get; init; }
}

public sealed class DomainCapabilities
{
    public string? Sending { get; init; }
    public string? Receiving { get; init; }
}

public sealed class DomainRecord
{
    public string? Record { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? Ttl { get; init; }
    public string? Status { get; init; }
    public string? Value { get; init; }
    public double? Priority { get; init; }
}

public sealed class Domain
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? CreatedAt { get; init; }
    public string? Region { get; init; }
    public bool OpenTracking { get; init; }
    public bool ClickTracking { get; init; }
    public string? TrackingSubdomain { get; init; }
    public DomainCapabilities? Capabilities { get; init; }
    /// <summary>On, mail to addresses without an inbox lands in the received list; off, those addresses are refused at SMTP time.</summary>
    public bool? ReceivingCatchAll { get; init; }
    /// <summary>DNS records to publish; absent on list items.</summary>
    public List<DomainRecord>? Records { get; init; }
}

// ---- webhooks ------------------------------------------------------------

public sealed class WebhookCreateOptions
{
    public string Endpoint { get; init; } = string.Empty;
    /// <summary>Event names such as <c>email.delivered</c>.</summary>
    public List<string> Events { get; init; } = new();
    /// <summary>Bring your own signing secret; generated by the server when omitted.</summary>
    public string? SigningSecret { get; init; }
    /// <summary>Deliver inbox events and <c>email.received</c> for these inboxes only.</summary>
    public List<Guid>? InboxIds { get; init; }
}

public sealed class WebhookUpdateOptions
{
    public string? Endpoint { get; init; }
    public List<string>? Events { get; init; }
    public WebhookStatus? Status { get; init; }
    /// <summary>Inbox events and <c>email.received</c> for these inboxes only; <c>Optional&lt;List&lt;Guid&gt;&gt;.Null</c> removes the filter.</summary>
    public Optional<List<Guid>>? InboxIds { get; init; }
}

public sealed class WebhookCreateResponse
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? SigningSecret { get; init; }
}

/// <summary>Both fields optional: an empty body mints a secret with the server's default overlap.</summary>
public sealed class WebhookRotateOptions
{
    /// <summary>Bring your own secret: <c>whsec_</c> followed by base64 of 24–64 bytes; minted when omitted.</summary>
    public string? SigningSecret { get; init; }
    /// <summary>0–72 hours the previous secret keeps signing alongside the new one; 0 drops it at once.</summary>
    public int? OverlapHours { get; init; }
}

public sealed class WebhookRotateResponse
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? SigningSecret { get; init; }
    /// <summary>When the previous secret stops signing; null when it was dropped at once.</summary>
    public string? PreviousSecretExpiresAt { get; init; }
}

public sealed class Webhook
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? Endpoint { get; init; }
    public string? CreatedAt { get; init; }
    public WebhookStatus Status { get; init; }
    public List<string>? Events { get; init; }
    /// <summary>Inbox events and <c>email.received</c> for these inboxes only; null for every inbox.</summary>
    public List<Guid>? InboxIds { get; init; }
    /// <summary>Returned by retrieve only; absent on list items.</summary>
    public string? SigningSecret { get; init; }
    /// <summary>Retrieve only. While set, deliveries are also signed with the secret this one replaced (a rotation's overlap window).</summary>
    public string? PreviousSecretExpiresAt { get; init; }
}

// ---- api keys ------------------------------------------------------------

public sealed class ApiKeyCreateResponse
{
    public Guid Id { get; init; }
    /// <summary>The bearer token; shown once, never retrievable again.</summary>
    public string? Token { get; init; }
}

/// <summary>Body of <c>POST /api-keys</c>.</summary>
public sealed class ApiKeyCreateOptions
{
    public string Name { get; init; } = string.Empty;
    public ApiKeyPermission? Permission { get; init; }
    /// <summary>Restrict a sending key to one domain.</summary>
    public Guid? DomainId { get; init; }
    /// <summary>Restrict a full-access key to one inbox: it reads that inbox and sends from its address only. Not combinable with <see cref="DomainId"/>.</summary>
    public Guid? InboxId { get; init; }
}

public sealed class ApiKey
{
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public string? CreatedAt { get; init; }
    public string? LastUsedAt { get; init; }
    /// <summary>Set for a key restricted to one inbox.</summary>
    public Guid? InboxId { get; init; }
}

// ---- templates -----------------------------------------------------------

public sealed class TemplateCreateOptions
{
    public string Name { get; init; } = string.Empty;
    public string Html { get; init; } = string.Empty;
    public string? Subject { get; init; }
    public string? Text { get; init; }
    /// <summary>Case-sensitive handle, unique per team; <c>TemplateRetrieveAsync(alias)</c> resolves it.</summary>
    public string? Alias { get; init; }
}

public sealed class TemplateUpdateOptions
{
    public string? Name { get; init; }
    public string? Html { get; init; }
    /// <summary>Assign a string, or <c>Optional&lt;string&gt;.Null</c> to clear.</summary>
    public Optional<string>? Subject { get; init; }
    public Optional<string>? Text { get; init; }
    public Optional<string>? Alias { get; init; }
}

public sealed class Template
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public string? Alias { get; init; }
    public string? Status { get; init; }
    public string? PublishedAt { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public Guid? CurrentVersionId { get; init; }
    public string? Subject { get; init; }
    /// <summary>Absent on list items.</summary>
    public string? Html { get; init; }
    public string? Text { get; init; }
    public bool HasUnpublishedVersions { get; init; }
}

// ---- usage ---------------------------------------------------------------

public sealed class UsageLimits
{
    /// <summary>null means unlimited.</summary>
    public int? EmailsPerDay { get; init; }
    public int? Domains { get; init; }
}

public sealed class UsageToday
{
    public long EmailsSent { get; init; }
    public string? ResetsAt { get; init; }
}

public sealed class UsageTeam
{
    public Guid Id { get; init; }
    public string? Name { get; init; }
}

/// <summary>Plan limits and today's consumption for the calling team.</summary>
public sealed class Usage
{
    public string? Object { get; init; }
    public bool Cloud { get; init; }
    /// <summary><c>free</c>, <c>pro</c>, <c>scale</c>, or null for self-hosted.</summary>
    public string? Plan { get; init; }
    public UsageLimits Limits { get; init; } = new();
    public UsageToday Today { get; init; } = new();
    public UsageTeam Team { get; init; } = new();
    public string? AppUrl { get; init; }
}

// ---- receiving -----------------------------------------------------------

// Verdicts, kinds, folders and statuses below are plain strings: the server may
// add values, and a strict enum would make the whole response fail to decode.

/// <summary>Filters for <c>GET /emails/receiving</c>. <see cref="Wait"/> (0–50 s)
/// long-polls: the call returns as soon as matching mail arrives, or an empty page
/// when the wait ends. The default <c>HttpClient</c> timeout (100 s) covers it.</summary>
public sealed class ReceivedEmailListOptions : ListOptions
{
    public int? Wait { get; init; }
    /// <summary>ISO 8601: only mail received after this instant.</summary>
    public string? Since { get; init; }
    public Guid? InboxId { get; init; }
    /// <summary>An address the message was delivered to (<c>received_for</c>).</summary>
    public string? To { get; init; }
    /// <summary>A sender address, or a domain that also covers its subdomains.</summary>
    public string? From { get; init; }
    public string? SubjectContains { get; init; }
    /// <summary>Needs <see cref="InboxId"/>.</summary>
    public bool? Unread { get; init; }
    /// <summary>Also list held mail; needs a full-access key that is not scoped to an inbox.</summary>
    public bool IncludeQuarantined { get; init; }
    /// <summary>List held mail only; needs a full-access key that is not scoped to an inbox.</summary>
    public bool Quarantined { get; init; }
}

/// <summary>An attachment of a received email. <see cref="DownloadUrl"/> is a signed
/// link valid for one hour, set by the attachment endpoints; it answers 410
/// <c>email_blocked</c> when the email carries malware.</summary>
public sealed class ReceivedAttachment
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? Filename { get; init; }
    public long? Size { get; init; }
    public string? ContentType { get; init; }
    /// <summary><c>inline</c> or <c>attachment</c>.</summary>
    public string? ContentDisposition { get; init; }
    public string? ContentId { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ExpiresAt { get; init; }
}

/// <summary>A signed link to the original RFC 822 message, valid for one hour.</summary>
public sealed class ReceivedEmailRaw
{
    public string? DownloadUrl { get; init; }
    public string? ExpiresAt { get; init; }
}

/// <summary>How an inbox screened a received email. Lists carry <see cref="Verdict"/>
/// and <see cref="SenderTrust"/> only.</summary>
public sealed class Screening
{
    /// <summary><c>pending</c>, <c>clean</c>, <c>suspicious</c>, <c>quarantined</c> or <c>released</c>.</summary>
    public string? Verdict { get; init; }
    /// <summary><c>owner</c>, <c>allowlisted</c>, <c>known_thread</c>, <c>external</c>, <c>unauthenticated</c> or <c>failed</c>.</summary>
    public string? SenderTrust { get; init; }
    /// <summary>Set only when quarantined or released, e.g. <c>not_allowlisted</c>, <c>injection</c>, <c>virus</c>.</summary>
    public string? QuarantineReason { get; init; }
    public List<string>? Reasons { get; init; }
    /// <summary>0–100; null when the email was not screened.</summary>
    public int? InjectionScore { get; init; }
    public int? PhishingScore { get; init; }
    public bool? HiddenTextRemoved { get; init; }
}

/// <summary>One inbox a received email was delivered to (owner views).</summary>
public sealed class ReceivedEmailInbox
{
    public Guid InboxId { get; init; }
    public Guid ThreadId { get; init; }
    public string? Verdict { get; init; }
}

/// <summary>Codes and sign-in links found in a received email. Lists carry
/// <see cref="Kinds"/> only; the values come only from <c>ReceivedEmailRetrieveAsync</c>.</summary>
public sealed class ReceivedEmailExtracted
{
    /// <summary>A subset of <c>code</c> and <c>link</c>.</summary>
    public List<string>? Kinds { get; init; }
    public string? Code { get; init; }
    public string? Link { get; init; }
    public bool? LinkDomainMatchesSender { get; init; }
    public bool? Ambiguous { get; init; }
}

/// <summary>A received email. List items leave the body fields and the detail-only
/// extensions null. Everything in it was written by its sender: show its HTML only
/// in a sandbox.</summary>
public sealed class ReceivedEmail
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    /// <summary>Bare address from the From header; the display name is in <see cref="Headers"/>.</summary>
    public string? From { get; init; }
    public List<string>? To { get; init; }
    public List<string>? Cc { get; init; }
    public List<string>? Bcc { get; init; }
    public List<string>? ReplyTo { get; init; }
    public string? Subject { get; init; }
    public string? CreatedAt { get; init; }
    /// <summary>The team's envelope recipients: the addresses the message was delivered to.</summary>
    public List<string>? ReceivedFor { get; init; }
    public string? MessageId { get; init; }
    public List<ReceivedAttachment>? Attachments { get; init; }
    public string? Html { get; init; }
    /// <summary><c>data_uri</c> or <c>cid</c>.</summary>
    public string? HtmlFormat { get; init; }
    /// <summary>The text part with invisible characters removed.</summary>
    public string? Text { get; init; }
    /// <summary>Top-level headers, lowercase names.</summary>
    public Dictionary<string, string>? Headers { get; init; }
    /// <summary><c>spf</c>, <c>dkim</c>, <c>dmarc</c> → <c>pass</c>, <c>fail</c>, <c>gray</c>, …</summary>
    public Dictionary<string, string>? Authentication { get; init; }
    /// <summary>Null once retention removed the message, or when it carries malware.</summary>
    public ReceivedEmailRaw? Raw { get; init; }
    /// <summary>The inbox this view reads it through; null for catch-all mail.</summary>
    public Guid? InboxId { get; init; }
    public Guid? ThreadId { get; init; }
    /// <summary>Every inbox it was delivered to; owner views only.</summary>
    public List<ReceivedEmailInbox>? Inboxes { get; init; }
    public Screening? Screening { get; init; }
    /// <summary><c>pass</c>, <c>gray</c> or <c>fail</c>; null when not computed.</summary>
    public string? SpamVerdict { get; init; }
    public int? SpamScore { get; init; }
    /// <summary><c>pass</c>, <c>fail</c> or <c>processing_failed</c>; null when not scanned.</summary>
    public string? VirusVerdict { get; init; }
    public string? VirusSignature { get; init; }
    /// <summary>The reply without quotes, signature or hidden text: the text to hand an agent.</summary>
    public string? ExtractedText { get; init; }
    public ReceivedEmailExtracted? Extracted { get; init; }
    /// <summary>Read state in the inbox this view reads it through.</summary>
    public bool? Read { get; init; }
}

/// <summary>A matching email a long-poll found held (quarantined or being screened): its id and why only.</summary>
public sealed class HeldEmail
{
    public Guid Id { get; init; }
    /// <summary>Null for a catch-all copy held for a virus.</summary>
    public Guid? InboxId { get; init; }
    /// <summary><c>pending</c> (still being screened) or <c>quarantined</c>.</summary>
    public string? Reason { get; init; }
    /// <summary>Why it was quarantined; null while pending.</summary>
    public string? QuarantineReason { get; init; }
    public string? CreatedAt { get; init; }
}

/// <summary>A page of received mail. With <c>Wait</c>, <see cref="Held"/> lists the
/// matching emails that arrived but are held, by id only.</summary>
public sealed class ReceivedEmailList : ListResponse<ReceivedEmail>
{
    public List<HeldEmail>? Held { get; init; }
}

/// <summary>Body of a reply, to a received email or to an email in an inbox thread.
/// Set <see cref="Html"/> and/or <see cref="Text"/>. <see cref="From"/> and
/// <see cref="InboxId"/> apply to <c>ReceivedEmailReplyAsync</c> only.</summary>
public sealed class EmailReplyOptions
{
    public string? Html { get; init; }
    public string? Text { get; init; }
    /// <summary>Defaults to <c>Re: </c> plus the thread subject.</summary>
    public string? Subject { get; init; }
    public Recipients? Cc { get; init; }
    public Recipients? Bcc { get; init; }
    /// <summary>Also copy the message's other recipients.</summary>
    public bool? ReplyAll { get; init; }
    /// <summary>Hold the reply as a draft for a person to approve.</summary>
    public bool? RequiresApproval { get; init; }
    /// <summary>An address on a verified team domain; defaults to the inbox address.</summary>
    public string? From { get; init; }
    /// <summary>Which of the email's inboxes replies, when it reached several.</summary>
    public Guid? InboxId { get; init; }
}

/// <summary>Body of a forward. <see cref="Html"/>/<see cref="Text"/> is an optional
/// note above the quoted original. <see cref="From"/>, <see cref="InboxId"/> and
/// <see cref="Mode"/> apply to <c>ReceivedEmailForwardAsync</c> only.</summary>
public sealed class EmailForwardOptions
{
    public Recipients To { get; init; } = null!;
    public Recipients? Cc { get; init; }
    public Recipients? Bcc { get; init; }
    public string? Html { get; init; }
    public string? Text { get; init; }
    /// <summary>Defaults to <c>Fwd: </c> plus the original subject.</summary>
    public string? Subject { get; init; }
    public bool? RequiresApproval { get; init; }
    public string? From { get; init; }
    public Guid? InboxId { get; init; }
    /// <summary><c>inline</c> (the default) quotes the original; <c>attachment</c> attaches it as <c>forwarded_message.eml</c>.</summary>
    public string? Mode { get; init; }
}

/// <summary>The outcome of a reply, forward or draft send: <see cref="Sent"/>, or the
/// <see cref="Draft"/> the inbox's send rules held for a person to approve (the API
/// answers 202 and nothing is sent).</summary>
public sealed class InboxSendResult<TSent> where TSent : class
{
    public bool IsDraft { get; init; }
    /// <summary><c>sent</c> or <c>draft</c>.</summary>
    public string Kind => IsDraft ? "draft" : "sent";
    public TSent? Sent { get; init; }
    public InboxDraft? Draft { get; init; }
}

// ---- inboxes -------------------------------------------------------------

/// <summary>An inbox: one address that receives mail, read by an agent or by people.
/// List items carry <c>Id</c>, <c>Name</c>, <c>EmailAddress</c>, <c>FromName</c>,
/// <c>Unread</c>, <c>LastReceived</c>, <c>Kind</c>, <c>Shared</c> and <c>ExpiresAt</c>.</summary>
public sealed class Inbox
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public string? EmailAddress { get; init; }
    /// <summary>The custom domain's id, or for a shared-domain inbox the team's shared subdomain id.</summary>
    public Guid? DomainId { get; init; }
    /// <summary>Set for forwarding inboxes: the address to forward mail to.</summary>
    public string? ReceivingAddress { get; init; }
    public string? FromName { get; init; }
    /// <summary>Threads with unread mail.</summary>
    public int? Unread { get; init; }
    /// <summary>Unsent drafts.</summary>
    public int? Drafts { get; init; }
    public string? LastReceived { get; init; }
    public string? CreatedAt { get; init; }
    /// <summary><c>agent</c> or <c>human</c>.</summary>
    public string? Kind { get; init; }
    /// <summary>True for an address on the shared domain (no DNS of your own).</summary>
    public bool? Shared { get; init; }
    /// <summary>When the inbox stops receiving and is deleted; null for never.</summary>
    public string? ExpiresAt { get; init; }
    public int? Quarantined { get; init; }
    public int? PendingApproval { get; init; }
}

/// <summary>Body of <c>POST /inboxes</c>. Without <see cref="EmailAddress"/> the inbox
/// gets a random address on the shared domain (when the instance has one).</summary>
public sealed class InboxCreateOptions
{
    public string? EmailAddress { get; init; }
    public string? Name { get; init; }
    public string? FromName { get; init; }
    /// <summary>Provision a <c>receiving_address</c> to forward mail to.</summary>
    public bool? Forwarding { get; init; }
    /// <summary><c>agent</c> (the default) or <c>human</c>.</summary>
    public string? Kind { get; init; }
    /// <summary>Like <c>1h</c> or <c>30d</c>. Shared-domain inboxes default to 24 hours, at most 30 days.</summary>
    public string? ExpiresIn { get; init; }
    /// <summary>Safety rules to start with; the rest keep the defaults for the kind.</summary>
    public InboxSafetyOptions? Safety { get; init; }
}

public sealed class InboxUpdateOptions
{
    public string? Name { get; init; }
    /// <summary><c>""</c> removes the display name.</summary>
    public string? FromName { get; init; }
    /// <summary>Like <c>1h</c> or <c>30d</c>, counted from now; <c>Optional&lt;string&gt;.Null</c> for never.</summary>
    public Optional<string>? ExpiresIn { get; init; }
}

public sealed class InboxListOptions : ListOptions
{
    /// <summary><c>agent</c> or <c>human</c>.</summary>
    public string? Kind { get; init; }
}

/// <summary>An inbox's safety rules.</summary>
public sealed class InboxSafety
{
    public string? Object { get; init; }
    public Guid InboxId { get; init; }
    /// <summary><c>anyone</c>, <c>verified</c> or <c>allowlist</c>: mail failing it is quarantined.</summary>
    public string? AcceptFrom { get; init; }
    public List<string>? Allowlist { get; init; }
    public List<string>? OwnerAddresses { get; init; }
    public bool? Screening { get; init; }
    public bool? ExtractCodes { get; init; }
    /// <summary><c>reply_only</c>, <c>allowlist</c> or <c>open</c>.</summary>
    public string? SendPolicy { get; init; }
    public bool? RequireApproval { get; init; }
    public bool? PrivateCodes { get; init; }
    /// <summary>Unattended recipients per UTC day; null for no inbox cap.</summary>
    public int? SendCap { get; init; }
    public List<string>? ForwardTo { get; init; }
}

/// <summary>A partial set of safety rules: only the fields you set are sent.</summary>
public sealed class InboxSafetyOptions
{
    public string? AcceptFrom { get; init; }
    /// <summary><c>local@domain</c>, or <c>*@domain</c> for a domain and its subdomains. Replaces the list.</summary>
    public List<string>? Allowlist { get; init; }
    public List<string>? OwnerAddresses { get; init; }
    public bool? Screening { get; init; }
    public bool? ExtractCodes { get; init; }
    public string? SendPolicy { get; init; }
    public bool? RequireApproval { get; init; }
    public bool? PrivateCodes { get; init; }
    /// <summary>Assign a number, or <c>Optional&lt;int?&gt;.Null</c> to remove the cap.</summary>
    public Optional<int?>? SendCap { get; init; }
    public List<string>? ForwardTo { get; init; }
}

/// <summary>The newest verification code or sign-in link an inbox received. The link is never fetched.</summary>
public sealed class VerificationCode
{
    public string? Object { get; init; }
    /// <summary><c>found</c>, <c>none</c>, <c>ambiguous</c> or <c>timeout</c> (a wait that ended empty).</summary>
    public string? Status { get; init; }
    /// <summary><c>otp</c> or <c>link</c>; null unless found.</summary>
    public string? Kind { get; init; }
    public string? Code { get; init; }
    public string? Url { get; init; }
    public bool? LinkDomainMatchesSender { get; init; }
    public Guid? EmailId { get; init; }
    public string? From { get; init; }
    public string? ReceivedAt { get; init; }
    public string? SenderTrust { get; init; }
}

public sealed class InboxCodeLatestOptions
{
    /// <summary>A sender address, or a domain that also covers its subdomains.</summary>
    public string? From { get; init; }
    /// <summary>Seconds (0–50) to wait for a code to arrive.</summary>
    public int? Wait { get; init; }
    /// <summary>Ignore codes older than this many seconds (60–86400; the API defaults to 600).</summary>
    public int? MaxAge { get; init; }
}

/// <summary>An email held in an inbox's quarantine.</summary>
public sealed class QuarantinedEmail
{
    public Guid Id { get; init; }
    public Guid? ThreadId { get; init; }
    public string? From { get; init; }
    public string? Subject { get; init; }
    public string? ReceivedAt { get; init; }
    public string? QuarantineReason { get; init; }
    public string? SenderTrust { get; init; }
    public int? InjectionScore { get; init; }
    public int? PhishingScore { get; init; }
    public string? VirusSignature { get; init; }
}

public sealed class QuarantineRelease
{
    public string? Object { get; init; }
    public Guid InboxId { get; init; }
    public Guid EmailId { get; init; }
    public string? ReleasedAt { get; init; }
    /// <summary>The sender address added to the allowlist; null unless trust was asked.</summary>
    public string? TrustedSender { get; init; }
}

/// <summary>A label of one inbox; a thread's labels carry <c>Id</c>, <c>Name</c> and <c>Color</c>.</summary>
public sealed class InboxLabel
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? Name { get; init; }
    /// <summary><c>#RRGGBB</c>, uppercase.</summary>
    public string? Color { get; init; }
    public string? CreatedAt { get; init; }
}

public sealed class InboxLabelOptions
{
    /// <summary>1–64 characters, unique per inbox ignoring case.</summary>
    public string? Name { get; init; }
    /// <summary><c>#RRGGBB</c>; a random color when omitted on create.</summary>
    public string? Color { get; init; }
}

/// <summary>A conversation in one inbox, both directions. Retrieve and update return
/// the summary (<c>Object</c>, <c>Id</c>, <c>Subject</c>, <c>Folder</c>, <c>Labels</c>,
/// <c>Read</c>, <c>Quarantined</c>); list items add the latest message's addresses,
/// counts and flags; search results add <c>MatchedEmailId</c> and <c>Highlights</c>.</summary>
public sealed class InboxThread
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    public string? Subject { get; init; }
    /// <summary><c>inbox</c>, <c>archive</c>, <c>spam</c>, <c>sent</c> or <c>trash</c>.</summary>
    public string? Folder { get; init; }
    public List<InboxLabel>? Labels { get; init; }
    /// <summary>True when every message in the thread is read.</summary>
    public bool? Read { get; init; }
    /// <summary>Held messages in the thread; owner views only, else 0.</summary>
    public int? Quarantined { get; init; }
    public string? From { get; init; }
    public List<string>? To { get; init; }
    public List<string>? Cc { get; init; }
    public List<string>? Bcc { get; init; }
    public int? MessageCount { get; init; }
    public bool? HasAttachment { get; init; }
    public bool? HasDraft { get; init; }
    /// <summary>When the thread was last active.</summary>
    public string? ReceivedAt { get; init; }
    public Guid? MatchedEmailId { get; init; }
    /// <summary>Matched fragments per field, with <c>**match**</c> markers.</summary>
    public Dictionary<string, List<string>>? Highlights { get; init; }
}

/// <summary>Filters for <c>GET /inboxes/{id}/threads</c>. <see cref="Folders"/> defaults
/// to <c>inbox</c> (<c>inbox,archive,sent</c> with <see cref="Labels"/>).</summary>
public class InboxThreadListOptions : ListOptions
{
    public List<string>? Folders { get; init; }
    /// <summary>A thread matches when it has any of these labels.</summary>
    public List<Guid>? Labels { get; init; }
    /// <summary>true: fully read threads; false: threads with unread mail.</summary>
    public bool? Read { get; init; }
    /// <summary>Also count held mail (owner views).</summary>
    public bool IncludeQuarantined { get; init; }
}

/// <summary>Search over subject, sender and recipients; every per-email filter must
/// match the same email.</summary>
public sealed class InboxThreadSearchOptions : InboxThreadListOptions
{
    /// <summary>Every word must match; <c>"a phrase"</c> and <c>-word</c> exclusions work.</summary>
    public string? Query { get; init; }
    /// <summary>Substrings of the sender's address or name; any may match.</summary>
    public List<string>? From { get; init; }
    public List<string>? To { get; init; }
    public List<string>? Cc { get; init; }
    public List<string>? Bcc { get; init; }
    public bool? HasAttachment { get; init; }
    /// <summary><c>YYYY-MM-DD</c> (the whole day) or ISO 8601.</summary>
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
}

public sealed class InboxThreadUpdateOptions
{
    /// <summary>Marks every message in the thread.</summary>
    public bool? Read { get; init; }
    /// <summary><c>inbox</c>, <c>archive</c>, <c>spam</c> or <c>trash</c>; out of trash restores it.</summary>
    public string? Folder { get; init; }
    /// <summary>Applies this label to the thread.</summary>
    public Guid? LabelId { get; init; }
}

public sealed class InboxThreadEmailListOptions : ListOptions
{
    /// <summary>Also list held mail (owner views; a full-access key that is not scoped to an inbox).</summary>
    public bool IncludeQuarantined { get; init; }
}

/// <summary>An attachment of a thread email. The id of an attachment the inbox sent is its position (<c>"0"</c>, <c>"1"</c>, …).</summary>
public sealed class ThreadEmailAttachment
{
    public string Id { get; init; } = string.Empty;
    public string? Filename { get; init; }
    public long? Size { get; init; }
}

/// <summary>One email in an inbox thread, either direction; also the sent email reply
/// and forward return. From <c>ReceivedEmailReplyAsync</c>/<c>ReceivedEmailForwardAsync</c>
/// only <c>Object</c>, <c>Id</c>, <c>ThreadId</c> and <c>InboxId</c> are set.</summary>
public sealed class ThreadEmail
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    /// <summary><c>inbound</c> or <c>outbound</c>.</summary>
    public string? Direction { get; init; }
    /// <summary>With the display name when there is one: <c>Name &lt;address&gt;</c>.</summary>
    public string? From { get; init; }
    public List<string>? To { get; init; }
    public List<string>? Cc { get; init; }
    public List<string>? Bcc { get; init; }
    public List<string>? ReplyTo { get; init; }
    public string? Subject { get; init; }
    public string? MessageId { get; init; }
    public string? Html { get; init; }
    /// <summary>Inbound text has invisible characters removed.</summary>
    public string? Text { get; init; }
    public List<ThreadEmailAttachment>? Attachments { get; init; }
    public bool? Read { get; init; }
    /// <summary>When it arrived or was sent.</summary>
    public string? ReceivedAt { get; init; }
    /// <summary>Inbound: the reply without quotes, signature or hidden text.</summary>
    public string? ExtractedText { get; init; }
    public Screening? Screening { get; init; }
    public string? SpamVerdict { get; init; }
    public string? VirusVerdict { get; init; }
    /// <summary>Outbound: the email's latest delivery event.</summary>
    public string? Status { get; init; }
    /// <summary>The sent email's id (reply and forward results).</summary>
    public Guid? EmailId { get; init; }
    public Guid? ThreadId { get; init; }
    public Guid? InboxId { get; init; }
}

/// <summary>A draft in an inbox, including one the send rules hold
/// (<c>status: pending_approval</c>) until a person approves it. List items carry
/// <see cref="Snippet"/> instead of the body. A draft send's 200 sets only
/// <c>Object</c>, <c>Id</c>, <c>ThreadId</c> and <c>EmailId</c>.</summary>
public sealed class InboxDraft
{
    public string? Object { get; init; }
    public Guid Id { get; init; }
    /// <summary><c>standalone</c> or <c>reply</c>.</summary>
    public string? Type { get; init; }
    public List<string>? To { get; init; }
    public List<string>? Cc { get; init; }
    public List<string>? Bcc { get; init; }
    public string? Subject { get; init; }
    public string? Html { get; init; }
    public string? Text { get; init; }
    public string? Snippet { get; init; }
    public Guid? ThreadId { get; init; }
    public Guid? ReplyToEmailId { get; init; }
    /// <summary>Set once the draft is sent.</summary>
    public Guid? EmailId { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
    /// <summary><c>draft</c>, <c>pending_approval</c>, <c>sent</c>, <c>rejected</c> or <c>expired</c>.</summary>
    public string? Status { get; init; }
    /// <summary>Why the send rules held it, e.g. <c>send_policy</c>, <c>ramp</c>, <c>private_code</c>.</summary>
    public List<string>? ApprovalReasons { get; init; }
    public bool? Forward { get; init; }
    public string? DecidedBy { get; init; }
    public string? DecidedAt { get; init; }
    /// <summary>A pending draft expires 7 days after it was held.</summary>
    public string? ExpiresAt { get; init; }
}

/// <summary>A reply draft sets both <see cref="ThreadId"/> and <see cref="ReplyToEmailId"/>; it does not copy recipients.</summary>
public sealed class InboxDraftCreateOptions
{
    public Recipients? To { get; init; }
    public Recipients? Cc { get; init; }
    public Recipients? Bcc { get; init; }
    public string? Subject { get; init; }
    public string? Html { get; init; }
    public string? Text { get; init; }
    public Guid? ThreadId { get; init; }
    public Guid? ReplyToEmailId { get; init; }
}

/// <summary>Only the fields you set are sent; <c>Optional&lt;T&gt;.Null</c> clears one.
/// Setting <see cref="Html"/> or <see cref="Text"/> replaces the whole body: the part
/// you leave out becomes null.</summary>
public sealed class InboxDraftUpdateOptions
{
    public Optional<List<string>>? To { get; init; }
    public Optional<List<string>>? Cc { get; init; }
    public Optional<List<string>>? Bcc { get; init; }
    public Optional<string>? Subject { get; init; }
    public Optional<string>? Html { get; init; }
    public Optional<string>? Text { get; init; }
}

// ---- internal ------------------------------------------------------------

// The API's non-2xx body. `statusCode` is camelCase on the wire (unlike the
// snake_case success bodies), so it needs an explicit name.
internal sealed class ErrorBody
{
    [JsonPropertyName("statusCode")] public int? StatusCode { get; init; }
    public string? Name { get; init; }
    public string? Message { get; init; }
}
