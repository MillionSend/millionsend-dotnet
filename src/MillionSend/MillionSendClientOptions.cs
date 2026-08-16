namespace MillionSend;

/// <summary>
/// Configuration for a <see cref="MillionSendClient"/>. Both values fall back to
/// environment variables when unset: <c>ApiToken</c> to <c>MILLIONSEND_API_KEY</c>,
/// <c>ApiUrl</c> to <c>MILLIONSEND_BASE_URL</c> (then to <c>http://localhost:3001</c>).
/// </summary>
public sealed class MillionSendClientOptions
{
    /// <summary>Bearer API token. Falls back to <c>MILLIONSEND_API_KEY</c>.</summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// Your MillionSend instance URL (no trailing slash needed). Falls back to
    /// <c>MILLIONSEND_BASE_URL</c>, then <c>http://localhost:3001</c>. MillionSend is
    /// self-hosted, so set this to your deployment in production.
    /// </summary>
    public string? ApiUrl { get; set; }
}
