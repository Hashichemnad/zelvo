using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Tenancy;
using Zelvo.Api.Workflows;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Changes;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Workflows.Approvals;

namespace Zelvo.Api.Changes;

/// <summary>A board with its people.</summary>
internal sealed record BoardResponse(
    Guid Id, ChangeBoardKind Kind, LocalizedTextDto Name, PersonResponse? Chair, PersonResponse? DeputyChair, IReadOnlyList<PersonResponse> Members,
    int DecideWithinHours, string Etag);

/// <summary>A meeting in a list.</summary>
internal sealed record MeetingSummary(
    Guid Id, LocalizedTextDto Title, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Location, PersonResponse Chair, CabMeetingState State,
    int ItemCount, int DecidedCount);

/// <summary>A member's vote.</summary>
internal sealed record VoteResponse(PersonResponse Member, CabVoteChoice Choice, string? Comment, DateTimeOffset VotedAt);

/// <summary>A change on a meeting's agenda, with the votes, the chair's decision and the minutes.</summary>
internal sealed record AgendaItemResponse(
    Guid Id, int Position, Guid ChangeId, string ChangeNumber, string ChangeTitle, ChangeKind Kind, ChangeStage Stage, ChangeRiskLevel? RiskLevel,
    DateTimeOffset? PlannedStart, DateTimeOffset? PlannedEnd, int ConflictCount, bool Waiting, IReadOnlyList<VoteResponse> Votes,
    IReadOnlyDictionary<CabVoteChoice, int> Tally, CabDecision? Decision, string? Comment, string? Minutes, PersonResponse? DecidedBy, DateTimeOffset? DecidedAt);

