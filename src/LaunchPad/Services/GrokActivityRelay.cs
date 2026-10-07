using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public sealed record GrokActivityPublication(SessionActivityContext Context, AgentActivitySnapshot Activity,
    bool Connected, IReadOnlyList<AgentActivityEvent> Events, bool Synchronized, bool HistoryComplete);

/// <summary>Local status/outbox handoff. No provider messages or agent-control writes.</summary>
public sealed class GrokActivityRelay(GrokActivityFeed feed, SessionActivityContext context,
    NotificationOutbox? outbox = null, Action<GrokActivityPublication>? publish = null)
{
    public void Publish(bool connected)
    {
        feed.PublishPending(state =>
        {
            if (context.Generation != state.Binding.Generation || context.AgentId != AgentChoice.Grok
                || context.ProjectPath != state.Binding.HostProject)
                throw new InvalidOperationException("Grok activity relay does not match its launch/project binding.");
            var value = new GrokActivityPublication(context, state.Checkpoint.Activity, connected,
                state.Pending.Select(item => item.Event).ToArray(), connected && feed.IsCurrent(state), state.Checkpoint.HistoryComplete);
            if (publish is not null) publish(value);
            else SessionActivityStore.Publish(value.Context, value.Activity, value.Connected, value.Events, value.Synchronized, value.HistoryComplete);
            foreach (var pending in state.Pending)
            {
                if (pending.Event.Kind is not (AgentEventKind.NeedsAttention or AgentEventKind.RunFinished or AgentEventKind.RunFailed or AgentEventKind.Interrupted)
                    || pending.Consent is not { } consent || !consent.Allows(consent.DestinationReference ?? "", consent.Epoch)) continue;
                if (!Guid.TryParseExact(consent.DestinationReference, "N", out _))
                    throw new InvalidDataException("Captured notification destination is invalid; unpublished activity was preserved.");
                if (outbox is null) throw new IOException("Notification outbox is unavailable; unpublished activity was preserved.");
                // The outbox deduplicates retries after a partial journal/queue
                // publication or a failed final acknowledgement. Dispatch still
                // rechecks current consent and owns provider ambiguity handling.
                outbox.Queue(new AcceptedAgentActivityEvent(pending.Event), state.Binding.HostProject, AgentChoice.Grok, consent);
            }
        });
    }
}
