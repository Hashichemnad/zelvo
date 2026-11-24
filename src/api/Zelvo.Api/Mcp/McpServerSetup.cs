using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Mcp;

/// <summary>
/// Zelvo's built-in MCP server (module 9, task E6): Streamable HTTP at <c>/api/v1/mcp</c>,
/// stateless, so any API instance answers. AI agents connect with their own integration
/// credentials (API key in <c>X-Api-Key</c> or an OAuth client's bearer token) and see only the
/// tools turned on for them that their role allows.
/// </summary>
internal static class McpServerSetup
{
    /// <summary>Path of the MCP endpoint.</summary>
    public const string Path = "/api/v1/mcp";

    internal const string AgentItem = "zelvo.mcp.agent";

    private const string Instructions =
        "Zelvo is an IT service management system. Use these tools to search the knowledge base, find and read incidents, " +
        "their status and SLA clocks, read SLA policies, and raise or change incidents. Changes may need a person's approval: " +
        "the answer then has status pending-approval and an actionId to check with get_action_status. Never claim a change " +
        "was made until the answer says so.";

    public static IServiceCollection AddZelvoMcpServer(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<McpRunner>();
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "zelvo", Title = "Zelvo", Version = "1.0" };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler(ListToolsAsync)
            .WithCallToolHandler(CallToolAsync);
        return services;
    }

    public static RouteGroupBuilder MapZelvoMcp(this RouteGroupBuilder v1)
    {
        v1.MapMcp("/mcp");
        return v1;
    }

    /// <summary>Lets only this tenant's AI agents in, while the tenant has the MCP server on.</summary>
    public static IApplicationBuilder UseZelvoMcpAccess(this IApplicationBuilder app) =>
        app.UseWhen(c => c.Request.Path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase), branch => branch.Use(async (context, next) =>
        {
            var tenant = context.RequestServices.GetRequiredService<ITenantContext>();
            var user = context.RequestServices.GetRequiredService<CurrentUser>();
            if (tenant.TenantId is null)
            {
                await ApiProblem.WriteAsync(context, StatusCodes.Status400BadRequest, "tenant-required",
                    $"The MCP server needs a tenant: send the {Tenancy.TenantResolutionMiddleware.HeaderName} header.").ConfigureAwait(false);
                return;
            }

            if (!user.IsSignedIn)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await ApiProblem.WriteAsync(context, StatusCodes.Status401Unauthorized, "unauthenticated",
                    "Connect with the AI agent's API key (X-Api-Key) or OAuth client token.").ConfigureAwait(false);
                return;
            }

            var settings = context.RequestServices.GetRequiredService<TenantSettingsStore>();
            if (!await settings.GetAsync(McpSettings.Enabled, context.RequestAborted).ConfigureAwait(false))
            {
                await ApiProblem.WriteAsync(context, StatusCodes.Status403Forbidden, "mcp-off",
                    "The MCP server is off. An administrator turns it on in Settings → AI → MCP server.").ConfigureAwait(false);
                return;
            }

            var db = context.RequestServices.GetRequiredService<ZelvoDbContext>();
            var agent = user.ApiClient is { } client ? await McpRunner.LoadAgentAsync(db, client.Id, context.RequestAborted).ConfigureAwait(false) : null;
            if (agent is null)
            {
                await ApiProblem.WriteAsync(context, StatusCodes.Status403Forbidden, "not-an-ai-agent",
                    "Only integrations set up as AI agents in Settings → AI → MCP server can use the MCP server.").ConfigureAwait(false);
                return;
            }

            context.Items[AgentItem] = agent;
            context.RequestServices.GetRequiredService<AuditContext>().SetActor(AuditActorType.AiAgent, agent.Client.Id.ToString(), agent.Client.Name);
            await next(context).ConfigureAwait(false);
        }));

    private static (HttpContext Http, McpAgentIdentity Agent) Request(IServiceProvider? services)
    {
        var http = (services ?? throw new InvalidOperationException("The MCP request has no services.")).GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new InvalidOperationException("MCP requests are answered within their HTTP request (stateless mode).");
        return (http, http.Items[AgentItem] as McpAgentIdentity ?? throw new InvalidOperationException("The MCP request has no AI agent."));
    }

    private static ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
    {
        var (http, agent) = Request(request.Services);
        var user = http.RequestServices.GetRequiredService<CurrentUser>();
        return ValueTask.FromResult(new ListToolsResult { Tools = [.. agent.Tools(user).Select(ToProtocol)] });
    }

    private static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var (http, agent) = Request(request.Services);
        var name = request.Params?.Name ?? string.Empty;
        var args = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
        var runner = http.RequestServices.GetRequiredService<McpRunner>();
        var outcome = await runner.CallAsync(agent, name, args, cancellationToken).ConfigureAwait(false);
        var json = outcome.Content.ToJsonString(McpTools.Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }],
            StructuredContent = JsonSerializer.SerializeToElement(outcome.Content, McpTools.Json),
            IsError = outcome.IsError,
        };
    }

    internal static Tool ToProtocol(McpTool tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = JsonSerializer.SerializeToElement(tool.InputSchema),
        Annotations = new ToolAnnotations
        {
            ReadOnlyHint = !tool.ChangesData,
            DestructiveHint = false,
            IdempotentHint = !tool.ChangesData,
            OpenWorldHint = false,
        },
    };
}
