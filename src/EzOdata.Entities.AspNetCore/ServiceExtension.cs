using System.Data.Common;
using System.Runtime.CompilerServices;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Schema;

namespace EzOdata.Entities.AspNetCore;

/// <summary>Configuration for one extended ez-odata service, plus its per-schema models.</summary>
internal sealed class ServiceExtension
{
    private readonly Dictionary<Type, TableRegistration> _tables = [];
    private readonly ConditionalWeakTable<SchemaSnapshot, ServiceModel> _byOriginal = new();
    private readonly ConditionalWeakTable<SchemaSnapshot, ServiceModel> _byOverlaid = new();
    private readonly object _gate = new();

    public ServiceExtension(string name) => Name = name;

    public string Name { get; }
    public Func<ConnectionSpec, DbConnection>? ConnectionFactory { get; set; }
    public IEntityEngine? Engine { get; set; }

    /// <summary>Engine-package extras (e.g. SimpleCRUD naming).</summary>
    public Dictionary<string, object?> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>Expose entity property names (not column names) for entity-mapped tables.</summary>
    public bool UsePropertyNames { get; set; }
    public IReadOnlyCollection<TableRegistration> Tables => _tables.Values;

    public void Register(TableRegistration registration) => _tables[registration.EntityType] = registration;

    /// <summary>The overlaid model for a runtime (built once per introspected snapshot).</summary>
    public ServiceModel GetModel(ServiceRuntime runtime)
    {
        lock (_gate)
        {
            if (_byOriginal.TryGetValue(runtime.Schema, out var cached)) return cached;
            var model = ServiceModel.Build(this, runtime);
            _byOriginal.Add(runtime.Schema, model);
            _byOverlaid.Add(model.Schema, model);
            return model;
        }
    }

    /// <summary>The model whose overlaid schema the engine passes back to the connector.</summary>
    public ServiceModel? FindModel(SchemaSnapshot overlaid) =>
        _byOverlaid.TryGetValue(overlaid, out var model) ? model : null;
}

internal sealed record BoundTable(TableRegistration Registration, EntityBinding Binding);

/// <summary>An extended service against one schema snapshot: engine, overlaid schema, table bindings.</summary>
internal sealed class ServiceModel
{
    private readonly Dictionary<string, BoundTable> _byTable;
    private readonly Dictionary<Type, EntityMap> _maps;
    private IEntityStore? _readStore;

    private ServiceModel(ServiceExtension extension, ServiceRuntime runtime, IEntityEngine engine,
        SchemaSnapshot schema, Dictionary<string, BoundTable> byTable, Dictionary<Type, EntityMap> maps)
    {
        Extension = extension;
        Runtime = runtime;
        Engine = engine;
        Schema = schema;
        _byTable = byTable;
        _maps = maps;
    }

    public ServiceExtension Extension { get; }
    public ServiceRuntime Runtime { get; }
    public IEntityEngine Engine { get; }
    public SchemaSnapshot Schema { get; }
    public IReadOnlyCollection<BoundTable> BoundTables => _byTable.Values;
    public IReadOnlyDictionary<Type, EntityMap> Maps => _maps;

    public BoundTable? Find(string table) => _byTable.TryGetValue(table, out var bound) ? bound : null;

    public DbConnection CreateConnection() =>
        Extension.ConnectionFactory?.Invoke(Runtime.Connection)
        ?? ProviderConnections.Create(Runtime.ConnectorType, Runtime.Connection);

    public IEntityStore ReadStore(IServiceProvider services) =>
        _readStore ??= new LazyReadStore(this, services);

