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

public sealed class Tag
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

/// <summary>Keyset list options. <see cref="After"/> and <see cref="Before"/> are
/// mutually exclusive cursors.</summary>
public sealed class ListOptions
{
    /// <summary>1–100; the API defaults to 20.</summary>
    public int? Limit { get; init; }
    public Guid? After { get; init; }
    public Guid? Before { get; init; }
}

public enum TopicSubscription { OptIn, OptOut }
public enum SegmentMatch { All, Any }

// ---- emails --------------------------------------------------------------

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

/// <summary>Envelope for endpoints that return a bare <c>{ "data": [...] }</c>
/// (batch send, topics list).</summary>
public sealed class DataResponse<T>
{
    public List<T> Data { get; init; } = new();
}

/// <summary>Paginated list envelope: <c>{ object:"list", data:[], has_more }</c>.</summary>
public sealed class ListResponse<T>
{
    public string? Object { get; init; }
    public List<T> Data { get; init; } = new();
    public bool HasMore { get; init; }
}

// ---- contacts (team-global) ----------------------------------------------

public sealed class ContactCreateOptions
{
    public string Email { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public bool? Unsubscribed { get; init; }
    public Dictionary<string, object?>? Properties { get; init; }
}

/// <summary>Addresses a contact by id or email (email wins when both are set).</summary>
public sealed class ContactAddress
{
    public Guid? Id { get; init; }
    public string? Email { get; init; }
}

public sealed class ContactUpdateOptions
{
    [JsonIgnore] public Guid? Id { get; init; }
    [JsonIgnore] public string? Email { get; init; }
    // ponytail: null omits the field (leave unchanged); explicit null-to-clear
    // isn't exposed. Add a tri-state wrapper here if clearing a field is needed.
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public bool? Unsubscribed { get; init; }
    public Dictionary<string, object?>? Properties { get; init; }
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
    public Dictionary<string, object?>? Properties { get; init; }
}

public sealed class ContactListItem
{
    public Guid Id { get; init; }
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? CreatedAt { get; init; }
    public bool Unsubscribed { get; init; }
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

// ---- topics --------------------------------------------------------------

public sealed class TopicCreateOptions
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public TopicSubscription DefaultSubscription { get; init; }
}

public sealed class Topic
{
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public TopicSubscription DefaultSubscription { get; init; }
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
    public Guid? TopicId { get; init; }
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
    public Guid? TopicId { get; init; }
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

// ---- internal ------------------------------------------------------------

// The API's non-2xx body. `statusCode` is camelCase on the wire (unlike the
// snake_case success bodies), so it needs an explicit name.
internal sealed class ErrorBody
{
    [JsonPropertyName("statusCode")] public int? StatusCode { get; init; }
    public string? Name { get; init; }
    public string? Message { get; init; }
}
