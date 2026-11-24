using Zelvo.Data.Tickets;

namespace Zelvo.Tickets;

/// <summary>Why a state change was refused; the API turns each into a problem code.</summary>
public enum TransitionError
{
    /// <summary>Allowed.</summary>
    None,

    /// <summary>The ticket cannot move from its state to the one asked for.</summary>
    NotAllowed,

    /// <summary>Putting a ticket on hold needs an active on-hold reason.</summary>
    OnHoldReasonRequired,

    /// <summary>Resolving needs resolution notes.</summary>
    ResolutionNotesRequired,

    /// <summary>Starting work needs an assignee.</summary>
    AssigneeRequired,
}

/// <summary>
/// The ticket life cycle (module 1): New → Assigned → In progress ⇄ On hold (with reason) →
/// Resolved (with notes) → Closed; Resolved → Reopened → In progress. Closed is final (product owner
/// decision 2026-11-22). Assigning and unassigning move between New and Assigned.
/// </summary>
public static class TicketWorkflow
{
    private static readonly Dictionary<TicketState, TicketState[]> Allowed = new()
    {
        [TicketState.New] = [TicketState.InProgress, TicketState.OnHold, TicketState.Resolved],
        [TicketState.Assigned] = [TicketState.InProgress, TicketState.OnHold, TicketState.Resolved],
        [TicketState.InProgress] = [TicketState.OnHold, TicketState.Resolved],
        [TicketState.OnHold] = [TicketState.InProgress, TicketState.Resolved],
        [TicketState.Reopened] = [TicketState.InProgress, TicketState.OnHold, TicketState.Resolved],
        [TicketState.Resolved] = [TicketState.Closed, TicketState.Reopened],
        [TicketState.Closed] = [],
    };

    /// <summary>States a ticket in <paramref name="state"/> can move to with a state change.</summary>
    public static IReadOnlyList<TicketState> NextStates(TicketState state) => Allowed[state];

    /// <summary>Whether the ticket is still being worked on (not resolved or closed).</summary>
    public static bool IsOpen(TicketState state) => state is not (TicketState.Resolved or TicketState.Closed);

    /// <summary>Checks a state change without applying it.</summary>
    public static TransitionError Check(Ticket ticket, TicketState to, string? onHoldReason, string? resolutionNotes)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!Allowed[ticket.State].Contains(to))
        {
            return TransitionError.NotAllowed;
        }

        return to switch
        {
            TicketState.InProgress when ticket.AssigneeId is null => TransitionError.AssigneeRequired,
            TicketState.OnHold when string.IsNullOrWhiteSpace(onHoldReason) => TransitionError.OnHoldReasonRequired,
            TicketState.Resolved when string.IsNullOrWhiteSpace(resolutionNotes) => TransitionError.ResolutionNotesRequired,
            _ => TransitionError.None,
        };
    }

    /// <summary>Applies a state change that <see cref="Check"/> allowed.</summary>
    public static void Apply(Ticket ticket, TicketState to, string? onHoldReason, string? resolutionNotes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (Check(ticket, to, onHoldReason, resolutionNotes) != TransitionError.None)
        {
            throw new InvalidOperationException($"A ticket cannot move from {ticket.State} to {to} this way.");
        }

        ticket.OnHoldReason = to == TicketState.OnHold ? onHoldReason!.Trim() : null;

        // The first response stops the response clock (task B3; product owner decision 2026-11-24):
        // starting work, or resolving before anyone started.
        if (to is TicketState.InProgress or TicketState.Resolved)
        {
            ticket.RespondedAt ??= now;
        }

        switch (to)
        {
            case TicketState.Resolved:
                ticket.ResolutionNotes = resolutionNotes!.Trim();
                ticket.ResolvedAt = now;
                break;
            case TicketState.Closed:
                ticket.ClosedAt = now;
                break;
            case TicketState.Reopened:
                ticket.ReopenCount++;
                ticket.ResolvedAt = null;
                ticket.ResolutionNotes = null;
                break;
        }

        ticket.State = to;
        ticket.UpdatedAt = now;
    }

    /// <summary>Gives the ticket to <paramref name="assigneeId"/> (or nobody): a new ticket becomes Assigned, an assigned one without an assignee becomes New again.</summary>
    public static void Assign(Ticket ticket, Guid? assigneeId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (ticket.State == TicketState.Closed)
        {
            throw new InvalidOperationException("A closed ticket cannot be reassigned.");
        }

        ticket.AssigneeId = assigneeId;
        ticket.State = (ticket.State, assigneeId) switch
        {
            (TicketState.New, not null) => TicketState.Assigned,
            (TicketState.Assigned, null) => TicketState.New,
            _ => ticket.State,
        };
        ticket.UpdatedAt = now;
    }
}
