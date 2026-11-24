using System.Text.Json;
using System.Text.Json.Nodes;
using Zelvo.Data.Localization;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Tests;

/// <summary>Task C1: format checks, rules, answer checks and form definition checks, without a database.</summary>
public sealed class FormEngineTests
{
    private static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "form-cases.json")))!;

    public static TheoryData<string, string, string?> FormatCases()
    {
        var data = new TheoryData<string, string, string?>();
        foreach (var c in Cases["formats"]!.AsArray())
        {
            data.Add(c!["format"]!.GetValue<string>(), c["input"]!.GetValue<string>(), c["expected"]?.GetValue<string>());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void Formats_match_the_shared_cases(string format, string input, string? expected) =>
        Assert.Equal(expected, FieldFormats.Normalize(Enum.Parse<FieldFormat>(format), input));

    [Fact]
    public void Rules_match_the_shared_cases()
    {
        var checkedCases = 0;
        foreach (var group in Cases["rules"]!.AsArray())
        {
            var fields = group!["fields"].Deserialize<List<FormField>>(FormJson.Options)!;
            Assert.Empty(FormDefinitionCheck.Check(fields));
            foreach (var c in group["cases"]!.AsArray())
            {
                var states = FormRules.Evaluate(fields, (JsonObject)c!["answers"]!.DeepClone());
                foreach (var (key, expected) in c["expected"]!.AsObject())
                {
                    var e = expected!.AsArray().Select(x => x!.GetValue<bool>()).ToArray();
                    var actual = states[key];
                    Assert.True(e[0] == actual.Visible && e[1] == actual.Required && e[2] == actual.ReadOnly,
                        $"{group["name"]}: {c["answers"]!.ToJsonString()} → {key} was {actual}, expected [{string.Join(", ", e)}]");
                    checkedCases++;
                }
            }
        }

        Assert.True(checkedCases > 15);
    }

    private static FormField Field(string key, FieldType type, Func<FormField, FormField>? configure = null)
    {
        var field = new FormField { Key = key, Type = type, Label = new LocalizedText("Label " + key, "تسمية " + key) };
        return configure?.Invoke(field) ?? field;
    }

    private static readonly FieldOption[] Sizes = [new("24", new("24 inch", "24 بوصة")), new("27", new("27 inch", "27 بوصة"))];

    [Fact]
    public void Answers_are_checked_cleaned_and_personal_data_kept_apart()
    {
        IReadOnlyList<FormField> fields =
        [
            Field("name", FieldType.Text, f => f with { Required = true, Validation = new FieldValidation { MaxLength = 10 } }),
            Field("eid", FieldType.Text, f => f with { Validation = new FieldValidation { Format = FieldFormat.EmiratesId } }),
            Field("count", FieldType.Number, f => f with { Validation = new FieldValidation { Min = 1, Max = 5 } }),
            Field("sizes", FieldType.Dropdown, f => f with { Multiple = true, Options = Sizes }),
            Field("when", FieldType.Date, f => f with { Validation = new FieldValidation { MinDate = "2026-01-01" } }),
            Field("agree", FieldType.Checkbox, f => f with { Required = true }),
        ];

        var ok = FormAnswers.Check(fields, (JsonObject)JsonNode.Parse("""
            {"name": "  Sara ", "eid": "784-1990-1234567-6", "count": "3", "sizes": ["27", "24", "27"], "when": "2026-02-01", "agree": true}
            """)!, []);
        Assert.Empty(ok.Errors);
        Assert.Equal("Sara", ok.Answers["name"]!.GetValue<string>());
        Assert.Equal(3m, ok.Answers["count"]!.GetValue<decimal>());
        Assert.Equal("""["24","27"]""", ok.Answers["sizes"]!.ToJsonString());
        Assert.False(ok.Answers.ContainsKey("eid"));
        Assert.Equal("784199012345676", ok.PersonalAnswers["eid"]!.GetValue<string>());

        var bad = FormAnswers.Check(fields, (JsonObject)JsonNode.Parse("""
            {"name": "much too long a name", "eid": "784199012345675", "count": 9, "sizes": ["30"], "when": "2025-12-31", "agree": false, "extra": 1}
            """)!, []);
        Assert.Equal(
            ["agree:required", "count:max", "eid:format-emirates-id", "extra:field-unknown", "name:max-length", "sizes:option-unknown", "when:min-date"],
            bad.Errors.Select(e => $"{e.Field}:{e.Code}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Hidden_answers_are_dropped_and_read_only_fields_keep_their_default()
    {
        IReadOnlyList<FormField> fields =
        [
            Field("addMonitor", FieldType.Checkbox),
            Field("monitorSize", FieldType.Dropdown, f => f with
            {
                Options = Sizes,
                Required = true,
                Rules = [new FieldRule(RuleAction.Show, RuleMatch.All, [new RuleCondition("addMonitor", ConditionOperator.Checked)])],
            }),
            Field("department", FieldType.Text, f => f with { ReadOnly = true, ProfileDefault = ProfileSource.Department }),
        ];
        var defaults = new JsonObject { ["department"] = "Finance" };

        var hidden = FormAnswers.Check(fields, new JsonObject { ["monitorSize"] = "27", ["department"] = "Something else" }, defaults);
        Assert.Empty(hidden.Errors);
        Assert.False(hidden.Answers.ContainsKey("monitorSize"));
        Assert.Equal("Finance", hidden.Answers["department"]!.GetValue<string>());

        var shown = FormAnswers.Check(fields, new JsonObject { ["addMonitor"] = true }, defaults);
        Assert.Equal(["monitorSize:required"], shown.Errors.Select(e => $"{e.Field}:{e.Code}"));
    }

    [Fact]
    public void Files_users_locations_signatures_and_tables_are_checked()
    {
        var upload = Guid.NewGuid();
        var drawing = Guid.NewGuid();
        var person = Guid.NewGuid();
        IReadOnlyList<FormField> fields =
        [
            Field("files", FieldType.File, f => f with { Validation = new FieldValidation { MaxFiles = 1 } }),
            Field("who", FieldType.User),
            Field("where", FieldType.Location),
            Field("drawn", FieldType.Signature),
            Field("typed", FieldType.Signature),
            Field("items", FieldType.Table, f => f with
            {
                MinRows = 1,
                Columns = [Field("item", FieldType.Text, c => c with { Required = true }), Field("qty", FieldType.Number, c => c with { Validation = new FieldValidation { Min = 1 } })],
            }),
        ];

        var ok = FormAnswers.Check(fields, (JsonObject)JsonNode.Parse($$"""
            {"files": ["{{upload}}"], "who": "{{person}}", "where": "{{Guid.Empty}}", "drawn": {"attachmentId": "{{drawing}}"},
             "typed": {"typedName": "Sara Ali", "confirmed": true}, "items": [{"item": "Mouse", "qty": 2}]}
            """)!, []);
        Assert.Empty(ok.Errors);
        Assert.Equal([upload, drawing], ok.Files.Select(f => f.AttachmentId));
        Assert.True(ok.Files[1].Signature);
        Assert.Equal(person, ok.Users.Single().Id);
        Assert.Single(ok.Locations);
        // Signatures are always personal data.
        Assert.True(ok.PersonalAnswers.ContainsKey("drawn") && ok.PersonalAnswers.ContainsKey("typed"));

        var bad = FormAnswers.Check(fields, (JsonObject)JsonNode.Parse($$"""
            {"files": ["{{upload}}", "{{drawing}}"], "who": "nobody", "typed": {"typedName": "Sara", "confirmed": false},
             "items": [{"qty": 0, "colour": "red"}]}
            """)!, []);
        Assert.Equal(
            ["files:max-files", "items.0.colour:field-unknown", "items.0.item:required", "items.0.qty:min", "typed:signature-incomplete", "who:type"],
            bad.Errors.Select(e => $"{e.Field}:{e.Code}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Patterns_must_match_the_whole_answer_and_bad_patterns_are_refused()
    {
        IReadOnlyList<FormField> fields = [Field("code", FieldType.Text, f => f with { Validation = new FieldValidation { Pattern = "[A-Z]{3}-[0-9]{3}" } })];
        Assert.Empty(FormAnswers.Check(fields, new JsonObject { ["code"] = "ABC-123" }, []).Errors);
        Assert.Equal("pattern", FormAnswers.Check(fields, new JsonObject { ["code"] = "xABC-1234" }, []).Errors.Single().Code);

        var problems = FormDefinitionCheck.Check([Field("code", FieldType.Text, f => f with { Validation = new FieldValidation { Pattern = "([a-z" } })]);
        Assert.Contains(problems, p => p.Code == "pattern-invalid");
    }

    [Fact]
    public void Definition_problems_are_found()
    {
        var problems = FormDefinitionCheck.Check(
        [
            Field("Bad key", FieldType.Text),
            Field("size", FieldType.Dropdown),
            Field("size", FieldType.Text, f => f with { Multiple = true }),
            Field("a", FieldType.Text, f => f with { Rules = [new FieldRule(RuleAction.Show, RuleMatch.All, [new RuleCondition("missing", ConditionOperator.NotEmpty)])] }),
            Field("b", FieldType.Number, f => f with { DefaultValue = JsonValue.Create("x"), ProfileDefault = ProfileSource.Manager }),
            Field("t", FieldType.Table),
        ]);
        Assert.Equal(
            ["Bad key:key-invalid", "a:rule-field-unknown", "b:default-invalid", "b:profile-default-type", "size:key-duplicate", "size:multiple-not-allowed", "size:options-required", "t:columns-required"],
            problems.Select(p => $"{p.Field}:{p.Code}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Rule_cycles_and_wrong_comparisons_are_found()
    {
        FieldRule ShowWhen(string other) => new(RuleAction.Show, RuleMatch.All, [new RuleCondition(other, ConditionOperator.NotEmpty)]);
        var cycle = FormDefinitionCheck.Check(
        [
            Field("a", FieldType.Text, f => f with { Rules = [ShowWhen("b")] }),
            Field("b", FieldType.Text, f => f with { Rules = [ShowWhen("c")] }),
            Field("c", FieldType.Text, f => f with { Rules = [ShowWhen("a")] }),
        ]);
        Assert.Equal(["rule-cycle"], cycle.Select(p => p.Code));

        var wrong = FormDefinitionCheck.Check(
        [
            Field("text", FieldType.Text),
            Field("size", FieldType.Dropdown, f => f with { Options = Sizes }),
            Field("x", FieldType.Text, f => f with
            {
                Rules =
                [
                    new FieldRule(RuleAction.Require, RuleMatch.Any,
                    [
                        new RuleCondition("text", ConditionOperator.Checked),
                        new RuleCondition("text", ConditionOperator.GreaterThan, "3"),
                        new RuleCondition("size", ConditionOperator.Equals, "32"),
                        new RuleCondition("size", ConditionOperator.Equals),
                        new RuleCondition("x", ConditionOperator.Empty),
                    ]),
                ],
            }),
        ]);
        Assert.Equal(["rule-operator", "rule-operator", "rule-option-unknown", "rule-option-unknown", "rule-self", "rule-value-required"],
            wrong.Select(p => p.Code).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Schemas_round_trip_as_camel_case_json_with_enum_names()
    {
        var schema = new FormSchema
        {
            Elements =
            [
                new FormElement { Type = FormElementType.Field, Field = Field("addMonitor", FieldType.Checkbox) },
                new FormElement { Type = FormElementType.FieldSet, FieldSetId = Guid.Empty },
            ],
        };
        var json = FormJson.Write(schema);
        Assert.Contains("\"type\":\"Checkbox\"", json, StringComparison.Ordinal);
        Assert.Contains("\"label\":{\"en\":\"Label addMonitor\"", json, StringComparison.Ordinal);
        Assert.Equal(json, FormJson.Write(FormJson.ReadForm(json)));
        Assert.Null(FormJson.ReadForm("{\"elements\": 5}"));
    }
}
