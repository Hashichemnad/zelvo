using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zelvo.Data;
using Zelvo.Data.Ai;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Integrations;
using Zelvo.Secrets;

namespace Zelvo.Ai;

/// <summary>Untrusted text given to the model as data, for example a ticket description.</summary>
/// <param name="Label">What it is, for example <c>ticket-description</c> (letters, digits, hyphens).</param>
/// <param name="Text">The text.</param>
public sealed record AiInput(string Label, string Text);

/// <summary>What a feature asks of the AI.</summary>
public sealed class AiRequest
{
    /// <summary>Feature key (<see cref="AiFeatures"/>).</summary>
    public required string Feature { get; init; }

    /// <summary>The feature's own instructions, written by Zelvo (trusted). Never put people's text here.</summary>
    public required string Instructions { get; init; }

    /// <summary>Data to work on, sent in data blocks.</summary>
    public IReadOnlyList<AiInput> Inputs { get; init; } = [];

    /// <summary>Earlier turns of a conversation (virtual agent); people's turns are sent as data.</summary>
    public IReadOnlyList<AiMessage> Conversation { get; init; } = [];

    /// <summary>Personal values the caller knows (names of the people involved, for example); masked before sending.</summary>
    public IReadOnlyCollection<string> PersonalValues { get; init; } = [];

    /// <summary>Most tokens the answer may have.</summary>
    public int MaxOutputTokens { get; init; } = 1024;

    /// <summary>The answer must be one JSON object; anything else is rejected.</summary>
    public bool Json { get; init; }

    /// <summary>What the call is about, for the AI log, for example <c>ticket</c>.</summary>
    public string? EntityType { get; init; }

    /// <summary>Id of what the call is about.</summary>
    public string? EntityId { get; init; }
}

/// <summary>What came back from an AI call.</summary>
/// <param name="Succeeded">There is an answer that passed the checks.</param>
/// <param name="Text">The answer, with personal data put back; for <see cref="AiRequest.Json"/>, the JSON object.</param>
/// <param name="ErrorCode">Why there is no answer: <c>ai-off</c>, <c>limit-reached</c>, <c>invalid-json</c> or a provider code such as <c>provider-auth-failed</c>.</param>
/// <param name="Error">Short explanation.</param>
/// <param name="CallId">The call in the AI log (null when nothing was sent because AI is off).</param>
/// <param name="InputTokens">Input tokens.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CostUsd">Cost, if the model has a price.</param>
/// <param name="DurationMs">How long the provider took.</param>
/// <param name="InjectionSuspected">The input looked like an attempt to give the AI instructions.</param>
/// <param name="Truncated">The answer stopped at the token limit.</param>
public sealed record AiResult(
    bool Succeeded, string? Text, string? ErrorCode, string? Error, long? CallId, int InputTokens, int OutputTokens, decimal? CostUsd,
    int DurationMs, bool InjectionSuspected, bool Truncated);

/// <summary>Whether a feature can make AI calls now.</summary>
/// <param name="Feature">Feature key.</param>
/// <param name="Available">On, with an active provider and a model, and the monthly limit not reached.</param>
/// <param name="Reason">Why not: <c>ai-off</c> or <c>limit-reached</c>.</param>
public sealed record AiFeatureAvailability(string Feature, bool Available, string? Reason);

/// <summary>
/// The only way Zelvo calls an AI provider (module 9). Every call: checks the feature is on and
/// the monthly limit is not reached; masks personal data; wraps people's text with the
/// prompt-injection guards; calls the feature's provider and model; checks the answer; puts the
/// personal data back on Zelvo's side; records usage, cost and the masked text in the AI log; and
/// writes the audit log. The gateway never changes tickets or other records.
/// </summary>
public interface IAiGateway
{
    /// <summary>Whether each feature can be used now (AI stays off until an administrator sets it up).</summary>
    Task<IReadOnlyList<AiFeatureAvailability>> AvailabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes one call for a feature.</summary>
    Task<AiResult> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The test button: a small call to <paramref name="model"/> of a provider, whatever the
    /// feature settings and the monthly limit (it costs a few tokens and an administrator asked
    /// for it). Recorded and audited like any call; the provider keeps the result.
    /// </summary>
    Task<AiResult> TestAsync(Guid providerId, string model, CancellationToken cancellationToken = default);