/// <summary>A meeting with its members, agenda and minutes.</summary>
internal sealed record MeetingResponse(
    Guid Id, LocalizedTextDto Title, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Location, PersonResponse Chair, CabMeetingState State,
    string? Minutes, IReadOnlyList<PersonResponse> Members, IReadOnlyList<AgendaItemResponse> Agenda, bool IsChair, bool IsMember, bool MayManage, string Etag,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A change's time before the board: each meeting it was on, with the votes, decision and minutes.</summary>
internal sealed record ChangeBoardEntry(Guid MeetingId, LocalizedTextDto MeetingTitle, DateTimeOffset MeetingStartsAt, CabMeetingState MeetingState, AgendaItemResponse Item);

/// <summary>
/// The change advisory board (module 17, task H4; product owner decision 8 of 2026-11-26): the
/// advisory and emergency boards with their members; meetings with the agenda built from the
/// changes waiting for the board, invitations with a calendar file, the members' votes (advice),
/// the chair's decision recorded through C3's approval service, and minutes in the meeting and on
/// each change. The emergency board decides in My approvals, without a meeting.
/// </summary>
internal static class ChangeBoardEndpoints
{
    /// <summary>Most members of a meeting.</summary>
    public const int MaxMembers = 50;

    public static RouteGroupBuilder MapChangeBoardEndpoints(this RouteGroupBuilder group)
    {
        var boards = group.MapGroup("/change-boards").WithTags("Changes").RequireTenant();
        boards.MapGet("/", BoardsAsync).WithName("ListChangeBoards")
            .WithSummary("The change advisory board and the emergency board with their chair, deputy chair and members. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<BoardResponse>>();
        boards.MapPut("/{kind}", UpdateBoardAsync).WithName("UpdateChangeBoard")
            .WithSummary("Set a board's name, chair and deputy chair (advisory), members (people whose roles hold changes.approve) and, for the emergency board, the hours within which it decides. Needs changes.manage; audited.")
            .RequirePermission(Permissions.ChangesManage).Produces<BoardResponse>().ProducesValidationProblem();

        var meetings = group.MapGroup("/cab-meetings").WithTags("Changes").RequireTenant();
        meetings.MapGet("/", MeetingsAsync).WithName("ListCabMeetings")
            .WithSummary("Meetings of the change advisory board, soonest first; upcoming=true for those not over or held yet. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<MeetingSummary>>();
        meetings.MapGet("/waiting", WaitingAsync).WithName("ListChangesWaitingForBoard")
            .WithSummary("Changes whose approval waits for the change advisory board and that no planned meeting has on its agenda yet. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<ChangeResponse>>();
        meetings.MapPost("/", CreateAsync).WithName("CreateCabMeeting")
            .WithSummary("Plan a meeting: bilingual title, start and end, location; the chair (the board's chair or deputy) and members default to the board's. The agenda is built from the changes waiting for the board; members get an invitation with a calendar file. The change manager (changes.manage) or the board's chair; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesApprove).Produces<MeetingResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        meetings.MapGet("/{id:guid}", GetAsync).WithName("GetCabMeeting")
            .WithSummary("A meeting with its members, agenda (each change with its risk, window, conflicts, votes and decision) and minutes. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<MeetingResponse>();
        meetings.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateCabMeeting")
            .WithSummary("Change a planned meeting's title, time, location or members; members get an updated invitation. The change manager or the meeting's chair; If-Match; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesApprove).Produces<MeetingResponse>().ProducesValidationProblem();
        meetings.MapPost("/{id:guid}/agenda/refresh", RefreshAgendaAsync).WithName("RefreshCabAgenda")
            .WithSummary("Add the changes now waiting for the board to the agenda and remove undecided ones that no longer wait. The change manager or the meeting's chair; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesApprove).Produces<MeetingResponse>();
        meetings.MapDelete("/{id:guid}/agenda/{itemId:guid}", RemoveItemAsync).WithName("RemoveCabAgendaItem")
            .WithSummary("Take an undecided change off the agenda. The change manager or the meeting's chair; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesApprove).Produces<MeetingResponse>();
        meetings.MapPut("/{id:guid}/agenda/{itemId:guid}/vote", VoteAsync).WithName("VoteOnCabAgendaItem")
            .WithSummary("A member's vote on a change: Approve, Reject or Abstain, with a comment; advice to the chair (decision 8), changed until the chair decides. Members of the meeting; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesApprove).Produces<MeetingResponse>().ProducesValidationProblem();
        meetings.MapPost("/{id:guid}/agenda/{itemId:guid}/decision", DecideAsync).WithName("DecideCabAgendaItem")
            .WithSummary("The chair's decision on a change: Approved or Rejected (with the reason) is recorded through the change's approval (C3), Deferred keeps it waiting for a later meeting; with the minutes about it. The meeting's chair only; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesApprove).Produces<MeetingResponse>().ProducesValidationProblem();
        meetings.MapPut("/{id:guid}/minutes", MinutesAsync).WithName("SetCabMinutes")
            .WithSummary("Write the meeting's minutes. The meeting's chair or the change manager; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesApprove).Produces<MeetingResponse>().ProducesValidationProblem();
        meetings.MapPost("/{id:guid}/complete", CompleteAsync).WithName("CompleteCabMeeting")
            .WithSummary("Mark the meeting held; undecided changes are deferred and keep waiting for the board. The meeting's chair; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesApprove).Produces<MeetingResponse>();
        meetings.MapPost("/{id:guid}/cancel", CancelAsync).WithName("CancelCabMeeting")
            .WithSummary("Call a planned meeting off; members get a cancellation for their calendars and its changes keep waiting. The change manager or the meeting's chair; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesApprove).Produces<MeetingResponse>();

        group.MapGet("/changes/{id:guid}/board", ChangeBoardAsync).WithName("GetChangeBoardHistory").WithTags("Changes").RequireTenant()
            .WithSummary("The meetings a change was on, with the members' votes, the chair's decision and the minutes about it. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<ChangeBoardEntry>>();
        return group;
    }

    // ---- boards ----

    /// <summary>The tenant's two boards, made with their default names when missing.</summary>
    internal static async Task<List<ChangeBoard>> EnsureBoardsAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var boards = await db.ChangeBoards.Include(b => b.Members).ToListAsync(cancellationToken);
        var added = false;
        foreach (var kind in Enum.GetValues<ChangeBoardKind>().Where(k => boards.All(b => b.Kind != k)))
        {
            var board = new ChangeBoard
            {
                Kind = kind,
                Name = kind == ChangeBoardKind.Advisory
                    ? new LocalizedText("Change advisory board", "المجلس الاستشاري للتغيير")
                    : new LocalizedText("Emergency change board", "مجلس التغييرات الطارئة"),
            };
            db.ChangeBoards.Add(board);
            boards.Add(board);
            added = true;
        }

        if (added)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Made at the same moment by another request.
                db.ChangeTracker.Clear();
                boards = await db.ChangeBoards.Include(b => b.Members).ToListAsync(cancellationToken);
            }
        }

        return [.. boards.OrderBy(b => b.Kind)];
    }

    internal static async Task<IResult> BoardsAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var boards = await EnsureBoardsAsync(db, cancellationToken);
        return TypedResults.Ok(await BoardResponsesAsync(db, boards, cancellationToken));
    }

    internal static async Task<IResult> UpdateBoardAsync(string kind, BoardRequest request, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ChangeBoardKind>(kind, ignoreCase: true, out var boardKind))
        {
            return FormsCommon.NotFound("change-board");
        }

        var board = (await EnsureBoardsAsync(db, cancellationToken)).Single(b => b.Kind == boardKind);
        var name = new LocalizedText(request.Name?.En, request.Name?.Ar);
        if (!name.HasAnyValue)
        {
            return UserEndpoints.Validation("name", "Name the board in English or Arabic.");
        }

        var members = (request.MemberIds ?? []).Distinct().ToList();
        var people = members.Concat(new[] { request.ChairId, request.DeputyChairId }.OfType<Guid>()).Distinct().ToList();
        if (await BoardMembersQuery(db).CountAsync(u => people.Contains(u.Id), cancellationToken) != people.Count)
        {
            return UserEndpoints.Validation("memberIds", "Board members and chairs are active people whose roles hold changes.approve.");
        }

        if (boardKind == ChangeBoardKind.Emergency && request.DecideWithinHours is not (null or (>= 1 and <= 168)))
        {
            return UserEndpoints.Validation("decideWithinHours", "Use 1 to 168 hours.");
        }

        board.Name = name;
        board.ChairId = boardKind == ChangeBoardKind.Advisory ? request.ChairId : null;
        board.DeputyChairId = boardKind == ChangeBoardKind.Advisory ? request.DeputyChairId : null;
        board.DecideWithinHours = request.DecideWithinHours ?? board.DecideWithinHours;
        board.UpdatedAt = DateTimeOffset.UtcNow;
        foreach (var gone in board.Members.Where(m => !members.Contains(m.UserId)).ToList())
        {
            board.Members.Remove(gone);
            db.ChangeBoardMembers.Remove(gone);
        }

        foreach (var added in members.Where(m => board.Members.All(x => x.UserId != m)))
        {
            board.Members.Add(new ChangeBoardMember { BoardId = board.Id, UserId = added });
        }

        audit.Add(AuditCategory.Admin, "changes.board.updated", "change_board", board.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = boardKind.ToString(), ["members"] = members.Count });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await BoardResponsesAsync(db, [board], cancellationToken)).Single());
    }

    /// <summary>People who can sit on a board: active, with changes.approve.</summary>
    private static IQueryable<UserAccount> BoardMembersQuery(ZelvoDbContext db) =>
        db.Users.Where(u => u.Status == UserStatus.Active && u.Roles.Any(r => db.RoleGrants.Any(g => g.RoleId == r.RoleId && g.Permission == Permissions.ChangesApprove)));

    private static async Task<List<BoardResponse>> BoardResponsesAsync(ZelvoDbContext db, IReadOnlyList<ChangeBoard> boards, CancellationToken cancellationToken)
    {
        var names = await IncidentEndpoints.Names(db, boards.SelectMany(b => b.Members.Select(m => m.UserId).Append(b.ChairId ?? Guid.Empty).Append(b.DeputyChairId ?? Guid.Empty)), cancellationToken);
        PersonResponse? Person(Guid? id) => id is { } p ? new PersonResponse(p, names.GetValueOrDefault(p)) : null;
        return [.. boards.Select(b => new BoardResponse(b.Id, b.Kind, LocalizedTextDto.From(b.Name), Person(b.ChairId), Person(b.DeputyChairId),
            [.. b.Members.Select(m => Person(m.UserId)!).OrderBy(p => p.DisplayName, StringComparer.CurrentCulture)], b.DecideWithinHours, $"\"{b.Version}\""))];
    }

    // ---- meetings ----

    internal static async Task<IResult> MeetingsAsync(bool? upcoming, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = db.CabMeetings.AsNoTracking();
        if (upcoming == true)
        {
            var now = DateTimeOffset.UtcNow;
            query = query.Where(m => m.State == CabMeetingState.Planned && m.EndsAt > now.AddDays(-1));
        }

        var rows = await query.OrderByDescending(m => m.StartsAt).Take(200).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await db.CabAgendaItems.AsNoTracking().Where(i => ids.Contains(i.MeetingId)).GroupBy(i => i.MeetingId)
            .Select(g => new { g.Key, All = g.Count(), Decided = g.Count(i => i.Decision != null) }).ToDictionaryAsync(g => g.Key, cancellationToken);
        var names = await IncidentEndpoints.Names(db, rows.Select(r => r.ChairId), cancellationToken);
        return TypedResults.Ok(rows.Select(m => new MeetingSummary(m.Id, LocalizedTextDto.From(m.Title), m.StartsAt, m.EndsAt, m.Location,
            new PersonResponse(m.ChairId, names.GetValueOrDefault(m.ChairId)), m.State,
            counts.TryGetValue(m.Id, out var c) ? c.All : 0, counts.TryGetValue(m.Id, out var d) ? d.Decided : 0)).ToList());
    }

    /// <summary>
    /// The changes whose pending approval waits at a level of the change advisory board, leaving out
    /// those an undecided item of another planned meeting has already.
    /// </summary>
    internal static async Task<List<Guid>> WaitingForBoardAsync(ZelvoDbContext db, Guid? exceptMeeting, CancellationToken cancellationToken)
    {
        var processes = await db.ApprovalProcesses.AsNoTracking()
            .Where(p => p.State == ApprovalProcessState.Pending && p.Stage == ApprovalStage.ChangeAuthorisation)
            .Select(p => new { p.TicketId, p.Levels, p.CurrentLevel }).ToListAsync(cancellationToken);
        var waiting = processes.Where(p => ApprovalPlanner.Read(p.Levels) is var levels && p.CurrentLevel < levels.Count
            && levels[p.CurrentLevel].Board == nameof(ChangeBoardKind.Advisory)).Select(p => p.TicketId).Distinct().ToList();
        var scheduled = await db.CabAgendaItems.AsNoTracking()
            .Where(i => i.Decision == null && waiting.Contains(i.ChangeId) && i.MeetingId != exceptMeeting
                && db.CabMeetings.Any(m => m.Id == i.MeetingId && m.State == CabMeetingState.Planned))
            .Select(i => i.ChangeId).ToListAsync(cancellationToken);
        return [.. waiting.Except(scheduled)];
    }

    internal static async Task<IResult> WaitingAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ids = await WaitingForBoardAsync(db, null, cancellationToken);
        var rows = await ChangeCore.Changes(db).AsNoTracking().Where(t => ids.Contains(t.Id)).OrderBy(t => t.Change!.PlannedStart).ToListAsync(cancellationToken);
        var context = await ChangeResponseContext.LoadAsync(db, user, rows, cancellationToken);
        return TypedResults.Ok(rows.Select(context.From).ToList());
    }

    internal static async Task<IResult> CreateAsync(MeetingRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var board = (await EnsureBoardsAsync(db, cancellationToken)).Single(b => b.Kind == ChangeBoardKind.Advisory);
        var me = user.Account!.Id;
        if (!MayManage(user, board, null))
        {
            return Forbidden();
        }

        var chair = request.ChairId ?? board.ChairId ?? me;
        var members = (request.MemberIds ?? board.Members.Select(m => m.UserId).ToList()).Append(chair).Distinct().ToList();
        if (await CheckAsync(db, request, chair, members, cancellationToken) is { } problem)
        {
            return problem;
        }

        var meeting = new CabMeeting
        {
            Title = new LocalizedText(request.Title!.En, request.Title.Ar),
            StartsAt = request.StartsAt!.Value,
            EndsAt = request.EndsAt!.Value,
            Location = ChangeCore.Clean(request.Location),
            ChairId = chair,
            CreatedById = me,
        };
        db.CabMeetings.Add(meeting);
        foreach (var member in members)
        {
            db.CabMeetingMembers.Add(new CabMeetingMember { MeetingId = meeting.Id, UserId = member });
        }

        var waiting = await WaitingForBoardAsync(db, null, cancellationToken);
        await AddItemsAsync(db, meeting.Id, waiting, 0, cancellationToken);
        audit.Add(AuditCategory.Change, "changes.board.meeting.planned", "cab_meeting", meeting.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["members"] = members.Count, ["items"] = waiting.Count });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/cab-meetings/{meeting.Id}", await ResponseAsync(db, user, meeting.Id, cancellationToken));
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken) =>
        await db.CabMeetings.AnyAsync(m => m.Id == id, cancellationToken)
            ? TypedResults.Ok(await ResponseAsync(db, user, id, cancellationToken))
            : NotFound();

    internal static async Task<IResult> UpdateAsync(Guid id, MeetingRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var (meeting, problem) = await PlannedAsync(db, user, id, manage: true, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (http.Request.Headers.IfMatch.ToString() is { Length: > 0 } match && match != $"\"{meeting!.Version}\"")
        {
            return FormsCommon.VersionMismatch();
        }

        var current = await db.CabMeetingMembers.Where(m => m.MeetingId == id).ToListAsync(cancellationToken);
        var chair = request.ChairId ?? meeting!.ChairId;
        var members = (request.MemberIds ?? current.Select(m => m.UserId).ToList()).Append(chair).Distinct().ToList();
        if (await CheckAsync(db, request, chair, members, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        meeting!.Title = new LocalizedText(request.Title!.En, request.Title.Ar);
        meeting.StartsAt = request.StartsAt!.Value;
        meeting.EndsAt = request.EndsAt!.Value;
        meeting.Location = ChangeCore.Clean(request.Location);
        meeting.ChairId = chair;
        meeting.Sequence++;
        meeting.UpdatedAt = DateTimeOffset.UtcNow;
        db.CabMeetingMembers.RemoveRange(current.Where(m => !members.Contains(m.UserId)));
        foreach (var added in members.Where(m => current.All(c => c.UserId != m)))
        {
            db.CabMeetingMembers.Add(new CabMeetingMember { MeetingId = id, UserId = added });
        }

        audit.Add(AuditCategory.Change, "changes.board.meeting.updated", "cab_meeting", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["members"] = members.Count, ["sequence"] = meeting.Sequence });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        return TypedResults.Ok(await ResponseAsync(db, user, id, cancellationToken));
    }

    internal static async Task<IResult> RefreshAgendaAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var (meeting, problem) = await PlannedAsync(db, user, id, manage: true, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var items = await db.CabAgendaItems.Where(i => i.MeetingId == id).ToListAsync(cancellationToken);
        var waiting = await WaitingForBoardAsync(db, id, cancellationToken);
        var removed = items.Where(i => i.Decision is null && !waiting.Contains(i.ChangeId)).ToList();
        db.CabAgendaItems.RemoveRange(removed);
        var added = waiting.Where(w => items.All(i => i.ChangeId != w)).ToList();
        await AddItemsAsync(db, id, added, items.Count == 0 ? 0 : items.Max(i => i.Position) + 1, cancellationToken);
        audit.Add(AuditCategory.Change, "changes.board.agenda.refreshed", "cab_meeting", meeting!.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["added"] = added.Count, ["removed"] = removed.Count });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, user, id, cancellationToken));
    }

    internal static async Task<IResult> RemoveItemAsync(Guid id, Guid itemId, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var (_, problem) = await PlannedAsync(db, user, id, manage: true, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var item = await db.CabAgendaItems.SingleOrDefaultAsync(i => i.Id == itemId && i.MeetingId == id, cancellationToken);
        if (item is null)
        {
            return FormsCommon.NotFound("agenda-item");
        }

        if (item.Decision is not null)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "already-decided", "The chair has decided this change already.");
        }

        db.CabAgendaItems.Remove(item);
        audit.Add(AuditCategory.Change, "changes.board.agenda.removed", "ticket", item.ChangeId.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["meetingId"] = id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, user, id, cancellationToken));
    }

    internal static async Task<IResult> VoteAsync(Guid id, Guid itemId, VoteRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var meeting = await db.CabMeetings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (meeting is null)
        {
            return NotFound();
        }

        var me = user.Account!.Id;
        if (!await db.CabMeetingMembers.AnyAsync(m => m.MeetingId == id && m.UserId == me, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only the meeting's members vote.");
        }

        if (meeting.State != CabMeetingState.Planned)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "meeting-not-planned", "The meeting is over.");
        }

        var item = await db.CabAgendaItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.MeetingId == id, cancellationToken);
        if (item is null)
        {
            return FormsCommon.NotFound("agenda-item");
        }

        if (item.Decision is not null)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "already-decided", "The chair has decided this change already.");
        }

        var vote = await db.CabVotes.SingleOrDefaultAsync(v => v.ItemId == itemId && v.UserId == me, cancellationToken);
        if (vote is null)
        {
            vote = new CabVote { ItemId = itemId, UserId = me };
            db.CabVotes.Add(vote);
        }

        vote.Choice = request.Choice!.Value;
        vote.Comment = ChangeCore.Clean(request.Comment);
        vote.VotedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "changes.board.voted", "ticket", item.ChangeId.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["meetingId"] = id.ToString(), ["choice"] = vote.Choice.ToString() });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Voted twice at the same moment: the first vote stands.
        }

        return TypedResults.Ok(await ResponseAsync(db, user, id, cancellationToken));
    }

    internal static async Task<IResult> DecideAsync(
        Guid id, Guid itemId, CabDecisionRequest request, ZelvoDbContext db, CurrentUser user, ApprovalService approvals, IAuditLog audit,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // One decision at a time per meeting.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM cab_meetings WHERE id = {id} FOR UPDATE", cancellationToken);
        var meeting = await db.CabMeetings.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (meeting is null)
        {
            return NotFound();
        }

        var me = user.Account!.Id;
        if (meeting.ChairId != me)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only the meeting's chair records the board's decisions.");
        }

        if (meeting.State != CabMeetingState.Planned)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "meeting-not-planned", "The meeting is over.");
        }

        var item = await db.CabAgendaItems.SingleOrDefaultAsync(i => i.Id == itemId && i.MeetingId == id, cancellationToken);
        if (item is null)
        {
            return FormsCommon.NotFound("agenda-item");
        }

        if (item.Decision is not null)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "already-decided", "The chair has decided this change already.");
        }

        var decision = request.Decision!.Value;
        if (decision == CabDecision.Rejected && string.IsNullOrWhiteSpace(request.Comment))
        {
            return UserEndpoints.Validation("comment", "Give the reason for rejecting.");
        }

        if (decision != CabDecision.Deferred)
        {
            // The board's outcome goes through C3's approval service (decision 8): the chair's own
            // decision on the change's pending board level.
            var pending = await db.ApprovalDecisions.AsNoTracking()
                .Where(d => d.TicketId == item.ChangeId && d.ApproverId == me && d.State == ApprovalDecisionState.Pending
                    && db.ApprovalProcesses.Any(p => p.Id == d.ProcessId && p.State == ApprovalProcessState.Pending && p.Stage == ApprovalStage.ChangeAuthorisation && p.CurrentLevel == d.Level))
                .Select(d => (Guid?)d.Id).FirstOrDefaultAsync(cancellationToken);
            if (pending is not { } decisionId)
            {
                return ApiProblem.Create(StatusCodes.Status409Conflict, "chair-not-approver",
                    "The change is not waiting for this chair's decision: it was decided already, or the board's chair is someone else.");
            }

            var comment = ChangeCore.Clean(request.Comment);
            var error = await approvals.DecideAsync(decisionId, decision == CabDecision.Approved, comment, "cab", cancellationToken);
            if (error == DecisionError.AlreadyDecided)
            {
                return ApiProblem.Create(StatusCodes.Status409Conflict, "already-decided", "The change's approval was decided already.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        item.Decision = decision;
        item.Comment = ChangeCore.Clean(request.Comment);
        item.Minutes = ChangeCore.Clean(request.Minutes);
        item.DecidedById = me;
        item.DecidedAt = now;
        meeting.UpdatedAt = now;
        audit.Add(AuditCategory.Change, "changes.board.decided", "ticket", item.ChangeId.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["meetingId"] = id.ToString(), ["decision"] = decision.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, user, id, cancellationToken));
    }

    internal static async Task<IResult> MinutesAsync(Guid id, MinutesRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var meeting = await db.CabMeetings.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (meeting is null)
        {
            return NotFound();
        }

        if (meeting.ChairId != user.Account!.Id && !user.Has(Permissions.ChangesManage))
        {
            return Forbidden();
        }

        if (meeting.State == CabMeetingState.Cancelled)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "meeting-cancelled", "The meeting was called off.");
        }

        meeting.Minutes = ChangeCore.Clean(request.Minutes);
        meeting.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "changes.board.minutes", "cab_meeting", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["length"] = meeting.Minutes?.Length ?? 0 });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, user, id, cancellationToken));
    }

    internal static async Task<IResult> CompleteAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var meeting = await db.CabMeetings.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (meeting is null)
        {
            return NotFound();
        }

        if (meeting.ChairId != user.Account!.Id)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only the meeting's chair marks it held.");
        }

        if (meeting.State != CabMeetingState.Planned)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "meeting-not-planned", "The meeting is over.");
        }

        var now = DateTimeOffset.UtcNow;
        var deferred = 0;
        foreach (var item in await db.CabAgendaItems.Where(i => i.MeetingId == id && i.Decision == null).ToListAsync(cancellationToken))
        {
            item.Decision = CabDecision.Deferred;
            item.DecidedById = meeting.ChairId;
            item.DecidedAt = now;
            deferred++;
        }

        meeting.State = CabMeetingState.Held;
        meeting.UpdatedAt = now;
        audit.Add(AuditCategory.Change, "changes.board.meeting.held", "cab_meeting", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["deferred"] = deferred });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, user, id, cancellationToken));
    }

    internal static async Task<IResult> CancelAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var (meeting, problem) = await PlannedAsync(db, user, id, manage: true, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        meeting!.State = CabMeetingState.Cancelled;
        meeting.Sequence++;
        meeting.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "changes.board.meeting.cancelled", "cab_meeting", id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal));
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, user, id, cancellationToken));
    }

    internal static async Task<IResult> ChangeBoardAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Change, cancellationToken))
        {
            return ChangeCore.NotFound();
        }

        var items = await db.CabAgendaItems.AsNoTracking().Where(i => i.ChangeId == id).ToListAsync(cancellationToken);
        var meetingIds = items.Select(i => i.MeetingId).ToList();
        var meetings = await db.CabMeetings.AsNoTracking().Where(m => meetingIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancellationToken);
        var responses = await ItemResponsesAsync(db, items, cancellationToken);
        return TypedResults.Ok(items.Zip(responses).Select(x => (Meeting: meetings[x.First.MeetingId], Item: x.Second))
            .OrderBy(x => x.Meeting.StartsAt)
            .Select(x => new ChangeBoardEntry(x.Meeting.Id, LocalizedTextDto.From(x.Meeting.Title), x.Meeting.StartsAt, x.Meeting.State, x.Item)).ToList());
    }

    // ---- helpers ----

    private static ProblemHttpResult NotFound() => FormsCommon.NotFound("cab-meeting");

    private static IResult Forbidden() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only the change manager or the board's chair plans and changes meetings.");

    /// <summary>The change manager, the board's chair or deputy, or (for an existing meeting) its chair.</summary>
    private static bool MayManage(CurrentUser user, ChangeBoard? board, CabMeeting? meeting)
    {
        var me = user.Account?.Id;
        return user.Has(Permissions.ChangesManage) || (me is not null && (board?.ChairId == me || board?.DeputyChairId == me || meeting?.ChairId == me));
    }

    private static async Task<(CabMeeting? Meeting, IResult? Problem)> PlannedAsync(ZelvoDbContext db, CurrentUser user, Guid id, bool manage, CancellationToken cancellationToken)
    {
        var meeting = await db.CabMeetings.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (meeting is null)
        {
            return (null, NotFound());
        }

        var board = await db.ChangeBoards.AsNoTracking().FirstOrDefaultAsync(b => b.Kind == ChangeBoardKind.Advisory, cancellationToken);
        if (manage && !MayManage(user, board, meeting))
        {
            return (null, Forbidden());
        }

        return meeting.State != CabMeetingState.Planned
            ? (null, ApiProblem.Create(StatusCodes.Status409Conflict, "meeting-not-planned", "The meeting is over."))
            : (meeting, null);
    }

    private static async Task<IResult?> CheckAsync(ZelvoDbContext db, MeetingRequest request, Guid chair, List<Guid> members, CancellationToken cancellationToken)
    {
        if (request.Title is null || (string.IsNullOrWhiteSpace(request.Title.En) && string.IsNullOrWhiteSpace(request.Title.Ar)))
        {
            return UserEndpoints.Validation("title", "Give the meeting a title in English or Arabic.");
        }

        if (request.EndsAt <= request.StartsAt)
        {
            return UserEndpoints.Validation("endsAt", "The meeting ends after it starts.");
        }

        if (members.Count > MaxMembers)
        {
            return UserEndpoints.Validation("memberIds", $"Invite at most {MaxMembers} members.");
        }

        if (await BoardMembersQuery(db).CountAsync(u => members.Contains(u.Id), cancellationToken) != members.Count)
        {
            return UserEndpoints.Validation("memberIds", "The chair and members are active people whose roles hold changes.approve.");
        }

        _ = chair;
        return null;
    }

    private static async Task AddItemsAsync(ZelvoDbContext db, Guid meetingId, IReadOnlyList<Guid> changeIds, int first, CancellationToken cancellationToken)
    {
        // Soonest window first, as the board reads them.
        var ordered = await db.Changes.AsNoTracking().Where(c => changeIds.Contains(c.TicketId))
            .OrderBy(c => c.PlannedStart).Select(c => c.TicketId).ToListAsync(cancellationToken);
        for (var i = 0; i < ordered.Count; i++)
        {
            db.CabAgendaItems.Add(new CabAgendaItem { MeetingId = meetingId, ChangeId = ordered[i], Position = first + i });
        }
    }

    internal static async Task<MeetingResponse> ResponseAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var meeting = await db.CabMeetings.AsNoTracking().SingleAsync(m => m.Id == id, cancellationToken);
        var members = await db.CabMeetingMembers.AsNoTracking().Where(m => m.MeetingId == id).Select(m => m.UserId).ToListAsync(cancellationToken);
        var items = await db.CabAgendaItems.AsNoTracking().Where(i => i.MeetingId == id).OrderBy(i => i.Position).ToListAsync(cancellationToken);
        var names = await IncidentEndpoints.Names(db, members.Append(meeting.ChairId), cancellationToken);
        var board = await db.ChangeBoards.AsNoTracking().FirstOrDefaultAsync(b => b.Kind == ChangeBoardKind.Advisory, cancellationToken);
        var me = user.Account?.Id;
        return new MeetingResponse(
            meeting.Id, LocalizedTextDto.From(meeting.Title), meeting.StartsAt, meeting.EndsAt, meeting.Location,
            new PersonResponse(meeting.ChairId, names.GetValueOrDefault(meeting.ChairId)), meeting.State, meeting.Minutes,
            [.. members.Select(m => new PersonResponse(m, names.GetValueOrDefault(m))).OrderBy(p => p.DisplayName, StringComparer.CurrentCulture)],
            await ItemResponsesAsync(db, items, cancellationToken),
            meeting.ChairId == me, me is not null && members.Contains(me.Value), MayManage(user, board, meeting), $"\"{meeting.Version}\"",
            meeting.CreatedAt, meeting.UpdatedAt);
    }

    private static async Task<List<AgendaItemResponse>> ItemResponsesAsync(ZelvoDbContext db, IReadOnlyList<CabAgendaItem> items, CancellationToken cancellationToken)
    {
        var changeIds = items.Select(i => i.ChangeId).Distinct().ToList();
        var changes = await ChangeCore.Changes(db).AsNoTracking().Where(t => changeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        var itemIds = items.Select(i => i.Id).ToList();
        var votes = await db.CabVotes.AsNoTracking().Where(v => itemIds.Contains(v.ItemId)).OrderBy(v => v.VotedAt).ToListAsync(cancellationToken);
        var names = await IncidentEndpoints.Names(db, votes.Select(v => v.UserId).Concat(items.Select(i => i.DecidedById ?? Guid.Empty)), cancellationToken);
        // "Waiting" counts every pending board level, also of changes on another agenda.
        var pendingBoard = await PendingBoardAsync(db, changeIds, cancellationToken);
        var result = new List<AgendaItemResponse>();
        foreach (var item in items)
        {
            if (!changes.TryGetValue(item.ChangeId, out var ticket))
            {
                continue;
            }

            var itemVotes = votes.Where(v => v.ItemId == item.Id).ToList();
            var conflicts = await ChangeConflicts.ForAsync(db, ticket, cancellationToken);
            result.Add(new AgendaItemResponse(
                item.Id, item.Position, ticket.Id, ticket.Number, ticket.Title, ticket.Change!.Kind, ChangeCore.StageOf(ticket), ticket.Change.RiskLevel,
                ticket.Change.PlannedStart, ticket.Change.PlannedEnd, conflicts.Count, pendingBoard.Contains(ticket.Id),
                [.. itemVotes.Select(v => new VoteResponse(new PersonResponse(v.UserId, names.GetValueOrDefault(v.UserId)), v.Choice, v.Comment, v.VotedAt))],
                Enum.GetValues<CabVoteChoice>().ToDictionary(c => c, c => itemVotes.Count(v => v.Choice == c)),
                item.Decision, item.Comment, item.Minutes,
                item.DecidedById is { } d ? new PersonResponse(d, names.GetValueOrDefault(d)) : null, item.DecidedAt));
        }

        return result;
    }

    private static async Task<HashSet<Guid>> PendingBoardAsync(ZelvoDbContext db, IReadOnlyList<Guid> changeIds, CancellationToken cancellationToken)
    {
        var processes = await db.ApprovalProcesses.AsNoTracking()
            .Where(p => changeIds.Contains(p.TicketId) && p.State == ApprovalProcessState.Pending && p.Stage == ApprovalStage.ChangeAuthorisation)
            .Select(p => new { p.TicketId, p.Levels, p.CurrentLevel }).ToListAsync(cancellationToken);
        return [.. processes.Where(p => ApprovalPlanner.Read(p.Levels) is var levels && p.CurrentLevel < levels.Count
            && levels[p.CurrentLevel].Board == nameof(ChangeBoardKind.Advisory)).Select(p => p.TicketId)];
    }
}

