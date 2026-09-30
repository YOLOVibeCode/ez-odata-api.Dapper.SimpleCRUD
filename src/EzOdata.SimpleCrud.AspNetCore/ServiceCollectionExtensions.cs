using EzOdata.Connectors.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EzOdata.SimpleCrud.AspNetCore;

/// <summary>Registration entry point: <c>services.ExtendEzOData(...)</c>.</summary>
public static class EzODataSimpleCrudServiceCollectionExtensions
{
    /// <summary>
    /// Extend services registered by <c>AddEzOData(...)</c> with SimpleCRUD entities and hooks.
    /// Call it after <c>AddEzOData</c>. It decorates ez-odata's registrations; nothing in ez-odata changes.
    /// </summary>
    public static IServiceCollection ExtendEzOData(this IServiceCollection services, Action<EzExtensionBuilder> configure)
    {
        var builder = new EzExtensionBuilder();
        configure(builder);

        // A second call merges into the first rather than decorating twice.
        if (services.FirstOrDefault(d => d.ServiceType == typeof(EzExtensionSet))?.ImplementationInstance is EzExtensionSet existing)
        {
            foreach (var pair in builder.Services) existing.Services[pair.Key] = pair.Value;
            return services;
        }

        var set = new EzExtensionSet(builder.Services);
        services.AddSingleton(set);
        services.AddHttpContextAccessor();

        Decorate<IServiceRuntimeResolver>(services, (sp, inner) => new ExtendedRuntimeResolver(inner, set));
        Decorate<IConnectorRegistry>(services, (sp, inner) => new ExtendedConnectorRegistry(inner, set, sp));
        services.AddHostedService<ExtensionStartupValidator>();
        return services;
    }

    private static void Decorate<TService>(IServiceCollection services, Func<IServiceProvider, TService, TService> decorate)
        where TService : class
    {
        var index = -1;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TService)) { index = i; break; }
        }

        if (index < 0)
        {
            throw new InvalidOperationException(
                $"{typeof(TService).Name} is not registered. Call services.AddEzOData(...) before services.ExtendEzOData(...).");
        }

        var original = services[index];
        services[index] = ServiceDescriptor.Singleton<TService>(sp =>
        {
            var inner = original.ImplementationInstance as TService
                ?? original.ImplementationFactory?.Invoke(sp) as TService
                ?? (TService)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
            return decorate(sp, inner);
        });
    }
}

/// <summary>
/// Binds every extended service right after ez-odata's startup introspection, so entity/table
/// mismatches stop the app at startup with a precise message instead of failing the first request.
/// </summary>
internal sealed class ExtensionStartupValidator(
    IServiceRuntimeResolver resolver, EzExtensionSet set, ILogger<ExtensionStartupValidator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var extension in set.Services.Values)
        {
            var runtime = await resolver.ResolveAsync(extension.Name, ct);
            if (runtime is null)
            {
                logger.LogWarning(
                    "ez-odata SimpleCRUD extension: service '{Service}' is not available (not declared in AddEzOData, or introspection failed).",
                    extension.Name);
                continue;
            }

            // Resolving through the decorated resolver already bound (and validated) the service.
            var model = extension.FindModel(runtime.Schema);
            logger.LogInformation("ez-odata SimpleCRUD extension: service '{Service}' binds {Tables} table(s) on {Engine}.",
                extension.Name, extension.Tables.Count, model?.Engine.Name);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