    /// <summary>The provider's models, where its API lists them.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(Guid providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Task E3: what is said in a voice note, with the provider and model of the transcription
    /// feature. The audio cannot be masked; the AI log keeps the transcript masked.
    /// </summary>
    Task<AiResult> TranscribeAsync(byte[] audio, string contentType, string? entityType, string? entityId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// Calls are recorded through a scope of their own, for the caller's tenant and actor, so the
/// AI log and audit entries are saved whatever the caller does with its own unit of work.
/// </remarks>
public sealed class AiGateway(
    IServiceScopeFactory scopes,
    ITenantContext tenant,
    IAuditContext actor,
    ZelvoDbContext db,
    ISecretProtector secrets,
    AiProviderClient client,
    TimeProvider time) : IAiGateway
{
    /// <summary>What the test button asks.</summary>
    public const string TestInstructions = "This is a connection test. Answer with the single word OK.";

    private const int TestMaxOutputTokens = 16;

    /// <summary>Secret purpose of a provider's API key.</summary>
    public static string KeyPurpose(Guid providerId) => $"ai.provider:{providerId:N}.apiKey";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiFeatureAvailability>> AvailabilityAsync(CancellationToken cancellationToken = default)
    {
        RequireTenant();
        var settings = await db.AiFeatureSettings.AsNoTracking().ToDictionaryAsync(f => f.Feature, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var active = await db.AiProviders.AsNoTracking().Where(p => p.Status == AiProviderStatus.Active).Select(p => p.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var budget = await AiBudget.StatusAsync(db, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return [.. AiFeatures.All.Select(feature =>
        {
            var on = settings.TryGetValue(feature, out var s) && s.Enabled && s.ProviderId is { } id && active.Contains(id) && !string.IsNullOrEmpty(s.Model);
            return !on
                ? new AiFeatureAvailability(feature, false, "ai-off")
                : budget.State == AiBudgetState.Reached ? new AiFeatureAvailability(feature, false, "limit-reached") : new AiFeatureAvailability(feature, true, null);
        })];
    }

    /// <inheritdoc />
    public async Task<AiResult> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTenant();
        if (!AiFeatures.IsFeature(request.Feature))
        {
            throw new ArgumentException($"Unknown AI feature '{request.Feature}'.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Instructions);
        if (request.MaxOutputTokens is < 1 or > 32_000)
        {
            throw new ArgumentException("MaxOutputTokens must be 1–32,000.", nameof(request));
        }

        var setting = await db.AiFeatureSettings.AsNoTracking().SingleOrDefaultAsync(f => f.Feature == request.Feature, cancellationToken).ConfigureAwait(false);
        var provider = setting is { Enabled: true, ProviderId: { } providerId }
            ? await db.AiProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == providerId && p.Status == AiProviderStatus.Active, cancellationToken).ConfigureAwait(false)
            : null;
        if (provider is null || string.IsNullOrEmpty(setting?.Model))
        {
            // Nothing is sent, so nothing is logged: features check availability first.
            return Failure("ai-off", "This AI feature is off. An administrator turns it on in Settings → AI.", null);
        }

        var budget = await AiBudget.StatusAsync(db, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (budget.State == AiBudgetState.Reached)
        {
            var blocked = new AiCall
            {
                Feature = request.Feature,
                ProviderId = provider.Id,
                ProviderKind = provider.Kind,
                Model = setting.Model,
                Outcome = AiCallOutcome.LimitReached,
                ErrorCode = "limit-reached",
                Error = $"The monthly AI limit of {AiBudget.Format(budget.LimitUsd!.Value)} USD is reached.",
                EntityType = request.EntityType,
                EntityId = request.EntityId,
            };
            var id = await RecordAsync(blocked, cancellationToken).ConfigureAwait(false);
            return Failure("limit-reached", blocked.Error, id);
        }

        return await CallAsync(provider, setting.Model!, request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AiResult> TestAsync(Guid providerId, string model, CancellationToken cancellationToken = default)
    {
        RequireTenant();
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var provider = await db.AiProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == providerId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The AI provider does not exist.");
        var request = new AiRequest
        {
            Feature = AiFeatures.Test,
            Instructions = TestInstructions,
            Inputs = [new AiInput("test", "Connection test from Settings → AI.")],
            MaxOutputTokens = TestMaxOutputTokens,
            EntityType = "ai_provider",
            EntityId = providerId.ToString(),
        };
        var result = await CallAsync(provider, model.Trim(), request, cancellationToken).ConfigureAwait(false);

        await using var scope = CallerScope();
        var scoped = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        if (await scoped.AiProviders.SingleOrDefaultAsync(p => p.Id == providerId, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            stored.LastTestAt = time.GetUtcNow();
            stored.LastTestSucceeded = result.Succeeded;
            stored.LastTestError = result.Succeeded ? null : Cut($"{result.ErrorCode}: {result.Error}", AiProvider.ErrorMaxLength);
            await scoped.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListModelsAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        RequireTenant();
        var provider = await db.AiProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == providerId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The AI provider does not exist.");
        return await client.ListModelsAsync(await ConnectionAsync(provider, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the transcription asks (the provider hears both languages).</summary>
    public const string TranscriptionInstructions =
        "Transcribe this voice note word for word in the language spoken: Modern Standard Arabic, Gulf Arabic or English. Answer with the transcript only.";

    /// <inheritdoc />
    public async Task<AiResult> TranscribeAsync(byte[] audio, string contentType, string? entityType, string? entityId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(contentType);
        RequireTenant();
        var setting = await db.AiFeatureSettings.AsNoTracking().SingleOrDefaultAsync(f => f.Feature == AiFeatures.Transcription, cancellationToken).ConfigureAwait(false);
        var provider = setting is { Enabled: true, ProviderId: { } providerId }
            ? await db.AiProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == providerId && p.Status == AiProviderStatus.Active, cancellationToken).ConfigureAwait(false)
            : null;
        if (provider is null || string.IsNullOrEmpty(setting?.Model))
        {
            return Failure("ai-off", "This AI feature is off. An administrator turns it on in Settings → AI.", null);
        }

        var call = new AiCall
        {
            Feature = AiFeatures.Transcription,
            ProviderId = provider.Id,
            ProviderKind = provider.Kind,
            Model = setting.Model!,
            EntityType = Cut(entityType, AiCall.EntityMaxLength),
            EntityId = Cut(entityId, AiCall.EntityMaxLength),
            Prompt = $"[system]\n{TranscriptionInstructions}\n\n[audio]\n{contentType}, {audio.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes",
            CorrelationId = Activity.Current?.TraceId.ToString(),
        };
        var budget = await AiBudget.StatusAsync(db, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (budget.State == AiBudgetState.Reached)
        {
            call.Outcome = AiCallOutcome.LimitReached;
            call.ErrorCode = "limit-reached";
            call.Error = $"The monthly AI limit of {AiBudget.Format(budget.LimitUsd!.Value)} USD is reached.";
            return Failure("limit-reached", call.Error, await RecordAsync(call, cancellationToken).ConfigureAwait(false));
        }

        using var activity = AiTelemetry.Source.StartActivity("ai.transcribe " + setting.Model, ActivityKind.Client);
        activity?.SetTag("gen_ai.operation.name", "transcription");
        activity?.SetTag("gen_ai.system", AiTelemetry.SystemName(provider.Kind));
        activity?.SetTag("gen_ai.request.model", setting.Model);
        activity?.SetTag("zelvo.ai.feature", AiFeatures.Transcription);
        var watch = Stopwatch.StartNew();
        string? text = null;
        try
        {
            var response = await client.TranscribeAsync(await ConnectionAsync(provider, cancellationToken).ConfigureAwait(false), setting.Model!, audio, contentType,
                TranscriptionInstructions, cancellationToken).ConfigureAwait(false);
            text = response.Text.Trim();
            call.InputTokens = response.InputTokens;
            call.OutputTokens = response.OutputTokens;
            // The log keeps what the AI heard with personal data masked.
            call.Response = Cut(new PersonalDataPseudonymizer().Mask(text), AiCall.TextMaxLength);
            call.Outcome = text.Length > 0 ? AiCallOutcome.Succeeded : AiCallOutcome.Rejected;
            if (text.Length == 0)
            {
                call.ErrorCode = "empty";
                call.Error = "The provider heard nothing.";
            }
        }
        catch (AiProviderException ex)
        {
            call.Outcome = AiCallOutcome.Failed;
            call.ErrorCode = ex.Code;
            call.Error = Cut(ex.Message, AiCall.ErrorMaxLength);
        }
        catch (SecretDecryptionException)
        {
            call.Outcome = AiCallOutcome.Failed;
            call.ErrorCode = "key-unreadable";
            call.Error = "The stored API key cannot be decrypted. Enter it again in Settings → AI.";
        }

        call.DurationMs = AiProviderClient.Elapsed(watch);
        var price = await db.AiModelPrices.AsNoTracking().SingleOrDefaultAsync(p => p.ProviderId == provider.Id && p.Model == setting.Model, cancellationToken).ConfigureAwait(false);
        call.CostUsd = AiBudget.Cost(call.InputTokens, call.OutputTokens, price?.InputPerMillion, price?.OutputPerMillion);
        activity?.SetTag("zelvo.ai.outcome", call.Outcome.ToString());
        AiTelemetry.Record(call);
        var id = await RecordAsync(call, cancellationToken).ConfigureAwait(false);
        return call.Outcome == AiCallOutcome.Succeeded
            ? new AiResult(true, text, null, null, id, call.InputTokens, call.OutputTokens, call.CostUsd, call.DurationMs, false, false)
            : new AiResult(false, null, call.ErrorCode, call.Error, id, call.InputTokens, call.OutputTokens, call.CostUsd, call.DurationMs, false, false);
    }

    private async Task<AiResult> CallAsync(AiProvider provider, string model, AiRequest request, CancellationToken cancellationToken)
    {
        // Mask, then guard. The same placeholders are used across the whole call.
        var pseudonyms = new PersonalDataPseudonymizer();
        foreach (var value in request.PersonalValues)
        {
            pseudonyms.AddKnownValue(value);
        }

        var marker = PromptGuard.NewMarker();
        var injection = request.Inputs.Any(i => PromptGuard.LooksLikeInjection(i.Text))
            || request.Conversation.Any(m => m.Role == AiRole.User && PromptGuard.LooksLikeInjection(m.Text));
        var messages = new List<AiMessage>();
        foreach (var turn in request.Conversation)
        {
            var text = pseudonyms.Mask(turn.Text);
            messages.Add(turn.Role == AiRole.User ? new AiMessage(AiRole.User, PromptGuard.Block(marker, "message", text)) : new AiMessage(AiRole.Assistant, text));
        }

        if (request.Inputs.Count > 0)
        {
            var blocks = string.Join("\n\n", request.Inputs.Select(i => PromptGuard.Block(marker, i.Label, pseudonyms.Mask(i.Text))));
            if (messages.Count > 0 && messages[^1].Role == AiRole.User)
            {
                messages[^1] = messages[^1] with { Text = messages[^1].Text + "\n\n" + blocks };
            }
            else
            {
                messages.Add(new AiMessage(AiRole.User, blocks));
            }
        }

        if (messages.Count == 0 || messages[^1].Role != AiRole.User)
        {
            throw new ArgumentException("A request needs inputs or a conversation that ends with the person's turn.", nameof(request));
        }

        var system = PromptGuard.System(request.Instructions + (request.Json ? "\nAnswer with one JSON object only, without any other text." : string.Empty), marker);
        var providerRequest = new AiProviderRequest(model, system, messages, request.MaxOutputTokens, request.Json);
        var call = new AiCall
        {
            Feature = request.Feature,
            ProviderId = provider.Id,
            ProviderKind = provider.Kind,
            Model = model,
            EntityType = Cut(request.EntityType, AiCall.EntityMaxLength),
            EntityId = Cut(request.EntityId, AiCall.EntityMaxLength),
            InjectionSuspected = injection,
            MaskedValues = pseudonyms.MaskedCount,
            Prompt = Cut(PromptText(providerRequest), AiCall.TextMaxLength),
            CorrelationId = Activity.Current?.TraceId.ToString(),
        };

        using var activity = AiTelemetry.Source.StartActivity("ai.chat " + model, ActivityKind.Client);
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("gen_ai.system", AiTelemetry.SystemName(provider.Kind));
        activity?.SetTag("gen_ai.request.model", model);
        activity?.SetTag("zelvo.ai.feature", request.Feature);

        var watch = Stopwatch.StartNew();
        string? answer = null;
        var truncated = false;
        try
        {
            var connection = await ConnectionAsync(provider, cancellationToken).ConfigureAwait(false);
            var response = await client.CompleteAsync(connection, providerRequest, cancellationToken).ConfigureAwait(false);
            call.InputTokens = response.InputTokens;
            call.OutputTokens = response.OutputTokens;
            call.Response = Cut(response.Text, AiCall.TextMaxLength);
            truncated = response.Truncated;
            answer = request.Json ? JsonObjectOf(response.Text) : response.Text.Trim();
            if (answer is null)
            {
                call.Outcome = AiCallOutcome.Rejected;
                call.ErrorCode = "invalid-json";
                call.Error = truncated ? "The answer was cut off at the token limit before the JSON ended." : "The answer is not the JSON object the feature needs.";
            }
            else
            {
                call.Outcome = AiCallOutcome.Succeeded;
            }
        }
        catch (AiProviderException ex)
        {
            call.Outcome = AiCallOutcome.Failed;
            call.ErrorCode = ex.Code;
            call.Error = Cut(ex.Message, AiCall.ErrorMaxLength);
        }
        catch (SecretDecryptionException)
        {
            call.Outcome = AiCallOutcome.Failed;
            call.ErrorCode = "key-unreadable";
            call.Error = "The stored API key cannot be decrypted. Enter it again in Settings → AI.";
        }

        call.DurationMs = AiProviderClient.Elapsed(watch);
        var price = await db.AiModelPrices.AsNoTracking().SingleOrDefaultAsync(p => p.ProviderId == provider.Id && p.Model == model, cancellationToken).ConfigureAwait(false);
        call.CostUsd = AiBudget.Cost(call.InputTokens, call.OutputTokens, price?.InputPerMillion, price?.OutputPerMillion);

        activity?.SetTag("gen_ai.usage.input_tokens", call.InputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", call.OutputTokens);
        activity?.SetTag("zelvo.ai.outcome", call.Outcome.ToString());
        if (call.Outcome != AiCallOutcome.Succeeded)
        {
            activity?.SetStatus(ActivityStatusCode.Error, call.ErrorCode);
        }

        AiTelemetry.Record(call);
        var id = await RecordAsync(call, cancellationToken).ConfigureAwait(false);
        return call.Outcome == AiCallOutcome.Succeeded
            ? new AiResult(true, pseudonyms.Restore(answer!), null, null, id, call.InputTokens, call.OutputTokens, call.CostUsd, call.DurationMs, injection, truncated)
            : new AiResult(false, null, call.ErrorCode, call.Error, id, call.InputTokens, call.OutputTokens, call.CostUsd, call.DurationMs, injection, truncated);
    }

    private async Task<AiProviderConnection> ConnectionAsync(AiProvider provider, CancellationToken cancellationToken)
    {
        var key = provider.ApiKeyCiphertext is { } ciphertext
            ? await secrets.UnprotectAsync(KeyPurpose(provider.Id), ciphertext, cancellationToken).ConfigureAwait(false)
            : null;
        return new AiProviderConnection(provider.Kind, provider.Endpoint is { } endpoint ? new Uri(endpoint) : null, provider.ApiVersion, key);
    }

    private async Task<long> RecordAsync(AiCall call, CancellationToken cancellationToken)
    {
        await using var scope = CallerScope();
        var scoped = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<IAuditContext>();
        if (context.ActorType == AuditActorType.User && Guid.TryParse(context.ActorId, out var userId))
        {
            call.UserId = userId;
        }

        scoped.AiCalls.Add(call);
        await scoped.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<IAuditLog>().RecordAsync(AuditCategory.Ai, "ai.call", "ai_call", call.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["feature"] = call.Feature,
                ["providerId"] = call.ProviderId?.ToString(),
                ["model"] = call.Model,
                ["outcome"] = call.Outcome.ToString(),
                ["errorCode"] = call.ErrorCode,
                ["inputTokens"] = call.InputTokens,
                ["outputTokens"] = call.OutputTokens,
                ["costUsd"] = call.CostUsd,
                ["injectionSuspected"] = call.InjectionSuspected,
                ["entityType"] = call.EntityType,
                ["entityId"] = call.EntityId,
            }, cancellationToken).ConfigureAwait(false);
        return call.Id;
    }

    // A scope for the same tenant and actor as the caller.
    private AsyncServiceScope CallerScope()
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant.TenantId!.Value, tenant.Slug!);
        var context = scope.ServiceProvider.GetRequiredService<AuditContext>();
        context.SetActor(actor.ActorType, actor.ActorId, actor.ActorName);
        foreach (var (key, value) in actor.Metadata)
        {
            context.SetMetadata(key, value);
        }

        return scope;
    }

    private void RequireTenant()
    {
        if (tenant.TenantId is null || tenant.Slug is null)
        {
            throw new InvalidOperationException("AI calls need a tenant, but none is set.");
        }
    }

    // What the AI log shows: exactly what was sent, already masked.
    private static string PromptText(AiProviderRequest request) =>
        "[system]\n" + request.System + string.Concat(request.Messages.Select(m => $"\n\n[{m.Role.ToString().ToLowerInvariant()}]\n{m.Text}"));

    // Accepts a JSON object, also inside a ```json fence; returns it compact, or null.
    internal static string? JsonObjectOf(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var start = trimmed.IndexOf('\n', StringComparison.Ordinal);
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            trimmed = start >= 0 && end > start ? trimmed[(start + 1)..end].Trim() : trimmed;
        }

        try
        {
            using var json = JsonDocument.Parse(trimmed);
            return json.RootElement.ValueKind == JsonValueKind.Object ? json.RootElement.GetRawText() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AiResult Failure(string code, string message, long? callId) =>
        new(false, null, code, message, callId, 0, 0, null, 0, false, false);

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
}

/// <summary>Traces and metrics of AI calls (OpenTelemetry GenAI conventions; never prompt or answer text).</summary>
internal static class AiTelemetry
{
    public static readonly ActivitySource Source = new("Zelvo.Ai");

    private static readonly Meter Meter = new("Zelvo.Ai");
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>("zelvo.ai.calls", description: "AI calls by feature, provider and outcome.");
    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>("gen_ai.client.token.usage.total", "{token}", "Tokens used by AI calls.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("gen_ai.client.operation.duration", "s", "Duration of AI calls.");

    public static string SystemName(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Anthropic => "anthropic",
        AiProviderKind.OpenAI => "openai",
        AiProviderKind.AzureOpenAI => "azure.ai.openai",
        AiProviderKind.Gemini => "gcp.gemini",
        _ => "openai_compatible",
    };

    public static void Record(AiCall call)
    {
        var system = call.ProviderKind is { } kind ? SystemName(kind) : "none";
        Calls.Add(1, new("zelvo.ai.feature", call.Feature), new("gen_ai.system", system), new("zelvo.ai.outcome", call.Outcome.ToString()));
        Tokens.Add(call.InputTokens, new("gen_ai.token.type", "input"), new("gen_ai.system", system));
        Tokens.Add(call.OutputTokens, new("gen_ai.token.type", "output"), new("gen_ai.system", system));
        Duration.Record(call.DurationMs / 1000.0, new("gen_ai.system", system), new("zelvo.ai.feature", call.Feature));
    }
}
