using System.Data.Common;
using EzOdata.Connectors.Abstractions;

namespace EzOdata.Entities.AspNetCore;

/// <summary>Configures which ez-odata services get entity-mapped tables and hooks.</summary>
public sealed class EzExtensionBuilder
{
    internal Dictionary<string, ServiceExtension> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extend a service declared with <c>ez.AddService(name, ...)</c>. Tables not listed keep the stock instant API.</summary>
    public EzExtensionBuilder Service(string name, Action<EzServiceExtensionBuilder> configure)
    {
        if (!Services.TryGetValue(name, out var extension))
        {
            Services[name] = extension = new ServiceExtension(name);
        }

        configure(new EzServiceExtensionBuilder(extension));
        return this;
    }
}

/// <summary>Configures one extended ez-odata service.</summary>
public sealed class EzServiceExtensionBuilder
{
    private readonly ServiceExtension _extension;

    internal EzServiceExtensionBuilder(ServiceExtension extension) => _extension = extension;

    /// <summary>The underlying service configuration (engine packages use this).</summary>
    internal ServiceExtension Extension => _extension;

    /// <summary>
    /// Override how the write engine connects (default: built from the service's ConnectionSpec, like ez-odata's connectors).
    /// </summary>
    public EzServiceExtensionBuilder UseConnection(Func<ConnectionSpec, DbConnection> factory)
    {
        _extension.ConnectionFactory = factory;
        return this;
    }

    /// <summary>The write engine for this service. Required: call <c>UseSimpleCrud()</c> or <c>UseEfCore&lt;TContext&gt;()</c>.</summary>
    public EzServiceExtensionBuilder UseEngine(IEntityEngine engine)
    {
        _extension.Engine = engine;
        return this;
    }

    /// <summary>
    /// Expose entity property names (<c>Name</c>) instead of column names (<c>full_name</c>) for the tables
    /// this service maps with entities. Row filters and field rules then use property names too.
    /// </summary>
    public EzServiceExtensionBuilder UsePropertyNames(bool value = true)
    {
        _extension.UsePropertyNames = value;
        return this;
    }

    /// <summary>
    /// Take over a table with an entity and optional inline hooks.
    /// Writes go through the configured engine; columns the entity doesn't write become read-only in the API.
    /// </summary>
    public EzServiceExtensionBuilder Table<T>(Action<EzTableHooks<T>>? hooks = null) where T : class, new()
    {
        var builder = new EzTableHooks<T>();
        hooks?.Invoke(builder);
        var handler = builder.Build();
        _extension.Register(new TableRegistration<T>(_ => handler));
        return this;
    }

    /// <summary>Take over a table with a handler class (created per operation from request services, so it can inject dependencies).</summary>
    public EzServiceExtensionBuilder Table<T, THandler>() where T : class, new() where THandler : EzTableHandler<T>
    {
        _extension.Register(new TableRegistration<T>(sp => ActivatorUtilitiesCreate<THandler>(sp)));
        return this;
    }

    private static THandler ActivatorUtilitiesCreate<THandler>(IServiceProvider sp) =>
        Microsoft.Extensions.DependencyInjection.ActivatorUtilities.GetServiceOrCreateInstance<THandler>(sp);
}
