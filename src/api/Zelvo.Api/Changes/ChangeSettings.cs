using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data.Changes;
using Zelvo.Data.Localization;
using Zelvo.Data.Workflows;
using Zelvo.Tickets.Forms;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Changes;

/// <summary>An answer of a risk question, with its weight in the score.</summary>
public sealed record RiskOption(string Key, LocalizedText Text, int Weight);

/// <summary>A question of the risk and impact questionnaire (task H3). Inactive questions are no longer asked; answers already given stay.</summary>
public sealed record RiskQuestion(string Key, LocalizedText Text, IReadOnlyList<RiskOption> Options, bool Active);

/// <summary>
/// The approval path per risk level (product owner decision 7 of 2026-11-26), as C3 approval
/// levels. Standard changes are pre-approved and have no path.
/// </summary>
public sealed record ChangeApprovalPaths(
    IReadOnlyList<ApprovalLevelDefinition> Low,
    IReadOnlyList<ApprovalLevelDefinition> Medium,
    IReadOnlyList<ApprovalLevelDefinition> High,
    IReadOnlyList<ApprovalLevelDefinition> Emergency)
{
    /// <summary>The path of a normal change of <paramref name="level"/>, or of an emergency change.</summary>
    public IReadOnlyList<ApprovalLevelDefinition> For(ChangeKind kind, ChangeRiskLevel level) => kind switch
    {
        ChangeKind.Standard => [],
        ChangeKind.Emergency => Emergency,
        _ => level switch
        {
            ChangeRiskLevel.High => High,
            ChangeRiskLevel.Medium => Medium,
            _ => Low,
        },
    };
}

/// <summary>
/// Change management settings of a tenant (Settings → Changes, task H3): the risk and impact
/// questionnaire, the scores at which a change becomes medium and high risk, and the approval path
/// of each risk level and of emergency changes.
/// </summary>
public sealed partial record ChangeSettingsValue(IReadOnlyList<RiskQuestion> Questions, int MediumFrom, int HighFrom, ChangeApprovalPaths Paths)
{
    /// <summary>Most questions.</summary>
    public const int MaxQuestions = 20;

    /// <summary>Most options of a question.</summary>
    public const int MaxOptions = 6;

    /// <summary>Most levels of one approval path.</summary>
    public const int MaxLevels = 5;

    /// <summary>Longest question or option text, per language.</summary>
    public const int TextMaxLength = 300;

    /// <summary>Highest weight of an option.</summary>
    public const int MaxWeight = 100;

    /// <summary>Role key of the executives (the high-risk path's second level).</summary>
    public const string ExecutiveRole = Data.Identity.BuiltInRoles.ExecutiveAuditor;

    /// <summary>The change manager: holders of <c>changes.manage</c>.</summary>
    public static ApprovalLevelDefinition ChangeManagerLevel { get; } = new()
    {
        Name = new("Change manager", "مدير التغيير"),
        Mode = ApprovalMode.Any,
        Approvers = [new ApproverRef { Kind = ApproverKind.Permission, Key = Zelvo.Tickets.ChangePermissions.Manage }],
    };

    /// <summary>The change advisory board (task H4): decided by its chair in a meeting, after the members' votes.</summary>
    public static ApprovalLevelDefinition BoardLevel { get; } = new()
    {
        Name = new("Change advisory board", "المجلس الاستشاري للتغيير"),
        Mode = ApprovalMode.Any,
        Approvers = [new ApproverRef { Kind = ApproverKind.ChangeBoard }],
    };

    /// <summary>An executive.</summary>
    public static ApprovalLevelDefinition ExecutiveLevel { get; } = new()
    {
        Name = new("Executive", "الإدارة التنفيذية"),
        Mode = ApprovalMode.Any,
        Approvers = [new ApproverRef { Kind = ApproverKind.Role, Key = ExecutiveRole }],
    };

    /// <summary>The emergency board (task H4): its members decide without a meeting, within hours.</summary>
    public static ApprovalLevelDefinition EmergencyLevel { get; } = new()
    {
        Name = new("Emergency change board", "مجلس التغييرات الطارئة"),
        Mode = ApprovalMode.Any,
        Approvers = [new ApproverRef { Kind = ApproverKind.EmergencyBoard }],
    };

