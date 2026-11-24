using Zelvo.Data.Localization;
using Zelvo.Data.Sla;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Tests;

/// <summary>Business time, policy matching and clock states (module 8, GOV-7) without a database.</summary>
public sealed class SlaEngineTests
{
    private static readonly TimeZoneInfo Dubai = WorkingCalendar.FindTimeZone("Asia/Dubai")!;

    /// <summary>UAE government week: Monday–Thursday 07:30–15:30, Friday 07:30–12:00.</summary>
    private static WorkingCalendar Government(params DateOnly[] holidays) =>
        new(Dubai, CalendarTemplate.UaeGovernment.Periods, holidays);

    /// <summary>A local Dubai time (UTC+4).</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(4));

    [Fact]
    public void Time_inside_one_working_day_counts_fully()
    {
        // Monday 2026-11-23.
        Assert.Equal(2 * 3600, Government().SecondsBetween(Local(2026, 11, 23, 9), Local(2026, 11, 23, 11)));
    }

    [Fact]
    public void Nights_and_the_Saturday_Sunday_weekend_do_not_count()
    {
        var calendar = Government();
        // Friday 11:00 → Monday 08:30: one hour on Friday (to 12:00) and one on Monday (from 07:30).
        Assert.Equal(2 * 3600, calendar.SecondsBetween(Local(2026, 11, 27, 11), Local(2026, 11, 30, 8, 30)));
        // Thursday 15:00 → Friday 08:00: 30 minutes on Thursday, 30 minutes on Friday.
        Assert.Equal(3600, calendar.SecondsBetween(Local(2026, 11, 26, 15), Local(2026, 11, 27, 8)));
    }

    [Fact]
    public void The_Friday_half_day_ends_at_noon()
    {
        var calendar = Government();
        // Four hours from Friday 10:00: 2 on Friday, then Monday 07:30 + 2 h = 09:30.
        Assert.Equal(Local(2026, 11, 30, 9, 30), calendar.Add(Local(2026, 11, 27, 10), 4 * 3600));
    }

