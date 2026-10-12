using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MillionSend;

/// <summary>
/// Client for a MillionSend instance. Construct with an API token (and optional
/// instance URL), or with <see cref="MillionSendClientOptions"/> which additionally
/// read <c>MILLIONSEND_API_KEY</c> / <c>MILLIONSEND_BASE_URL</c> as fallbacks.
/// </summary>
public sealed class MillionSendClient : IMillionSend
{
    private const string DefaultBaseUrl = "https://api.millionsend.com";
    private const string Version = "0.9.0";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    /// <param name="options">Token and URL; each falls back to its environment variable.</param>
    /// <param name="httpClient">Injectable client (tests, proxies). A default is created when null.</param>
    public MillionSendClient(MillionSendClientOptions options, HttpClient? httpClient = null)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        var key = string.IsNullOrEmpty(options.ApiToken)
            ? Environment.GetEnvironmentVariable("MILLIONSEND_API_KEY")
            : options.ApiToken;
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException(
                "Missing API key. Set MillionSendClientOptions.ApiToken or the MILLIONSEND_API_KEY environment variable.",
                nameof(options));
        _apiKey = key;

        var url = string.IsNullOrEmpty(options.ApiUrl)
            ? Environment.GetEnvironmentVariable("MILLIONSEND_BASE_URL")
            : options.ApiUrl;
        _baseUrl = (string.IsNullOrEmpty(url) ? DefaultBaseUrl : url).TrimEnd('/');
        if (!options.AllowInsecureHttp && IsInsecureHttpUrl(_baseUrl))
            throw new ArgumentException(
                $"Refusing to send the API key over plain http to {_baseUrl}. Use https, or set MillionSendClientOptions.AllowInsecureHttp = true.",
                nameof(options));

