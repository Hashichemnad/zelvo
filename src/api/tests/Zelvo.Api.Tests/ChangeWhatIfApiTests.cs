using System.Net;
using Microsoft.EntityFrameworkCore;
using Zelvo.Ai;
using Zelvo.Api.Changes;
using Zelvo.Data.Changes;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Tests;

/// <summary>The what-if simulation of changes (task H5, module 17), against PostgreSQL.</summary>
public sealed class ChangeWhatIfApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(4).AddHours(20);

    private static object Low => new Dictionary<string, string>
    {
        ["reach"] = "few", ["experience"] = "often", ["downtime"] = "none", ["backout"] = "easy", ["testing"] = "full",
    };

    private static async Task<Guid> PlannedAsync(HttpClient agent, string title, Guid item, DateTimeOffset start)
    {
        var change = await agent.SendOkAsync(HttpMethod.Post, "/api/v1/changes", new
        {
            kind = "Normal", title, plannedStart = start, plannedEnd = start.AddHours(1), implementationPlan = "Do it.", backoutPlan = "Undo it.",
            configurationItemIds = new[] { item },
        }, HttpStatusCode.Created);
        await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/changes/{change.Id()}/risk", new { answers = Low });
        return change.Id();
    }

    /// <summary>A change on <paramref name="item"/> carried out and reviewed with <paramref name="outcome"/> (low risk is approved by the change manager).</summary>
    private static async Task PastAsync(HttpClient agent, HttpClient lead, Guid leadId, Guid group, string title, Guid item, string outcome, int days)
    {
        var id = await PlannedAsync(agent, title, item, Start.AddDays(-days - 10));
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/submit");
        var pending = (await lead.SendOkAsync(HttpMethod.Get, "/api/v1/approvals?state=Pending"))["items"]!.AsArray().Single()!["id"]!.ToString();
        await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{pending}/decision", new { decision = "Approve" });
        await lead.SendOkAsync(HttpMethod.Put, $"/api/v1/changes/{id}/assignment", new { groupId = group, assigneeId = leadId });
        await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/transitions", new { to = "StartImplementation" });
        await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/transitions", new { to = "Implemented", notes = "Done." });
        await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/review", new { outcome, notes = outcome == "Successful" ? null : "Sessions dropped; restored the old version." });
    }

    [Fact]
    public async Task The_simulation_shows_the_blast_radius_similar_changes_and_a_predicted_risk_and_is_kept_on_approval()
    {
        var tenant = await database.CreateTenantAsync("whatif");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var leadUser = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var lead = _factory.ForTenant(tenant).As(leadUser);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        var staff = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var holder = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var group = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/groups", new { name = new { en = "Web team", ar = "فريق الويب" } }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{leadUser.Id}", new { isLead = true });

        // The e-services portal runs on web-01; the customer happiness department uses it; web-01 is an asset someone holds.
        Guid happiness;
        await using (var db = TestData.AppContext(database, tenant))
        {
            var department = new Department { Name = new LocalizedText("Customer happiness", "إسعاد المتعاملين") };
            db.Departments.Add(department);
            await db.SaveChangesAsync(Ct);
            (await db.Users.SingleAsync(u => u.Id == staff.Id, Ct)).DepartmentId = department.Id;
            await db.SaveChangesAsync(Ct);
            happiness = department.Id;
        }

        var classes = await admin.StandardClassesAsync();
        var types = await admin.TypesAsync();
        var server = await admin.ItemAsync(classes["server"], "web-01");
        var other = await admin.ItemAsync(classes["server"], "db-09");
        var portal = await admin.ItemAsync(classes["business-service"], "E-services portal");
        await admin.RelateAsync(portal, types["runs-on"], server);
        await admin.RelateAsync(portal, types["used-by"], departmentId: happiness);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assets", new { configurationItemId = server, state = "Assigned", assignedToId = holder.Id }, HttpStatusCode.Created);

        // History: on web-01 a change was backed out; elsewhere in the same class, one went fine.
        await PastAsync(agent, lead, leadUser.Id, group, "Upgrade the web server", server, "BackedOut", 40);
        await PastAsync(agent, lead, leadUser.Id, group, "Patch the database server", other, "Successful", 20);

        var id = await PlannedAsync(agent, "Upgrade the web server again", server, Start);
        await lead.SendOkAsync(HttpMethod.Put, $"/api/v1/changes/{id}/assignment", new { groupId = group, assigneeId = leadUser.Id });
        var simulation = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/changes/{id}/what-if");
        Assert.False((bool)simulation["kept"]!);
        var blast = simulation["blast"]!;
        Assert.Equal("E-services portal", (string)blast["impact"]!["businessServices"]!.AsArray().Single()!["node"]!["name"]!);
        Assert.Equal(1, (int)blast["assetUsers"]!);
        Assert.Equal(2, (int)blast["peopleCount"]!);

        // The backed-out change on the same item comes first, with why it is similar.
        var similar = simulation["similar"]!.AsArray();
        Assert.Equal("BackedOut", (string)similar[0]!["outcome"]!);
        Assert.Contains("items", similar[0]!["reasons"]!.AsArray().Select(r => (string)r!));
        Assert.Contains("words", similar[0]!["reasons"]!.AsArray().Select(r => (string)r!));

        // Predicted from the outcomes on the same item, class and team, beside the questionnaire's low level.
        var predicted = simulation["predicted"]!;
        Assert.Equal("Low", (string)predicted["questionnaire"]!);
        Assert.True((bool)predicted["history"]!);
        Assert.Equal(1, (int)predicted["reasons"]!.AsArray().Single(r => (string)r!["cohort"]! == "items")!["failed"]!);
        Assert.NotEqual("Low", (string)predicted["level"]!);

        // Approved: the simulation is kept with the change.
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/submit");
        var pending = (await lead.SendOkAsync(HttpMethod.Get, "/api/v1/approvals?state=Pending"))["items"]!.AsArray().Single()!["id"]!.ToString();
        await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{pending}/decision", new { decision = "Approve" });
        var kept = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/changes/{id}/what-if");
        Assert.True((bool)kept["kept"]!);
        Assert.Equal((int)predicted["failureChance"]!, (int)kept["predicted"]!["failureChance"]!);

        // Requesters cannot see simulations.
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        Assert.Equal(HttpStatusCode.NotFound, (await requester.SendAsync(HttpMethod.Get, $"/api/v1/changes/{id}/what-if")).StatusCode);
    }

    [Fact]
    public async Task The_AI_only_explains_the_simulation_when_it_is_set_up()
    {
        var tenant = await database.CreateTenantAsync("whatif");
        await using var fake = await FakeAiProvider.StartAsync();
        await using var factory = ApiFactory.ForDatabase(database).WithLocalAi().WithSetting("Ai:AllowInsecureHttp", "true");
        using var admin = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var classes = await admin.StandardClassesAsync();
        var server = await admin.ItemAsync(classes["server"], "app-01");
        var id = await PlannedAsync(agent, "Restart app-01", server, Start);

        Assert.False((bool)(await agent.SendOkAsync(HttpMethod.Get, "/api/v1/changes/what-if/availability"))["available"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/changes/{id}/what-if/explain", new { language = "ar" })).StatusCode);

        var provider = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/ai/providers",
            new { name = "Local model", kind = "OpenAICompatible", endpoint = new Uri(fake.Url, "v1").ToString() }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/ai/features/{AiFeatures.ChangeWhatIf}", new { enabled = true, providerId = provider, model = "test-model" });
        fake.Respond = body => body.Contains("Task: changeWhatIf.", StringComparison.Ordinal) ? "يمسّ التغيير خادمًا واحدًا دون خدمات مؤسسية." : null;
        var explained = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/what-if/explain", new { language = "ar" });
        Assert.Equal("ar", (string)explained["explanation"]!["language"]!);
        Assert.Contains("خادمًا", (string)explained["explanation"]!["text"]!, StringComparison.Ordinal);
        var prompt = fake.Received.Last().Body;
        Assert.Contains("Arabic", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not recommend approving or rejecting", prompt, StringComparison.Ordinal);
        // Kept with the change.
        Assert.Contains("خادمًا", (string)(await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/changes/{id}/what-if"))["explanation"]!["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_predicted_risk_weighs_close_cohorts_and_falls_back_to_the_usual_rate()
    {
        var none = ChangeSimulator.Predict(new Dictionary<string, List<ChangeOutcome>>(), 0.15, ChangeRiskLevel.Medium);
        Assert.Equal((ChangeRiskLevel.Low, 15, false), (none.Level, none.FailureChance, none.History));
        Assert.Equal(ChangeRiskLevel.Medium, none.Questionnaire);

        var backedOut = ChangeSimulator.Predict(new Dictionary<string, List<ChangeOutcome>>
        {
            ["items"] = [ChangeOutcome.BackedOut, ChangeOutcome.Failed, ChangeOutcome.SuccessfulWithProblems],
            ["team"] = [ChangeOutcome.Successful, ChangeOutcome.Successful],
        }, 0.1, ChangeRiskLevel.Low);
        Assert.Equal(ChangeRiskLevel.High, backedOut.Level);
        var items = backedOut.Reasons.Single(r => r.Cohort == "items");
        Assert.Equal((3, 2, 1), (items.Changes, items.Failed, items.WithProblems));
        // (2.5 + 0.1 * 2) / (3 + 2) = 54 %.
        Assert.Equal(54, items.FailureRate);

        var calm = ChangeSimulator.Predict(new Dictionary<string, List<ChangeOutcome>>
        {
            ["class"] = [ChangeOutcome.Successful, ChangeOutcome.Successful, ChangeOutcome.Successful, ChangeOutcome.Successful],
        }, 0.15, ChangeRiskLevel.High);
        Assert.Equal(ChangeRiskLevel.Low, calm.Level);
    }

    [Fact]
    public void Words_compare_English_and_Arabic_forms()
    {
        Assert.Equal(ChangeSimulator.Words("ترقية خادم البوابة"), ChangeSimulator.Words("تَرقيةُ خادمِ بوابة"));
        Assert.Contains("بوابه", ChangeSimulator.Words("البوابة الإلكترونية"));
        Assert.Contains("server", ChangeSimulator.Words("Upgrade the web servers"));
        Assert.DoesNotContain("the", ChangeSimulator.Words("Upgrade the web servers"));
    }
}