/// <summary>A board's settings.</summary>
public sealed class BoardRequest
{
    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public BlackoutName? Name { get; init; }

    /// <summary>The chair (advisory board).</summary>
    public Guid? ChairId { get; init; }

    /// <summary>The deputy chair (advisory board).</summary>
    public Guid? DeputyChairId { get; init; }

    /// <summary>Members, at most 50.</summary>
    [MaxLength(ChangeBoard.MaxMembers)]
    public IReadOnlyList<Guid>? MemberIds { get; init; }

    /// <summary>Emergency board: hours within which it decides (1–168).</summary>
    public int? DecideWithinHours { get; init; }
}

/// <summary>A meeting.</summary>
public sealed class MeetingRequest
{
    /// <summary>Title in English and Arabic.</summary>
    [Required]
    public BlackoutName? Title { get; init; }

    /// <summary>Start.</summary>
    [Required]
    public DateTimeOffset? StartsAt { get; init; }

    /// <summary>End.</summary>
    [Required]
    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>A room or an online meeting address.</summary>
    [StringLength(CabMeeting.LocationMaxLength)]
    public string? Location { get; init; }

    /// <summary>The chair; the board's chair by default.</summary>
    public Guid? ChairId { get; init; }

    /// <summary>Members; the board's by default (the chair is always one).</summary>
    [MaxLength(ChangeBoardEndpoints.MaxMembers)]
    public IReadOnlyList<Guid>? MemberIds { get; init; }
}

/// <summary>A member's vote.</summary>
public sealed class VoteRequest
{
    /// <summary>Approve, Reject or Abstain.</summary>
    [Required]
    public CabVoteChoice? Choice { get; init; }

    /// <summary>Why.</summary>
    [StringLength(CabVote.CommentMaxLength)]
    public string? Comment { get; init; }
}

/// <summary>The chair's decision.</summary>
public sealed class CabDecisionRequest
{
    /// <summary>Approved, Rejected or Deferred.</summary>
    [Required]
    public CabDecision? Decision { get; init; }

    /// <summary>The comment (the reason of a rejection).</summary>
    [StringLength(Data.Workflows.ApprovalDecision.CommentMaxLength)]
    public string? Comment { get; init; }

    /// <summary>The minutes about this change.</summary>
    [StringLength(CabAgendaItem.TextMaxLength)]
    public string? Minutes { get; init; }
}

/// <summary>A meeting's minutes.</summary>
public sealed class MinutesRequest
{
    /// <summary>The minutes (empty clears them).</summary>
    [StringLength(CabMeeting.MinutesMaxLength)]
    public string? Minutes { get; init; }
}
