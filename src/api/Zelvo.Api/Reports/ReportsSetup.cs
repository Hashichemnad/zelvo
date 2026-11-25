using Zelvo.Tickets.Reports;

namespace Zelvo.Api.Reports;

/// <summary>Registers reporting (module 10, task F2) in the API.</summary>
internal static class ReportsSetup
{
    public static IServiceCollection AddZelvoReports(this IServiceCollection services)
    {
        services.AddScoped<ReportBuilder>();
        return services;
    }

    public static RouteGroupBuilder MapZelvoReportEndpoints(this RouteGroupBuilder group) =>
        group.MapReportScheduleEndpoints().MapReportEndpoints();
}