    /// <summary>
    /// The defaults: five questions with weights, medium risk from 5 and high risk from 10, and the
    /// paths of decision 7 (low: the change manager; medium: the board; high: the board and an
    /// executive; emergency: the emergency board).
    /// </summary>
    public static ChangeSettingsValue Default { get; } = new(
    [
        new("reach", new("How many people does the change affect?", "كم عدد الأشخاص الذين يتأثرون بالتغيير؟"),
        [
            new("few", new("A few people or one team", "عدد قليل أو فريق واحد"), 0),
            new("department", new("A department or one service", "إدارة أو خدمة واحدة"), 2),
            new("organisation", new("The whole organisation or the public", "الجهة كلها أو الجمهور"), 4),
        ], true),
        new("experience", new("Has this kind of change been done before?", "هل سبق تنفيذ تغيير مماثل؟"),
        [
            new("often", new("Yes, often", "نعم، كثيرًا"), 0),
            new("sometimes", new("Once or twice", "مرة أو مرتين"), 1),
            new("never", new("Never", "لم يسبق"), 3),
        ], true),
        new("downtime", new("Does the change interrupt a service?", "هل يؤدي التغيير إلى انقطاع خدمة؟"),
        [
            new("none", new("No interruption", "لا انقطاع"), 0),
            new("window", new("Within the agreed window", "خلال النافذة المتفق عليها"), 2),
            new("business", new("During working hours", "خلال ساعات العمل"), 4),
        ], true),
        new("backout", new("How easy is it to undo?", "ما مدى سهولة التراجع عن التغيير؟"),
        [
            new("easy", new("Easy and quick", "سهل وسريع"), 0),
            new("hard", new("Possible but slow", "ممكن لكنه بطيء"), 2),
            new("impossible", new("Not possible", "غير ممكن"), 4),
        ], true),
        new("testing", new("Has the change been tested?", "هل جرى اختبار التغيير؟"),
        [
            new("full", new("Fully, in a test environment", "بالكامل في بيئة اختبار"), 0),
            new("partial", new("Partly", "جزئيًا"), 2),
            new("none", new("Not tested", "لم يُختبر"), 4),
        ], true),
    ],
    5,
    10,
    new ChangeApprovalPaths([ChangeManagerLevel], [BoardLevel], [BoardLevel, ExecutiveLevel], [EmergencyLevel]));

    /// <summary>Everything wrong with the settings.</summary>
    public List<FormProblem> Problems()
    {
        var problems = new List<FormProblem>();
        if (Questions is null || Questions.Count > MaxQuestions)
        {
            problems.Add(new("questions", "questions-invalid", $"Ask at most {MaxQuestions} questions."));
            return problems;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Questions.Count; i++)
        {
            var q = Questions[i];
            var at = $"questions.{i}";
            if (q is null || !IsKey(q.Key) || !keys.Add(q.Key))
            {
                problems.Add(new($"{at}.key", "question-key-invalid", "Give each question its own key (lower-case letters, digits and hyphens)."));
                continue;
            }

            if (!IsText(q.Text))
            {
                problems.Add(new($"{at}.text", "text-invalid", $"Word the question in English or Arabic, at most {TextMaxLength} characters each."));
            }

            if (q.Options is not { Count: >= 2 and <= MaxOptions })
            {
                problems.Add(new($"{at}.options", "options-invalid", $"Give 2 to {MaxOptions} answers."));
                continue;
            }

            var optionKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < q.Options.Count; j++)
            {
                var o = q.Options[j];
                if (o is null || !IsKey(o.Key) || !optionKeys.Add(o.Key) || !IsText(o.Text) || o.Weight is < 0 or > MaxWeight)
                {
                    problems.Add(new($"{at}.options.{j}", "option-invalid", $"Give each answer its own key, its wording and a weight of 0 to {MaxWeight}."));
                }
            }
        }

        if (MediumFrom < 1 || HighFrom <= MediumFrom || HighFrom > MaxQuestions * MaxWeight)
        {
            problems.Add(new("mediumFrom", "thresholds-invalid", "Medium risk starts at a score of 1 or more, and high risk at a higher score."));
        }

        if (Paths is null)
        {
            problems.Add(new("paths", "paths-invalid", "Give the approval paths."));
            return problems;
        }

