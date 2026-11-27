using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Tickets.Forms;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Workflows;

/// <summary>
/// Checks that what rules and approval levels refer to exists (task C3): services, categories,
/// locations, groups, departments, customer groups (task K1), users and roles. The shape is checked by <see cref="RuleDocuments"/>.
/// </summary>
internal static class WorkflowReferences
{
    /// <summary>Adds a problem for each condition whose ids do not exist.</summary>
    public static async Task CheckConditionsAsync(ZelvoDbContext db, IReadOnlyList<WorkflowCondition> conditions, string at, List<FormProblem> problems, CancellationToken cancellationToken)
    {
        for (var i = 0; i < conditions.Count; i++)
        {
            var c = conditions[i];
            if (!RuleFields.IdLists.Contains(c.Field) || RuleDocuments.Values(c.Value) is not { } values)
            {
                continue;
            }

            var ids = values.Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
            if (ids.Count == 0)
            {
                continue;
            }

            IQueryable<Guid> known = c.Field switch
            {
                RuleFields.Service => db.CatalogServices.Select(s => s.Id),
                RuleFields.Category => db.Categories.Select(s => s.Id),
                RuleFields.Location => db.Locations.Select(s => s.Id),
                RuleFields.Group => db.SupportGroups.Select(s => s.Id),
                RuleFields.RequesterCustomerGroup => db.CustomerGroups.Select(s => s.Id),
                _ => db.Departments.Select(s => s.Id),
            };
            if (await known.CountAsync(k => ids.Contains(k), cancellationToken) != ids.Count)
            {
                problems.Add(new($"{at}.{i}.value", "reference-unknown", "Something chosen here no longer exists."));
            }
        }
    }

    /// <summary>Adds a problem for each approver that does not exist.</summary>
    public static async Task CheckLevelsAsync(ZelvoDbContext db, IReadOnlyList<ApprovalLevelDefinition> levels, string at, List<FormProblem> problems, CancellationToken cancellationToken)
    {
        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            for (var j = 0; j < level.Approvers.Count; j++)
            {
                var approver = level.Approvers[j];
                var ok = approver.Kind switch
                {
                    ApproverKind.User when approver.Id is { } user => await db.Users.AnyAsync(u => u.Id == user, cancellationToken),
                    ApproverKind.Group or ApproverKind.GroupLeads when approver.Id is { } group => await db.SupportGroups.AnyAsync(g => g.Id == group, cancellationToken),
                    ApproverKind.Role when approver.Key is { } role => await db.Roles.AnyAsync(r => r.Key == role, cancellationToken),
                    _ => true,
                };
                if (!ok)
                {
                    problems.Add(new($"{at}.{i}.approvers.{j}", "approver-unknown", "The approver no longer exists."));
                }
            }

            await CheckConditionsAsync(db, level.Conditions, $"{at}.{i}.conditions", problems, cancellationToken);
        }
    }

    /// <summary>Adds a problem for each action whose references do not exist.</summary>
    public static async Task CheckActionsAsync(ZelvoDbContext db, IReadOnlyList<WorkflowAction> actions, string at, List<FormProblem> problems, CancellationToken cancellationToken)
    {
        for (var i = 0; i < actions.Count; i++)
        {
            switch (actions[i])
            {
                case AssignAction assign:
                    if (assign.GroupId is not { } group || !await db.SupportGroups.AnyAsync(g => g.Id == group && g.Active, cancellationToken))
                    {
                        problems.Add(new($"{at}.{i}.groupId", "group-unknown", "Choose an active group."));
                    }
                    else if (assign.AssigneeId is { } member && !await db.SupportGroupMembers.AnyAsync(m => m.GroupId == group && m.UserId == member, cancellationToken))
                    {
                        problems.Add(new($"{at}.{i}.assigneeId", "not-a-member", "The assignee is not a member of the group."));
                    }

                    break;
                case NotifyAction notify:
                    foreach (var recipient in notify.Recipients)
                    {
                        var ok = recipient switch
                        {
                            { } r when r.StartsWith("user:", StringComparison.Ordinal) && Guid.TryParse(r[5..], out var u) => await db.Users.AnyAsync(x => x.Id == u, cancellationToken),
                            { } r when r.StartsWith("group:", StringComparison.Ordinal) && Guid.TryParse(r[6..], out var g) => await db.SupportGroups.AnyAsync(x => x.Id == g, cancellationToken),
                            { } r when r.StartsWith("groupLeads:", StringComparison.Ordinal) && Guid.TryParse(r[11..], out var l) => await db.SupportGroups.AnyAsync(x => x.Id == l, cancellationToken),
                            { } r when r.StartsWith("role:", StringComparison.Ordinal) => await db.Roles.AnyAsync(x => x.Key == r.Substring(5), cancellationToken),
                            _ => true,
                        };
                        if (!ok)
                        {
                            problems.Add(new($"{at}.{i}.recipients", "recipient-unknown", "A recipient no longer exists."));
                        }
                    }

                    break;
                case ApprovalAction approval:
                    await CheckLevelsAsync(db, approval.Levels, $"{at}.{i}.levels", problems, cancellationToken);
                    break;
                case ConnectorAction { ConnectorId: { } connectorId } connector:
                    var target = await db.Connectors.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectorId && c.Kind == Data.Connectors.ConnectorKind.Rest, cancellationToken);
                    if (target is null)
                    {
                        problems.Add(new($"{at}.{i}.connectorId", "connector-unknown", "Choose one of the REST connectors."));
                    }
                    else if (Zelvo.Connectors.ConnectorSettings.Read(target.Settings).Rest?.Operations.Any(o => o.Key == connector.Operation) != true)
                    {
                        problems.Add(new($"{at}.{i}.operation", "operation-unknown", "Choose one of the connector's operations."));
                    }

                    break;
            }
        }
    }
}