    public static ServiceModel Build(ServiceExtension extension, ServiceRuntime runtime)
    {
        var engine = extension.Engine
            ?? throw new InvalidOperationException(
                $"Service '{extension.Name}' has no write engine. Call UseSimpleCrud() or UseEfCore<TContext>() on the service builder.");

        var caseSensitive = runtime.ConnectorType == Core.Services.ConnectorTypes.PostgreSql;
        var errors = new List<string>();
        var bound = new List<(TableRegistration Registration, EntityMap Map, TableModel Table, Dictionary<string, EntityPropertyMap> ByColumn)>();

        foreach (var registration in extension.Tables)
        {
            var map = engine.Map(registration.EntityType, runtime);
            var entity = registration.EntityType.Name;

            var table = Match(runtime.Schema.Tables.Where(t => map.Schema is null || string.Equals(t.DbSchema, map.Schema, StringComparison.OrdinalIgnoreCase)),
                t => t.DbName, map.TableName, caseSensitive, out var tableError);
            if (table is null)
            {
                errors.Add($"{entity}: table {map.QuotedTableName} {tableError ?? "was not found in the database schema"} (service '{extension.Name}').");
                continue;
            }

            if (bound.Any(b => ReferenceEquals(b.Table, table)))
            {
                errors.Add($"{entity}: table '{table.ExposedName}' is already mapped by another entity.");
                continue;
            }

            var byColumn = new Dictionary<string, EntityPropertyMap>(StringComparer.Ordinal);
            foreach (var property in map.Properties)
            {
                var column = Match(table.Columns, c => c.DbName, property.ColumnName, caseSensitive, out var columnError);
                if (column is null)
                {
                    errors.Add($"{entity}.{property.Property.Name}: column \"{property.ColumnName}\" {columnError ?? $"does not exist in '{table.DbName}'"}.");
                    continue;
                }

                byColumn[column.ExposedName] = property;
            }

            if (!ValidateKeys(entity, map, table, byColumn, errors)) continue;
            engine.Validate(map, table, byColumn, errors);
            bound.Add((registration, map, table, byColumn));
        }

        if (errors.Count > 0) throw new EzExtensionConfigurationException(extension.Name, errors);

        var renames = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var replaced = new Dictionary<TableModel, TableModel>();
        foreach (var (_, map, table, byColumn) in bound)
        {
            var rename = extension.UsePropertyNames
                ? byColumn.Where(kv => kv.Key != kv.Value.Property.Name).ToDictionary(kv => kv.Key, kv => kv.Value.Property.Name, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            var clashes = rename.Values.Where(n => table.Columns.Any(c => c.ExposedName == n && !rename.ContainsKey(c.ExposedName))).ToList();
            if (clashes.Count > 0)
            {
                errors.Add($"{map.EntityType.Name}: property name(s) {string.Join(", ", clashes)} collide with other columns of '{table.ExposedName}'.");
                continue;
            }

            string Name(string column) => rename.TryGetValue(column, out var n) ? n : column;
            var columns = table.Columns.Select(c =>
            {
                var writable = c.IsPrimaryKey || c.IsComputed
                    || (byColumn.TryGetValue(c.ExposedName, out var p) && (p.IsInsertable || p.IsUpdatable));
                var overlaid = writable || c.IsComputed ? c : c with { IsComputed = true };
                return overlaid with { ExposedName = Name(c.ExposedName) };
            }).ToList();

            renames[table.ExposedName] = rename;
            replaced[table] = table with
            {
                Columns = columns,
                PrimaryKey = table.PrimaryKey.Select(Name).ToList(),
                UniqueConstraints = table.UniqueConstraints.Select(u => (IReadOnlyList<string>)u.Select(Name).ToList()).ToList(),
            };
        }

        if (errors.Count > 0) throw new EzExtensionConfigurationException(extension.Name, errors);

        string Renamed(string table, string column) =>
            renames.TryGetValue(table, out var map) && map.TryGetValue(column, out var n) ? n : column;
        var tables = runtime.Schema.Tables.Select(t =>
        {
            var current = replaced.TryGetValue(t, out var r) ? r : t;
            if (renames.Count == 0 || current.ForeignKeys.Count == 0) return current;
            return current with
            {
                ForeignKeys = current.ForeignKeys.Select(fk => fk with
                {
                    Columns = fk.Columns.Select(c => Renamed(t.ExposedName, c)).ToList(),
                    RefColumns = fk.RefColumns.Select(c => Renamed(fk.RefTable, c)).ToList(),
                }).ToList(),
            };
        }).ToList();

        var schema = runtime.Schema with { Tables = tables };
        var maps = new Dictionary<Type, EntityMap>();
        var byTable = bound.ToDictionary(
            b => b.Table.ExposedName,
            b =>
            {
                var rename = renames[b.Table.ExposedName];
                var byApiName = b.ByColumn.ToDictionary(kv => rename.TryGetValue(kv.Key, out var n) ? n : kv.Key, kv => kv.Value, StringComparer.Ordinal);
                maps[b.Map.EntityType] = b.Map;
                return new BoundTable(b.Registration, new EntityBinding(b.Map, schema.FindTable(b.Table.ExposedName)!, byApiName));
            },
            StringComparer.Ordinal);

        return new ServiceModel(extension, runtime, engine, schema, byTable, maps);
    }

    private static readonly HashSet<Type> InsertableKeyTypes =
        [typeof(int), typeof(long), typeof(short), typeof(uint), typeof(ulong), typeof(ushort), typeof(Guid), typeof(string)];

    private static bool ValidateKeys(string entity, EntityMap map, TableModel table,
        Dictionary<string, EntityPropertyMap> byColumn, List<string> errors)
    {
        var keyColumns = byColumn.Where(kv => kv.Value.IsKey).Select(kv => kv.Key).ToList();
        if (map.Keys.Count == 0 || keyColumns.Count != map.Keys.Count
            || !new HashSet<string>(keyColumns, StringComparer.Ordinal).SetEquals(table.PrimaryKey))
        {
            errors.Add($"{entity}: key properties ({string.Join(", ", map.Keys.Select(k => k.Property.Name))}) must map exactly to the primary key of '{table.ExposedName}' ({string.Join(", ", table.PrimaryKey)}).");
            return false;
        }

        var firstKeyType = Nullable.GetUnderlyingType(map.Keys[0].Property.PropertyType) ?? map.Keys[0].Property.PropertyType;
        if (!InsertableKeyTypes.Contains(firstKeyType))
        {
            errors.Add($"{entity}: keys of type {firstKeyType.Name} are not supported (use int/long/short/Guid/string).");
            return false;
        }

        return true;
    }

    private static TItem? Match<TItem>(IEnumerable<TItem> items, Func<TItem, string> name, string wanted, bool caseSensitive, out string? error)
        where TItem : class
    {
        error = null;
        var list = items.ToList();
        var exact = list.Where(i => name(i) == wanted).ToList();
        if (exact.Count == 1) return exact[0];

        var loose = exact.Count > 1 ? exact : list.Where(i => string.Equals(name(i), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (loose.Count == 0) return null;

        if (loose.Count > 1)
        {
            var preferred = loose.OfType<TableModel>().Where(t => t.DbSchema is "" or "public" or "dbo" or "main").ToList();
            if (preferred.Count == 1) return preferred[0] as TItem;
            error = $"is ambiguous ({loose.Count} matches); set a schema on the entity mapping";
            return null;
        }

        if (caseSensitive)
        {
            error = $"differs only by case from '{name(loose[0])}' (PostgreSQL identifiers are case-sensitive when quoted)";
            return null;
        }

        return loose[0];
    }

    /// <summary>Opens a short-lived store per call (for read-hook side writes that are not in a write transaction).</summary>
    private sealed class LazyReadStore(ServiceModel model, IServiceProvider services) : IEntityStore
    {
        public DbConnection Connection => throw new InvalidOperationException("The read-side store opens a connection per call.");
        public System.Data.IDbTransaction? Transaction => null;

        public async Task<T?> GetAsync<T>(object key) where T : class
        {
            await using var store = await Open();
            return await store.GetAsync<T>(key);
        }

        public async Task<object?> InsertAsync<T>(T entity) where T : class
        {
            await using var store = await Open();
            var result = await store.InsertAsync(entity);
            store.Commit();
            return result;
        }

        public async Task<int> UpdateAsync<T>(T entity, T? original = null) where T : class
        {
            await using var store = await Open();
            var result = await store.UpdateAsync(entity, original);
            store.Commit();
            return result;
        }

        public async Task<int> DeleteAsync<T>(T entity) where T : class
        {
            await using var store = await Open();
            var result = await store.DeleteAsync(entity);
            store.Commit();
            return result;
        }

        public void Commit() { }
        public void Rollback() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<IEntityStore> Open()
        {
            var connection = model.CreateConnection();
            await connection.OpenAsync();
            var request = new EntityStoreRequest(model.Runtime, connection, services, model.Maps);
            var store = await model.Engine.OpenStoreAsync(request, System.Data.IsolationLevel.Unspecified, CancellationToken.None);
            return new OwningStore(store, connection);
        }

        private sealed class OwningStore(IEntityStore inner, DbConnection connection) : IEntityStore
        {
            public DbConnection Connection => inner.Connection;
            public System.Data.IDbTransaction? Transaction => inner.Transaction;
            public Task<T?> GetAsync<T>(object key) where T : class => inner.GetAsync<T>(key);
            public Task<object?> InsertAsync<T>(T entity) where T : class => inner.InsertAsync(entity);
            public Task<int> UpdateAsync<T>(T entity, T? original = null) where T : class => inner.UpdateAsync(entity, original);
            public Task<int> DeleteAsync<T>(T entity) where T : class => inner.DeleteAsync(entity);
            public void Commit() => inner.Commit();
            public void Rollback() => inner.Rollback();
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }
}

/// <summary>Entity ↔ table mapping for one bound table (keyed by API column names).</summary>
internal sealed class EntityBinding
{
    private readonly Dictionary<string, string> _columnByProperty;

    public EntityBinding(EntityMap map, TableModel table, Dictionary<string, EntityPropertyMap> byColumn)
    {
        Map = map;
        Table = table;
        ByColumn = byColumn;
        KeyColumns = table.PrimaryKey;
        Keys = KeyColumns.Select(c => byColumn[c]).ToList();
        _columnByProperty = byColumn.ToDictionary(kv => kv.Value.Property.Name, kv => kv.Key, StringComparer.Ordinal);
    }

    public EntityMap Map { get; }
    public TableModel Table { get; }
    public IReadOnlyDictionary<string, EntityPropertyMap> ByColumn { get; }

    /// <summary>Primary key columns (API names), aligned with <see cref="Keys"/>.</summary>
    public IReadOnlyList<string> KeyColumns { get; }
    public IReadOnlyList<EntityPropertyMap> Keys { get; }
    public bool IsComposite => Keys.Count > 1;

    public string? ColumnForProperty(string propertyName) => _columnByProperty.TryGetValue(propertyName, out var c) ? c : null;
}

/// <summary>Entity/table mismatches found when binding (fails startup rather than the first request).</summary>
public sealed class EzExtensionConfigurationException : InvalidOperationException
{
    /// <summary>Creates the exception listing every mismatch found for <paramref name="service"/>.</summary>
    public EzExtensionConfigurationException(string service, IReadOnlyList<string> problems)
        : base($"ez-odata entity extension for service '{service}' does not match the database:{Environment.NewLine} - "
               + string.Join(Environment.NewLine + " - ", problems))
    {
        Problems = problems;
    }

    /// <summary>Each entity/table mismatch, one per entry.</summary>
    public IReadOnlyList<string> Problems { get; }
}