        _http = httpClient ?? new HttpClient();
    }

    /// <summary>True for an http:// URL whose host is not loopback. Unparseable URLs are left to HttpClient.</summary>
    private static bool IsInsecureHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return false;
        return !uri.IsLoopback;
    }

    /// <summary>Convenience constructor. <paramref name="apiToken"/> and
    /// <paramref name="apiUrl"/> fall back to their environment variables when empty.</summary>
    public MillionSendClient(string apiToken, string? apiUrl = null)
        : this(new MillionSendClientOptions { ApiToken = apiToken, ApiUrl = apiUrl })
    {
    }

    // ---- HTTP core -------------------------------------------------------

    private Task<MillionSendResponse<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body = null,
        IReadOnlyDictionary<string, object?>? query = null,
        string? idempotencyKey = null,
        BatchValidationMode? validation = null,
        CancellationToken cancellationToken = default)
        => SendCoreAsync(method, path, static (_, text) => JsonSerializer.Deserialize<T>(text, Json), body, query, idempotencyKey, validation, cancellationToken);

    // The inbox send endpoints answer 202 with the draft their send rules held
    // instead of the sent object, so the status picks the body's type.
    private Task<MillionSendResponse<InboxSendResult<TSent>>> SendOrHoldAsync<TSent>(
        string path, object? body, string? idempotencyKey, CancellationToken cancellationToken)
        where TSent : class
        => SendCoreAsync(
            HttpMethod.Post,
            path,
            static (status, text) => status == 202
                ? new InboxSendResult<TSent> { IsDraft = true, Draft = JsonSerializer.Deserialize<InboxDraft>(text, Json) }
                : new InboxSendResult<TSent> { Sent = JsonSerializer.Deserialize<TSent>(text, Json) },
            body,
            null,
            idempotencyKey,
            null,
            cancellationToken);

    private async Task<MillionSendResponse<T>> SendCoreAsync<T>(
        HttpMethod method,
        string path,
        Func<int, string, T?> read,
        object? body,
        IReadOnlyDictionary<string, object?>? query,
        string? idempotencyKey,
        BatchValidationMode? validation,
        CancellationToken cancellationToken)
    {
        // URL normalization resolves an empty, "." or ".." segment away, so an id
        // such as ".." would send the request to another resource
        // (DELETE /templates/.. would reach /).
        if (path.Split('/').Skip(1).Any(s => s.Length == 0 || s == "." || s == ".."))
            return MillionSendResponse<T>.Fail(new MillionSendException(
                "An id in the request path is empty, \".\" or \"..\".", null, "validation_error"));

        using var request = new HttpRequestMessage(method, _baseUrl + path + QueryString(query));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"millionsend-dotnet/{Version}");
        if (body is not null && (method == HttpMethod.Post || method == HttpMethod.Patch))
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            // Idempotency is POST-only on the wire; sending it elsewhere is a no-op.
            // Validated add: a caller-supplied key carrying CR/LF must never split
            // into extra headers, so a bad key fails the call instead.
            if (idempotencyKey is not null && method == HttpMethod.Post)
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            if (validation.HasValue)
                request.Headers.Add("x-batch-validation", Wire(validation.Value));
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Transport/client failure never reached the API → statusCode null.
            var message = string.IsNullOrEmpty(ex.Message) ? "request failed" : ex.Message;
            return MillionSendResponse<T>.Fail(new MillionSendException(message, null, "application_error"));
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return MillionSendResponse<T>.Fail(ParseError((int)response.StatusCode, text));
            try
            {
                var content = string.IsNullOrEmpty(text) ? default : read((int)response.StatusCode, text);
                return MillionSendResponse<T>.Ok(content);
            }
            catch (JsonException ex)
            {
                // A 200 whose body is not the expected JSON (proxy interstitial,
                // truncated response) is a client-side failure, not a throw —
                // the documented contract is that methods never throw.
                return MillionSendResponse<T>.Fail(
                    new MillionSendException($"Unparseable response body: {ex.Message}", null, "application_error"));
            }
        }
    }

    /// <summary>Enum member as the API spells it (snake_case), for headers and query strings.</summary>
    private static string Wire(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static string QueryString(IReadOnlyDictionary<string, object?>? query)
    {
        if (query is null || query.Count == 0) return string.Empty;
        var parts = new List<string>();
        foreach (var kv in query)
        {
            if (kv.Value is null) continue;
            var value = kv.Value switch
            {
                Enum e => Wire(e),
                bool b => b ? "true" : "false",
                _ => Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? string.Empty,
            };
            parts.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(value)}");
        }
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    private static MillionSendException ParseError(int status, string? text)
    {
        var name = "application_error";
        var message = $"Request failed with status {status}";
        int? statusCode = status;
        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                var body = JsonSerializer.Deserialize<ErrorBody>(text, Json);
                if (body is not null)
                {
                    if (!string.IsNullOrEmpty(body.Name)) name = body.Name!;
                    if (!string.IsNullOrEmpty(body.Message)) message = body.Message!;
                    if (body.StatusCode.HasValue) statusCode = body.StatusCode;
                }
            }
            catch (JsonException)
            {
                // Non-JSON error body → keep the status-derived defaults.
            }
        }
        return new MillionSendException(message, statusCode, name);
    }

    private static string Enc(string value) => Uri.EscapeDataString(value);

    // Email wins over id (matches the API's addressability).
    private static string ContactPath(Guid? id, string? email)
        => "/contacts/" + (!string.IsNullOrEmpty(email) ? Enc(email!) : (id?.ToString() ?? string.Empty));

    private static Dictionary<string, object?> ListQuery(ListOptions? options)
    {
        var query = new Dictionary<string, object?>();
        if (options is null) return query;
        if (options.Limit.HasValue) query["limit"] = options.Limit.Value;
        if (options.After.HasValue) query["after"] = options.After.Value;
        if (options.Before.HasValue) query["before"] = options.Before.Value;
        return query;
    }

    private static string? Csv<T>(IEnumerable<T>? values)
    {
        var joined = values is null ? string.Empty : string.Join(",", values);
        return joined.Length == 0 ? null : joined;
    }

    private static Dictionary<string, object?> ThreadQuery(InboxThreadListOptions? options)
    {
        var query = ListQuery(options);
        if (options is null) return query;
        query["folders"] = Csv(options.Folders);
        query["labels"] = Csv(options.Labels);
        query["read"] = options.Read;
        if (options.IncludeQuarantined) query["include_quarantined"] = true;
        return query;
    }

    private static Dictionary<string, object?> ContactListQuery(ListOptions? options)
    {
        var query = ListQuery(options);
        if (options is ContactListOptions { Include: { Count: > 0 } include })
            query["include"] = string.Join(",", include.Select(i => Wire(i)));
        return query;
    }

    // ---- emails ----------------------------------------------------------

    public Task<MillionSendResponse<CreateEmailResponse>> EmailSendAsync(EmailMessage message, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        => SendAsync<CreateEmailResponse>(HttpMethod.Post, "/emails", message, idempotencyKey: idempotencyKey, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<CreateEmailResponse>> EmailSendAsync(string idempotencyKey, EmailMessage message, CancellationToken cancellationToken = default)
        => EmailSendAsync(message, idempotencyKey, cancellationToken);

    public Task<MillionSendResponse<Email>> EmailRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Email>(HttpMethod.Get, $"/emails/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<Email>>> EmailListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<Email>>(HttpMethod.Get, "/emails", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ObjectId>> EmailRescheduleAsync(Guid id, string scheduledAt, CancellationToken cancellationToken = default)
        => SendAsync<ObjectId>(HttpMethod.Patch, $"/emails/{id}", new { scheduledAt }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> EmailDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, $"/emails/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<EmailInsights>> EmailInsightsRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<EmailInsights>(HttpMethod.Get, $"/emails/{id}/insights", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<CancelEmailResponse>> EmailCancelAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<CancelEmailResponse>(HttpMethod.Post, $"/emails/{id}/cancel", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DataResponse<CreateEmailResponse>>> EmailBatchAsync(IEnumerable<EmailMessage> messages, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        => SendAsync<DataResponse<CreateEmailResponse>>(HttpMethod.Post, "/emails/batch", messages.ToList(), idempotencyKey: idempotencyKey, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DataResponse<CreateEmailResponse>>> EmailBatchAsync(string idempotencyKey, IEnumerable<EmailMessage> messages, CancellationToken cancellationToken = default)
        => EmailBatchAsync(messages, idempotencyKey, cancellationToken);

    public Task<MillionSendResponse<DataResponse<CreateEmailResponse>>> EmailBatchAsync(IEnumerable<EmailMessage> messages, BatchValidationMode validation, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        => SendAsync<DataResponse<CreateEmailResponse>>(HttpMethod.Post, "/emails/batch", messages.ToList(), idempotencyKey: idempotencyKey, validation: validation, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DataResponse<CreateEmailResponse>>> EmailBatchAsync(string idempotencyKey, IEnumerable<EmailMessage> messages, BatchValidationMode validation, CancellationToken cancellationToken = default)
        => EmailBatchAsync(messages, validation, idempotencyKey, cancellationToken);

    // ---- contacts (team-global) ------------------------------------------

    public Task<MillionSendResponse<ContactId>> ContactAddAsync(ContactCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<ContactId>(HttpMethod.Post, "/contacts", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ContactBatchResponse>> ContactBatchAsync(IEnumerable<ContactCreateOptions> contacts, ContactBatchOptions? options = null, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, object?>();
        if (options?.OnConflict is { } onConflict) query["on_conflict"] = onConflict;
        return SendAsync<ContactBatchResponse>(HttpMethod.Post, "/contacts/batch", contacts.ToList(), query, validation: options?.Validation, cancellationToken: cancellationToken);
    }

    public Task<MillionSendResponse<Contact>> ContactRetrieveAsync(ContactAddress address, CancellationToken cancellationToken = default)
        => SendAsync<Contact>(HttpMethod.Get, ContactPath(address.Id, address.Email), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ContactId>> ContactUpdateAsync(ContactUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<ContactId>(HttpMethod.Patch, ContactPath(options.Id, options.Email), options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<RemoveContactResponse>> ContactDeleteAsync(ContactAddress address, ContactDeleteOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<RemoveContactResponse>(HttpMethod.Delete, ContactPath(address.Id, address.Email),
            query: options?.Erase is true ? new Dictionary<string, object?> { ["erase"] = "true" } : null, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DataResponse<RemoveContactResponse>>> ContactBatchRemoveAsync(IEnumerable<string> emails, ContactDeleteOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<DataResponse<RemoveContactResponse>>(HttpMethod.Post, "/contacts/batch/remove", new { emails = emails.ToList(), erase = options?.Erase }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DataResponse<RemoveContactResponse>>> ContactBatchRemoveAsync(IEnumerable<Guid> ids, ContactDeleteOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<DataResponse<RemoveContactResponse>>(HttpMethod.Post, "/contacts/batch/remove", new { ids = ids.ToList(), erase = options?.Erase }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ContactBatchGetResponse>> ContactBatchGetAsync(IEnumerable<ContactAddress> addresses, ContactBatchGetOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ContactBatchGetResponse>(HttpMethod.Post, "/contacts/batch/get", new
        {
            // The wire takes exactly one of id/email per entry; email wins, as in ContactPath.
            contacts = addresses.Select(a => string.IsNullOrEmpty(a.Email) ? new ContactAddress { Id = a.Id } : new ContactAddress { Email = a.Email }).ToList(),
            include = options?.Include,
        }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<ContactListItem>>> ContactListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<ContactListItem>>(HttpMethod.Get, "/contacts", query: ContactListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ContactId>> ContactTopicsUpdateAsync(ContactTopicsUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<ContactId>(HttpMethod.Patch, ContactPath(options.Id, options.Email) + "/topics", options.Topics, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<ContactTopic>>> ContactListTopicsAsync(ContactAddress address, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<ContactTopic>>(HttpMethod.Get, ContactPath(address.Id, address.Email) + "/topics", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ContactPreferencesLink>> ContactPreferencesLinkAsync(ContactAddress address, CancellationToken cancellationToken = default)
        => SendAsync<ContactPreferencesLink>(HttpMethod.Post, ContactPath(address.Id, address.Email) + "/preferences-link", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ObjectId>> ContactAddToSegmentAsync(ContactAddress address, Guid segmentId, CancellationToken cancellationToken = default)
        => SendAsync<ObjectId>(HttpMethod.Post, ContactPath(address.Id, address.Email) + $"/segments/{segmentId}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> ContactRemoveFromSegmentAsync(ContactAddress address, Guid segmentId, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, ContactPath(address.Id, address.Email) + $"/segments/{segmentId}", cancellationToken: cancellationToken);

    // ---- contact properties ----------------------------------------------

    public Task<MillionSendResponse<ContactProperty>> ContactPropCreateAsync(ContactPropertyCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<ContactProperty>(HttpMethod.Post, "/contact-properties", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<ContactProperty>>> ContactPropListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<ContactProperty>>(HttpMethod.Get, "/contact-properties", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ContactProperty>> ContactPropRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<ContactProperty>(HttpMethod.Get, $"/contact-properties/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ObjectId>> ContactPropUpdateAsync(Guid id, ContactPropertyUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<ObjectId>(HttpMethod.Patch, $"/contact-properties/{id}", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> ContactPropDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, $"/contact-properties/{id}", cancellationToken: cancellationToken);

    // ---- topics ----------------------------------------------------------

    public Task<MillionSendResponse<TopicId>> TopicAddAsync(TopicCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<TopicId>(HttpMethod.Post, "/topics", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Topic>> TopicRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Topic>(HttpMethod.Get, $"/topics/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DataResponse<Topic>>> TopicListAsync(CancellationToken cancellationToken = default)
        => SendAsync<DataResponse<Topic>>(HttpMethod.Get, "/topics", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<TopicId>> TopicUpdateAsync(Guid id, TopicUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<TopicId>(HttpMethod.Patch, $"/topics/{id}", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<RemoveTopicResponse>> TopicDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<RemoveTopicResponse>(HttpMethod.Delete, $"/topics/{id}", cancellationToken: cancellationToken);

    // ---- broadcasts ------------------------------------------------------

    public Task<MillionSendResponse<BroadcastId>> BroadcastAddAsync(BroadcastCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<BroadcastId>(HttpMethod.Post, "/broadcasts", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Broadcast>> BroadcastRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Broadcast>(HttpMethod.Get, $"/broadcasts/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<BroadcastListItem>>> BroadcastListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<BroadcastListItem>>(HttpMethod.Get, "/broadcasts", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<BroadcastId>> BroadcastUpdateAsync(Guid id, BroadcastUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<BroadcastId>(HttpMethod.Patch, $"/broadcasts/{id}", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<RemoveBroadcastResponse>> BroadcastDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<RemoveBroadcastResponse>(HttpMethod.Delete, $"/broadcasts/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<BroadcastId>> BroadcastSendAsync(Guid id, string? scheduledAt = null, CancellationToken cancellationToken = default)
        => SendAsync<BroadcastId>(HttpMethod.Post, $"/broadcasts/{id}/send", scheduledAt is null ? new object() : new { scheduledAt }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<CancelBroadcastResponse>> BroadcastCancelAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<CancelBroadcastResponse>(HttpMethod.Post, $"/broadcasts/{id}/cancel", cancellationToken: cancellationToken);

    // ---- segments --------------------------------------------------------

    public Task<MillionSendResponse<Segment>> SegmentAddAsync(SegmentCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<Segment>(HttpMethod.Post, "/segments", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Segment>> SegmentRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Segment>(HttpMethod.Get, $"/segments/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<Segment>>> SegmentListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<Segment>>(HttpMethod.Get, "/segments", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Segment>> SegmentUpdateAsync(Guid id, SegmentUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<Segment>(HttpMethod.Patch, $"/segments/{id}", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<RemoveSegmentResponse>> SegmentDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<RemoveSegmentResponse>(HttpMethod.Delete, $"/segments/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<ContactListItem>>> SegmentContactListAsync(Guid id, ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<ContactListItem>>(HttpMethod.Get, $"/segments/{id}/contacts", query: ContactListQuery(options), cancellationToken: cancellationToken);

    // ---- suppressions ----------------------------------------------------

    public Task<MillionSendResponse<ObjectId>> SuppressionAddAsync(string email, SuppressionOrigin? origin = null, CancellationToken cancellationToken = default)
        => SendAsync<ObjectId>(HttpMethod.Post, "/suppressions", new { email, origin }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<Suppression>>> SuppressionListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
    {
        var query = ListQuery(options);
        if (options is SuppressionListOptions { Origin: { } origin }) query["origin"] = origin;
        return SendAsync<ListResponse<Suppression>>(HttpMethod.Get, "/suppressions", query: query, cancellationToken: cancellationToken);
    }

    public Task<MillionSendResponse<Suppression>> SuppressionRetrieveAsync(string idOrEmail, CancellationToken cancellationToken = default)
        => SendAsync<Suppression>(HttpMethod.Get, "/suppressions/" + Enc(idOrEmail), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> SuppressionRemoveAsync(string idOrEmail, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, "/suppressions/" + Enc(idOrEmail), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DataResponse<ObjectId>>> SuppressionBatchAddAsync(IEnumerable<string> emails, SuppressionOrigin? origin = null, CancellationToken cancellationToken = default)
        => SendAsync<DataResponse<ObjectId>>(HttpMethod.Post, "/suppressions/batch/add", new { emails = emails.ToList(), origin }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DataResponse<DeletedResponse>>> SuppressionBatchRemoveAsync(IEnumerable<string> emails, CancellationToken cancellationToken = default)
        => SendAsync<DataResponse<DeletedResponse>>(HttpMethod.Post, "/suppressions/batch/remove", new { emails = emails.ToList() }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DataResponse<DeletedResponse>>> SuppressionBatchRemoveAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
        => SendAsync<DataResponse<DeletedResponse>>(HttpMethod.Post, "/suppressions/batch/remove", new { ids = ids.ToList() }, cancellationToken: cancellationToken);

    // ---- domains ---------------------------------------------------------

    public Task<MillionSendResponse<Domain>> DomainAddAsync(DomainCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<Domain>(HttpMethod.Post, "/domains", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<Domain>>> DomainListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<Domain>>(HttpMethod.Get, "/domains", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Domain>> DomainRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Domain>(HttpMethod.Get, $"/domains/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Domain>> DomainVerifyAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Domain>(HttpMethod.Post, $"/domains/{id}/verify", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Domain>> DomainUpdateAsync(Guid id, DomainUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<Domain>(HttpMethod.Patch, $"/domains/{id}", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> DomainDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, $"/domains/{id}", cancellationToken: cancellationToken);

    // ---- webhooks --------------------------------------------------------

    public Task<MillionSendResponse<WebhookCreateResponse>> WebhookCreateAsync(WebhookCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<WebhookCreateResponse>(HttpMethod.Post, "/webhooks", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<Webhook>>> WebhookListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<Webhook>>(HttpMethod.Get, "/webhooks", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Webhook>> WebhookRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Webhook>(HttpMethod.Get, $"/webhooks/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ObjectId>> WebhookUpdateAsync(Guid id, WebhookUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<ObjectId>(HttpMethod.Patch, $"/webhooks/{id}", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> WebhookDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, $"/webhooks/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<WebhookRotateResponse>> WebhookRotateAsync(Guid id, WebhookRotateOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<WebhookRotateResponse>(HttpMethod.Post, $"/webhooks/{id}/rotate", options ?? new WebhookRotateOptions(), cancellationToken: cancellationToken);

    // ---- api keys --------------------------------------------------------

    public Task<MillionSendResponse<ApiKeyCreateResponse>> ApiKeyCreateAsync(string name, ApiKeyPermission? permission = null, Guid? domainId = null, CancellationToken cancellationToken = default)
        => ApiKeyCreateAsync(new ApiKeyCreateOptions { Name = name, Permission = permission, DomainId = domainId }, cancellationToken);

    public Task<MillionSendResponse<ApiKeyCreateResponse>> ApiKeyCreateAsync(ApiKeyCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<ApiKeyCreateResponse>(HttpMethod.Post, "/api-keys", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<ApiKey>>> ApiKeyListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<ApiKey>>(HttpMethod.Get, "/api-keys", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> ApiKeyDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, $"/api-keys/{id}", cancellationToken: cancellationToken);

    // ---- templates -------------------------------------------------------

    public Task<MillionSendResponse<ObjectId>> TemplateCreateAsync(TemplateCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<ObjectId>(HttpMethod.Post, "/templates", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<Template>>> TemplateListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<Template>>(HttpMethod.Get, "/templates", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Template>> TemplateRetrieveAsync(string idOrAlias, CancellationToken cancellationToken = default)
        => SendAsync<Template>(HttpMethod.Get, "/templates/" + Enc(idOrAlias), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ObjectId>> TemplateUpdateAsync(string idOrAlias, TemplateUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<ObjectId>(HttpMethod.Patch, "/templates/" + Enc(idOrAlias), options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> TemplateDeleteAsync(string idOrAlias, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, "/templates/" + Enc(idOrAlias), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ObjectId>> TemplatePublishAsync(string idOrAlias, CancellationToken cancellationToken = default)
        => SendAsync<ObjectId>(HttpMethod.Post, "/templates/" + Enc(idOrAlias) + "/publish", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ObjectId>> TemplateDuplicateAsync(string idOrAlias, CancellationToken cancellationToken = default)
        => SendAsync<ObjectId>(HttpMethod.Post, "/templates/" + Enc(idOrAlias) + "/duplicate", cancellationToken: cancellationToken);

    // ---- usage -----------------------------------------------------------

    public Task<MillionSendResponse<Usage>> UsageRetrieveAsync(CancellationToken cancellationToken = default)
        => SendAsync<Usage>(HttpMethod.Get, "/usage", cancellationToken: cancellationToken);

    // ---- deliverability --------------------------------------------------

    public Task<MillionSendResponse<Deliverability>> DeliverabilityRetrieveAsync(CancellationToken cancellationToken = default)
        => SendAsync<Deliverability>(HttpMethod.Get, "/deliverability", cancellationToken: cancellationToken);

    // ---- receiving -------------------------------------------------------

    public Task<MillionSendResponse<ReceivedEmailList>> ReceivedEmailListAsync(ReceivedEmailListOptions? options = null, CancellationToken cancellationToken = default)
    {
        var query = ListQuery(options);
        if (options is not null)
        {
            query["wait"] = options.Wait;
            query["since"] = options.Since;
            query["inbox_id"] = options.InboxId;
            query["to"] = options.To;
            query["from"] = options.From;
            query["subject_contains"] = options.SubjectContains;
            query["unread"] = options.Unread;
            if (options.IncludeQuarantined) query["include_quarantined"] = true;
            if (options.Quarantined) query["quarantined"] = true;
        }
        return SendAsync<ReceivedEmailList>(HttpMethod.Get, "/emails/receiving", query: query, cancellationToken: cancellationToken);
    }

    public Task<MillionSendResponse<ReceivedEmail>> ReceivedEmailRetrieveAsync(Guid id, string? htmlFormat = null, CancellationToken cancellationToken = default)
        => SendAsync<ReceivedEmail>(HttpMethod.Get, $"/emails/receiving/{id}",
            query: new Dictionary<string, object?> { ["html_format"] = htmlFormat }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<InboxSendResult<ThreadEmail>>> ReceivedEmailReplyAsync(Guid id, EmailReplyOptions options, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        => SendOrHoldAsync<ThreadEmail>($"/emails/receiving/{id}/reply", options, idempotencyKey, cancellationToken);

    public Task<MillionSendResponse<InboxSendResult<ThreadEmail>>> ReceivedEmailForwardAsync(Guid id, EmailForwardOptions options, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        => SendOrHoldAsync<ThreadEmail>($"/emails/receiving/{id}/forward", options, idempotencyKey, cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> ReceivedEmailDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, $"/emails/receiving/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<ReceivedAttachment>>> ReceivedEmailAttachmentListAsync(Guid emailId, ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<ReceivedAttachment>>(HttpMethod.Get, $"/emails/receiving/{emailId}/attachments", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ReceivedAttachment>> ReceivedEmailAttachmentRetrieveAsync(Guid emailId, Guid id, CancellationToken cancellationToken = default)
        => SendAsync<ReceivedAttachment>(HttpMethod.Get, $"/emails/receiving/{emailId}/attachments/{id}", cancellationToken: cancellationToken);

    // ---- inboxes ---------------------------------------------------------

    public Task<MillionSendResponse<Inbox>> InboxCreateAsync(InboxCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<Inbox>(HttpMethod.Post, "/inboxes", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<Inbox>>> InboxListAsync(InboxListOptions? options = null, CancellationToken cancellationToken = default)
    {
        var query = ListQuery(options);
        query["kind"] = options?.Kind;
        return SendAsync<ListResponse<Inbox>>(HttpMethod.Get, "/inboxes", query: query, cancellationToken: cancellationToken);
    }

    public Task<MillionSendResponse<Inbox>> InboxRetrieveAsync(string idOrEmail, CancellationToken cancellationToken = default)
        => SendAsync<Inbox>(HttpMethod.Get, "/inboxes/" + Enc(idOrEmail), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Inbox>> InboxUpdateAsync(Guid id, InboxUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<Inbox>(HttpMethod.Patch, $"/inboxes/{id}", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> InboxDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, $"/inboxes/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<InboxSafety>> InboxSafetyRetrieveAsync(Guid inboxId, CancellationToken cancellationToken = default)
        => SendAsync<InboxSafety>(HttpMethod.Get, $"/inboxes/{inboxId}/safety", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<InboxSafety>> InboxSafetyUpdateAsync(Guid inboxId, InboxSafetyOptions options, CancellationToken cancellationToken = default)
        => SendAsync<InboxSafety>(HttpMethod.Patch, $"/inboxes/{inboxId}/safety", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<VerificationCode>> InboxCodeLatestAsync(Guid inboxId, InboxCodeLatestOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<VerificationCode>(HttpMethod.Get, $"/inboxes/{inboxId}/codes/latest", query: new Dictionary<string, object?>
        {
            ["from"] = options?.From,
            ["wait"] = options?.Wait,
            ["max_age"] = options?.MaxAge,
        }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<QuarantinedEmail>>> InboxQuarantineListAsync(Guid inboxId, ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<QuarantinedEmail>>(HttpMethod.Get, $"/inboxes/{inboxId}/quarantine", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<QuarantineRelease>> InboxQuarantineReleaseAsync(Guid inboxId, Guid emailId, bool trustSender = false, CancellationToken cancellationToken = default)
        => SendAsync<QuarantineRelease>(HttpMethod.Post, $"/inboxes/{inboxId}/quarantine/{emailId}/release",
            trustSender ? new { trustSender } : new object(), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<InboxLabel>>> InboxLabelListAsync(Guid inboxId, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<InboxLabel>>(HttpMethod.Get, $"/inboxes/{inboxId}/labels", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<InboxLabel>> InboxLabelCreateAsync(Guid inboxId, InboxLabelOptions options, CancellationToken cancellationToken = default)
        => SendAsync<InboxLabel>(HttpMethod.Post, $"/inboxes/{inboxId}/labels", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<InboxLabel>> InboxLabelUpdateAsync(Guid inboxId, Guid labelId, InboxLabelOptions options, CancellationToken cancellationToken = default)
        => SendAsync<InboxLabel>(HttpMethod.Patch, $"/inboxes/{inboxId}/labels/{labelId}", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> InboxLabelDeleteAsync(Guid inboxId, Guid labelId, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, $"/inboxes/{inboxId}/labels/{labelId}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<InboxThread>>> InboxThreadListAsync(Guid inboxId, InboxThreadListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<InboxThread>>(HttpMethod.Get, $"/inboxes/{inboxId}/threads", query: ThreadQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<InboxThread>>> InboxThreadSearchAsync(Guid inboxId, InboxThreadSearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        var query = ThreadQuery(options);
        if (options is not null)
        {
            query["query"] = options.Query;
            query["from"] = Csv(options.From);
            query["to"] = Csv(options.To);
            query["cc"] = Csv(options.Cc);
            query["bcc"] = Csv(options.Bcc);
            query["has_attachment"] = options.HasAttachment;
            query["start_date"] = options.StartDate;
            query["end_date"] = options.EndDate;
        }
        return SendAsync<ListResponse<InboxThread>>(HttpMethod.Get, $"/inboxes/{inboxId}/threads/search", query: query, cancellationToken: cancellationToken);
    }

    public Task<MillionSendResponse<InboxThread>> InboxThreadRetrieveAsync(Guid inboxId, Guid threadId, CancellationToken cancellationToken = default)
        => SendAsync<InboxThread>(HttpMethod.Get, $"/inboxes/{inboxId}/threads/{threadId}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<InboxThread>> InboxThreadUpdateAsync(Guid inboxId, Guid threadId, InboxThreadUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<InboxThread>(HttpMethod.Patch, $"/inboxes/{inboxId}/threads/{threadId}", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> InboxThreadDeleteAsync(Guid inboxId, Guid threadId, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, $"/inboxes/{inboxId}/threads/{threadId}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<ThreadEmail>>> InboxThreadEmailListAsync(Guid inboxId, Guid threadId, InboxThreadEmailListOptions? options = null, CancellationToken cancellationToken = default)
    {
        var query = ListQuery(options);
        if (options?.IncludeQuarantined == true) query["include_quarantined"] = true;
        return SendAsync<ListResponse<ThreadEmail>>(HttpMethod.Get, $"/inboxes/{inboxId}/threads/{threadId}/emails", query: query, cancellationToken: cancellationToken);
    }

    public Task<MillionSendResponse<ThreadEmail>> InboxThreadEmailRetrieveAsync(Guid inboxId, Guid threadId, Guid emailId, bool includeQuarantined = false, CancellationToken cancellationToken = default)
        => SendAsync<ThreadEmail>(HttpMethod.Get, $"/inboxes/{inboxId}/threads/{threadId}/emails/{emailId}",
            query: new Dictionary<string, object?> { ["include_quarantined"] = includeQuarantined ? true : null }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<InboxSendResult<ThreadEmail>>> InboxThreadEmailReplyAsync(Guid inboxId, Guid threadId, Guid emailId, EmailReplyOptions options, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        => SendOrHoldAsync<ThreadEmail>($"/inboxes/{inboxId}/threads/{threadId}/emails/{emailId}/reply", options, idempotencyKey, cancellationToken);

    public Task<MillionSendResponse<InboxSendResult<ThreadEmail>>> InboxThreadEmailForwardAsync(Guid inboxId, Guid threadId, Guid emailId, EmailForwardOptions options, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        => SendOrHoldAsync<ThreadEmail>($"/inboxes/{inboxId}/threads/{threadId}/emails/{emailId}/forward", options, idempotencyKey, cancellationToken);

    public Task<MillionSendResponse<ListResponse<InboxDraft>>> InboxDraftListAsync(Guid inboxId, ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<InboxDraft>>(HttpMethod.Get, $"/inboxes/{inboxId}/drafts", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<InboxDraft>> InboxDraftCreateAsync(Guid inboxId, InboxDraftCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<InboxDraft>(HttpMethod.Post, $"/inboxes/{inboxId}/drafts", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<InboxDraft>> InboxDraftRetrieveAsync(Guid inboxId, Guid draftId, CancellationToken cancellationToken = default)
        => SendAsync<InboxDraft>(HttpMethod.Get, $"/inboxes/{inboxId}/drafts/{draftId}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<InboxDraft>> InboxDraftUpdateAsync(Guid inboxId, Guid draftId, InboxDraftUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<InboxDraft>(HttpMethod.Patch, $"/inboxes/{inboxId}/drafts/{draftId}", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeletedResponse>> InboxDraftDeleteAsync(Guid inboxId, Guid draftId, CancellationToken cancellationToken = default)
        => SendAsync<DeletedResponse>(HttpMethod.Delete, $"/inboxes/{inboxId}/drafts/{draftId}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<InboxSendResult<InboxDraft>>> InboxDraftSendAsync(Guid inboxId, Guid draftId, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        => SendOrHoldAsync<InboxDraft>($"/inboxes/{inboxId}/drafts/{draftId}/send", null, idempotencyKey, cancellationToken);

    public Task<MillionSendResponse<InboxDraft>> InboxDraftRejectAsync(Guid inboxId, Guid draftId, CancellationToken cancellationToken = default)
        => SendAsync<InboxDraft>(HttpMethod.Post, $"/inboxes/{inboxId}/drafts/{draftId}/reject", cancellationToken: cancellationToken);
}