        foreach (var (name, levels) in new[] { ("low", Paths.Low), ("medium", Paths.Medium), ("high", Paths.High), ("emergency", Paths.Emergency) })
        {
            if (levels is null || levels.Count > MaxLevels)
            {
                problems.Add(new($"paths.{name}", "levels-invalid", $"Use at most {MaxLevels} levels."));
                continue;
            }

            RuleDocuments.CheckLevels(levels, $"paths.{name}", problems);
        }

        return problems;
    }

    /// <summary>Whether the settings can be used.</summary>
    public bool IsValid() => Problems().Count == 0;

    /// <summary>Score and level of <paramref name="answers"/>; the active questions not answered are listed.</summary>
    public (int Score, ChangeRiskLevel Level, IReadOnlyList<string> Unanswered) Assess(IReadOnlyDictionary<string, string> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var score = 0;
        var unanswered = new List<string>();
        foreach (var question in Questions)
        {
            if (answers.TryGetValue(question.Key, out var chosen) && question.Options.FirstOrDefault(o => o.Key == chosen) is { } option)
            {
                score += option.Weight;
            }
            else if (question.Active)
            {
                unanswered.Add(question.Key);
            }
        }

        return (score, LevelOf(score), unanswered);
    }

    /// <summary>The level of a score.</summary>
    public ChangeRiskLevel LevelOf(int score) => score >= HighFrom ? ChangeRiskLevel.High : score >= MediumFrom ? ChangeRiskLevel.Medium : ChangeRiskLevel.Low;

    /// <summary>Whether <paramref name="key"/> is a valid question or answer key.</summary>
    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    private static bool IsText(LocalizedText? text) =>
        text is not null && text.HasAnyValue && (text.En?.Length ?? 0) <= TextMaxLength && (text.Ar?.Length ?? 0) <= TextMaxLength;

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex KeyPattern();
}

/// <summary>Settings → Changes.</summary>
internal static class ChangeSettings
{
    public static SettingDefinition<ChangeSettingsValue> Changes { get; } =
        new("changes.settings", ChangeSettingsValue.Default, Permissions.ChangesManage, v => v.IsValid());

    public static RouteGroupBuilder MapChangeSettingsEndpoints(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/settings/changes").WithTags("Settings").RequireTenant();
        settings.MapGet("/", GetAsync).WithName("GetChangeSettings")
            .WithSummary("Change management settings: the risk and impact questionnaire with weights, the scores at which risk is medium and high, and the approval path of each risk level and of emergency changes. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<ChangeSettingsValue>();
        settings.MapPut("/", UpdateAsync).WithName("UpdateChangeSettings")
            .WithSummary("Replace the change management settings. Changes already assessed keep their score. Needs changes.manage; audited.")
            .RequirePermission(Permissions.ChangesManage).Produces<ChangeSettingsValue>().ProducesValidationProblem();
        return group;
    }

    internal static async Task<Ok<ChangeSettingsValue>> GetAsync(TenantSettingsStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok(await store.GetAsync(Changes, cancellationToken));

    internal static async Task<Results<Ok<ChangeSettingsValue>, ValidationProblem>> UpdateAsync(
        ChangeSettingsValue request, TenantSettingsStore store, Data.ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var value = Normalise(request);
        if (value.Problems() is { Count: > 0 } problems)
        {
            return TypedResults.ValidationProblem(problems.GroupBy(p => p.Field ?? "settings")
                .ToDictionary(g => g.Key, g => g.Select(p => p.Message).Distinct().ToArray()));
        }

        await store.SetAsync(Changes, value, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(value);
    }

    private static ChangeSettingsValue Normalise(ChangeSettingsValue value) => value with
    {
        Questions = [.. (value.Questions ?? []).Select(q => q is null ? null! : q with
        {
            Key = q.Key?.Trim() ?? "",
            Text = Trim(q.Text),
            Options = [.. (q.Options ?? []).Select(o => o is null ? null! : o with { Key = o.Key?.Trim() ?? "", Text = Trim(o.Text) })],
        })],
    };

    private static LocalizedText Trim(LocalizedText? text) =>
        new(string.IsNullOrWhiteSpace(text?.En) ? null : text.En.Trim(), string.IsNullOrWhiteSpace(text?.Ar) ? null : text.Ar.Trim());
}
