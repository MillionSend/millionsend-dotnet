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
    private const string DefaultBaseUrl = "http://localhost:3001";
    private const string Version = "0.1.0";

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

        _http = httpClient ?? new HttpClient();
    }

    /// <summary>Convenience constructor. <paramref name="apiToken"/> and
    /// <paramref name="apiUrl"/> fall back to their environment variables when empty.</summary>
    public MillionSendClient(string apiToken, string? apiUrl = null)
        : this(new MillionSendClientOptions { ApiToken = apiToken, ApiUrl = apiUrl })
    {
    }

    // ---- HTTP core -------------------------------------------------------

    private async Task<MillionSendResponse<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body = null,
        IReadOnlyDictionary<string, object?>? query = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path + QueryString(query));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"millionsend-dotnet/{Version}");
        // Idempotency is POST-only on the wire; sending it elsewhere is a no-op.
        if (idempotencyKey is not null && method == HttpMethod.Post)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (body is not null && (method == HttpMethod.Post || method == HttpMethod.Patch))
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
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
                var content = string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<T>(text, Json);
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

    private static string QueryString(IReadOnlyDictionary<string, object?>? query)
    {
        if (query is null || query.Count == 0) return string.Empty;
        var parts = new List<string>();
        foreach (var kv in query)
        {
            if (kv.Value is null) continue;
            var value = Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? string.Empty;
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
    private static string ContactPath(Guid? id, string? email, Guid? audienceId)
    {
        var key = !string.IsNullOrEmpty(email) ? Enc(email!) : (id?.ToString() ?? string.Empty);
        return audienceId is Guid aid ? $"/audiences/{aid}/contacts/{key}" : $"/contacts/{key}";
    }

    private static IReadOnlyDictionary<string, object?>? ListQuery(ListOptions? options)
    {
        if (options is null) return null;
        var query = new Dictionary<string, object?>();
        if (options.Limit.HasValue) query["limit"] = options.Limit.Value;
        if (options.After.HasValue) query["after"] = options.After.Value;
        if (options.Before.HasValue) query["before"] = options.Before.Value;
        return query.Count == 0 ? null : query;
    }

    // ---- emails ----------------------------------------------------------

    public Task<MillionSendResponse<CreateEmailResponse>> EmailSendAsync(EmailMessage message, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        => SendAsync<CreateEmailResponse>(HttpMethod.Post, "/emails", message, idempotencyKey: idempotencyKey, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Email>> EmailRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Email>(HttpMethod.Get, $"/emails/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<CancelEmailResponse>> EmailCancelAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<CancelEmailResponse>(HttpMethod.Post, $"/emails/{id}/cancel", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DataResponse<CreateEmailResponse>>> EmailBatchAsync(IEnumerable<EmailMessage> messages, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        => SendAsync<DataResponse<CreateEmailResponse>>(HttpMethod.Post, "/emails/batch", messages.ToList(), idempotencyKey: idempotencyKey, cancellationToken: cancellationToken);

    // ---- audiences -------------------------------------------------------

    public Task<MillionSendResponse<Audience>> AudienceAddAsync(string name, CancellationToken cancellationToken = default)
        => SendAsync<Audience>(HttpMethod.Post, "/audiences", new { name }, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Audience>> AudienceRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Audience>(HttpMethod.Get, $"/audiences/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<AudienceListItem>>> AudienceListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<AudienceListItem>>(HttpMethod.Get, "/audiences", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DeleteResponse>> AudienceDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<DeleteResponse>(HttpMethod.Delete, $"/audiences/{id}", cancellationToken: cancellationToken);

    // ---- contacts --------------------------------------------------------

    public Task<MillionSendResponse<ContactId>> ContactAddAsync(ContactCreateOptions options, CancellationToken cancellationToken = default)
    {
        var path = options.AudienceId is Guid aid ? $"/audiences/{aid}/contacts" : "/contacts";
        return SendAsync<ContactId>(HttpMethod.Post, path, options, cancellationToken: cancellationToken);
    }

    public Task<MillionSendResponse<Contact>> ContactRetrieveAsync(ContactAddress address, CancellationToken cancellationToken = default)
        => SendAsync<Contact>(HttpMethod.Get, ContactPath(address.Id, address.Email, address.AudienceId), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ContactId>> ContactUpdateAsync(ContactUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<ContactId>(HttpMethod.Patch, ContactPath(options.Id, options.Email, options.AudienceId), options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<RemoveContactResponse>> ContactDeleteAsync(ContactAddress address, CancellationToken cancellationToken = default)
        => SendAsync<RemoveContactResponse>(HttpMethod.Delete, ContactPath(address.Id, address.Email, address.AudienceId), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<ContactListItem>>> ContactListAsync(Guid? audienceId = null, ListOptions? options = null, CancellationToken cancellationToken = default)
    {
        var path = audienceId is Guid aid ? $"/audiences/{aid}/contacts" : "/contacts";
        return SendAsync<ListResponse<ContactListItem>>(HttpMethod.Get, path, query: ListQuery(options), cancellationToken: cancellationToken);
    }

    public Task<MillionSendResponse<ContactId>> ContactTopicsUpdateAsync(ContactTopicsUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<ContactId>(HttpMethod.Patch, ContactPath(options.Id, options.Email, null) + "/topics", options.Topics, cancellationToken: cancellationToken);

    // ---- topics ----------------------------------------------------------

    public Task<MillionSendResponse<TopicId>> TopicAddAsync(TopicCreateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<TopicId>(HttpMethod.Post, "/topics", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Topic>> TopicRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Topic>(HttpMethod.Get, $"/topics/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<DataResponse<Topic>>> TopicListAsync(CancellationToken cancellationToken = default)
        => SendAsync<DataResponse<Topic>>(HttpMethod.Get, "/topics", cancellationToken: cancellationToken);

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
        => SendAsync<Segment>(HttpMethod.Post, "/segments2", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Segment>> SegmentRetrieveAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<Segment>(HttpMethod.Get, $"/segments2/{id}", cancellationToken: cancellationToken);

    public Task<MillionSendResponse<ListResponse<Segment>>> SegmentListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => SendAsync<ListResponse<Segment>>(HttpMethod.Get, "/segments2", query: ListQuery(options), cancellationToken: cancellationToken);

    public Task<MillionSendResponse<Segment>> SegmentUpdateAsync(Guid id, SegmentUpdateOptions options, CancellationToken cancellationToken = default)
        => SendAsync<Segment>(HttpMethod.Patch, $"/segments2/{id}", options, cancellationToken: cancellationToken);

    public Task<MillionSendResponse<RemoveSegmentResponse>> SegmentDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<RemoveSegmentResponse>(HttpMethod.Delete, $"/segments2/{id}", cancellationToken: cancellationToken);
}
