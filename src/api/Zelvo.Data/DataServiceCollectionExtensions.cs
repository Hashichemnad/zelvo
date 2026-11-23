using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data;

/// <summary>Registers the database, tenant context and audit log.</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="ZelvoDbContext"/> connecting with <paramref name="connectionString"/>, plus
    /// scoped <see cref="ITenantContext"/>, <see cref="IAuditContext"/> and <see cref="IAuditLog"/>.
    /// Use the application role's connection string, never the migration owner's.
    /// </summary>
    public static IServiceCollection AddZelvoData(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<AuditContext>();
        services.AddScoped<IAuditContext>(sp => sp.GetRequiredService<AuditContext>());
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddScoped<AuditSaveChangesInterceptor>();
        services.AddScoped<IAuditLog, AuditLog>();

        // Module interceptors (for example webhook events) run before the audit interceptor, so
        // that the rows they add are stamped and checked for the tenant like any other.
        services.AddDbContext<ZelvoDbContext>((sp, options) => options
            .UseZelvoNpgsql(connectionString)
            .AddInterceptors([
                sp.GetRequiredService<TenantConnectionInterceptor>(),
                .. sp.GetServices<IZelvoSaveChangesInterceptor>(),
                sp.GetRequiredService<AuditSaveChangesInterceptor>(),
            ]));

        return services;
    }

    /// <summary>
    /// Adds a save-changes interceptor of a module to every <see cref="ZelvoDbContext"/> of the scope,
    /// ahead of the audit interceptor.
    /// </summary>
    public static IServiceCollection AddZelvoSaveChangesInterceptor<T>(this IServiceCollection services)
        where T : class, IZelvoSaveChangesInterceptor
    {
        services.AddScoped<IZelvoSaveChangesInterceptor, T>();
        return services;
    }

    /// <summary>Provider settings shared by the application, migrations and design-time tools.</summary>
    public static DbContextOptionsBuilder UseZelvoNpgsql(this DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();

    /// <summary>Options for a <see cref="ZelvoDbContext"/> created outside dependency injection.</summary>
    public static DbContextOptions<ZelvoDbContext> CreateOptions(string connectionString, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<ZelvoDbContext>();
        builder.UseZelvoNpgsql(connectionString).AddInterceptors(interceptors);
        return builder.Options;
    }
}

/// <summary>A module's save-changes interceptor, added by <see cref="DataServiceCollectionExtensions.AddZelvoSaveChangesInterceptor{T}"/>.</summary>
public interface IZelvoSaveChangesInterceptor : ISaveChangesInterceptor;
