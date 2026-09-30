using System.Data;
using System.Data.Common;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Schema;
using EzOdata.Entities.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EzOdata.EntityFrameworkCore.AspNetCore;

/// <summary>Write engine that maps entities and runs writes through an EF Core <typeparamref name="TContext"/>.</summary>
public sealed class EfCoreEntityEngine<TContext> : IEntityEngine where TContext : DbContext
{
    private readonly EfCoreProviderConfigure<TContext> _configure;
    private readonly List<string> _warnings = [];
    private bool _checkedStrategy;

    /// <summary>Creates an engine using <paramref name="configure"/> to pick the provider.</summary>
    public EfCoreEntityEngine(EfCoreProviderConfigure<TContext> configure) => _configure = configure;

    /// <inheritdoc />
    public string Name => $"EF Core ({typeof(TContext).Name})";

    /// <summary>Startup warnings (value converters, etc.).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <inheritdoc />
    public EntityMap Map(Type entityType, ServiceRuntime runtime)
    {
        using var context = Create(runtime.ConnectorType, connection: null);
        EnsureStrategy(context);
        return MapType(context.Model, entityType, _warnings);
    }

    internal static EntityMap MapType(IModel model, Type entityType, List<string>? warnings = null)
    {
        var entity = model.FindEntityType(entityType)
            ?? throw new InvalidOperationException($"The model has no entity type for {entityType.Name}.");

        GuardShape(entity, warnings);

        var store = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)
            ?? throw new InvalidOperationException($"{entityType.Name} is not mapped to a table.");
        var table = entity.GetTableName()
            ?? throw new InvalidOperationException($"{entityType.Name} has no table name.");
        var pk = entity.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{entityType.Name} has no primary key.");

        var keys = pk.Properties.ToHashSet();
        var properties = new List<EntityPropertyMap>();
        foreach (var property in entity.GetProperties())
        {
            if (property.IsShadowProperty()) continue;
            var clr = property.PropertyInfo;
            if (clr is null) continue;

            var column = property.GetColumnName(store);
            if (string.IsNullOrEmpty(column)) continue;

            if (property.GetValueConverter() is not null)
            {
                warnings?.Add($"{entityType.Name}.{clr.Name}: EF value converter will apply on writes; reads use raw SQL values.");
            }

            var isKey = keys.Contains(property);
            var keyType = Nullable.GetUnderlyingType(clr.PropertyType) ?? clr.PropertyType;
            if (isKey && !AllowedKeyTypes.Contains(keyType))
            {
                throw new EzExtensionConfigurationException("efcore",
                    [$"{entityType.Name}.{clr.Name}: keys of type {keyType.Name} are not supported (use int/long/short/Guid/string)."]);
            }

            properties.Add(new EntityPropertyMap(
                clr,
                column,
                isKey,
                isInsertable: property.GetBeforeSaveBehavior() != PropertySaveBehavior.Ignore,
                isUpdatable: !isKey && property.GetAfterSaveBehavior() != PropertySaveBehavior.Ignore,
                isSelectable: true));
        }

        return new EntityMap(entityType, table, entity.GetSchema(), properties, quotedTableName: $"\"{table}\"");
    }

    /// <inheritdoc />
    public void Validate(EntityMap map, TableModel table, IReadOnlyDictionary<string, EntityPropertyMap> byColumn, List<string> errors)
    {
        foreach (var warning in _warnings)
        {
            // Warnings stay warnings; guards that should fail already threw in Map.
            _ = warning;
        }
    }

    /// <inheritdoc />
    public Task<IEntityStore> OpenStoreAsync(EntityStoreRequest request, IsolationLevel isolation, CancellationToken ct)
    {
        var context = Create(request.Runtime.ConnectorType, request.Connection);
        EnsureStrategy(context);
        var tx = isolation == IsolationLevel.Unspecified
            ? request.Connection.BeginTransaction()
            : request.Connection.BeginTransaction(isolation);
        context.Database.UseTransaction(tx);
        return Task.FromResult<IEntityStore>(new EfCoreEntityStore<TContext>(context, tx, request.Maps));
    }

    private TContext Create(string connectorType, DbConnection? connection)
    {
        var options = new DbContextOptionsBuilder<TContext>();
        _configure(options, connectorType, connection);
        return (TContext)(Activator.CreateInstance(typeof(TContext), options.Options)
            ?? throw new InvalidOperationException($"{typeof(TContext).Name} needs a public constructor that takes DbContextOptions<{typeof(TContext).Name}>."));
    }

    private void EnsureStrategy(DbContext context)
    {
        if (_checkedStrategy) return;
        _checkedStrategy = true;
        if (context.Database.CreateExecutionStrategy().RetriesOnFailure)
        {
            throw new EzExtensionConfigurationException("efcore",
            [
                $"{typeof(TContext).Name} uses a retrying execution strategy (EnableRetryOnFailure). " +
                "The entity extension starts its own transactions; disable retries on this context.",
            ]);
        }
    }

    private static readonly HashSet<Type> AllowedKeyTypes =
        [typeof(int), typeof(long), typeof(short), typeof(uint), typeof(ulong), typeof(ushort), typeof(Guid), typeof(string)];

    private static void GuardShape(IEntityType entity, List<string>? warnings)
    {
        if (entity.IsOwned())
        {
            throw new EzExtensionConfigurationException("efcore",
                [$"{entity.ClrType.Name} is an owned type; v1 of the EF Core engine maps one entity per table only."]);
        }

        if (entity.BaseType is not null || entity.GetDerivedTypes().Any())
        {
            throw new EzExtensionConfigurationException("efcore",
                [$"{entity.ClrType.Name} uses inheritance mapping (TPH/TPT/TPC); that is not supported."]);
        }

        if (entity.GetNavigations().Any(n => n.TargetEntityType.IsOwned()))
        {
            throw new EzExtensionConfigurationException("efcore",
                [$"{entity.ClrType.Name} has owned navigations (table splitting / owned types); that is not supported."]);
        }

        _ = warnings;
    }
}
