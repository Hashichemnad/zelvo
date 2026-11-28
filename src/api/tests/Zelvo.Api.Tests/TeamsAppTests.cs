using System.Net;
using System.Text.Json.Nodes;
using Zelvo.Api.Channels;
using Zelvo.Data.Channels;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>The Teams app of module 19 (task J4; decision 17): dialog, message action, commands, status cards and the app package, against PostgreSQL.</summary>
public sealed class TeamsAppTests : IAsyncDisposable
{
    private readonly DatabaseFixture _database;
    private readonly FakeMessagingClients _providers = new();
    private readonly ApiFactory _factory;

    public TeamsAppTests(DatabaseFixture database)
    {
        _database = database;
        _factory = ApiFactory.ForDatabase(database).WithMessaging(_providers).WithEmailSender(new CapturingEmailSender());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static JsonObject Invoke(string directory, string objectId, string userId, string name, JsonObject value, string conversationType = "personal")
    {
        var activity = MessagingApi.TeamsActivity(directory, objectId, userId, null, value, type: "invoke");
        activity["name"] = name;
        activity["conversation"]!["conversationType"] = conversationType;
        if (conversationType != "personal")
        {
            activity["conversation"]!["id"] = "19:channel@thread.tacv2";
        }

        return activity;
    }

    private static async Task<JsonObject> AnswerAsync(HttpClient microsoft, Guid channel, string appId, JsonObject activity)
    {
        using var response = await microsoft.TeamsAsync(channel, appId, activity);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
    }

    private static JsonObject CardOf(JsonObject answer) => answer["task"]!["value"]!["card"]!["content"]!.AsObject();

    private static IEnumerable<string> InputIds(JsonObject card) => card["body"]!.AsArray().Where(b => ((string?)b!["type"])!.StartsWith("Input.", StringComparison.Ordinal)).Select(b => (string)b!["id"]!);

    [Fact]
    public async Task The_dialog_raises_a_ticket_with_the_categorys_form_and_answers_with_a_status_card()
    {
        var tenant = await _database.CreateTenantAsync("j4dialog");
        var adminUser = await _database.CreateUserAsync(tenant, BuiltInRoles.TenantAdmin);
        using var admin = _factory.ForTenant(tenant).As(adminUser);
        var requesterUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        using var microsoft = _factory.ForTenant(tenant);
        var directory = Guid.NewGuid().ToString();
        var appId = Guid.NewGuid().ToString();
        var channel = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/messaging/channels", new
        {
            name = "Teams", kind = "Teams", provider = "TeamsBot", accountId = appId, directoryId = directory, credential = MessagingApi.Secret("teams"), newSenders = "KnownUsers",
        }, HttpStatusCode.Created)).Id();
        _providers.TeamsEmails["29:requester"] = requesterUser.Email;
        var person = Guid.NewGuid().ToString();
        await microsoft.TeamsAsync(channel, appId, MessagingApi.TeamsActivity(directory, person, "29:requester", null, type: "conversationUpdate"));

        // A category with a form: a dropdown and a required text the card can show, an optional file it cannot.
        var hardware = await admin.CategoryAsync("Hardware", "الأجهزة");
        var formId = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/forms", new
        {
            key = "hardware",
            name = new { en = "Hardware", ar = "الأجهزة" },
            schema = new
            {
                elements = new object[]
                {
                    new { type = "Field", field = new { key = "device", type = "Dropdown", label = new { en = "Device", ar = "الجهاز" }, required = true,
                        options = new[] { new { value = "laptop", label = new { en = "Laptop", ar = "حاسوب محمول" } }, new { value = "monitor", label = new { en = "Monitor", ar = "شاشة" } } } } },
                    new { type = "Field", field = new { key = "asset", type = "Text", label = new { en = "Asset tag", ar = "رقم الأصل" }, required = true } },
                    new { type = "Field", field = new { key = "photo", type = "File", label = new { en = "Photo", ar = "صورة" } } },
                },
            },
        }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{formId}/publish");
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/form-bindings", new { recordType = "Incident", categoryId = hardware, formId });

