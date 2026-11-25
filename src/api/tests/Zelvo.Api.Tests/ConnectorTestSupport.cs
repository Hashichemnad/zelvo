using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Connectors;
using Zelvo.Data.Connectors;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Connected systems in memory (task E7): a directory, a CRM and a REST API.</summary>
internal sealed class FakeConnectorClients : IConnectorClients
{
    private int _cases;

    public List<DirectoryPerson> Directory { get; } = [];

    public List<CrmContact> Contacts { get; } = [];

    public ConcurrentQueue<CrmCase> Cases { get; } = new();

    public ConcurrentQueue<RestCall> RestCalls { get; } = new();

    public int RestStatus { get; set; } = 200;

    public string RestBody { get; set; } = """{"case":{"id":"C-1001","status":"Open"}}""";

    public List<DateTimeOffset?> ContactReads { get; } = [];

    public Task<IReadOnlyList<DirectoryPerson>> ReadDirectoryAsync(Connector connector, ConnectorSettings settings, string? credential, ExchangeLog log, CancellationToken cancellationToken)
    {
        Assert.False(string.IsNullOrEmpty(credential));
        return Task.FromResult<IReadOnlyList<DirectoryPerson>>([.. Directory]);
    }

    public Task<IReadOnlyList<CrmContact>> ReadContactsAsync(Connector connector, ConnectorSettings settings, string? credential, DateTimeOffset? since, ExchangeLog log, CancellationToken cancellationToken)
    {
        ContactReads.Add(since);
        return Task.FromResult<IReadOnlyList<CrmContact>>([.. Contacts]);
    }

    public Task<string> SaveCaseAsync(Connector connector, ConnectorSettings settings, string? credential, CrmCase ticketCase, ExchangeLog log, CancellationToken cancellationToken)
    {
        Cases.Enqueue(ticketCase);
        log.Exchanges.Add(new HttpExchange("POST", "https://crm.example.com/cases", 201, 5, """{"subject":"x","email":"someone@example.com"}""", "application/json", "{}", "application/json", null));
        return Task.FromResult(ticketCase.CaseId ?? $"CASE-{Interlocked.Increment(ref _cases)}");
    }

    public Task<RestAnswer> CallAsync(Connector connector, RestSettings settings, string? credential, RestCall request, ExchangeLog log, CancellationToken cancellationToken)
    {
        RestCalls.Enqueue(request);
        log.Exchanges.Add(new HttpExchange(request.Method, request.Url.AbsoluteUri, RestStatus, 7, request.Body, "application/json", RestBody, "application/json", null));
        return Task.FromResult(new RestAnswer(RestStatus, "application/json", RestBody, 7));
    }

    public Task<string> TestAsync(Connector connector, ConnectorSettings settings, string? credential, ExchangeLog log, CancellationToken cancellationToken) =>
        Task.FromResult($"connected as {connector.Kind}");
}

/// <summary>Helpers for connector tests.</summary>
internal static class ConnectorApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ApiFactory WithConnectors(this ApiFactory factory, FakeConnectorClients clients) =>
        factory.WithMasterKey().WithServices(s =>
        {
            s.RemoveAll<IConnectorClients>();
            s.AddSingleton<IConnectorClients>(clients);
        });

    /// <summary>Runs the worker's connector job once for <paramref name="tenant"/>.</summary>
    public static Task<int> RunConnectorsAsync(this ApiFactory factory, Tenant tenant) =>
        new ConnectorJob(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<ConnectorOptions>(), NullLogger<ConnectorJob>.Instance)
            .RunTenantAsync(tenant.Id, tenant.Slug, Ct);
}
