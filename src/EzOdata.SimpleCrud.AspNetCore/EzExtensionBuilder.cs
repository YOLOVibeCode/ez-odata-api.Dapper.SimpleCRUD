using System.Data.Common;
using EzOdata.Connectors.Abstractions;
using EzOdata.SimpleCrud;
using Microsoft.Extensions.DependencyInjection;

namespace EzOdata.SimpleCrud.AspNetCore;

/// <summary>Configures which ez-odata services get SimpleCRUD entities and hooks.</summary>
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

public sealed class EzServiceExtensionBuilder
{
    private readonly ServiceExtension _extension;

    internal EzServiceExtensionBuilder(ServiceExtension extension) => _extension = extension;

    /// <summary>
    /// Override how SimpleCRUD connects (default: built from the service's ConnectionSpec, like ez-odata's connectors).
    /// </summary>
    public EzServiceExtensionBuilder UseConnection(Func<ConnectionSpec, DbConnection> factory)
    {
        _extension.ConnectionFactory = factory;
        return this;
    }

    /// <summary>Per-service naming conventions (installed into this service's isolated SimpleCRUD engine).</summary>
    public EzServiceExtensionBuilder UseNaming(SimpleCrudNaming naming)
    {
        _extension.Naming = naming;
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
    /// Take over a table with a SimpleCRUD entity (mapped by SimpleCRUD's own attributes) and optional inline hooks.
    /// Writes go through SimpleCRUD; columns the entity doesn't write become read-only in the API.
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
        _extension.Register(new TableRegistration<T>(sp => ActivatorUtilities.GetServiceOrCreateInstance<THandler>(sp)));
        return this;
    }
}
