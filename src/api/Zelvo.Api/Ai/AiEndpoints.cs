using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Zelvo.Ai;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Ai;
using Zelvo.Data.Auditing;
using Zelvo.Secrets;

namespace Zelvo.Api.Ai;

/// <summary>
/// Settings → AI (module 9): provider connections with encrypted keys and a test button, the
/// provider and model of each feature, model prices, the monthly spending limit, the usage and
/// cost dashboard and the AI log. AI stays off until an administrator sets it up.
/// </summary>
internal static class AiEndpoints
{
    /// <summary>The monthly limit as a tenant setting (US dollars; 0 = no limit).</summary>
    public static SettingDefinition<decimal> MonthlyLimit { get; } =
        new(AiBudget.MonthlyLimitKey, 0m, Permissions.AiManage, AiBudget.IsLimit);

    public static IServiceCollection AddZelvoApiAi(this IServiceCollection services, IConfiguration configuration) =>
        services.AddZelvoAi(configuration);

    public static RouteGroupBuilder MapAiEndpoints(this RouteGroupBuilder group)
    {
        var ai = group.MapGroup("/ai").WithTags("AI").RequireTenant();

        ai.MapGet("/status", StatusAsync)
            .WithName("GetAiStatus")
            .WithSummary("Which AI features can be used now: on, with an active provider and a model, and the monthly limit not reached. No configuration details. For every signed-in user and integration (screens show AI buttons only when a feature is available).")
            .RequireAuthorization()
            .Produces<AiStatusResponse>();

        var providers = ai.MapGroup("/providers");
        providers.MapGet("/", ListProvidersAsync)
            .WithName("ListAiProviders")
            .WithSummary("AI provider connections of the tenant. Keys are never returned, only whether one is set and when it was last changed. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<IReadOnlyList<AiProviderResponse>>();

        providers.MapGet("/{id:guid}", GetProviderAsync)
            .WithName("GetAiProvider")
            .WithSummary("One AI provider connection. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<AiProviderResponse>();

        providers.MapPost("/", CreateProviderAsync)
            .WithName("CreateAiProvider")
            .WithSummary("Connects a provider: Anthropic, OpenAI, AzureOpenAI, Gemini or OpenAICompatible (any endpoint that speaks the OpenAI Chat Completions API, including a model the customer hosts). Azure OpenAI and OpenAI-compatible need an endpoint; the key is stored encrypted and is optional only for OpenAI-compatible endpoints. Internal addresses need the installation's permission (Ai:AllowedPrivateNetworks). Needs ai.manage; audited without the key.")
            .RequirePermission(Permissions.AiManage)
            .Produces<AiProviderResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        providers.MapPatch("/{id:guid}", UpdateProviderAsync)
            .WithName("UpdateAiProvider")
            .WithSummary("Changes a provider's name, endpoint, Azure api-version or key, or switches it off and on (Disabled stops every feature that uses it). Leave a field out to keep it; an empty apiKey removes the key. A new endpoint needs the key again, so a stored key is never sent to an address it was not given for. Needs ai.manage; audited without the key.")
            .RequirePermission(Permissions.AiManage)
            .Produces<AiProviderResponse>()
            .ProducesValidationProblem();

        providers.MapPost("/{id:guid}/test", TestProviderAsync)
            .WithName("TestAiProvider")
            .WithSummary("Test button: sends a short connection test to the given model now and answers with the result (answer, time, tokens, or why it failed). Works for disabled providers and whatever the monthly limit; recorded in usage and the AI log and audited. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<AiTestResponse>()
            .ProducesValidationProblem();

        providers.MapGet("/{id:guid}/models", ListModelsAsync)
            .WithName("ListAiProviderModels")
            .WithSummary("The models the provider offers, where its API lists them (Azure deployment addresses and some self-hosted servers do not; type the model or deployment name instead). Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<AiModelsResponse>();

        ai.MapGet("/features", ListFeaturesAsync)
            .WithName("ListAiFeatures")
            .WithSummary("Every AI feature with its switch, provider and model. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<IReadOnlyList<AiFeatureResponse>>();

        ai.MapPut("/features/{feature}", UpdateFeatureAsync)
            .WithName("UpdateAiFeature")
            .WithSummary("Sets a feature's provider and model and turns it on or off. A feature can be on only with an active provider and a model. Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces<AiFeatureResponse>()
            .ProducesValidationProblem();

        ai.MapGet("/prices", ListPricesAsync)
            .WithName("ListAiPrices")
            .WithSummary("Model prices entered by administrators, in US dollars per million input and output tokens. Calls of models without a price are counted but have no cost. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<IReadOnlyList<AiPriceResponse>>();

        ai.MapPut("/prices", SetPriceAsync)
            .WithName("SetAiPrice")
            .WithSummary("Sets the price of a provider's model (US dollars per million tokens, 0–10,000). Applies to calls from now on. Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces<AiPriceResponse>()
            .ProducesValidationProblem();

        ai.MapDelete("/prices", DeletePriceAsync)
            .WithName("DeleteAiPrice")
            .WithSummary("Removes the price of a provider's model. Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces(StatusCodes.Status204NoContent);

        ai.MapGet("/limit", GetLimitAsync)
            .WithName("GetAiLimit")
            .WithSummary("The monthly spending limit and this month's spending: warning at 80 %, AI calls stop at 100 % until the next calendar month (UTC) or until the limit is raised. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<AiBudgetResponse>();

        ai.MapPut("/limit", SetLimitAsync)
            .WithName("SetAiLimit")
            .WithSummary("Sets the monthly spending limit in US dollars (0 = no limit, up to 1,000,000, two decimals). Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces<AiBudgetResponse>()
            .ProducesValidationProblem();

        ai.MapGet("/usage", UsageAsync)
            .WithName("GetAiUsage")
            .WithSummary("Usage and cost dashboard for a calendar month (UTC; default this month, format yyyy-MM): calls, tokens and cost in total, per day, per feature and per provider and model; calls without a price; and the monthly limit. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<AiUsageResponse>();

        ai.MapGet("/calls", ListCallsAsync)
            .WithName("ListAiCalls")
            .WithSummary("AI log, newest first: every AI call with feature, provider, model, outcome, tokens, cost and who it was made for. Filter by feature, outcome or provider. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<PageResult<AiCallResponse>>();

        ai.MapGet("/calls/{id:long}", GetCallAsync)
            .WithName("GetAiCall")
            .WithSummary("One AI call with what was sent and the provider's answer, both with personal data masked, while within the retention period (30 days). Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<AiCallDetailResponse>();

        return group;
    }

    internal static async Task<IResult> StatusAsync(IAiGateway gateway, CancellationToken cancellationToken)
    {
        var features = await gateway.AvailabilityAsync(cancellationToken);
        return TypedResults.Ok(new AiStatusResponse(features.Any(f => f.Available), features));
    }

    internal static async Task<IResult> ListProvidersAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var providers = await db.AiProviders.AsNoTracking().OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync(cancellationToken);
        return TypedResults.Ok(providers.Select(AiProviderResponse.From).ToList());
    }

    internal static async Task<IResult> GetProviderAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.AiProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken) is { } provider
            ? TypedResults.Ok(AiProviderResponse.From(provider))
            : ProviderNotFound();

    internal static async Task<IResult> CreateProviderAsync(
        CreateAiProviderRequest request, ZelvoDbContext db, AiProviderClient client, ISecretProtector secrets, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var kind = request.Kind!.Value;
        if (CheckEndpoint(client, kind, request.Endpoint, out var endpoint) is { } invalidEndpoint)
        {
            return invalidEndpoint;
        }

        if (CheckApiVersion(kind, request.ApiVersion) is { } invalidVersion)
        {
            return invalidVersion;
        }

        var key = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim();
        if (key is null && kind != AiProviderKind.OpenAICompatible)
        {
            return UserEndpoints.Validation("apiKey", "Enter the API key the provider gave you.");
        }

        if (key is not null && !IsKey(key))
        {
            return UserEndpoints.Validation("apiKey", $"Use the API key exactly as the provider gave it: up to {MaxKeyLength} characters, without spaces.");
        }

        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var provider = new AiProvider
        {
            Id = id,
            Name = request.Name!.Trim(),
            Kind = kind,
            Endpoint = endpoint?.AbsoluteUri,
            ApiVersion = kind == AiProviderKind.AzureOpenAI && !string.IsNullOrWhiteSpace(request.ApiVersion) ? request.ApiVersion.Trim() : null,
            ApiKeyCiphertext = key is null ? null : await secrets.ProtectAsync(AiGateway.KeyPurpose(id), key, cancellationToken),
            ApiKeyChangedAt = key is null ? null : now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AiProviders.Add(provider);
        if (key is not null)
        {
            audit.Add(AuditCategory.Security, "ai.provider.key-changed", "ai_provider", id.ToString());
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/ai/providers/{id}", AiProviderResponse.From(provider));
    }

    internal static async Task<IResult> UpdateProviderAsync(
        Guid id, UpdateAiProviderRequest request, ZelvoDbContext db, AiProviderClient client, ISecretProtector secrets, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var provider = await db.AiProviders.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (provider is null)
        {
            return ProviderNotFound();
        }

        var endpointChanged = false;
        if (request.Endpoint is not null)
        {
            var given = request.Endpoint.Trim();
            if (CheckEndpoint(client, provider.Kind, given.Length == 0 ? null : given, out var endpoint) is { } invalidEndpoint)
            {
                return invalidEndpoint;
            }

            endpointChanged = !string.Equals(endpoint?.AbsoluteUri, provider.Endpoint, StringComparison.Ordinal);
            provider.Endpoint = endpoint?.AbsoluteUri;
        }

        if (request.ApiVersion is not null)
        {
            if (CheckApiVersion(provider.Kind, request.ApiVersion) is { } invalidVersion)
            {
                return invalidVersion;
            }

            provider.ApiVersion = string.IsNullOrWhiteSpace(request.ApiVersion) ? null : request.ApiVersion.Trim();
        }

        if (request.ApiKey is not null)
        {
            var key = request.ApiKey.Trim();
            if (key.Length == 0)
            {
                if (provider.Kind != AiProviderKind.OpenAICompatible)
                {
                    return UserEndpoints.Validation("apiKey", "This provider needs an API key; enter a new one to replace it.");
                }

                provider.ApiKeyCiphertext = null;
                provider.ApiKeyChangedAt = null;
            }
            else
            {
                if (!IsKey(key))
                {
                    return UserEndpoints.Validation("apiKey", $"Use the API key exactly as the provider gave it: up to {MaxKeyLength} characters, without spaces.");
                }

                provider.ApiKeyCiphertext = await secrets.ProtectAsync(AiGateway.KeyPurpose(id), key, cancellationToken);
                provider.ApiKeyChangedAt = DateTimeOffset.UtcNow;
            }

            audit.Add(AuditCategory.Security, "ai.provider.key-changed", "ai_provider", id.ToString());
        }
        else if (endpointChanged && provider.ApiKeyCiphertext is not null)
        {
            // Otherwise anyone who may edit the endpoint could have the stored key sent to an address of their choosing.
            return UserEndpoints.Validation("apiKey", "Enter the API key again when you change the endpoint.");
        }

        if (request.Name is not null)
        {
            provider.Name = request.Name.Trim();
        }

        if (request.Status is { } status)
        {
            provider.Status = status;
        }

        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(AiProviderResponse.From(provider));
    }

    internal static async Task<IResult> TestProviderAsync(Guid id, TestAiProviderRequest request, ZelvoDbContext db, IAiGateway gateway, CancellationToken cancellationToken)
    {
        if (!await db.AiProviders.AnyAsync(p => p.Id == id, cancellationToken))
        {
            return ProviderNotFound();
        }

        var model = request.Model!.Trim();
        if (!IsModel(model))
        {
            return UserEndpoints.Validation("model", ModelMessage);
        }

        var result = await gateway.TestAsync(id, model, cancellationToken);
        return TypedResults.Ok(new AiTestResponse(result.Succeeded, result.Text, result.ErrorCode, result.Error, result.DurationMs,
            result.InputTokens, result.OutputTokens, result.CallId));
    }

    internal static async Task<IResult> ListModelsAsync(Guid id, ZelvoDbContext db, IAiGateway gateway, CancellationToken cancellationToken)
    {
        if (!await db.AiProviders.AnyAsync(p => p.Id == id, cancellationToken))
        {
            return ProviderNotFound();
        }

        try
        {
            return TypedResults.Ok(new AiModelsResponse(await gateway.ListModelsAsync(id, cancellationToken), null, null));
        }
        catch (AiProviderException ex)
        {
            return TypedResults.Ok(new AiModelsResponse([], ex.Code, ex.Message));
        }
        catch (SecretDecryptionException)
        {
            return TypedResults.Ok(new AiModelsResponse([], "key-unreadable", "The stored API key cannot be decrypted. Enter it again."));
        }
    }

    internal static async Task<IResult> ListFeaturesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.AiFeatureSettings.AsNoTracking().ToDictionaryAsync(f => f.Feature, StringComparer.Ordinal, cancellationToken);
        return TypedResults.Ok(AiFeatures.All.Select(f => AiFeatureResponse.From(f, settings.GetValueOrDefault(f))).ToList());
    }

    internal static async Task<IResult> UpdateFeatureAsync(string feature, UpdateAiFeatureRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!AiFeatures.IsFeature(feature))
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "ai-feature-not-found", "There is no such AI feature.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim();
        if (model is not null && !IsModel(model))
        {
            return UserEndpoints.Validation("model", ModelMessage);
        }

        AiProvider? provider = null;
        if (request.ProviderId is { } providerId)
        {
            provider = await db.AiProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == providerId, cancellationToken);
            if (provider is null)
            {
                return UserEndpoints.Validation("providerId", "Choose one of the tenant's AI providers.");
            }
        }

        if (request.Enabled == true && (provider is null || model is null))
        {
            return UserEndpoints.Validation("enabled", "Choose a provider and a model before turning the feature on.");
        }

        if (request.Enabled == true && provider!.Status != AiProviderStatus.Active)
        {
            return UserEndpoints.Validation("providerId", "This provider is switched off. Switch it on, or choose another provider.");
        }

        var setting = await db.AiFeatureSettings.SingleOrDefaultAsync(f => f.Feature == feature, cancellationToken);
        if (setting is null)
        {
            setting = new AiFeatureSetting { Feature = feature };
            db.AiFeatureSettings.Add(setting);
        }

        setting.Enabled = request.Enabled == true;
        setting.ProviderId = provider?.Id;
        setting.Model = model;
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(AiFeatureResponse.From(feature, setting));
    }

    internal static async Task<IResult> ListPricesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var prices = await db.AiModelPrices.AsNoTracking().OrderBy(p => p.ProviderId).ThenBy(p => p.Model).ToListAsync(cancellationToken);
        return TypedResults.Ok(prices.Select(AiPriceResponse.From).ToList());
    }

    internal static async Task<IResult> SetPriceAsync(SetAiPriceRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var model = request.Model!.Trim();
        if (!IsModel(model))
        {
            return UserEndpoints.Validation("model", ModelMessage);
        }

        if (!await db.AiProviders.AnyAsync(p => p.Id == request.ProviderId, cancellationToken))
        {
            return UserEndpoints.Validation("providerId", "Choose one of the tenant's AI providers.");
        }

        foreach (var (field, value) in new[] { ("inputPerMillion", request.InputPerMillion!.Value), ("outputPerMillion", request.OutputPerMillion!.Value) })
        {
            if (value is < 0 or > AiModelPrice.MaxPrice || decimal.Round(value, 6) != value)
            {
                return UserEndpoints.Validation(field, "Enter US dollars per million tokens from 0 to 10,000, with up to 6 decimals.");
            }
        }

        var price = await db.AiModelPrices.SingleOrDefaultAsync(p => p.ProviderId == request.ProviderId && p.Model == model, cancellationToken);
        if (price is null)
        {
            price = new AiModelPrice { ProviderId = request.ProviderId!.Value, Model = model };
            db.AiModelPrices.Add(price);
        }

        price.InputPerMillion = request.InputPerMillion!.Value;
        price.OutputPerMillion = request.OutputPerMillion!.Value;
        price.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(AiPriceResponse.From(price));
    }

    internal static async Task<IResult> DeletePriceAsync([AsParameters] AiPriceKey key, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var model = key.Model?.Trim();
        var price = await db.AiModelPrices.SingleOrDefaultAsync(p => p.ProviderId == key.ProviderId && p.Model == model, cancellationToken);
        if (price is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "ai-price-not-found", "No price is set for this model.");
        }

        db.AiModelPrices.Remove(price);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> GetLimitAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(AiBudgetResponse.From(await AiBudget.StatusAsync(db, DateTimeOffset.UtcNow, cancellationToken)));

    internal static async Task<IResult> SetLimitAsync(SetAiLimitRequest request, TenantSettingsStore store, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var limit = request.MonthlyLimitUsd!.Value;
        if (!AiBudget.IsLimit(limit))
        {
            return UserEndpoints.Validation("monthlyLimitUsd", "Enter US dollars from 0 (no limit) to 1,000,000, with up to 2 decimals.");
        }

        await store.SetAsync(MonthlyLimit, limit, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(AiBudgetResponse.From(await AiBudget.StatusAsync(db, DateTimeOffset.UtcNow, cancellationToken)));
    }

    internal static async Task<IResult> UsageAsync([AsParameters] AiUsageQuery query, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset reference = now;
        if (query.Month is { Length: > 0 } month)
        {
            if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return UserEndpoints.Validation("month", "Use the form yyyy-MM, for example 2026-11.");
            }

            reference = new DateTimeOffset(parsed.Year, parsed.Month, 1, 0, 0, 0, TimeSpan.Zero);
        }

        var (start, end) = AiBudget.MonthOf(reference);
        var calls = db.AiCalls.AsNoTracking().Where(c => c.OccurredAt >= start && c.OccurredAt < end);

        var rows = await calls
            .GroupBy(c => new { c.OccurredAt.Day, c.Feature, c.ProviderId, c.Model, c.Outcome, Priced = c.CostUsd != null })
            .Select(g => new UsageRow(g.Key.Day, g.Key.Feature, g.Key.ProviderId, g.Key.Model, g.Key.Outcome, g.Key.Priced,
                g.LongCount(), g.Sum(c => (long)c.InputTokens), g.Sum(c => (long)c.OutputTokens), g.Sum(c => c.CostUsd) ?? 0m))
            .ToListAsync(cancellationToken);

        var names = await db.AiProviders.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var limit = await AiBudget.ReadLimitAsync(db, cancellationToken);
        var budget = AiBudget.Evaluate(limit, rows.Sum(r => r.Cost), start, end);

        var days = Enumerable.Range(0, (end - start).Days).Select(d => start.UtcDateTime.Date.AddDays(d))
            .Select(day => Totals(DateOnly.FromDateTime(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), rows.Where(r => r.Day == day.Day)))
            .ToList();
        var features = rows.GroupBy(r => r.Feature, StringComparer.Ordinal).Select(g => Totals(g.Key, g)).OrderByDescending(t => t.CostUsd).ThenByDescending(t => t.Calls).ToList();
        var models = rows.GroupBy(r => (r.ProviderId, r.Model))
            .Select(g => new AiModelUsage(g.Key.ProviderId, g.Key.ProviderId is { } id ? names.GetValueOrDefault(id) : null, g.Key.Model,
                Totals(g.Key.Model ?? string.Empty, g), g.Where(r => !r.Priced && r.Outcome != AiCallOutcome.LimitReached).Sum(r => r.Calls)))
            .OrderByDescending(m => m.Totals.CostUsd).ThenByDescending(m => m.Totals.Calls).ToList();

        return TypedResults.Ok(new AiUsageResponse(
            start.ToString("yyyy-MM", CultureInfo.InvariantCulture), start, end,
            Totals("total", rows),
            rows.Where(r => !r.Priced && r.Outcome is not AiCallOutcome.LimitReached).Sum(r => r.Calls),
            start <= now && now < end ? AiBudgetResponse.From(budget) : null,
            days, features, models));
    }

    internal static async Task<IResult> ListCallsAsync([AsParameters] PageQuery page, [AsParameters] AiCallQuery filter, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = db.AiCalls.AsNoTracking();
        if (filter.Feature is { Length: > 0 } feature)
        {
            query = query.Where(c => c.Feature == feature);
        }

        if (filter.Outcome is { } outcome)
        {
            query = query.Where(c => c.Outcome == outcome);
        }

        if (filter.ProviderId is { } providerId)
        {
            query = query.Where(c => c.ProviderId == providerId);
        }

        if (filter.InjectionSuspected == true)
        {
            query = query.Where(c => c.InjectionSuspected);
        }

        query = query.OrderByDescending(c => c.Id);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var lookups = await LookupsAsync(db, items, cancellationToken);
        return TypedResults.Ok(new PageResult<AiCallResponse>([.. items.Select(c => AiCallResponse.From(c, lookups))], page.Number, page.Size, total));
    }

    internal static async Task<IResult> GetCallAsync(long id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var call = await db.AiCalls.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (call is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "ai-call-not-found", "The AI call does not exist.");
        }

        var lookups = await LookupsAsync(db, [call], cancellationToken);
        return TypedResults.Ok(new AiCallDetailResponse(AiCallResponse.From(call, lookups), call.Prompt, call.Response, call.Prompt is null && call.Response is null));
    }

    private static async Task<Lookups> LookupsAsync(ZelvoDbContext db, IReadOnlyList<AiCall> calls, CancellationToken cancellationToken)
    {
        var providerIds = calls.Where(c => c.ProviderId is not null).Select(c => c.ProviderId!.Value).Distinct().ToList();
        var userIds = calls.Where(c => c.UserId is not null).Select(c => c.UserId!.Value).Distinct().ToList();
        var providers = await db.AiProviders.AsNoTracking().Where(p => providerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        return new Lookups(providers, users);
    }

    private static AiUsageTotals Totals(string key, IEnumerable<UsageRow> rows)
    {
        var list = rows as IReadOnlyCollection<UsageRow> ?? rows.ToList();
        return new AiUsageTotals(key,
            list.Sum(r => r.Calls),
            list.Where(r => r.Outcome != AiCallOutcome.Succeeded).Sum(r => r.Calls),
            list.Sum(r => r.InputTokens),
            list.Sum(r => r.OutputTokens),
            list.Sum(r => r.Cost));
    }

    private const int MaxKeyLength = 1000;
    private const string ModelMessage = "Enter the model id as the provider names it (for Azure OpenAI, the deployment name): up to 200 characters, without spaces.";

    private static bool IsKey(string key) => key.Length <= MaxKeyLength && !key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static bool IsModel(string model) =>
        model.Length is > 0 and <= AiFeatureSetting.ModelMaxLength && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/' or '@');

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? CheckApiVersion(AiProviderKind kind, string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? null
            : kind != AiProviderKind.AzureOpenAI
                ? UserEndpoints.Validation("apiVersion", "Only Azure OpenAI uses an api-version.")
                : version.Trim().Length > AiProvider.ApiVersionMaxLength || !version.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
                    ? UserEndpoints.Validation("apiVersion", "Use an Azure api-version such as 2024-10-21.")
                    : null;

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? CheckEndpoint(AiProviderClient client, AiProviderKind kind, string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return kind is AiProviderKind.AzureOpenAI or AiProviderKind.OpenAICompatible
                ? EndpointProblem("endpoint-required", kind == AiProviderKind.AzureOpenAI
                    ? "Enter your Azure OpenAI resource address, for example https://my-resource.openai.azure.com."
                    : "Enter the address of the OpenAI-compatible API, for example https://llm.example.ae/v1.")
                : null;
        }

        if (value.Length > AiProvider.EndpointMaxLength || !string.IsNullOrEmpty(new UriBuilder(value).Query))
        {
            return EndpointProblem("url-invalid", "Use a full address without query string, user name, password or #fragment.");
        }

        return client.Policy.CheckUrl(value, out endpoint) switch
        {
            null => null,
            "url-not-https" => EndpointProblem("url-not-https", "Use an https:// address."),
            "address-not-allowed" => EndpointProblem("address-not-allowed", "Zelvo may not connect to this address (this server, internal or reserved networks). The installation's operator can allow internal networks for self-hosted models."),
            _ => EndpointProblem("url-invalid", "Use a full address without query string, user name, password or #fragment."),
        };
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem EndpointProblem(string code, string message) =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["endpoint"] = [message] },
            type: ApiProblem.TypeFor("validation-failed"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation-failed", ["endpointProblem"] = code });

    private static IResult ProviderNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "ai-provider-not-found", "The AI provider does not exist.");

    // Day of the month (UTC).
    private sealed record UsageRow(int Day, string Feature, Guid? ProviderId, string? Model, AiCallOutcome Outcome, bool Priced, long Calls, long InputTokens, long OutputTokens, decimal Cost);

    internal sealed record Lookups(IReadOnlyDictionary<Guid, string> Providers, IReadOnlyDictionary<Guid, string?> Users);
}

/// <summary>A new provider connection.</summary>
public sealed class CreateAiProviderRequest
{
    /// <summary>Name shown to administrators, 1 to 100 characters.</summary>
    [Required]
    [StringLength(AiProvider.NameMaxLength, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary><c>Anthropic</c>, <c>OpenAI</c>, <c>AzureOpenAI</c>, <c>Gemini</c> or <c>OpenAICompatible</c>.</summary>
    [Required]
    public AiProviderKind? Kind { get; init; }

    /// <summary>Base address; required for AzureOpenAI and OpenAICompatible.</summary>
    [StringLength(AiProvider.EndpointMaxLength)]
    public string? Endpoint { get; init; }

    /// <summary>Azure OpenAI only: api-version for deployment addresses; leave out for Azure's v1 API.</summary>
    [StringLength(AiProvider.ApiVersionMaxLength)]
    public string? ApiVersion { get; init; }

    /// <summary>The API key; optional only for OpenAICompatible. Stored encrypted, never returned.</summary>
    [StringLength(1000)]
    public string? ApiKey { get; init; }
}

/// <summary>Changes to a provider. Leave a field out to keep it.</summary>
public sealed class UpdateAiProviderRequest
{
    /// <summary>Name, 1 to 100 characters.</summary>
    [StringLength(AiProvider.NameMaxLength, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary>Base address; empty removes it where it is optional. A new endpoint needs the key again.</summary>
    [StringLength(AiProvider.EndpointMaxLength)]
    public string? Endpoint { get; init; }

    /// <summary>Azure OpenAI api-version; empty uses Azure's v1 API.</summary>
    [StringLength(AiProvider.ApiVersionMaxLength)]
    public string? ApiVersion { get; init; }

    /// <summary>A new API key; empty removes the key (OpenAI-compatible only).</summary>
    [StringLength(1000)]
    public string? ApiKey { get; init; }

    /// <summary><c>Active</c> or <c>Disabled</c>.</summary>
    public AiProviderStatus? Status { get; init; }
}

/// <summary>What to test.</summary>
public sealed class TestAiProviderRequest
{
    /// <summary>Model id (Azure OpenAI: deployment name).</summary>
    [Required]
    [StringLength(AiFeatureSetting.ModelMaxLength, MinimumLength = 1)]
    public string? Model { get; init; }
}

/// <summary>A feature's settings.</summary>
public sealed class UpdateAiFeatureRequest
{
    /// <summary>Whether the feature may make AI calls.</summary>
    [Required]
    public bool? Enabled { get; init; }

    /// <summary>The provider connection.</summary>
    public Guid? ProviderId { get; init; }

    /// <summary>Model id (Azure OpenAI: deployment name).</summary>
    [StringLength(AiFeatureSetting.ModelMaxLength)]
    public string? Model { get; init; }
}

/// <summary>The price of a model.</summary>
public sealed class SetAiPriceRequest
{
    /// <summary>The provider connection.</summary>
    [Required]
    public Guid? ProviderId { get; init; }

    /// <summary>Model id.</summary>
    [Required]
    [StringLength(AiFeatureSetting.ModelMaxLength, MinimumLength = 1)]
    public string? Model { get; init; }

    /// <summary>US dollars per million input tokens.</summary>
    [Required]
    public decimal? InputPerMillion { get; init; }

    /// <summary>US dollars per million output tokens.</summary>
    [Required]
    public decimal? OutputPerMillion { get; init; }
}

/// <summary>Which price to remove.</summary>
public sealed class AiPriceKey
{
    /// <summary>The provider connection.</summary>
    [Required]
    public Guid? ProviderId { get; init; }

    /// <summary>Model id.</summary>
    [Required]
    [StringLength(AiFeatureSetting.ModelMaxLength, MinimumLength = 1)]
    public string? Model { get; init; }
}

/// <summary>The monthly limit.</summary>
public sealed class SetAiLimitRequest
{
    /// <summary>US dollars; 0 means no limit.</summary>
    [Required]
    public decimal? MonthlyLimitUsd { get; init; }
}

/// <summary>Which month the dashboard shows.</summary>
public sealed class AiUsageQuery
{
    /// <summary>yyyy-MM (UTC); this month when left out.</summary>
    [StringLength(7)]
    public string? Month { get; init; }
}

/// <summary>Filters of the AI log.</summary>
public sealed class AiCallQuery
{
    /// <summary>Only calls of this feature (or <c>test</c>).</summary>
    [StringLength(AiFeatureSetting.FeatureMaxLength)]
    public string? Feature { get; init; }

    /// <summary>Only calls with this outcome.</summary>
    public AiCallOutcome? Outcome { get; init; }

    /// <summary>Only calls to this provider.</summary>
    public Guid? ProviderId { get; init; }

    /// <summary>True: only calls whose input looked like prompt injection.</summary>
    public bool? InjectionSuspected { get; init; }
}

/// <summary>Which AI features can be used now.</summary>
/// <param name="Enabled">At least one feature is available.</param>
/// <param name="Features">Each feature.</param>
internal sealed record AiStatusResponse(bool Enabled, IReadOnlyList<AiFeatureAvailability> Features);

/// <summary>A provider connection. The key is never included.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Name.</param>
/// <param name="Kind">Which API it speaks.</param>
/// <param name="Endpoint">Base address, if set.</param>
/// <param name="ApiVersion">Azure OpenAI api-version, if set.</param>
/// <param name="Status"><c>Active</c> or <c>Disabled</c>.</param>
/// <param name="ApiKeySet">Whether a key is stored.</param>
/// <param name="ApiKeyChangedAt">When the key was last given.</param>
/// <param name="LastTestAt">When it was last tested.</param>
/// <param name="LastTestSucceeded">Whether the last test succeeded.</param>
/// <param name="LastTestError">Why the last test failed.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it was last changed.</param>
internal sealed record AiProviderResponse(
    Guid Id, string Name, AiProviderKind Kind, string? Endpoint, string? ApiVersion, AiProviderStatus Status, bool ApiKeySet,
    DateTimeOffset? ApiKeyChangedAt, DateTimeOffset? LastTestAt, bool? LastTestSucceeded, string? LastTestError, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static AiProviderResponse From(AiProvider p) => new(
        p.Id, p.Name, p.Kind, p.Endpoint, p.ApiVersion, p.Status, p.ApiKeyCiphertext is not null, p.ApiKeyChangedAt,
        p.LastTestAt, p.LastTestSucceeded, p.LastTestError, p.CreatedAt, p.UpdatedAt);
}

/// <summary>Result of the test button.</summary>
/// <param name="Succeeded">The model answered.</param>
/// <param name="Answer">Its answer.</param>
/// <param name="ErrorCode">Why it failed, for example <c>provider-auth-failed</c>.</param>
/// <param name="Error">Explanation.</param>
/// <param name="DurationMs">How long it took.</param>
/// <param name="InputTokens">Input tokens.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CallId">The call in the AI log.</param>
internal sealed record AiTestResponse(bool Succeeded, string? Answer, string? ErrorCode, string? Error, int DurationMs, int InputTokens, int OutputTokens, long? CallId);

/// <summary>Models a provider offers.</summary>
/// <param name="Models">Model ids, empty when the provider does not list them.</param>
/// <param name="ErrorCode">Why they could not be listed.</param>
/// <param name="Error">Explanation.</param>
internal sealed record AiModelsResponse(IReadOnlyList<string> Models, string? ErrorCode, string? Error);

/// <summary>A feature's settings.</summary>
/// <param name="Feature">Feature key.</param>
/// <param name="Enabled">On or off.</param>
/// <param name="ProviderId">The provider connection.</param>
/// <param name="Model">Model id.</param>
/// <param name="UpdatedAt">When it was last changed; null if never set.</param>
internal sealed record AiFeatureResponse(string Feature, bool Enabled, Guid? ProviderId, string? Model, DateTimeOffset? UpdatedAt)
{
    public static AiFeatureResponse From(string feature, AiFeatureSetting? s) =>
        new(feature, s?.Enabled ?? false, s?.ProviderId, s?.Model, s?.UpdatedAt);
}

/// <summary>A model's price.</summary>
/// <param name="ProviderId">The provider connection.</param>
/// <param name="Model">Model id.</param>
/// <param name="InputPerMillion">US dollars per million input tokens.</param>
/// <param name="OutputPerMillion">US dollars per million output tokens.</param>
/// <param name="UpdatedAt">When it was last changed.</param>
internal sealed record AiPriceResponse(Guid ProviderId, string Model, decimal InputPerMillion, decimal OutputPerMillion, DateTimeOffset UpdatedAt)
{
    public static AiPriceResponse From(AiModelPrice p) => new(p.ProviderId, p.Model, p.InputPerMillion, p.OutputPerMillion, p.UpdatedAt);
}

/// <summary>The monthly limit and this month's spending.</summary>
/// <param name="MonthlyLimitUsd">The limit; 0 when none is set.</param>
/// <param name="SpentUsd">Spent this month.</param>
/// <param name="Percent">Spent as a percentage of the limit.</param>
/// <param name="State"><c>NoLimit</c>, <c>Ok</c>, <c>Warning</c> (80 % or more) or <c>Reached</c>.</param>
/// <param name="WarningPercent">Percentage at which Settings warns.</param>
/// <param name="PeriodStart">Start of the month (UTC).</param>
/// <param name="PeriodEnd">Start of next month (UTC).</param>
internal sealed record AiBudgetResponse(decimal MonthlyLimitUsd, decimal SpentUsd, int? Percent, AiBudgetState State, int WarningPercent, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd)
{
    public static AiBudgetResponse From(AiBudgetStatus s) =>
        new(s.LimitUsd ?? 0m, s.SpentUsd, s.Percent, s.State, AiBudget.WarningPercent, s.PeriodStart, s.PeriodEnd);
}

/// <summary>Counts for a day, a feature or a model.</summary>
/// <param name="Key">The day (yyyy-MM-dd), feature, model or <c>total</c>.</param>
/// <param name="Calls">Calls.</param>
/// <param name="FailedCalls">Calls that did not succeed (failed, rejected or stopped by the limit).</param>
/// <param name="InputTokens">Input tokens.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CostUsd">Cost in US dollars.</param>
internal sealed record AiUsageTotals(string Key, long Calls, long FailedCalls, long InputTokens, long OutputTokens, decimal CostUsd);

/// <summary>Usage of one provider's model.</summary>
/// <param name="ProviderId">The provider connection.</param>
/// <param name="ProviderName">Its name.</param>
/// <param name="Model">Model id.</param>
/// <param name="Totals">Counts.</param>
/// <param name="UnpricedCalls">Calls that have no cost because the model has no price.</param>
internal sealed record AiModelUsage(Guid? ProviderId, string? ProviderName, string? Model, AiUsageTotals Totals, long UnpricedCalls);

/// <summary>The usage and cost dashboard of one month.</summary>
/// <param name="Month">yyyy-MM.</param>
/// <param name="PeriodStart">Start (UTC).</param>
/// <param name="PeriodEnd">End (UTC).</param>
/// <param name="Totals">The whole month.</param>
/// <param name="UnpricedCalls">Calls without a cost because their model has no price.</param>
/// <param name="Budget">The monthly limit, for the current month only.</param>
/// <param name="Days">Each day of the month.</param>
/// <param name="Features">Each feature used.</param>
/// <param name="Models">Each provider and model used.</param>
internal sealed record AiUsageResponse(
    string Month, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, AiUsageTotals Totals, long UnpricedCalls, AiBudgetResponse? Budget,
    IReadOnlyList<AiUsageTotals> Days, IReadOnlyList<AiUsageTotals> Features, IReadOnlyList<AiModelUsage> Models);

/// <summary>One AI call of the AI log.</summary>
/// <param name="Id">Id.</param>
/// <param name="OccurredAt">When it started.</param>
/// <param name="Feature">Feature key or <c>test</c>.</param>
/// <param name="ProviderId">The provider connection.</param>
/// <param name="ProviderName">Its name.</param>
/// <param name="ProviderKind">Which API.</param>
/// <param name="Model">Model id.</param>
/// <param name="UserId">The person it was made for.</param>
/// <param name="UserName">Their name.</param>
/// <param name="EntityType">What it was about.</param>
/// <param name="EntityId">Id of what it was about.</param>
/// <param name="Outcome"><c>Succeeded</c>, <c>Failed</c>, <c>Rejected</c> or <c>LimitReached</c>.</param>
/// <param name="ErrorCode">Error code.</param>
/// <param name="Error">Explanation.</param>
/// <param name="InputTokens">Input tokens.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CostUsd">Cost, if the model has a price.</param>
/// <param name="DurationMs">Duration.</param>
/// <param name="InjectionSuspected">The input looked like prompt injection.</param>
/// <param name="MaskedValues">Personal values masked before sending.</param>
/// <param name="CorrelationId">Trace id.</param>
internal sealed record AiCallResponse(
    long Id, DateTimeOffset OccurredAt, string Feature, Guid? ProviderId, string? ProviderName, AiProviderKind? ProviderKind, string? Model,
    Guid? UserId, string? UserName, string? EntityType, string? EntityId, AiCallOutcome Outcome, string? ErrorCode, string? Error,
    int InputTokens, int OutputTokens, decimal? CostUsd, int DurationMs, bool InjectionSuspected, int MaskedValues, string? CorrelationId)
{
    public static AiCallResponse From(AiCall c, AiEndpoints.Lookups lookups) => new(
        c.Id, c.OccurredAt, c.Feature, c.ProviderId, c.ProviderId is { } p ? lookups.Providers.GetValueOrDefault(p) : null, c.ProviderKind, c.Model,
        c.UserId, c.UserId is { } u ? lookups.Users.GetValueOrDefault(u) : null, c.EntityType, c.EntityId, c.Outcome, c.ErrorCode, c.Error,
        c.InputTokens, c.OutputTokens, c.CostUsd, c.DurationMs, c.InjectionSuspected, c.MaskedValues, c.CorrelationId);
}

/// <summary>A call with its text.</summary>
/// <param name="Call">The call.</param>
/// <param name="Prompt">What was sent, personal data masked.</param>
/// <param name="Response">The provider's answer as received (still masked).</param>
/// <param name="TextExpired">The text was cleared after the retention period.</param>
internal sealed record AiCallDetailResponse(AiCallResponse Call, string? Prompt, string? Response, bool TextExpired);
