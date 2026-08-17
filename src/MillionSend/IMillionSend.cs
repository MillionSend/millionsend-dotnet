using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MillionSend;

/// <summary>
/// The MillionSend API surface. Every method returns a
/// <see cref="MillionSendResponse{T}"/> and never throws for an API or transport
/// error. Domains and API keys are dashboard-managed and have no methods here.
/// </summary>
public interface IMillionSend
{
    // Emails
    Task<MillionSendResponse<CreateEmailResponse>> EmailSendAsync(EmailMessage message, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<Email>> EmailRetrieveAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<CancelEmailResponse>> EmailCancelAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<DataResponse<CreateEmailResponse>>> EmailBatchAsync(IEnumerable<EmailMessage> messages, string? idempotencyKey = null, CancellationToken cancellationToken = default);

    // Contacts (team-global)
    Task<MillionSendResponse<ContactId>> ContactAddAsync(ContactCreateOptions options, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<Contact>> ContactRetrieveAsync(ContactAddress address, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<ContactId>> ContactUpdateAsync(ContactUpdateOptions options, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<RemoveContactResponse>> ContactDeleteAsync(ContactAddress address, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<ListResponse<ContactListItem>>> ContactListAsync(ListOptions? options = null, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<ContactId>> ContactTopicsUpdateAsync(ContactTopicsUpdateOptions options, CancellationToken cancellationToken = default);

    // Topics
    Task<MillionSendResponse<TopicId>> TopicAddAsync(TopicCreateOptions options, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<Topic>> TopicRetrieveAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<DataResponse<Topic>>> TopicListAsync(CancellationToken cancellationToken = default);
    Task<MillionSendResponse<RemoveTopicResponse>> TopicDeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // Broadcasts
    Task<MillionSendResponse<BroadcastId>> BroadcastAddAsync(BroadcastCreateOptions options, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<Broadcast>> BroadcastRetrieveAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<ListResponse<BroadcastListItem>>> BroadcastListAsync(ListOptions? options = null, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<BroadcastId>> BroadcastUpdateAsync(Guid id, BroadcastUpdateOptions options, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<RemoveBroadcastResponse>> BroadcastDeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<BroadcastId>> BroadcastSendAsync(Guid id, string? scheduledAt = null, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<CancelBroadcastResponse>> BroadcastCancelAsync(Guid id, CancellationToken cancellationToken = default);

    // Segments (MillionSend extension: saved filters over the team's contacts)
    Task<MillionSendResponse<Segment>> SegmentAddAsync(SegmentCreateOptions options, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<Segment>> SegmentRetrieveAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<ListResponse<Segment>>> SegmentListAsync(ListOptions? options = null, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<Segment>> SegmentUpdateAsync(Guid id, SegmentUpdateOptions options, CancellationToken cancellationToken = default);
    Task<MillionSendResponse<RemoveSegmentResponse>> SegmentDeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
