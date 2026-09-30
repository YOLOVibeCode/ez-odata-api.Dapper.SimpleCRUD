#if NET
using System.Data;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace EzOdata.SimpleCrud;

public static class SimpleCrudServiceCollectionExtensions
{
    /// <summary>Registers an <see cref="ISimpleCrud"/> singleton for one database.</summary>
    public static IServiceCollection AddSimpleCrud(this IServiceCollection services, SimpleCRUD.Dialect dialect,
        Func<IServiceProvider, IDbConnection> connectionFactory, Action<SimpleCrudBuilder>? configure = null) =>
        services.AddSingleton(sp => Build(sp, dialect, connectionFactory, configure));

    /// <summary>
    /// Registers a keyed <see cref="ISimpleCrud"/> singleton; inject with <c>[FromKeyedServices("name")]</c>.
    /// Several databases, several dialects, one process.
    /// </summary>
    public static IServiceCollection AddSimpleCrud(this IServiceCollection services, string name, SimpleCRUD.Dialect dialect,
        Func<IServiceProvider, IDbConnection> connectionFactory, Action<SimpleCrudBuilder>? configure = null) =>
        services.AddKeyedSingleton(name, (sp, _) => Build(sp, dialect, connectionFactory, configure));

    private static ISimpleCrud Build(IServiceProvider sp, SimpleCRUD.Dialect dialect,
        Func<IServiceProvider, IDbConnection> connectionFactory, Action<SimpleCrudBuilder>? configure)
    {
        var builder = SimpleCrud.For(dialect).WithConnection(() => connectionFactory(sp));
        configure?.Invoke(builder);
        return builder.Build();
    }
}
#endif
