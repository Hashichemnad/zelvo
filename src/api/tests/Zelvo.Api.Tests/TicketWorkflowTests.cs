using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>The ticket life cycle and the impact × urgency matrix (module 1), without a database.</summary>
public sealed class TicketWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static Ticket NewTicket() => new() { Number = "INC-000001", Title = "Printer", RequesterId = Guid.NewGuid() };

    [Fact]
    public void Assigning_a_new_ticket_makes_it_assigned_and_unassigning_makes_it_new_again()
    {
        var ticket = NewTicket();
        TicketWorkflow.Assign(ticket, Guid.NewGuid(), Now);
        Assert.Equal(TicketState.Assigned, ticket.State);

        TicketWorkflow.Assign(ticket, null, Now);
        Assert.Equal(TicketState.New, ticket.State);
    }

    [Fact]
    public void Work_needs_an_assignee_on_hold_needs_a_reason_and_resolving_needs_notes()
    {
        var ticket = NewTicket();
        Assert.Equal(TransitionError.AssigneeRequired, TicketWorkflow.Check(ticket, TicketState.InProgress, null, null));
        Assert.Equal(TransitionError.OnHoldReasonRequired, TicketWorkflow.Check(ticket, TicketState.OnHold, " ", null));
        Assert.Equal(TransitionError.ResolutionNotesRequired, TicketWorkflow.Check(ticket, TicketState.Resolved, null, ""));
        Assert.Equal(TransitionError.NotAllowed, TicketWorkflow.Check(ticket, TicketState.Closed, null, null));
    }

    [Fact]
    public void A_full_life_cycle_records_reason_notes_reopening_and_closing()
    {
        var ticket = NewTicket();
        TicketWorkflow.Assign(ticket, Guid.NewGuid(), Now);
        TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, Now);
        TicketWorkflow.Apply(ticket, TicketState.OnHold, "awaiting-supplier", null, Now);
        Assert.Equal("awaiting-supplier", ticket.OnHoldReason);

        TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, Now);
        Assert.Null(ticket.OnHoldReason);

        TicketWorkflow.Apply(ticket, TicketState.Resolved, null, "Replaced the toner", Now);
        Assert.Equal((Now, "Replaced the toner"), (ticket.ResolvedAt!.Value, ticket.ResolutionNotes));

        TicketWorkflow.Apply(ticket, TicketState.Reopened, null, null, Now.AddHours(1));
        Assert.Equal((1, (DateTimeOffset?)null, (string?)null), (ticket.ReopenCount, ticket.ResolvedAt, ticket.ResolutionNotes));

        TicketWorkflow.Apply(ticket, TicketState.Resolved, null, "Cleaned the rollers", Now.AddHours(2));
        TicketWorkflow.Apply(ticket, TicketState.Closed, null, null, Now.AddDays(5));
        Assert.Equal(TicketState.Closed, ticket.State);
        Assert.Empty(TicketWorkflow.NextStates(TicketState.Closed));
        Assert.Throws<InvalidOperationException>(() => TicketWorkflow.Apply(ticket, TicketState.Reopened, null, null, Now));
        Assert.Throws<InvalidOperationException>(() => TicketWorkflow.Assign(ticket, null, Now));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 3, 3)]
    [InlineData(2, 2, 3)]
    [InlineData(3, 1, 3)]
    [InlineData(3, 3, 5)]
    public void The_default_matrix_gives_P1_to_P5(int impact, int urgency, int priority) =>
        Assert.Equal(priority, PriorityMatrix.Default.PriorityOf(impact, urgency));

    [Fact]
    public void A_matrix_needs_three_levels_five_named_priorities_and_priorities_in_range()
    {
        Assert.True(PriorityMatrix.Default.IsValid());
        Assert.False((PriorityMatrix.Default with { Matrix = [[1, 2, 3], [2, 3, 4], [3, 4, 6]] }).IsValid());
        Assert.False((PriorityMatrix.Default with { Impacts = [new("High", null), new("Low", null)] }).IsValid());
        Assert.False((PriorityMatrix.Default with { Priorities = [.. PriorityMatrix.Default.Priorities.Take(4), new SettingText(null, " ")] }).IsValid());
    }

    [Fact]
    public void Numbers_have_the_prefix_and_at_least_six_digits()
    {
        Assert.Equal("INC-000123", TicketNumbers.Format("INC", 123));
        Assert.Equal("INC-1234567", TicketNumbers.Format("INC", 1_234_567));
        Assert.False(TicketNumbers.AreValid(new Dictionary<TicketType, string> { [TicketType.Incident] = "inc" }));
        Assert.True(TicketNumbers.AreValid(new Dictionary<TicketType, string> { [TicketType.Incident] = "HD1" }));
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\me\\scan.png", "scan.png")]
    [InlineData("invoice\u202Efdp.exe", "invoicefdp.exe")]
    [InlineData("  .hidden.txt  ", "hidden.txt")]
    public void File_names_lose_folders_and_characters_that_disguise_them(string uploaded, string stored) =>
        Assert.Equal(stored, FileTypes.CleanFileName(uploaded));

    [Fact]
    public void Content_must_match_the_file_type()
    {
        Assert.True(FileTypes.Find("pdf")!.Check("%PDF-1.7\n"u8.ToArray()));
        Assert.False(FileTypes.Find("pdf")!.Check("MZ\x90\x00"u8.ToArray()));
        Assert.True(FileTypes.Find("txt")!.Check("مرحبا hello"u8.ToArray()));
        Assert.False(FileTypes.Find("txt")!.Check(new byte[] { 0x48, 0x00, 0x49 }));
        Assert.False(FileTypes.Find("png")!.Check("%PDF-"u8.ToArray()));
        Assert.Null(FileTypes.Find("exe"));
    }

    [Theory]
    [InlineData("stream: OK\0", true, null)]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND\0", false, "Win.Test.EICAR_HDB-1")]
    public void ClamAV_replies_are_read(string reply, bool clean, string? signature) =>
        Assert.Equal(new ScanVerdict(clean, signature), ClamAvScanner.Parse(reply));

    [Fact]
    public void A_ClamAV_error_is_not_taken_as_clean() =>
        Assert.Throws<MalwareScannerException>(() => ClamAvScanner.Parse("INSTREAM size limit exceeded. ERROR\0"));
}