        // "Raise a ticket" is a command: a card with the dialog button, no ticket.
        await _factory.RunMessagingAsync(tenant);
        _providers.Sent.Clear();
        await microsoft.TeamsAsync(channel, appId, MessagingApi.TeamsActivity(directory, person, "29:requester", "Raise a ticket"));
        await _factory.RunMessagingAsync(tenant);
        var button = Assert.Single(_providers.Sent).Message;
        Assert.Equal(ChannelMessageKind.Card, button.Kind);
        Assert.Equal("task/fetch", (string?)JsonNode.Parse(button.Payload!)!["actions"]![0]!["data"]!["msteams"]!["type"]);
        Assert.Empty((await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets"))["items"]!.AsArray());

        // The dialog: title, details and the category.
        var first = CardOf(await AnswerAsync(microsoft, channel, appId, Invoke(directory, person, "29:requester", "task/fetch", new JsonObject { ["data"] = new JsonObject { ["zelvo"] = "raise" } })));
        Assert.Equal(["title", "description", "categoryId"], InputIds(first));
        Assert.Contains(first["body"]![2]!["choices"]!.AsArray(), c => (string?)c!["value"] == hardware.ToString());

        // Too short a title comes back with the reason.
        var tooShort = CardOf(await AnswerAsync(microsoft, channel, appId, Invoke(directory, person, "29:requester", "task/submit",
            new JsonObject { ["data"] = new JsonObject { ["zelvo"] = "raise", ["step"] = "ticket", ["title"] = "x" } })));
        Assert.Equal("Attention", (string?)tooShort["body"]![0]!["color"]);

        // The category's form: the fields a card can show, a note about the rest and the full form.
        var formCard = CardOf(await AnswerAsync(microsoft, channel, appId, Invoke(directory, person, "29:requester", "task/submit",
            new JsonObject { ["data"] = new JsonObject { ["zelvo"] = "raise", ["step"] = "ticket", ["title"] = "Laptop will not start", ["description"] = "Black screen", ["categoryId"] = hardware.ToString() } })));
        Assert.Equal(["f_device", "f_asset"], InputIds(formCard));
        Assert.Contains(formCard["actions"]!.AsArray(), a => (string?)a!["type"] == "Action.OpenUrl" && ((string)a["url"]!).Contains($"my-incidents/new?category={hardware}", StringComparison.Ordinal));

        // A missing required answer is refused by the form engine and asked again.
        var carry = formCard["actions"]![0]!["data"]!.AsObject();
        var missing = (JsonObject)carry.DeepClone();
        missing["f_device"] = "laptop";
        var again = CardOf(await AnswerAsync(microsoft, channel, appId, Invoke(directory, person, "29:requester", "task/submit", new JsonObject { ["data"] = missing })));
        Assert.Equal("Attention", (string?)again["body"]![0]!["color"]);
        Assert.Empty((await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets"))["items"]!.AsArray());

        var complete = (JsonObject)carry.DeepClone();
        complete["f_device"] = "laptop";
        complete["f_asset"] = "AST-1042";
        _providers.Sent.Clear();
        var done = await AnswerAsync(microsoft, channel, appId, Invoke(directory, person, "29:requester", "task/submit", new JsonObject { ["data"] = complete }));
        Assert.Equal("message", (string?)done["task"]!["type"]);
        var ticket = Assert.Single((await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets"))["items"]!.AsArray())!;
        Assert.Equal("Laptop will not start", (string?)ticket["title"]);
        Assert.Contains((string)ticket["number"]!, (string)done["task"]!["value"]!, StringComparison.Ordinal);
        var incident = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{(string)ticket["id"]!}");
        Assert.Equal(("Teams", hardware.ToString()), ((string?)incident["channel"], (string?)incident["category"]?["id"]));
        Assert.Contains("AST-1042", (await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{(string)ticket["id"]!}/form")).ToJsonString(), StringComparison.Ordinal);

        // The status card in the chat: number, state, the team (never a person) and a link.
        await _factory.RunMessagingAsync(tenant);
        var status = JsonNode.Parse(Assert.Single(_providers.Sent, s => s.Message.Kind == ChannelMessageKind.Card).Message.Payload!)!;
        Assert.Equal((string?)ticket["number"], (string?)status["body"]![0]!["text"]);
        Assert.Contains($"/en/my-incidents/{(string)ticket["id"]!}", (string)status["actions"]![0]!["url"]!, StringComparison.Ordinal);

        // "My tickets" lists the open tickets as status cards.
        _providers.Sent.Clear();
        await microsoft.TeamsAsync(channel, appId, MessagingApi.TeamsActivity(directory, person, "29:requester", "My tickets"));
        await _factory.RunMessagingAsync(tenant);
        Assert.Single(_providers.Sent, s => s.Message.Kind == ChannelMessageKind.Card && s.Message.Text!.StartsWith((string)ticket["number"]!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_message_action_raises_a_ticket_from_a_channel_message_for_the_person_who_used_it_and_others_are_refused()
    {
        var tenant = await _database.CreateTenantAsync("j4action");
        var adminUser = await _database.CreateUserAsync(tenant, BuiltInRoles.TenantAdmin);
        using var admin = _factory.ForTenant(tenant).As(adminUser);
        var requesterUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        await _database.ExecuteAsOwnerAsync("UPDATE users SET preferred_language = 'ar' WHERE id = @u", ("u", requesterUser.Id));
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        using var microsoft = _factory.ForTenant(tenant);
        var directory = Guid.NewGuid().ToString();
        var appId = Guid.NewGuid().ToString();
        var channel = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/messaging/channels", new
        {
            name = "Teams", kind = "Teams", provider = "TeamsBot", accountId = appId, directoryId = directory, credential = MessagingApi.Secret("teams"), newSenders = "KnownUsers",
        }, HttpStatusCode.Created)).Id();
        _providers.TeamsEmails["29:requester"] = requesterUser.Email;
        var person = Guid.NewGuid().ToString();

        // From a channel message: the dialog filled in with its text and a link back.
        var payload = new JsonObject
        {
            ["commandId"] = TeamsApp.MessageActionCommand,
            ["messagePayload"] = new JsonObject
            {
                ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = "<p>The printer on floor 3 jams</p><p>every morning &amp; evening</p>" },
                ["linkToMessage"] = "https://teams.microsoft.com/l/message/19:channel/1",
            },
        };
        var fetched = await AnswerAsync(microsoft, channel, appId, Invoke(directory, person, "29:requester", "composeExtension/fetchTask", payload, "channel"));
        var card = CardOf(fetched);
        Assert.True((bool)card["rtl"]!); // In the person's language.
        Assert.Equal("The printer on floor 3 jams", (string?)card["body"]![0]!["value"]);
        Assert.Equal("The printer on floor 3 jams\nevery morning & evening", (string?)card["body"]![1]!["value"]);
        var carry = (JsonObject)card["actions"]![0]!["data"]!.DeepClone();
        carry["title"] = "The printer on floor 3 jams";
        carry["description"] = "every morning & evening";
        var done = await AnswerAsync(microsoft, channel, appId, Invoke(directory, person, "29:requester", "composeExtension/submitAction", new JsonObject { ["data"] = carry }, "channel"));
        Assert.Equal("message", (string?)done["task"]!["type"]);
        var ticket = Assert.Single((await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets"))["items"]!.AsArray())!;
        var incident = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{(string)ticket["id"]!}");
        Assert.Contains("https://teams.microsoft.com/l/message/19:channel/1", (string?)incident["description"], StringComparison.Ordinal);

        // A channel conversation is never kept as the person's chat (notifications stay in their own chat).
        await using (var db = TestData.AppContext(_database, tenant))
        {
            var contact = db.MessagingContacts.Single(c => c.Address == person);
            Assert.Null(contact.TeamsConversationId);
        }

        // Someone Zelvo does not know is told, and nothing is raised; other directories are ignored.
        var stranger = await AnswerAsync(microsoft, channel, appId, Invoke(directory, Guid.NewGuid().ToString(), "29:stranger", "task/fetch", new JsonObject()));
        Assert.Equal("message", (string?)stranger["task"]!["type"]);
        using var foreign = await microsoft.TeamsAsync(channel, appId, Invoke(Guid.NewGuid().ToString(), person, "29:requester", "task/fetch", new JsonObject()));
        Assert.Equal("", await foreign.Content.ReadAsStringAsync(Ct));
        Assert.Single((await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets"))["items"]!.AsArray());
    }

    [Fact]
    public async Task The_app_package_has_the_tabs_commands_and_message_action_in_both_languages()
    {
        var tenant = await _database.CreateTenantAsync("j4pkg");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var appId = Guid.NewGuid().ToString();
        var channel = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/messaging/channels", new
        {
            name = "Teams", kind = "Teams", provider = "TeamsBot", accountId = appId, directoryId = Guid.NewGuid().ToString(), credential = MessagingApi.Secret("teams"),
        }, HttpStatusCode.Created)).Id();
        using var package = await admin.GetAsync(new Uri($"/api/v1/messaging/channels/{channel}/teams-app", UriKind.Relative), Ct);
        using var zip = new System.IO.Compression.ZipArchive(await package.Content.ReadAsStreamAsync(Ct));
        JsonNode Read(string name)
        {
            using var stream = zip.GetEntry(name)!.Open();
            return JsonNode.Parse(stream)!;
        }

        var manifest = Read("manifest.json");
        Assert.Equal("1.1.0", (string?)manifest["version"]);
        var tabs = manifest["staticTabs"]!.AsArray();
        Assert.Equal(["My tickets", "My work", "Approvals"], tabs.Select(t => (string?)t!["name"]));
        Assert.All(tabs, t => Assert.Matches(@"^http://localhost:3000/auth/teams/tab\?view=[a-z-]+&locale=\{locale\}$", (string)t!["contentUrl"]!));
        Assert.Equal(["Raise a ticket", "My tickets", "Help"], manifest["bots"]![0]!["commandLists"]![0]!["commands"]!.AsArray().Select(c => (string?)c!["title"]));
        var action = manifest["composeExtensions"]![0]!;
        Assert.Equal((appId, TeamsApp.MessageActionCommand, "message"), ((string?)action["botId"], (string?)action["commands"]![0]!["id"], (string?)action["commands"]![0]!["context"]![0]));

        // The Arabic names are what the commands answer to.
        var arabic = Read("ar.json");
        Assert.Equal("رفع طلب", (string?)arabic["bots[0].commandLists[0].commands[0].title"]);
        Assert.Equal(TeamsCommand.Raise, TeamsApp.CommandOf((string?)arabic["bots[0].commandLists[0].commands[0].title"]));
        Assert.Equal(TeamsCommand.Status, TeamsApp.CommandOf((string?)arabic["bots[0].commandLists[0].commands[1].title"]));
        Assert.Equal("طلباتي", (string?)arabic["staticTabs[0].name"]);
        Assert.Equal("رفع طلب", (string?)arabic["composeExtensions[0].commands[0].title"]);
    }
}
