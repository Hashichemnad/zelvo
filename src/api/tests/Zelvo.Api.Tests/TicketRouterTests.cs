using Zelvo.Data.Directory;
using Zelvo.Data.Localization;
using Zelvo.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>Rule matching and round-robin order (task B2), without a database.</summary>
public sealed class TicketRouterTests
{
    private static readonly Guid Hardware = Guid.NewGuid();
    private static readonly Guid Laptop = Guid.NewGuid();
    private static readonly Guid MainBuilding = Guid.NewGuid();
    private static readonly Guid Desk = Guid.NewGuid();
    private static readonly Guid Network = Guid.NewGuid();

    private static AssignmentRule Rule(int position, Guid group, Guid? category = null, Guid? location = null, string? language = null, bool active = true) => new()
    {
        Name = new LocalizedText($"Rule {position}", null),
        Position = position,
        GroupId = group,
        CategoryId = category,
        LocationId = location,
        Language = language,
        Active = active,
    };

    [Fact]
    public void A_rule_on_a_category_matches_the_categories_below_it()
    {
        var rule = Rule(0, Desk, category: Hardware);
        Assert.True(TicketRouter.Matches(rule, [Laptop, Hardware], [], "en"));
        Assert.True(TicketRouter.Matches(rule, [Hardware], [], "en"));
        Assert.False(TicketRouter.Matches(rule, [Guid.NewGuid()], [], "en"));
        Assert.False(TicketRouter.Matches(rule, [], [], "en"));
    }

    [Fact]
    public void Every_condition_of_a_rule_must_hold()
    {
        var rule = Rule(0, Desk, category: Hardware, location: MainBuilding, language: "ar");
        Assert.True(TicketRouter.Matches(rule, [Hardware], [MainBuilding], "ar"));
        Assert.False(TicketRouter.Matches(rule, [Hardware], [MainBuilding], "en"));
        Assert.False(TicketRouter.Matches(rule, [Hardware], [], "ar"));
        Assert.True(TicketRouter.Matches(Rule(0, Desk), [], [], null));
    }

    [Fact]
    public void The_first_active_rule_in_order_whose_group_is_active_wins()
    {
        var inactive = Rule(0, Desk, category: Hardware, active: false);
        var inactiveGroup = Rule(1, Guid.NewGuid(), category: Hardware);
        var network = Rule(2, Network, category: Hardware);
        var catchAll = Rule(3, Desk);
        var rules = new[] { catchAll, network, inactiveGroup, inactive };
        var groups = new HashSet<Guid> { Desk, Network };

        Assert.Same(network, TicketRouter.FirstMatch(rules, groups, [Laptop, Hardware], [], "en"));
        Assert.Same(catchAll, TicketRouter.FirstMatch(rules, groups, [], [], "en"));
        Assert.Null(TicketRouter.FirstMatch([network], groups, [], [], "en"));
    }

    [Fact]
    public void Round_robin_gives_the_ticket_to_whoever_waited_longest_and_new_members_first()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new RotationCandidate(Guid.Parse("00000000-0000-0000-0000-00000000000a"), now.AddMinutes(-5));
        var b = new RotationCandidate(Guid.Parse("00000000-0000-0000-0000-00000000000b"), now.AddMinutes(-10));
        var newcomer = new RotationCandidate(Guid.Parse("00000000-0000-0000-0000-00000000000c"), null);

        Assert.Equal(newcomer.UserId, TicketRouter.PickNext([a, b, newcomer]));
        Assert.Equal(b.UserId, TicketRouter.PickNext([a, b]));
        Assert.Null(TicketRouter.PickNext([]));
    }
}
