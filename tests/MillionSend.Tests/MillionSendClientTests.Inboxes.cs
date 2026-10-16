using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace MillionSend.Tests;

// Received mail and inboxes: method, path, query, body keys and headers per call,
// the 200/202 send outcome, and wire fixtures with extension fields and values a
// newer server may add.
public partial class MillionSendClientTests
{
    private static readonly Guid R1 = Guid.Parse("b1b1b1b1-b1b1-4b1b-8b1b-b1b1b1b1b1b1");
    private static readonly Guid I1 = Guid.Parse("b2b2b2b2-b2b2-4b2b-8b2b-b2b2b2b2b2b2");
    private static readonly Guid TH1 = Guid.Parse("b3b3b3b3-b3b3-4b3b-8b3b-b3b3b3b3b3b3");
    private static readonly Guid L1 = Guid.Parse("b4b4b4b4-b4b4-4b4b-8b4b-b4b4b4b4b4b4");
    private static readonly Guid DR1 = Guid.Parse("b5b5b5b5-b5b5-4b5b-8b5b-b5b5b5b5b5b5");
    private static readonly Guid SE1 = Guid.Parse("b6b6b6b6-b6b6-4b6b-8b6b-b6b6b6b6b6b6");
    private static readonly Guid SD1 = Guid.Parse("b7b7b7b7-b7b7-4b7b-8b7b-b7b7b7b7b7b7");

    private static readonly string ReceivedJson = $$"""
        {
          "object": "email", "id": "{{R1}}", "to": ["bot@x.dev"], "from": "ada@y.dev",
          "created_at": "2026-10-12T10:00:00.000Z", "subject": "Your code", "bcc": [], "cc": [],
          "reply_to": [], "received_for": ["bot@x.dev"], "message_id": "<m1@y.dev>",
          "attachments": [{ "id": "{{L1}}", "filename": "a.pdf", "content_type": "application/pdf",
                            "content_id": null, "content_disposition": "attachment", "size": 42 }],
          "html": "<p>482913</p>", "html_format": "cid", "text": "482913",
          "headers": { "from": "Ada <ada@y.dev>" },
          "authentication": { "spf": "pass", "dkim": "gray", "dmarc": "pass" },
          "raw": { "download_url": "https://api.x.dev/raw/r1", "expires_at": "2026-10-12T11:00:00.000Z" },
          "inbox_id": "{{I1}}", "thread_id": "{{TH1}}",
          "inboxes": [{ "inbox_id": "{{I1}}", "thread_id": "{{TH1}}", "verdict": "clean" }],
          "screening": { "verdict": "clean", "sender_trust": "a_tier_from_a_newer_server",
                         "quarantine_reason": null, "reasons": ["targets_agent"],
                         "injection_score": 3, "phishing_score": null, "hidden_text_removed": true },
          "spam_verdict": "pass", "spam_score": 4, "virus_verdict": "pass", "virus_signature": null,
          "extracted_text": "Your code is 482913",
          "extracted": { "kinds": ["code"], "code": "482913", "link": null,
                         "link_domain_matches_sender": null, "ambiguous": false },
          "read": false, "a_field_from_a_newer_server": { "x": 1 }
        }
        """;

    private static readonly string DraftJson = $$"""
        {
          "object": "inbox_draft", "id": "{{DR1}}", "type": "reply", "to": ["ada@y.dev"], "cc": [],
          "bcc": [], "subject": "Re: Hi", "html": null, "text": "Thanks", "thread_id": "{{TH1}}",
          "reply_to_email_id": "{{R1}}", "email_id": null, "created_at": "2026-10-12T10:00:00.000Z",
          "updated_at": "2026-10-12T10:00:00.000Z", "status": "pending_approval",
          "approval_reasons": ["send_policy", "ramp"], "forward": false, "decided_by": null,
          "decided_at": null, "expires_at": "2026-10-19T10:00:00.000Z"
        }
        """;

    // ---- receiving -------------------------------------------------------

