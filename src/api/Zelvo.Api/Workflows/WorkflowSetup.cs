using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Workflows.Approvals;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Workflows;

/// <summary>Registers the API side of workflows (task C3).</summary>
internal static class WorkflowSetup
{
    public static IServiceCollection AddZelvoApiWorkflows(this IServiceCollection services)
    {
        services.AddScoped<ApprovalService>();
        services.AddSingleton<IMissingTranslationSource, WorkflowMissingTranslations>();
        return services;
    }

    public static RouteGroupBuilder MapZelvoWorkflowEndpoints(this RouteGroupBuilder group) =>
        group.MapPlainLanguageRuleEndpoints().MapWorkflowRuleEndpoints().MapApprovalEndpoints();
}

/// <summary>
/// Reports workflow wording that misses a language (LANG-5): rule names and descriptions, the
/// subjects and messages of notify actions and the names of approval levels.
/// </summary>
internal sealed class WorkflowMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var missing = new List<MissingTranslation>();
        void Add(Guid id, string field, LocalizedText text)
        {
            if (text.HasAnyValue && !text.IsComplete)
            {
                missing.Add(new MissingTranslation("workflow_rule", id.ToString(), field, text.MissingLanguages));
            }
        }

        foreach (var rule in await db.WorkflowRules.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!rule.Name.IsComplete)
            {
                missing.Add(new MissingTranslation("workflow_rule", rule.Id.ToString(), "name", rule.Name.MissingLanguages));
            }

            Add(rule.Id, "description", rule.Description);
            var actions = RuleDocuments.ReadActions(rule.Actions);
            for (var i = 0; i < actions.Count; i++)
            {
                switch (actions[i])
                {
                    case NotifyAction notify:
                        Add(rule.Id, $"actions.{i}.subject", notify.Subject);
                        Add(rule.Id, $"actions.{i}.body", notify.Body);
                        break;
                    case ApprovalAction approval:
                        for (var j = 0; j < approval.Levels.Count; j++)
                        {
                            Add(rule.Id, $"actions.{i}.levels.{j}.name", approval.Levels[j].Name);
                        }

                        break;
                }
            }
        }

        return missing;
    }
}