    [Fact]
    public void Public_holidays_are_skipped()
    {
        // National Day: Wednesday 2 and Thursday 3 December 2026.
        var calendar = Government(new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 3));
        // From Tuesday 15:00, two hours: 30 minutes on Tuesday, then Friday 07:30 + 90 minutes.
        Assert.Equal(Local(2026, 12, 4, 9), calendar.Add(Local(2026, 12, 1, 15), 2 * 3600));
        Assert.False(calendar.IsWorkingDay(new DateOnly(2026, 12, 2)));
        Assert.True(calendar.IsWorkingDay(new DateOnly(2026, 12, 4)));
    }

    [Fact]
    public void Adding_starts_at_the_next_working_period_when_outside_hours()
    {
        // Saturday night → Monday 07:30 + 1 h.
        Assert.Equal(Local(2026, 11, 30, 8, 30), Government().Add(Local(2026, 11, 28, 22), 3600));
        // Zero seconds is now, even outside hours.
        Assert.Equal(Local(2026, 11, 28, 22), Government().Add(Local(2026, 11, 28, 22), 0));
    }

    [Fact]
    public void Adding_and_counting_agree()
    {
        var calendar = Government(new DateOnly(2026, 12, 2));
        var start = Local(2026, 11, 26, 14, 17);
        foreach (var seconds in new long[] { 1, 59, 3600, 8 * 3600, 30 * 3600, 100 * 3600 })
        {
            var end = calendar.Add(start, seconds)!.Value;
            Assert.Equal(seconds, calendar.SecondsBetween(start, end));
        }
    }

    [Fact]
    public void Around_the_clock_counts_every_second()
    {
        var from = new DateTimeOffset(2026, 11, 28, 23, 0, 0, TimeSpan.Zero);
        Assert.Equal(26 * 3600, WorkingCalendar.AllDay.SecondsBetween(from, from.AddHours(26)));
        Assert.Equal(from.AddHours(5), WorkingCalendar.AllDay.Add(from, 5 * 3600));
    }

    [Fact]
    public void A_calendar_with_only_holidays_ahead_has_no_due_time()
    {
        var calendar = new WorkingCalendar(Dubai, [(DayOfWeek.Monday, new WorkingPeriod(480, 960))], []);
        Assert.False(new WorkingCalendar(Dubai, [], []).HasWorkingTime);
        Assert.NotNull(calendar.Add(Local(2026, 11, 24, 9), 3600));
        Assert.Null(new WorkingCalendar(Dubai, [], []).Add(Local(2026, 11, 24, 9), 1));
    }

    [Fact]
    public void Overlapping_periods_are_merged()
    {
        var calendar = new WorkingCalendar(Dubai,
            [(DayOfWeek.Monday, new WorkingPeriod(480, 720)), (DayOfWeek.Monday, new WorkingPeriod(600, 900))], []);
        Assert.Equal(7 * 3600, calendar.SecondsBetween(Local(2026, 11, 23, 0), Local(2026, 11, 24, 0)));
    }

    [Fact]
    public void Daylight_saving_changes_are_handled()
    {
        // Berlin moves from 02:00 to 03:00 on 29 March 2026; a 00:00–06:00 period is 5 hours that day.
        var berlin = WorkingCalendar.FindTimeZone("Europe/Berlin")!;
        var calendar = new WorkingCalendar(berlin, [(DayOfWeek.Sunday, new WorkingPeriod(0, 360))], []);
        var day = new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(5 * 3600, calendar.SecondsBetween(day, day.AddDays(2)));
    }

    [Fact]
    public void Working_days_keep_the_time_of_day_and_skip_weekends_and_holidays()
    {
        var calendar = Government(new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 3));
        // Thursday 26 Nov 10:00 + 5 working days: Fri 27, Mon 30, Tue 1, (Wed 2, Thu 3 holidays) Fri 4, Mon 7.
        Assert.Equal(Local(2026, 12, 7, 10), calendar.AddWorkingDays(Local(2026, 11, 26, 10), 5));
    }

    [Fact]
    public void Unknown_time_zones_are_not_found()
    {
        Assert.Null(WorkingCalendar.FindTimeZone("Mars/Olympus"));
        Assert.Null(WorkingCalendar.FindTimeZone(""));
    }

    private static SlaPolicy Policy(int position, TicketType? type = null, Guid? category = null, Guid? group = null, bool active = true) => new()
    {
        Name = new LocalizedText($"Policy {position}", null), Position = position, RecordType = type, CategoryId = category, CustomerGroupId = group, Active = active,
    };

    [Fact]
    public void The_first_matching_active_policy_wins()
    {
        var hardware = Guid.NewGuid();
        var laptop = Guid.NewGuid();
        var vip = Guid.NewGuid();
        var vipPolicy = Policy(0, group: vip);
        var hardwarePolicy = Policy(1, TicketType.Incident, hardware);
        var inactive = Policy(2, active: false);
        var catchAll = Policy(3);
        SlaPolicy[] policies = [catchAll, inactive, hardwarePolicy, vipPolicy];

        Assert.Same(vipPolicy, SlaMatcher.FirstMatch(policies, new SlaSubject(TicketType.Incident, 3, [laptop, hardware], [vip])));
        // A policy on a parent category matches its children.
        Assert.Same(hardwarePolicy, SlaMatcher.FirstMatch(policies, new SlaSubject(TicketType.Incident, 3, [laptop, hardware], [])));
        Assert.Same(catchAll, SlaMatcher.FirstMatch(policies, new SlaSubject(TicketType.Incident, 3, [], [])));
        Assert.Null(SlaMatcher.FirstMatch([inactive], new SlaSubject(TicketType.Incident, 3, [], [])));
    }

    [Fact]
    public void Targets_are_per_priority_and_thresholds_always_include_the_breach()
    {
        var policy = Policy(0);
        policy.Targets.Add(new SlaPolicyTarget { Priority = 1, ResponseMinutes = 30, ResolutionMinutes = 240 });
        policy.Escalations.Add(new SlaEscalation { Percent = 75, NotifyAssignee = true });
        policy.Escalations.Add(new SlaEscalation { Percent = 150, Metric = SlaMetric.Resolution, NotifyGroupLeads = true });

        Assert.Equal(30, SlaMatcher.TargetOf(policy, SlaMetric.Response, 1));
        Assert.Equal(240, SlaMatcher.TargetOf(policy, SlaMetric.Resolution, 1));
        Assert.Null(SlaMatcher.TargetOf(policy, SlaMetric.Response, 2));
        Assert.Null(SlaMatcher.TargetOf(null, SlaMetric.Response, 1));
        Assert.Equal([75, 100], SlaMatcher.Thresholds(policy, SlaMetric.Response));
        Assert.Equal([75, 100, 150], SlaMatcher.Thresholds(policy, SlaMetric.Resolution));
    }

    [Fact]
    public void Customer_groups_match_by_email_domain_ignoring_case()
    {
        var group = new CustomerGroup { Name = new LocalizedText("Ministry", null), EmailDomains = ["moi.gov.ae"] };
        Assert.True(SlaMatcher.InDomain(group, "Sara@MOI.gov.ae"));
        Assert.False(SlaMatcher.InDomain(group, "sara@moi.gov.ae.example"));
        Assert.False(SlaMatcher.InDomain(group, null));
    }

    [Fact]
    public void Clocks_follow_the_ticket()
    {
        var pausing = new HashSet<string>(StringComparer.Ordinal) { OnHoldReason.AwaitingRequester };
        var ticket = new Ticket { Number = "INC-000001", Title = "Test", State = TicketState.New };
        Assert.Equal(SlaClockState.Running, SlaTracker.DesiredState(SlaMetric.Response, ticket, pausing));

        TicketWorkflow.Assign(ticket, Guid.NewGuid(), DateTimeOffset.UtcNow);
        TicketWorkflow.Apply(ticket, TicketState.OnHold, OnHoldReason.AwaitingRequester, null, DateTimeOffset.UtcNow);
        Assert.Equal(SlaClockState.Paused, SlaTracker.DesiredState(SlaMetric.Response, ticket, pausing));
        Assert.Equal(SlaClockState.Paused, SlaTracker.DesiredState(SlaMetric.Resolution, ticket, pausing));

        TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, DateTimeOffset.UtcNow);
        Assert.NotNull(ticket.RespondedAt);
        Assert.Equal(SlaClockState.Stopped, SlaTracker.DesiredState(SlaMetric.Response, ticket, pausing));
        Assert.Equal(SlaClockState.Running, SlaTracker.DesiredState(SlaMetric.Resolution, ticket, pausing));

        // A reason that does not pause keeps the resolution clock running.
        TicketWorkflow.Apply(ticket, TicketState.OnHold, "awaiting-supplier", null, DateTimeOffset.UtcNow);
        Assert.Equal(SlaClockState.Running, SlaTracker.DesiredState(SlaMetric.Resolution, ticket, pausing));

        TicketWorkflow.Apply(ticket, TicketState.Resolved, null, "Fixed", DateTimeOffset.UtcNow);
        Assert.Equal(SlaClockState.Stopped, SlaTracker.DesiredState(SlaMetric.Resolution, ticket, pausing));
        TicketWorkflow.Apply(ticket, TicketState.Reopened, null, null, DateTimeOffset.UtcNow);
        Assert.Equal(SlaClockState.Running, SlaTracker.DesiredState(SlaMetric.Resolution, ticket, pausing));
        Assert.Equal(SlaClockState.Stopped, SlaTracker.DesiredState(SlaMetric.Response, ticket, pausing));
    }

    [Fact]
    public void Resolving_before_work_started_counts_as_the_response()
    {
        var ticket = new Ticket { Number = "INC-000002", Title = "Test" };
        var now = DateTimeOffset.UtcNow;
        TicketWorkflow.Apply(ticket, TicketState.Resolved, null, "Answered on the phone", now);
        Assert.Equal(now, ticket.RespondedAt);
    }

    [Fact]
    public void Reasons_saved_before_B3_pause_only_when_waiting_for_the_requester()
    {
        Assert.True(new OnHoldReason(OnHoldReason.AwaitingRequester, new SettingText("Waiting", null), true).Pauses);
        Assert.False(new OnHoldReason("awaiting-supplier", new SettingText("Supplier", null), true).Pauses);
        Assert.True(new OnHoldReason("awaiting-supplier", new SettingText("Supplier", null), true, true).Pauses);
    }

    [Fact]
    public void The_government_template_is_the_UAE_federal_week()
    {
        var calendar = new WorkingCalendar(Dubai, CalendarTemplate.UaeGovernment.Periods, []);
        // 4 × 8 h + 4.5 h.
        Assert.Equal((4 * 8 + 4.5) * 3600, calendar.SecondsBetween(Local(2026, 11, 23, 0), Local(2026, 11, 30, 0)));
    }
}
