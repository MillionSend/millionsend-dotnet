using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace MillionSend.Tests;

public class MillionSendClientTests
{
    private static readonly Guid A1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid C1 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid B1 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid S1 = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid T1 = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid E1 = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private static (MillionSendClient client, RecordingHandler handler) NewClient(Action<RecordingHandler>? setup = null)
    {
        var handler = new RecordingHandler();
        setup?.Invoke(handler);
        var client = new MillionSendClient(
            new MillionSendClientOptions { ApiToken = "ms_test", ApiUrl = "https://api.test" },
            new HttpClient(handler));
        return (client, handler);
    }

    // ---- construction ----------------------------------------------------

    [Fact]
    public void MissingApiKey_throws()
    {
        var prior = Environment.GetEnvironmentVariable("MILLIONSEND_API_KEY");
        Environment.SetEnvironmentVariable("MILLIONSEND_API_KEY", null);
        try
        {
            Assert.Throws<ArgumentException>(() => new MillionSendClient(new MillionSendClientOptions()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MILLIONSEND_API_KEY", prior);
        }
    }

    [Fact]
    public async Task EnvVars_supply_key_and_base_url()
    {
        var priorKey = Environment.GetEnvironmentVariable("MILLIONSEND_API_KEY");
        var priorUrl = Environment.GetEnvironmentVariable("MILLIONSEND_BASE_URL");
        Environment.SetEnvironmentVariable("MILLIONSEND_API_KEY", "ms_env");
        Environment.SetEnvironmentVariable("MILLIONSEND_BASE_URL", "https://env.test/");
        try
        {
            var handler = new RecordingHandler();
            var client = new MillionSendClient(new MillionSendClientOptions(), new HttpClient(handler));
            await client.AudienceRetrieveAsync(A1);

            Assert.Equal("Bearer ms_env", handler.Last.Authorization);
            // Trailing slash on the base URL is trimmed.
            Assert.Equal($"https://env.test/audiences/{A1}", handler.Last.Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MILLIONSEND_API_KEY", priorKey);
            Environment.SetEnvironmentVariable("MILLIONSEND_BASE_URL", priorUrl);
        }
    }

    [Fact]
    public async Task Sends_auth_and_user_agent_headers()
    {
        var (client, handler) = NewClient();
        await client.AudienceRetrieveAsync(A1);
        Assert.Equal("Bearer ms_test", handler.Last.Authorization);
        Assert.StartsWith("millionsend-dotnet/", handler.Last.UserAgent);
    }

    // ---- emails ----------------------------------------------------------

    [Fact]
    public async Task EmailSend_maps_path_and_body()
    {
        var (client, handler) = NewClient();
        await client.EmailSendAsync(new EmailMessage
        {
            From = "a@x.dev",
            To = new[] { "b@x.dev" },
            Subject = "s",
            Html = "<p>h</p>",
            ReplyTo = "r@x.dev",
            ScheduledAt = "2999-01-01T00:00:00Z",
        });

        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal("/emails", handler.Last.Path);

        var body = handler.LastJson();
        Assert.Equal("a@x.dev", body.GetProperty("from").GetString());
        Assert.Equal("b@x.dev", body.GetProperty("to")[0].GetString());
        Assert.Equal("s", body.GetProperty("subject").GetString());
        Assert.Equal("<p>h</p>", body.GetProperty("html").GetString());
        Assert.Equal("r@x.dev", body.GetProperty("reply_to").GetString());
        Assert.Equal("2999-01-01T00:00:00Z", body.GetProperty("scheduled_at").GetString());
        // Unset optionals are omitted, not sent as null.
        Assert.False(body.TryGetProperty("text", out _));
        Assert.False(body.TryGetProperty("cc", out _));
    }

    [Fact]
    public async Task EmailSend_single_recipient_serializes_as_string()
    {
        var (client, handler) = NewClient();
        await client.EmailSendAsync(new EmailMessage { From = "a@x.dev", To = "b@x.dev", Subject = "s" });
        Assert.Equal(JsonValueKind.String, handler.LastJson().GetProperty("to").ValueKind);
    }

    [Fact]
    public async Task EmailSend_idempotency_header()
    {
        var (client, handler) = NewClient();
        await client.EmailSendAsync(new EmailMessage { From = "a@x.dev", To = "b@x.dev", Subject = "s" }, idempotencyKey: "key-1");
        Assert.Equal("key-1", handler.Last.IdempotencyKey);
    }

    [Fact]
    public async Task EmailSend_without_idempotency_omits_header()
    {
        var (client, handler) = NewClient();
        await client.EmailSendAsync(new EmailMessage { From = "a@x.dev", To = "b@x.dev", Subject = "s" });
        Assert.Null(handler.Last.IdempotencyKey);
    }

    [Fact]
    public async Task EmailGet_and_Cancel_paths()
    {
        var (client, handler) = NewClient();
        await client.EmailRetrieveAsync(E1);
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal($"/emails/{E1}", handler.Last.Path);

        await client.EmailCancelAsync(E1);
        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal($"/emails/{E1}/cancel", handler.Last.Path);
    }

    [Fact]
    public async Task Batch_sends_bare_array_with_idempotency()
    {
        var (client, handler) = NewClient(h => h.ResponseBody =
            "{\"data\":[{\"id\":\"11111111-1111-1111-1111-111111111111\"},{\"id\":\"22222222-2222-2222-2222-222222222222\"}]}");

        var res = await client.EmailBatchAsync(new[]
        {
            new EmailMessage { From = "a@x.dev", To = "b@x.dev", Subject = "1", Text = "one" },
            new EmailMessage { From = "a@x.dev", To = "c@x.dev", Subject = "2", Text = "two" },
        }, idempotencyKey: "batch-1");

        Assert.Equal("/emails/batch", handler.Last.Path);
        Assert.Equal(JsonValueKind.Array, handler.LastJson().ValueKind);
        Assert.Equal(2, handler.LastJson().GetArrayLength());
        Assert.Equal("batch-1", handler.Last.IdempotencyKey);
        Assert.True(res.Success);
        Assert.Equal(2, res.Content!.Data.Count);
    }

    // ---- audiences -------------------------------------------------------

    [Fact]
    public async Task Audiences_crud()
    {
        var (client, handler) = NewClient();

        await client.AudienceAddAsync("Users");
        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal("/audiences", handler.Last.Path);
        Assert.Equal("Users", handler.LastJson().GetProperty("name").GetString());

        await client.AudienceRetrieveAsync(A1);
        Assert.Equal($"/audiences/{A1}", handler.Last.Path);

        await client.AudienceListAsync(new ListOptions { Limit = 10 });
        Assert.Equal("/audiences", handler.Last.Path);
        Assert.Equal("?limit=10", handler.Last.Query);

        await client.AudienceDeleteAsync(A1);
        Assert.Equal("DELETE", handler.Last.Method);
        Assert.Equal($"/audiences/{A1}", handler.Last.Path);
    }

    // ---- contacts --------------------------------------------------------

    [Fact]
    public async Task Contacts_create_scoped_and_top_level()
    {
        var (client, handler) = NewClient();

        await client.ContactAddAsync(new ContactCreateOptions { AudienceId = A1, Email = "c@x.dev", FirstName = "Ada" });
        Assert.Equal($"/audiences/{A1}/contacts", handler.Last.Path);
        var body = handler.LastJson();
        Assert.Equal("c@x.dev", body.GetProperty("email").GetString());
        Assert.Equal("Ada", body.GetProperty("first_name").GetString());
        // audience_id scopes the path, never the body.
        Assert.False(body.TryGetProperty("audience_id", out _));

        await client.ContactAddAsync(new ContactCreateOptions { Email = "c@x.dev" });
        Assert.Equal("/contacts", handler.Last.Path);
    }

    [Fact]
    public async Task Contacts_addressing_by_id_email_and_scope()
    {
        var (client, handler) = NewClient();

        await client.ContactRetrieveAsync(new ContactAddress { Id = C1 });
        Assert.Equal($"/contacts/{C1}", handler.Last.Path);

        await client.ContactRetrieveAsync(new ContactAddress { Email = "c@x.dev" });
        Assert.Equal("/contacts/" + Uri.EscapeDataString("c@x.dev"), handler.Last.Path);

        await client.ContactRetrieveAsync(new ContactAddress { AudienceId = A1, Id = C1 });
        Assert.Equal($"/audiences/{A1}/contacts/{C1}", handler.Last.Path);
    }

    [Fact]
    public async Task Contacts_email_wins_over_id()
    {
        var (client, handler) = NewClient();
        await client.ContactRetrieveAsync(new ContactAddress { Id = C1, Email = "c@x.dev" });
        Assert.Equal("/contacts/" + Uri.EscapeDataString("c@x.dev"), handler.Last.Path);
    }

    [Fact]
    public async Task Contacts_update_sends_only_provided_keys()
    {
        var (client, handler) = NewClient();
        await client.ContactUpdateAsync(new ContactUpdateOptions { Id = C1, Unsubscribed = true });

        Assert.Equal("PATCH", handler.Last.Method);
        Assert.Equal($"/contacts/{C1}", handler.Last.Path);
        var body = handler.LastJson();
        Assert.True(body.GetProperty("unsubscribed").GetBoolean());
        Assert.False(body.TryGetProperty("first_name", out _));
        Assert.False(body.TryGetProperty("last_name", out _));
    }

    [Fact]
    public async Task Contacts_remove_and_list()
    {
        var (client, handler) = NewClient();

        await client.ContactDeleteAsync(new ContactAddress { Email = "c@x.dev" });
        Assert.Equal("DELETE", handler.Last.Method);
        Assert.Equal("/contacts/" + Uri.EscapeDataString("c@x.dev"), handler.Last.Path);

        await client.ContactListAsync(audienceId: A1, options: new ListOptions { After = C1 });
        Assert.Equal($"/audiences/{A1}/contacts", handler.Last.Path);
        Assert.Equal($"?after={C1}", handler.Last.Query);
    }

    [Fact]
    public async Task Contacts_topics_update_sends_bare_array()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = $"{{\"id\":\"{C1}\"}}");
        await client.ContactTopicsUpdateAsync(new ContactTopicsUpdateOptions
        {
            Id = C1,
            Topics = new List<ContactTopicUpdate> { new() { Id = T1, Subscription = TopicSubscription.OptOut } },
        });

        Assert.Equal("PATCH", handler.Last.Method);
        Assert.Equal($"/contacts/{C1}/topics", handler.Last.Path);
        var body = handler.LastJson();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(T1.ToString(), body[0].GetProperty("id").GetString());
        Assert.Equal("opt_out", body[0].GetProperty("subscription").GetString());
    }

    // ---- topics ----------------------------------------------------------

    [Fact]
    public async Task Topics_crud()
    {
        var (client, handler) = NewClient();

        await client.TopicAddAsync(new TopicCreateOptions { Name = "Product", DefaultSubscription = TopicSubscription.OptIn });
        Assert.Equal("/topics", handler.Last.Path);
        var body = handler.LastJson();
        Assert.Equal("Product", body.GetProperty("name").GetString());
        Assert.Equal("opt_in", body.GetProperty("default_subscription").GetString());

        await client.TopicRetrieveAsync(T1);
        Assert.Equal($"/topics/{T1}", handler.Last.Path);

        await client.TopicListAsync();
        Assert.Equal("/topics", handler.Last.Path);
        // GET /topics is unpaginated — no query string.
        Assert.Equal(string.Empty, handler.Last.Query);

        await client.TopicDeleteAsync(T1);
        Assert.Equal("DELETE", handler.Last.Method);
    }

    // ---- broadcasts ------------------------------------------------------

    [Fact]
    public async Task Broadcasts_lifecycle()
    {
        var (client, handler) = NewClient();

        await client.BroadcastAddAsync(new BroadcastCreateOptions { AudienceId = A1, From = "a@x.dev", Subject = "News", Html = "<p>hi</p>" });
        Assert.Equal("/broadcasts", handler.Last.Path);
        var created = handler.LastJson();
        Assert.Equal(A1.ToString(), created.GetProperty("audience_id").GetString());
        Assert.Equal("a@x.dev", created.GetProperty("from").GetString());

        await client.BroadcastRetrieveAsync(B1);
        Assert.Equal($"/broadcasts/{B1}", handler.Last.Path);

        await client.BroadcastListAsync();
        Assert.Equal("/broadcasts", handler.Last.Path);

        await client.BroadcastUpdateAsync(B1, new BroadcastUpdateOptions { Subject = "New" });
        Assert.Equal("PATCH", handler.Last.Method);
        Assert.Equal($"/broadcasts/{B1}", handler.Last.Path);
        Assert.Equal("New", handler.LastJson().GetProperty("subject").GetString());

        await client.BroadcastSendAsync(B1, scheduledAt: "2999-01-01T00:00:00Z");
        Assert.Equal($"/broadcasts/{B1}/send", handler.Last.Path);
        Assert.Equal("2999-01-01T00:00:00Z", handler.LastJson().GetProperty("scheduled_at").GetString());

        await client.BroadcastSendAsync(B1);
        Assert.Equal(JsonValueKind.Object, handler.LastJson().ValueKind);
        Assert.Empty(handler.LastJson().EnumerateObject());

        await client.BroadcastCancelAsync(B1);
        Assert.Equal($"/broadcasts/{B1}/cancel", handler.Last.Path);

        await client.BroadcastDeleteAsync(B1);
        Assert.Equal("DELETE", handler.Last.Method);
    }

    // ---- segments --------------------------------------------------------

    [Fact]
    public async Task Segments_crud_on_segments2()
    {
        var (client, handler) = NewClient();
        var filter = new SegmentFilter
        {
            Match = SegmentMatch.All,
            Conditions = new List<SegmentCondition> { new() { Field = "email", Op = "is_set" } },
        };

        await client.SegmentAddAsync(new SegmentCreateOptions { Name = "Active", AudienceId = A1, Filter = filter });
        Assert.Equal("/segments2", handler.Last.Path);
        var body = handler.LastJson();
        Assert.Equal("Active", body.GetProperty("name").GetString());
        Assert.Equal(A1.ToString(), body.GetProperty("audience_id").GetString());
        Assert.Equal("all", body.GetProperty("filter").GetProperty("match").GetString());

        await client.SegmentRetrieveAsync(S1);
        Assert.Equal($"/segments2/{S1}", handler.Last.Path);

        await client.SegmentListAsync(new ListOptions { Before = S1 });
        Assert.Equal("/segments2", handler.Last.Path);
        Assert.Equal($"?before={S1}", handler.Last.Query);

        await client.SegmentUpdateAsync(S1, new SegmentUpdateOptions { Name = "Renamed" });
        Assert.Equal("PATCH", handler.Last.Method);
        Assert.Equal($"/segments2/{S1}", handler.Last.Path);
        var updated = handler.LastJson();
        Assert.Equal("Renamed", updated.GetProperty("name").GetString());
        Assert.False(updated.TryGetProperty("filter", out _));

        await client.SegmentDeleteAsync(S1);
        Assert.Equal("DELETE", handler.Last.Method);
    }

    // ---- errors ----------------------------------------------------------

    [Fact]
    public async Task ApiError_is_parsed_into_exception()
    {
        var (client, handler) = NewClient(h =>
        {
            h.Status = HttpStatusCode.UnprocessableEntity;
            h.ResponseBody = "{\"statusCode\":422,\"name\":\"validation_error\",\"message\":\"bad input\"}";
        });

        var res = await client.AudienceRetrieveAsync(A1);
        Assert.False(res.Success);
        Assert.Null(res.Content);
        Assert.NotNull(res.Exception);
        Assert.Equal(422, res.Exception!.StatusCode);
        Assert.Equal("validation_error", res.Exception.ErrorName);
        Assert.Equal("bad input", res.Exception.Message);
    }

    [Fact]
    public async Task NonJson_error_body_falls_back_to_status()
    {
        var (client, handler) = NewClient(h =>
        {
            h.Status = HttpStatusCode.InternalServerError;
            h.ResponseBody = "oops, not json";
        });

        var res = await client.AudienceRetrieveAsync(A1);
        Assert.False(res.Success);
        Assert.Equal(500, res.Exception!.StatusCode);
        Assert.Equal("application_error", res.Exception.ErrorName);
        Assert.Equal("Request failed with status 500", res.Exception.Message);
    }

    [Fact]
    public async Task TransportError_has_null_status()
    {
        var (client, _) = NewClient(h => h.Throw = new HttpRequestException("connection refused"));

        var res = await client.AudienceRetrieveAsync(A1);
        Assert.False(res.Success);
        Assert.Null(res.Exception!.StatusCode);
        Assert.Equal("application_error", res.Exception.ErrorName);
        Assert.Equal("connection refused", res.Exception.Message);
    }
}