    [Fact]
    public async Task ReceivedEmailList_carries_every_filter_and_held_ids()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = $$"""
            { "object": "list", "has_more": false, "data": [{{ReceivedJson}}],
              "held": [{ "id": "{{SE1}}", "inbox_id": "{{I1}}", "reason": "quarantined",
                         "quarantine_reason": "not_allowlisted", "created_at": "2026-10-12T10:00:01.000Z" }] }
            """);

        var res = await client.ReceivedEmailListAsync(new ReceivedEmailListOptions
        {
            Limit = 5, After = R1, Wait = 20, Since = "2026-10-12", InboxId = I1, To = "bot@x.dev",
            From = "y.dev", SubjectContains = "code", Unread = true, IncludeQuarantined = true,
        });

        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal("/emails/receiving", handler.Last.Path);
        Assert.Equal(
            $"?limit=5&after={R1}&wait=20&since=2026-10-12&inbox_id={I1}&to=bot%40x.dev&from=y.dev"
            + "&subject_contains=code&unread=true&include_quarantined=true",
            handler.Last.Query);
        Assert.True(res.Success);
        var held = Assert.Single(res.Content!.Held!);
        Assert.Equal(SE1, held.Id);
        Assert.Equal("quarantined", held.Reason);
        Assert.Equal("not_allowlisted", held.QuarantineReason);
        Assert.Equal("a_tier_from_a_newer_server", Assert.Single(res.Content.Data).Screening!.SenderTrust);

        handler.ResponseBody = """{ "object": "list", "has_more": false, "data": [] }""";
        var empty = await client.ReceivedEmailListAsync();
        Assert.Equal(string.Empty, handler.Last.Query);
        Assert.Null(empty.Content!.Held);

        await client.ReceivedEmailListAsync(new ReceivedEmailListOptions { Unread = false });
        Assert.Equal("?unread=false", handler.Last.Query);

        await client.ReceivedEmailListAsync(new ReceivedEmailListOptions { Quarantined = true });
        Assert.Equal("?quarantined=true", handler.Last.Query);
    }

    [Fact]
    public async Task ReceivedEmailList_unknown_inbox_is_404_even_with_wait()
    {
        var (client, handler) = NewClient(h =>
        {
            h.Status = HttpStatusCode.NotFound;
            h.ResponseBody = """{ "statusCode": 404, "name": "not_found", "message": "Inbox not found." }""";
        });

        var res = await client.ReceivedEmailListAsync(new ReceivedEmailListOptions { InboxId = I1, Wait = 30 });
        Assert.Equal($"?wait=30&inbox_id={I1}", handler.Last.Query);
        Assert.Equal(404, res.Exception!.StatusCode);
        Assert.Equal("not_found", res.Exception.ErrorName);
    }

    [Fact]
    public async Task ReceivedEmailRetrieve_decodes_the_extension_fields()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = ReceivedJson);

        var res = await client.ReceivedEmailRetrieveAsync(R1, "cid");
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal($"/emails/receiving/{R1}", handler.Last.Path);
        Assert.Equal("?html_format=cid", handler.Last.Query);
        Assert.True(res.Success, res.Exception?.Message);

        var e = res.Content!;
        Assert.Equal("cid", e.HtmlFormat);
        Assert.Equal("pass", e.Authentication!["spf"]);
        Assert.Equal("Ada <ada@y.dev>", e.Headers!["from"]);
        Assert.Equal("https://api.x.dev/raw/r1", e.Raw!.DownloadUrl);
        Assert.Equal(42, Assert.Single(e.Attachments!).Size);
        Assert.Equal(I1, e.InboxId);
        Assert.Equal("clean", Assert.Single(e.Inboxes!).Verdict);
        Assert.Equal(3, e.Screening!.InjectionScore);
        Assert.Null(e.Screening.PhishingScore);
        Assert.True(e.Screening.HiddenTextRemoved);
        Assert.Equal(4, e.SpamScore);
        Assert.Equal("482913", e.Extracted!.Code);
        Assert.Equal("code", Assert.Single(e.Extracted.Kinds!));
        Assert.False(e.Extracted.Ambiguous);
        Assert.Equal("Your code is 482913", e.ExtractedText);
        Assert.False(e.Read);

        await client.ReceivedEmailRetrieveAsync(R1);
        Assert.Equal(string.Empty, handler.Last.Query);
    }

    [Fact]
    public async Task ReceivedEmailReply_sent_or_held()
    {
        var (client, handler) = NewClient(h => h.ResponseBody =
            $$"""{ "object": "email", "id": "{{SE1}}", "thread_id": "{{TH1}}", "inbox_id": "{{I1}}" }""");

        var sent = await client.ReceivedEmailReplyAsync(R1, new EmailReplyOptions
        {
            Text = "Thanks", Html = "<p>Thanks</p>", Subject = "Re: Hi", Cc = "cc@x.dev",
            Bcc = new[] { "bcc@x.dev" }, ReplyAll = true, RequiresApproval = false, From = "bot@x.dev", InboxId = I1,
        }, idempotencyKey: "reply-1");

        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal($"/emails/receiving/{R1}/reply", handler.Last.Path);
        Assert.Equal("reply-1", handler.Last.IdempotencyKey);
        AssertJson($$"""
            { "html": "<p>Thanks</p>", "text": "Thanks", "subject": "Re: Hi", "cc": "cc@x.dev",
              "bcc": ["bcc@x.dev"], "reply_all": true, "requires_approval": false,
              "from": "bot@x.dev", "inbox_id": "{{I1}}" }
            """, handler.Last.Body);
        Assert.True(sent.Success);
        Assert.False(sent.Content!.IsDraft);
        Assert.Equal("sent", sent.Content.Kind);
        Assert.Equal(SE1, sent.Content.Sent!.Id);
        Assert.Equal(TH1, sent.Content.Sent.ThreadId);
        Assert.Null(sent.Content.Draft);

        handler.Status = HttpStatusCode.Accepted;
        handler.ResponseBody = DraftJson;
        var held = await client.ReceivedEmailReplyAsync(R1, new EmailReplyOptions { Text = "Thanks" });
        Assert.Null(handler.Last.IdempotencyKey);
        AssertJson("""{ "text": "Thanks" }""", handler.Last.Body);
        Assert.True(held.Content!.IsDraft);
        Assert.Equal("draft", held.Content.Kind);
        Assert.Null(held.Content.Sent);
        Assert.Equal("pending_approval", held.Content.Draft!.Status);
        Assert.Equal(new List<string> { "send_policy", "ramp" }, held.Content.Draft.ApprovalReasons);
    }

    [Fact]
    public async Task ReceivedEmailForward_and_delete()
    {
        var (client, handler) = NewClient(h => h.ResponseBody =
            $$"""{ "object": "email", "id": "{{SE1}}", "thread_id": null, "inbox_id": null }""");

        var res = await client.ReceivedEmailForwardAsync(R1, new EmailForwardOptions
        {
            To = new[] { "ops@x.dev" }, Cc = "cc@x.dev", Bcc = "bcc@x.dev", Text = "FYI", Html = "<p>FYI</p>",
            Subject = "Fwd: Hi", RequiresApproval = true, From = "bot@x.dev", InboxId = I1, Mode = "attachment",
        }, idempotencyKey: "fwd-1");

        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal($"/emails/receiving/{R1}/forward", handler.Last.Path);
        Assert.Equal("fwd-1", handler.Last.IdempotencyKey);
        AssertJson($$"""
            { "to": ["ops@x.dev"], "cc": "cc@x.dev", "bcc": "bcc@x.dev", "html": "<p>FYI</p>", "text": "FYI",
              "subject": "Fwd: Hi", "requires_approval": true, "from": "bot@x.dev",
              "inbox_id": "{{I1}}", "mode": "attachment" }
            """, handler.Last.Body);
        Assert.False(res.Content!.IsDraft);
        Assert.Null(res.Content.Sent!.ThreadId);

        handler.ResponseBody = $$"""{ "object": "email", "id": "{{R1}}", "deleted": true }""";
        var removed = await client.ReceivedEmailDeleteAsync(R1);
        Assert.Equal("DELETE", handler.Last.Method);
        Assert.Equal($"/emails/receiving/{R1}", handler.Last.Path);
        Assert.True(removed.Content!.Deleted);
    }

    [Fact]
    public async Task ReceivedEmailAttachments_list_and_retrieve()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = $$"""
            { "object": "list", "has_more": false, "data": [{ "id": "{{L1}}", "filename": "a.pdf", "size": 42,
              "content_type": "application/pdf", "content_disposition": "attachment",
              "download_url": "https://api.x.dev/dl/a1", "expires_at": "2026-10-12T11:00:00.000Z" }] }
            """);

        var list = await client.ReceivedEmailAttachmentListAsync(R1);
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal($"/emails/receiving/{R1}/attachments", handler.Last.Path);
        Assert.Equal(string.Empty, handler.Last.Query);
        Assert.Equal("https://api.x.dev/dl/a1", Assert.Single(list.Content!.Data).DownloadUrl);

        await client.ReceivedEmailAttachmentListAsync(R1, new ListOptions { Limit = 2 });
        Assert.Equal("?limit=2", handler.Last.Query);

        handler.ResponseBody = $$"""
            { "object": "attachment", "id": "{{L1}}", "size": 42, "content_type": "application/pdf",
              "content_disposition": "inline", "content_id": "cid-1",
              "download_url": "https://api.x.dev/dl/a1", "expires_at": "2026-10-12T11:00:00.000Z" }
            """;
        var one = await client.ReceivedEmailAttachmentRetrieveAsync(R1, L1);
        Assert.Equal($"/emails/receiving/{R1}/attachments/{L1}", handler.Last.Path);
        Assert.Equal("attachment", one.Content!.Object);
        Assert.Equal("cid-1", one.Content.ContentId);
        Assert.Null(one.Content.Filename);
    }

    // ---- inboxes ---------------------------------------------------------

    private static readonly string InboxJson = $$"""
        {
          "object": "inbox", "id": "{{I1}}", "name": "Signup bot", "email_address": "bot-7f3k@acme.agents.dev",
          "domain_id": "{{SD1}}", "receiving_address": null, "from_name": null, "unread": 2, "drafts": 1,
          "last_received": null, "created_at": "2026-10-12T10:00:00.000Z", "kind": "agent", "shared": true,
          "expires_at": "2026-10-13T10:00:00.000Z", "quarantined": 3, "pending_approval": 1
        }
        """;

    [Fact]
    public async Task Inboxes_crud()
    {
        var (client, handler) = NewClient(h =>
        {
            h.Status = HttpStatusCode.Created;
            h.ResponseBody = InboxJson;
        });

        var created = await client.InboxCreateAsync(new InboxCreateOptions
        {
            EmailAddress = "bot@x.dev", Name = "Signup bot", FromName = "Acme", Forwarding = false,
            Kind = "agent", ExpiresIn = "24h",
            Safety = new InboxSafetyOptions { AcceptFrom = "anyone", SendCap = Optional<int?>.Null },
        });
        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal("/inboxes", handler.Last.Path);
        AssertJson("""
            { "email_address": "bot@x.dev", "name": "Signup bot", "from_name": "Acme", "forwarding": false,
              "kind": "agent", "expires_in": "24h", "safety": { "accept_from": "anyone", "send_cap": null } }
            """, handler.Last.Body);
        Assert.True(created.Success, created.Exception?.Message);
        var inbox = created.Content!;
        Assert.Equal(I1, inbox.Id);
        Assert.Equal(SD1, inbox.DomainId);
        Assert.Equal("agent", inbox.Kind);
        Assert.True(inbox.Shared);
        Assert.Equal(3, inbox.Quarantined);
        Assert.Equal(1, inbox.PendingApproval);
        Assert.Equal(2, inbox.Unread);

        handler.Status = HttpStatusCode.OK;
        await client.InboxCreateAsync(new InboxCreateOptions());
        AssertJson("{}", handler.Last.Body);

        handler.ResponseBody = $$"""
            { "object": "list", "has_more": true, "data": [{ "id": "{{I1}}", "name": null,
              "email_address": "bot@x.dev", "from_name": null, "unread": 0, "last_received": null,
              "kind": "human", "shared": false, "expires_at": null }] }
            """;
        var list = await client.InboxListAsync(new InboxListOptions { Limit = 10, Before = I1, Kind = "human" });
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal("/inboxes", handler.Last.Path);
        Assert.Equal($"?limit=10&before={I1}&kind=human", handler.Last.Query);
        Assert.True(list.Content!.HasMore);
        var item = Assert.Single(list.Content.Data);
        Assert.Equal("human", item.Kind);
        Assert.Null(item.Drafts);
        Assert.Null(item.DomainId);

        await client.InboxListAsync();
        Assert.Equal(string.Empty, handler.Last.Query);

        handler.ResponseBody = InboxJson;
        await client.InboxRetrieveAsync("Bot+1@x.dev");
        Assert.Equal("/inboxes/" + Uri.EscapeDataString("Bot+1@x.dev"), handler.Last.Path);
        await client.InboxRetrieveAsync(I1.ToString());
        Assert.Equal($"/inboxes/{I1}", handler.Last.Path);

        await client.InboxUpdateAsync(I1, new InboxUpdateOptions { Name = "Bot", FromName = "", ExpiresIn = Optional<string>.Null });
        Assert.Equal("PATCH", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}", handler.Last.Path);
        AssertJson("""{ "name": "Bot", "from_name": "", "expires_in": null }""", handler.Last.Body);

        await client.InboxUpdateAsync(I1, new InboxUpdateOptions { ExpiresIn = "7d" });
        AssertJson("""{ "expires_in": "7d" }""", handler.Last.Body);

        handler.ResponseBody = $$"""{ "object": "inbox", "id": "{{I1}}", "deleted": true }""";
        var removed = await client.InboxDeleteAsync(I1);
        Assert.Equal("DELETE", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}", handler.Last.Path);
        Assert.True(removed.Content!.Deleted);
    }

    [Fact]
    public async Task InboxCreate_sends_username_and_domain_id_only_when_given()
    {
        var (client, handler) = NewClient(h =>
        {
            h.Status = HttpStatusCode.Created;
            h.ResponseBody = InboxJson;
        });

        await client.InboxCreateAsync(new InboxCreateOptions { Username = "ada" });
        AssertJson("""{ "username": "ada" }""", handler.Last.Body);

        await client.InboxCreateAsync(new InboxCreateOptions { DomainId = SD1 });
        AssertJson($$"""{ "domain_id": "{{SD1}}" }""", handler.Last.Body);

        await client.InboxCreateAsync(new InboxCreateOptions { DomainId = D1, Username = "support" });
        AssertJson($$"""{ "domain_id": "{{D1}}", "username": "support" }""", handler.Last.Body);
    }

    [Fact]
    public async Task InboxSafety_retrieve_and_update()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = $$"""
            { "object": "inbox_safety", "inbox_id": "{{I1}}", "accept_from": "verified", "allowlist": ["*@y.dev"],
              "owner_addresses": [], "screening": true, "extract_codes": true, "send_policy": "reply_only",
              "require_approval": false, "private_codes": true, "send_cap": null, "forward_to": [] }
            """);

        var safety = await client.InboxSafetyRetrieveAsync(I1);
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/safety", handler.Last.Path);
        Assert.Equal("verified", safety.Content!.AcceptFrom);
        Assert.Equal("*@y.dev", Assert.Single(safety.Content.Allowlist!));
        Assert.Null(safety.Content.SendCap);
        Assert.True(safety.Content.PrivateCodes);

        await client.InboxSafetyUpdateAsync(I1, new InboxSafetyOptions
        {
            AcceptFrom = "allowlist", Allowlist = new() { "ada@y.dev", "*@z.dev" },
            OwnerAddresses = new() { "me@x.dev" }, Screening = true, ExtractCodes = false,
            SendPolicy = "allowlist", RequireApproval = true, PrivateCodes = true, SendCap = 50,
            ForwardTo = new() { "me@x.dev" },
        });
        Assert.Equal("PATCH", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/safety", handler.Last.Path);
        AssertJson("""
            { "accept_from": "allowlist", "allowlist": ["ada@y.dev", "*@z.dev"], "owner_addresses": ["me@x.dev"],
              "screening": true, "extract_codes": false, "send_policy": "allowlist", "require_approval": true,
              "private_codes": true, "send_cap": 50, "forward_to": ["me@x.dev"] }
            """, handler.Last.Body);

        await client.InboxSafetyUpdateAsync(I1, new InboxSafetyOptions { SendCap = Optional<int?>.Null });
        AssertJson("""{ "send_cap": null }""", handler.Last.Body);
    }

    [Fact]
    public async Task InboxCodes_and_quarantine()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = $$"""
            { "object": "verification_code", "status": "found", "kind": "otp", "code": "482913", "url": null,
              "link_domain_matches_sender": true, "email_id": "{{R1}}", "from": "ada@y.dev",
              "received_at": "2026-10-12T10:00:00.000Z", "sender_trust": "external" }
            """);

        var code = await client.InboxCodeLatestAsync(I1, new InboxCodeLatestOptions { From = "y.dev", Wait = 10, MaxAge = 600 });
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/codes/latest", handler.Last.Path);
        Assert.Equal("?from=y.dev&wait=10&max_age=600", handler.Last.Query);
        Assert.Equal("found", code.Content!.Status);
        Assert.Equal("482913", code.Content.Code);
        Assert.Equal(R1, code.Content.EmailId);

        handler.ResponseBody = """
            { "object": "verification_code", "status": "timeout", "kind": null, "code": null, "url": null,
              "link_domain_matches_sender": null, "email_id": null, "from": null, "received_at": null,
              "sender_trust": null }
            """;
        var none = await client.InboxCodeLatestAsync(I1);
        Assert.Equal(string.Empty, handler.Last.Query);
        Assert.Equal("timeout", none.Content!.Status);
        Assert.Null(none.Content.EmailId);

        await client.InboxCodeLatestAsync(I1, new InboxCodeLatestOptions { Since = "2026-10-16T12:00:00.000Z" });
        Assert.Equal("?since=2026-10-16T12%3A00%3A00.000Z", handler.Last.Query);

        handler.ResponseBody = $$"""
            { "object": "list", "has_more": false, "data": [{ "id": "{{SE1}}", "thread_id": "{{TH1}}",
              "from": "eve@z.dev", "subject": "Hi", "received_at": "2026-10-12T10:00:00.000Z",
              "quarantine_reason": "injection", "sender_trust": "unauthenticated", "injection_score": 97,
              "phishing_score": 12, "virus_signature": null }] }
            """;
        var held = await client.InboxQuarantineListAsync(I1, new ListOptions { Limit = 5 });
        Assert.Equal($"/inboxes/{I1}/quarantine", handler.Last.Path);
        Assert.Equal("?limit=5", handler.Last.Query);
        var q = Assert.Single(held.Content!.Data);
        Assert.Equal("injection", q.QuarantineReason);
        Assert.Equal(97, q.InjectionScore);

        handler.ResponseBody = $$"""
            { "object": "inbox_release", "inbox_id": "{{I1}}", "email_id": "{{SE1}}",
              "released_at": "2026-10-12T10:05:00.000Z", "trusted_sender": "eve@z.dev" }
            """;
        var released = await client.InboxQuarantineReleaseAsync(I1, SE1, trustSender: true);
        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/quarantine/{SE1}/release", handler.Last.Path);
        AssertJson("""{ "trust_sender": true }""", handler.Last.Body);
        Assert.Equal("eve@z.dev", released.Content!.TrustedSender);

        await client.InboxQuarantineReleaseAsync(I1, SE1);
        AssertJson("{}", handler.Last.Body);
    }

    [Fact]
    public async Task InboxLabels_crud()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = $$"""
            { "object": "list", "data": [{ "id": "{{L1}}", "name": "Billing", "color": "#FF0000",
              "created_at": "2026-10-12T10:00:00.000Z" }] }
            """);

        var list = await client.InboxLabelListAsync(I1);
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/labels", handler.Last.Path);
        Assert.Equal("#FF0000", Assert.Single(list.Content!.Data).Color);
        Assert.False(list.Content.HasMore);

        handler.Status = HttpStatusCode.Created;
        handler.ResponseBody = $$"""
            { "object": "inbox_label", "id": "{{L1}}", "name": "Billing", "color": "#FF0000",
              "created_at": "2026-10-12T10:00:00.000Z" }
            """;
        var created = await client.InboxLabelCreateAsync(I1, new InboxLabelOptions { Name = "Billing", Color = "#ff0000" });
        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/labels", handler.Last.Path);
        AssertJson("""{ "name": "Billing", "color": "#ff0000" }""", handler.Last.Body);
        Assert.Equal("inbox_label", created.Content!.Object);

        handler.Status = HttpStatusCode.OK;
        await client.InboxLabelUpdateAsync(I1, L1, new InboxLabelOptions { Color = "#00FF00" });
        Assert.Equal("PATCH", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/labels/{L1}", handler.Last.Path);
        AssertJson("""{ "color": "#00FF00" }""", handler.Last.Body);

        handler.ResponseBody = $$"""{ "object": "inbox_label", "id": "{{L1}}", "deleted": true }""";
        await client.InboxLabelDeleteAsync(I1, L1);
        Assert.Equal("DELETE", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/labels/{L1}", handler.Last.Path);
    }

    private static readonly string ThreadItemJson = $$"""
        "id": "{{TH1}}", "subject": null, "from": "Ada <ada@y.dev>", "to": ["bot@x.dev"], "cc": [], "bcc": [],
        "labels": [{ "id": "{{L1}}", "name": "Billing", "color": "#FF0000" }], "message_count": 2,
        "has_attachment": false, "has_draft": true, "read": false, "received_at": "2026-10-12T10:00:00.000Z",
        "folder": "a_folder_from_a_newer_server"
        """;

    [Fact]
    public async Task InboxThreads_list_search_retrieve_update_delete()
    {
        var (client, handler) = NewClient(h => h.ResponseBody =
            $$"""{ "object": "list", "has_more": false, "data": [{ {{ThreadItemJson}} }] }""");

        var list = await client.InboxThreadListAsync(I1, new InboxThreadListOptions
        {
            Folders = new() { "inbox", "archive" }, Labels = new() { L1 }, Read = false, Limit = 10, After = TH1,
            IncludeQuarantined = true,
        });
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/threads", handler.Last.Path);
        Assert.Equal(
            $"?limit=10&after={TH1}&folders=inbox%2Carchive&labels={L1}&read=false&include_quarantined=true",
            handler.Last.Query);
        var t = Assert.Single(list.Content!.Data);
        Assert.Equal("a_folder_from_a_newer_server", t.Folder);
        Assert.Equal("Billing", Assert.Single(t.Labels!).Name);
        Assert.Equal(2, t.MessageCount);
        Assert.True(t.HasDraft);
        Assert.Null(t.Subject);

        await client.InboxThreadListAsync(I1);
        Assert.Equal(string.Empty, handler.Last.Query);
        await client.InboxThreadListAsync(I1, new InboxThreadListOptions { Folders = new() });
        Assert.Equal(string.Empty, handler.Last.Query);

        handler.ResponseBody = $$"""
            { "object": "list", "has_more": false, "data": [{ {{ThreadItemJson}}, "matched_email_id": "{{R1}}",
              "highlights": { "subject": ["Your **code**"] } }] }
            """;
        var found = await client.InboxThreadSearchAsync(I1, new InboxThreadSearchOptions
        {
            Query = "code -spam", From = new() { "ada", "y.dev" }, To = new() { "bot" }, Cc = new() { "c" },
            Bcc = new() { "b" }, HasAttachment = false, StartDate = "2026-10-01", EndDate = "2026-10-12",
            Folders = new() { "sent" }, Read = true, Limit = 5,
        });
        Assert.Equal($"/inboxes/{I1}/threads/search", handler.Last.Path);
        Assert.Equal(
            "?limit=5&folders=sent&read=true&query=code%20-spam&from=ada%2Cy.dev&to=bot&cc=c&bcc=b"
            + "&has_attachment=false&start_date=2026-10-01&end_date=2026-10-12",
            handler.Last.Query);
        var hit = Assert.Single(found.Content!.Data);
        Assert.Equal(R1, hit.MatchedEmailId);
        Assert.Equal("Your **code**", Assert.Single(hit.Highlights!["subject"]));

        handler.ResponseBody = $$"""
            { "object": "inbox_thread", "id": "{{TH1}}", "subject": "Hi", "folder": "inbox", "labels": [],
              "read": true, "quarantined": 0 }
            """;
        var summary = await client.InboxThreadRetrieveAsync(I1, TH1);
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/threads/{TH1}", handler.Last.Path);
        Assert.Equal("inbox_thread", summary.Content!.Object);
        Assert.Equal(0, summary.Content.Quarantined);

        await client.InboxThreadUpdateAsync(I1, TH1, new InboxThreadUpdateOptions { Read = true, Folder = "archive", LabelId = L1 });
        Assert.Equal("PATCH", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/threads/{TH1}", handler.Last.Path);
        AssertJson($$"""{ "read": true, "folder": "archive", "label_id": "{{L1}}" }""", handler.Last.Body);

        handler.ResponseBody = $$"""{ "object": "inbox_thread", "id": "{{TH1}}", "deleted": false }""";
        var trashed = await client.InboxThreadDeleteAsync(I1, TH1);
        Assert.Equal("DELETE", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/threads/{TH1}", handler.Last.Path);
        Assert.False(trashed.Content!.Deleted);
    }

    [Fact]
    public async Task InboxThread_items_carry_quarantined_only_when_sent()
    {
        var (client, _) = NewClient(h => h.ResponseBody =
            $$"""{ "object": "list", "has_more": false, "data": [{ {{ThreadItemJson}}, "quarantined": 2 }, { {{ThreadItemJson}} }] }""");

        var list = await client.InboxThreadListAsync(I1, new InboxThreadListOptions { IncludeQuarantined = true });
        Assert.Equal(2, list.Content!.Data[0].Quarantined);
        Assert.Null(list.Content.Data[1].Quarantined);
    }

    [Fact]
    public async Task InboxThreadEmails_list_retrieve_reply_forward()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = $$"""
            { "object": "list", "has_more": false, "data": [{ "id": "{{R1}}", "direction": "inbound",
              "from": "Ada <ada@y.dev>", "to": ["bot@x.dev"], "cc": [], "bcc": [], "reply_to": [], "subject": "Hi",
              "message_id": "<m1@y.dev>", "html": null, "text": "Hi", "attachments": [], "read": false,
              "received_at": "2026-10-12T10:00:00.000Z", "extracted_text": "Hi",
              "screening": { "verdict": "suspicious", "sender_trust": "unauthenticated" },
              "spam_verdict": "gray", "virus_verdict": null }] }
            """);

        var list = await client.InboxThreadEmailListAsync(I1, TH1, new InboxThreadEmailListOptions { After = R1, IncludeQuarantined = true });
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/threads/{TH1}/emails", handler.Last.Path);
        Assert.Equal($"?after={R1}&include_quarantined=true", handler.Last.Query);
        var inbound = Assert.Single(list.Content!.Data);
        Assert.Equal("inbound", inbound.Direction);
        Assert.Equal("suspicious", inbound.Screening!.Verdict);
        Assert.Equal("gray", inbound.SpamVerdict);

        handler.ResponseBody = $$"""
            { "id": "{{SE1}}", "email_id": "{{SE1}}", "direction": "outbound", "from": "Acme <bot@x.dev>",
              "to": ["ada@y.dev"], "cc": [], "bcc": [], "reply_to": [], "subject": "Re: Hi", "message_id": null,
              "html": null, "text": "Thanks", "attachments": [{ "id": "0", "filename": "a.pdf", "size": 42 }],
              "read": true, "received_at": "2026-10-12T10:00:00.000Z", "status": "queued",
              "thread_id": "{{TH1}}", "inbox_id": "{{I1}}" }
            """;
        var sentEmail = await client.InboxThreadEmailRetrieveAsync(I1, TH1, SE1);
        Assert.Equal($"/inboxes/{I1}/threads/{TH1}/emails/{SE1}", handler.Last.Path);
        Assert.Equal(string.Empty, handler.Last.Query);
        Assert.True(sentEmail.Success, sentEmail.Exception?.Message);
        Assert.Equal("0", Assert.Single(sentEmail.Content!.Attachments!).Id);
        Assert.Equal("queued", sentEmail.Content.Status);
        await client.InboxThreadEmailRetrieveAsync(I1, TH1, SE1, includeQuarantined: true);
        Assert.Equal("?include_quarantined=true", handler.Last.Query);

        var reply = await client.InboxThreadEmailReplyAsync(I1, TH1, R1, new EmailReplyOptions
        {
            Text = "Thanks", Cc = "cc@x.dev", ReplyAll = true, RequiresApproval = true,
        }, idempotencyKey: "k-1");
        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/threads/{TH1}/emails/{R1}/reply", handler.Last.Path);
        Assert.Equal("k-1", handler.Last.IdempotencyKey);
        AssertJson("""{ "text": "Thanks", "cc": "cc@x.dev", "reply_all": true, "requires_approval": true }""", handler.Last.Body);
        Assert.False(reply.Content!.IsDraft);
        Assert.Equal(SE1, reply.Content.Sent!.EmailId);
        Assert.Equal(I1, reply.Content.Sent.InboxId);

        handler.Status = HttpStatusCode.Accepted;
        handler.ResponseBody = DraftJson;
        var forward = await client.InboxThreadEmailForwardAsync(I1, TH1, R1, new EmailForwardOptions { To = new[] { "ops@x.dev", "cto@x.dev" } });
        Assert.Equal($"/inboxes/{I1}/threads/{TH1}/emails/{R1}/forward", handler.Last.Path);
        AssertJson("""{ "to": ["ops@x.dev", "cto@x.dev"] }""", handler.Last.Body);
        Assert.True(forward.Content!.IsDraft);
        Assert.Equal(DR1, forward.Content.Draft!.Id);
    }

    [Fact]
    public async Task InboxDrafts_crud_send_and_reject()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = $$"""
            { "object": "list", "has_more": false, "data": [{ "id": "{{DR1}}", "type": "standalone", "to": null,
              "cc": [], "bcc": [], "subject": null, "snippet": "Hello", "thread_id": null, "reply_to_email_id": null,
              "updated_at": "2026-10-12T10:00:00.000Z", "status": "draft", "approval_reasons": [] }] }
            """);

        var list = await client.InboxDraftListAsync(I1, new ListOptions { Limit = 3 });
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/drafts", handler.Last.Path);
        Assert.Equal("?limit=3", handler.Last.Query);
        var item = Assert.Single(list.Content!.Data);
        Assert.Equal("Hello", item.Snippet);
        Assert.Null(item.To);

        handler.Status = HttpStatusCode.Created;
        handler.ResponseBody = DraftJson;
        var created = await client.InboxDraftCreateAsync(I1, new InboxDraftCreateOptions
        {
            To = "ada@y.dev", Cc = new[] { "cc@x.dev" }, Bcc = "bcc@x.dev", Subject = "Re: Hi", Html = "<p>Thanks</p>",
            Text = "Thanks", ThreadId = TH1, ReplyToEmailId = R1,
        });
        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/drafts", handler.Last.Path);
        AssertJson($$"""
            { "to": "ada@y.dev", "cc": ["cc@x.dev"], "bcc": "bcc@x.dev", "subject": "Re: Hi", "html": "<p>Thanks</p>",
              "text": "Thanks", "thread_id": "{{TH1}}", "reply_to_email_id": "{{R1}}" }
            """, handler.Last.Body);
        Assert.Equal("reply", created.Content!.Type);
        Assert.Equal("2026-10-19T10:00:00.000Z", created.Content.ExpiresAt);

        handler.Status = HttpStatusCode.OK;
        await client.InboxDraftRetrieveAsync(I1, DR1);
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/drafts/{DR1}", handler.Last.Path);

        await client.InboxDraftUpdateAsync(I1, DR1, new InboxDraftUpdateOptions
        {
            To = Optional<List<string>>.Null, Cc = new List<string>(), Bcc = new List<string> { "b@x.dev" },
            Subject = Optional<string>.Null, Text = "New", Html = Optional<string>.Null,
        });
        Assert.Equal("PATCH", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/drafts/{DR1}", handler.Last.Path);
        AssertJson("""{ "to": null, "cc": [], "bcc": ["b@x.dev"], "subject": null, "html": null, "text": "New" }""", handler.Last.Body);

        handler.ResponseBody = $$"""{ "object": "inbox_draft", "id": "{{DR1}}", "thread_id": "{{TH1}}", "email_id": "{{SE1}}" }""";
        var sent = await client.InboxDraftSendAsync(I1, DR1, idempotencyKey: "send-1");
        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/drafts/{DR1}/send", handler.Last.Path);
        Assert.Null(handler.Last.Body);
        Assert.Equal("send-1", handler.Last.IdempotencyKey);
        Assert.False(sent.Content!.IsDraft);
        Assert.Equal(SE1, sent.Content.Sent!.EmailId);

        handler.Status = HttpStatusCode.Accepted;
        handler.ResponseBody = DraftJson;
        var held = await client.InboxDraftSendAsync(I1, DR1);
        Assert.True(held.Content!.IsDraft);
        Assert.Equal("pending_approval", held.Content.Draft!.Status);

        handler.Status = HttpStatusCode.OK;
        handler.ResponseBody = DraftJson.Replace("pending_approval", "rejected");
        var rejected = await client.InboxDraftRejectAsync(I1, DR1);
        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/drafts/{DR1}/reject", handler.Last.Path);
        Assert.Equal("rejected", rejected.Content!.Status);

        handler.ResponseBody = $$"""{ "object": "inbox_draft", "id": "{{DR1}}", "deleted": true }""";
        await client.InboxDraftDeleteAsync(I1, DR1);
        Assert.Equal("DELETE", handler.Last.Method);
        Assert.Equal($"/inboxes/{I1}/drafts/{DR1}", handler.Last.Path);
    }

    [Fact]
    public async Task InboxDraftSend_approval_required_is_an_error_response()
    {
        var (client, _) = NewClient(h =>
        {
            h.Status = HttpStatusCode.Forbidden;
            h.ResponseBody = """{ "statusCode": 403, "name": "approval_required", "message": "A person must approve this draft" }""";
        });
        var res = await client.InboxDraftSendAsync(I1, DR1);
        Assert.False(res.Success);
        Assert.Equal(403, res.Exception!.StatusCode);
        Assert.Equal("approval_required", res.Exception.ErrorName);
    }

    [Fact]
    public async Task Inbox_fields_on_domains_keys_and_webhooks()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = $$"""
            { "object": "domain", "id": "{{D1}}", "name": "acme.dev", "status": "verified",
              "open_tracking": false, "click_tracking": false, "receiving_catch_all": true }
            """);

        Assert.True((await client.DomainRetrieveAsync(D1)).Content!.ReceivingCatchAll);
        await client.DomainUpdateAsync(D1, new DomainUpdateOptions { ReceivingCatchAll = false });
        AssertJson("""{ "receiving_catch_all": false }""", handler.Last.Body);

        handler.ResponseBody = $$"""{ "id": "{{K1}}", "token": "ms_secret" }""";
        await client.ApiKeyCreateAsync(new ApiKeyCreateOptions { Name = "agent", Permission = ApiKeyPermission.FullAccess, InboxId = I1 });
        Assert.Equal("/api-keys", handler.Last.Path);
        AssertJson($$"""{ "name": "agent", "permission": "full_access", "inbox_id": "{{I1}}" }""", handler.Last.Body);
        handler.ResponseBody = $$"""
            { "object": "list", "has_more": false, "data": [{ "id": "{{K1}}", "name": "agent",
              "created_at": "2026-01-01T00:00:00.000Z", "last_used_at": null, "inbox_id": "{{I1}}" }] }
            """;
        Assert.Equal(I1, Assert.Single((await client.ApiKeyListAsync()).Content!.Data).InboxId);

        handler.ResponseBody = $$"""{ "object": "webhook", "id": "{{W1}}", "signing_secret": "whsec_1" }""";
        await client.WebhookCreateAsync(new WebhookCreateOptions
        {
            Endpoint = "https://x.dev/hook", Events = new() { "email.received", "inbox.email.quarantined" },
            InboxIds = new() { I1 },
        });
        AssertJson($$"""
            { "endpoint": "https://x.dev/hook", "events": ["email.received", "inbox.email.quarantined"],
              "inbox_ids": ["{{I1}}"] }
            """, handler.Last.Body);
        await client.WebhookUpdateAsync(W1, new WebhookUpdateOptions { InboxIds = Optional<List<Guid>>.Null });
        AssertJson("""{ "inbox_ids": null }""", handler.Last.Body);

        handler.ResponseBody = $$"""
            { "object": "webhook", "id": "{{W1}}", "endpoint": "https://x.dev/hook", "status": "enabled",
              "events": ["inbox.thread.created"], "inbox_ids": ["{{I1}}"] }
            """;
        var w = await client.WebhookRetrieveAsync(W1);
        Assert.Equal(I1, Assert.Single(w.Content!.InboxIds!));
        Assert.Equal("inbox.thread.created", Assert.Single(w.Content.Events!));
    }

    // ---- the team's shared domain row --------------------------------------

    private static readonly string SharedDomainJson = $$"""
        "id": "{{SD1}}", "name": "acme.agents.example.com", "status": "verified",
        "created_at": "2026-10-12T10:00:00.000Z", "region": "us-east-1", "open_tracking": false,
        "click_tracking": false, "tracking_subdomain": null,
        "capabilities": { "sending": "enabled", "receiving": "enabled" }, "receiving_catch_all": false, "shared": true
        """;

    [Fact]
    public async Task Domains_list_ends_with_the_shared_row()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = $$"""
            { "object": "list", "has_more": false, "data": [
              { "id": "{{D1}}", "name": "acme.dev", "status": "verified", "created_at": "2026-10-14T09:00:00.000Z",
                "region": "us-east-1", "open_tracking": true, "click_tracking": true, "tracking_subdomain": "links",
                "capabilities": { "sending": "enabled", "receiving": "enabled" }, "receiving_catch_all": false,
                "shared": false },
              { {{SharedDomainJson}}, "sending_mode": "reply_only", "sending_mode_reason": "plan" }
            ] }
            """);

        var list = await client.DomainListAsync();
        var own = list.Content!.Data[0];
        Assert.False(own.Shared);
        Assert.Null(own.SendingMode);
        Assert.Null(own.SendingModeReason);
        var shared = list.Content.Data[1];
        Assert.True(shared.Shared);
        Assert.Equal("reply_only", shared.SendingMode);
        Assert.Equal("plan", shared.SendingModeReason);

        // Servers before v0.14.0 send no shared key; sending_mode is an open set.
        handler.ResponseBody = $$"""{ "object": "domain", "id": "{{D1}}", "name": "acme.dev", "status": "verified" }""";
        Assert.False((await client.DomainRetrieveAsync(D1)).Content!.Shared);
        handler.ResponseBody = $$"""{ "object": "domain", {{SharedDomainJson}}, "sending_mode": "paused_for_review" }""";
        Assert.Equal("paused_for_review", (await client.DomainRetrieveAsync(SD1)).Content!.SendingMode);
    }

    [Fact]
    public async Task Domains_shared_row_detail_and_403_on_change()
    {
        var (client, handler) = NewClient(h => h.ResponseBody = $$"""
            { "object": "domain", {{SharedDomainJson}}, "sending_mode": "open", "sending_mode_reason": null, "records": [] }
            """);

        var detail = (await client.DomainRetrieveAsync(SD1)).Content!;
        Assert.Equal($"/domains/{SD1}", handler.Last.Path);
        Assert.Equal("domain", detail.Object);
        Assert.Equal("acme.agents.example.com", detail.Name);
        Assert.Equal("verified", detail.Status);
        Assert.True(detail.Shared);
        Assert.Equal("open", detail.SendingMode);
        Assert.Null(detail.SendingModeReason);
        Assert.Equal("enabled", detail.Capabilities!.Receiving);
        Assert.Empty(detail.Records!);

        handler.Status = HttpStatusCode.Forbidden;
        handler.ResponseBody = """
            { "statusCode": 403, "name": "forbidden",
              "message": "acme.agents.example.com is your team's subdomain of the shared domain: it can't be changed or deleted" }
            """;
        var refused = await client.DomainUpdateAsync(SD1, new DomainUpdateOptions { ClickTracking = true });
        Assert.Equal("PATCH", handler.Last.Method);
        Assert.Equal(403, refused.Exception!.StatusCode);
        Assert.Equal("forbidden", refused.Exception.ErrorName);
    }
}
