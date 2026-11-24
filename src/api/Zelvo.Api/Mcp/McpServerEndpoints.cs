using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Ai;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Mcp;
using Zelvo.Integrations;
using Zelvo.Secrets;

namespace Zelvo.Api.Mcp;

/// <summary>
/// Settings → AI → MCP servers (module 9, task E6): customers' own MCP servers whose tools Zelvo's
/// AI may use. Credentials are encrypted and never returned; addresses follow the outgoing-request
/// policy; tools are read from the server and turned on one by one; tools that change data need a
/// person's approval before they run.
/// </summary>
internal static class McpServerEndpoints
{
    public static RouteGroupBuilder MapMcpServerEndpoints(this RouteGroupBuilder group)
    {
        var servers = group.MapGroup("/ai/mcp-servers").WithTags("AI").RequireTenant();
        servers.MapGet("/", ListAsync)
            .WithName("ListMcpServers")
            .WithSummary("Customers' MCP servers that Zelvo's AI may use, with their tools. Credentials are never returned. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<IReadOnlyList<McpServerConnectionResponse>>();

        servers.MapGet("/{id:guid}", GetAsync)
            .WithName("GetMcpServerConnection")
            .WithSummary("One MCP server with its tools. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<McpServerConnectionResponse>();

        servers.MapPost("/", CreateAsync)
            .WithName("CreateMcpServerConnection")
            .WithSummary("Adds a customer's MCP server: name, Streamable HTTP address (HTTPS; internal networks only when the operator allows them), and optionally the header and credential to send (Authorization sends Bearer <credential>). The credential is stored encrypted. Tools are read with refresh. Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces<McpServerConnectionResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        servers.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateMcpServerConnection")
            .WithSummary("Changes name, address, credential or status (Active, Disabled). A new address needs the credential again. Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces<McpServerConnectionResponse>()
            .ProducesValidationProblem();

        servers.MapPost("/{id:guid}/refresh", RefreshAsync)
            .WithName("RefreshMcpServerTools")
            .WithSummary("Tests the connection and reads the server's tools. New tools start off; tools count as changing data unless the server says they are read-only. Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces<McpServerConnectionResponse>();

        servers.MapPut("/{id:guid}/tools", SetToolsAsync)
            .WithName("SetMcpServerTools")
            .WithSummary("Turns tools on or off and says which change data (those run only after a person's approval). Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces<McpServerConnectionResponse>()
            .ProducesValidationProblem();

        return group;
    }

    internal static async Task<IResult> ListAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.McpServers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(cancellationToken)).Select(McpServerConnectionResponse.From).ToList());

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.McpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is { } server
            ? TypedResults.Ok(McpServerConnectionResponse.From(server))
            : NotFound();

    internal static async Task<IResult> CreateAsync(
        CreateMcpServerRequest request, ZelvoDbContext db, McpServerConnector connector, ISecretProtector secrets, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (UrlProblem(connector.Policy, request.Url, out var url) is { } problem)
        {
            return problem;
        }

        if (HeaderProblem(request.AuthHeader, request.Secret) is { } headerProblem)
        {
            return headerProblem;
        }

        var server = new McpServerConnection
        {
            Name = request.Name!.Trim(),
            Url = url!.ToString(),
            AuthHeader = Blank(request.AuthHeader),
        };
        await SetSecretAsync(server, request.Secret, secrets, audit, cancellationToken);
        db.McpServers.Add(server);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/ai/mcp-servers/{server.Id}", McpServerConnectionResponse.From(server));
    }

    internal static async Task<IResult> UpdateAsync(
        Guid id, UpdateMcpServerRequest request, ZelvoDbContext db, McpServerConnector connector, ISecretProtector secrets, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var server = await db.McpServers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return NotFound();
        }

        if (request.Name is { } name)
        {
            server.Name = name.Trim();
        }

        if (request.Url is not null)
        {
            if (UrlProblem(connector.Policy, request.Url, out var url) is { } problem)
            {
                return problem;
            }

            if (url!.ToString() != server.Url)
            {
                // Otherwise anyone allowed to change the address could have the credential sent where they like.
                if (server.SecretCiphertext is not null && string.IsNullOrEmpty(request.Secret))
                {
                    return UserEndpoints.Validation("secret", "A new address needs the credential again.");
                }

                server.Url = url.ToString();
            }
        }

        if (request.AuthHeader is not null)
        {
            server.AuthHeader = Blank(request.AuthHeader);
        }

        if (HeaderProblem(server.AuthHeader, request.Secret) is { } headerProblem)
        {
            return headerProblem;
        }

        if (request.Secret is not null)
        {
            await SetSecretAsync(server, request.Secret, secrets, audit, cancellationToken);
        }

        if (request.Status is { } status)
        {
            server.Status = status;
        }

        server.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(McpServerConnectionResponse.From(server));
    }

    internal static async Task<IResult> RefreshAsync(
        Guid id, ZelvoDbContext db, McpServerConnector connector, AiToolRunner runner, CancellationToken cancellationToken)
    {
        var server = await db.McpServers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        server.LastTestAt = now;
        try
        {
            var remote = await connector.ListToolsAsync(await runner.EndpointAsync(server, cancellationToken), cancellationToken);
            var stored = McpServerToolSetting.Read(server.Tools).ToDictionary(t => t.Name, StringComparer.Ordinal);
            var merged = new List<McpServerToolSetting>();
            foreach (var tool in remote.Where(t => !string.IsNullOrWhiteSpace(t.Name)).DistinctBy(t => t.Name).Take(200))
            {
                stored.TryGetValue(tool.Name, out var previous);
                merged.Add(new McpServerToolSetting
                {
                    Name = Cut(tool.Name, 128),
                    Title = tool.Title is null ? null : Cut(tool.Title, 200),
                    Description = tool.Description is null ? null : Cut(tool.Description, 2000),
                    InputSchema = tool.InputSchema.ValueKind == JsonValueKind.Object && tool.InputSchema.GetRawText().Length <= 16_000 ? tool.InputSchema.Clone() : null,
                    ReadOnlyHint = tool.ReadOnlyHint,
                    Enabled = previous?.Enabled ?? false,
                    ChangesData = previous?.ChangesData ?? !tool.ReadOnlyHint,
                });
            }

            // Tools the server no longer lists keep the administrator's choice but are not used.
            merged.AddRange(stored.Values.Where(t => merged.All(m => m.Name != t.Name)).Select(t => { t.Missing = true; return t; }));
            server.Tools = McpServerToolSetting.Write(merged);
            server.ToolsRefreshedAt = now;
            server.LastTestSucceeded = true;
            server.LastTestError = null;
        }
        catch (McpConnectionException ex)
        {
            server.LastTestSucceeded = false;
            server.LastTestError = Cut($"{ex.Code}: {ex.Message}", McpServerConnection.ErrorMaxLength);
        }
        catch (SecretDecryptionException)
        {
            server.LastTestSucceeded = false;
            server.LastTestError = "secret-unreadable: Enter the credential again.";
        }

        server.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(McpServerConnectionResponse.From(server));
    }

    internal static async Task<IResult> SetToolsAsync(Guid id, SetMcpServerToolsRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var server = await db.McpServers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return NotFound();
        }

        var tools = McpServerToolSetting.Read(server.Tools);
        foreach (var choice in request.Tools!)
        {
            var tool = tools.FirstOrDefault(t => t.Name == choice.Name);
            if (tool is null)
            {
                return UserEndpoints.Validation("tools", $"The server has no tool '{choice.Name}'. Refresh the tools first.");
            }

            tool.Enabled = choice.Enabled!.Value;
            tool.ChangesData = choice.ChangesData!.Value;
        }

        server.Tools = McpServerToolSetting.Write(tools);
        server.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(McpServerConnectionResponse.From(server));
    }

    private static async Task SetSecretAsync(McpServerConnection server, string? secret, ISecretProtector secrets, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (secret is null)
        {
            return;
        }

        server.SecretCiphertext = secret.Length == 0 ? null : await secrets.ProtectAsync(AiToolRunner.SecretPurpose(server.Id), secret, cancellationToken);
        server.SecretSetAt = secret.Length == 0 ? null : DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "ai.mcpServer.secret-changed", "mcp_server", server.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["removed"] = secret.Length == 0 });
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? UrlProblem(OutboundAddressPolicy policy, string? value, out Uri? url)
    {
        url = null;
        if (value is null || value.Length > McpServerConnection.UrlMaxLength)
        {
            return UrlValidation("url-invalid", "Enter the MCP server's full address, for example https://hr.example.ae/mcp.");
        }

        return policy.CheckUrl(value, out url) switch
        {
            null => null,
            "url-not-https" => UrlValidation("url-not-https", "Use an https:// address."),
            "address-not-allowed" => UrlValidation("address-not-allowed", "Zelvo may not connect to this address (this server, internal or reserved networks). The installation's operator can allow internal networks."),
            var code => UrlValidation(code!, "Use a full address without user name, password or #fragment."),
        };
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? HeaderProblem(string? header, string? secret)
    {
        var name = Blank(header);
        if (name is not null && (name.Length > McpServerConnection.HeaderMaxLength || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || !char.IsAsciiLetter(name[0])))
        {
            return UserEndpoints.Validation("authHeader", "Use a header name of letters, digits and hyphens, for example Authorization or X-Api-Key.");
        }

        return name is null && !string.IsNullOrEmpty(secret)
            ? UserEndpoints.Validation("authHeader", "Say which header carries the credential.")
            : null;
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem UrlValidation(string code, string message) =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["url"] = [message] },
            type: ApiProblem.TypeFor("validation-failed"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation-failed", ["urlProblem"] = code });

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Cut(string value, int max) => value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");

    private static IResult NotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "not-found", "There is no such MCP server.");
}

/// <summary>A new MCP server.</summary>
public sealed class CreateMcpServerRequest
{
    /// <summary>Name shown to administrators.</summary>
    [Required]
    [StringLength(McpServerConnection.NameMaxLength, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary>Streamable HTTP address.</summary>
    [Required]
    public string? Url { get; init; }

    /// <summary>Header for the credential, for example <c>Authorization</c>.</summary>
    public string? AuthHeader { get; init; }

    /// <summary>The credential; stored encrypted, never returned.</summary>
    [StringLength(4000)]
    public string? Secret { get; init; }
}

/// <summary>Changes to an MCP server; missing values stay as they are.</summary>
public sealed class UpdateMcpServerRequest
{
    /// <summary>New name.</summary>
    [StringLength(McpServerConnection.NameMaxLength, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary>New address (then the credential again, if one is set).</summary>
    public string? Url { get; init; }

    /// <summary>New header name; empty for none.</summary>
    public string? AuthHeader { get; init; }

    /// <summary>New credential; empty removes it.</summary>
    [StringLength(4000)]
    public string? Secret { get; init; }

    /// <summary>Active or Disabled.</summary>
    public McpServerStatus? Status { get; init; }
}

/// <summary>Choices for the server's tools.</summary>
public sealed class SetMcpServerToolsRequest
{
    /// <summary>One entry per tool to change.</summary>
    [Required]
    [MaxLength(200)]
    public IReadOnlyList<McpServerToolChoice>? Tools { get; init; }
}

/// <summary>An administrator's choice for one tool.</summary>
public sealed class McpServerToolChoice
{
    /// <summary>Tool name on the server.</summary>
    [Required]
    public string? Name { get; init; }

    /// <summary>Zelvo's AI may use it.</summary>
    [Required]
    public bool? Enabled { get; init; }

    /// <summary>It changes data: a person approves each use.</summary>
    [Required]
    public bool? ChangesData { get; init; }
}

/// <summary>A customer's MCP server as administrators see it.</summary>
internal sealed record McpServerConnectionResponse(
    Guid Id, string Name, string Url, string? AuthHeader, bool SecretSet, DateTimeOffset? SecretSetAt, McpServerStatus Status,
    IReadOnlyList<McpServerToolSetting> Tools, DateTimeOffset? ToolsRefreshedAt, DateTimeOffset? LastTestAt, bool? LastTestSucceeded, string? LastTestError,
    DateTimeOffset UpdatedAt)
{
    public static McpServerConnectionResponse From(McpServerConnection s) => new(
        s.Id, s.Name, s.Url, s.AuthHeader, s.SecretCiphertext is not null, s.SecretSetAt, s.Status, McpServerToolSetting.Read(s.Tools),
        s.ToolsRefreshedAt, s.LastTestAt, s.LastTestSucceeded, s.LastTestError, s.UpdatedAt);
}
