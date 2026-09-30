using System.Data.Common;
using EzOdata.Core.Services;
using EzOdata.Entities.AspNetCore;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.EntityFrameworkCore.AspNetCore;

/// <summary>How to configure EF Core for an ez-odata connector type, optionally on an existing connection.</summary>
public delegate void EfCoreProviderConfigure<TContext>(DbContextOptionsBuilder<TContext> options, string connectorType, DbConnection? connection)
    where TContext : DbContext;

/// <summary>EF Core write-engine registration and hook escape hatches.</summary>
public static class EfCoreExtensions
{
    /// <summary>
    /// Use EF Core as this service's write engine. The provider is chosen from the ez-odata connector
    /// (SQLite, PostgreSQL, SQL Server, MySQL) unless <paramref name="configure"/> is supplied.
    /// </summary>
    public static EzServiceExtensionBuilder UseEfCore<TContext>(this EzServiceExtensionBuilder builder,
        EfCoreProviderConfigure<TContext>? configure = null)
        where TContext : DbContext
    {
        return builder.UseEngine(new EfCoreEntityEngine<TContext>(configure ?? DefaultProvider));
    }

    /// <summary>The <typeparamref name="TContext"/> enlisted in this write's transaction.</summary>
    public static TContext DbContext<TContext>(this EzHookContext ctx) where TContext : DbContext =>
        ctx.WriteStore is EfCoreEntityStore<TContext> store
            ? store.Context
            : throw new InvalidOperationException($"This write is not using EF Core with {typeof(TContext).Name}. Call UseEfCore<{typeof(TContext).Name}>().");

    internal static void DefaultProvider<TContext>(DbContextOptionsBuilder<TContext> options, string connectorType, DbConnection? connection)
        where TContext : DbContext
    {
        switch (connectorType)
        {
            case ConnectorTypes.Sqlite:
                if (connection is not null) options.UseSqlite(connection);
                else options.UseSqlite("Data Source=:memory:");
                break;
            case ConnectorTypes.PostgreSql:
                if (connection is not null) options.UseNpgsql(connection);
                else options.UseNpgsql("Host=localhost;Database=ez;Username=ez;Password=ez");
                break;
            case ConnectorTypes.SqlServer:
                if (connection is not null) options.UseSqlServer(connection);
                else options.UseSqlServer("Server=localhost;Database=ez;TrustServerCertificate=True;User Id=sa;Password=ez");
                break;
            case ConnectorTypes.MySql:
                if (connection is not null) options.UseMySQL(connection);
                else options.UseMySQL("Server=localhost;Database=ez;User=ez;Password=ez");
                break;
            default:
                throw new NotSupportedException($"No EF Core provider for connector '{connectorType}'.");
        }
    }
}
