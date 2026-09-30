using System.Data.Common;
using System.Runtime.CompilerServices;
using Dapper;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Schema;
using EzOdata.SimpleCrud;

namespace EzOdata.SimpleCrud.AspNetCore;

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
    public SimpleCrudNaming? Naming { get; set; }
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
    private ISimpleCrud? _client;

    private ServiceModel(ServiceExtension extension, ServiceRuntime runtime, SimpleCRUD.Dialect dialect, SimpleCrudEngine engine,
        SchemaSnapshot schema, Dictionary<string, BoundTable> byTable)
    {
        Extension = extension;
        Runtime = runtime;
        Dialect = dialect;
        Engine = engine;
        Schema = schema;
        _byTable = byTable;
    }

    public ServiceExtension Extension { get; }
    public ServiceRuntime Runtime { get; }
    public SimpleCRUD.Dialect Dialect { get; }
    public SimpleCrudEngine Engine { get; }
    public SchemaSnapshot Schema { get; }
    public IReadOnlyCollection<BoundTable> BoundTables => _byTable.Values;

    public BoundTable? Find(string table) => _byTable.TryGetValue(table, out var bound) ? bound : null;

    public DbConnection CreateConnection() =>
        Extension.ConnectionFactory?.Invoke(Runtime.Connection)
        ?? ProviderConnections.Create(Runtime.ConnectorType, Runtime.Connection);

    /// <summary>A connection-per-call SimpleCRUD client on this service's engine (for hooks during reads).</summary>
    public ISimpleCrud Client => _client ??= Extension.Naming is { } naming
        ? SimpleCrud.For(Dialect).WithNaming(naming).WithConnection(CreateConnection).Build()
        : SimpleCrud.For(Dialect).WithConnection(CreateConnection).Build();

    public static ServiceModel Build(ServiceExtension extension, ServiceRuntime runtime)
    {
        var dialect = ProviderConnections.DialectFor(runtime.ConnectorType);
        var engine = SimpleCrudEngines.For(dialect, extension.Naming);
        var caseSensitive = runtime.ConnectorType == Core.Services.ConnectorTypes.PostgreSql; // SimpleCRUD quotes identifiers
        var errors = new List<string>();
        var replaced = new Dictionary<TableModel, TableModel>();
        var bound = new List<(TableRegistration Registration, SimpleCrudEntityInfo Info, TableModel Table, Dictionary<string, SimpleCrudPropertyInfo> ByColumn)>();

        foreach (var registration in extension.Tables)
        {
            var info = engine.Describe(registration.EntityType);
            var entity = registration.EntityType.Name;

            var table = Match(runtime.Schema.Tables.Where(t => info.Schema is null || string.Equals(t.DbSchema, info.Schema, StringComparison.OrdinalIgnoreCase)),
                t => t.DbName, info.TableName, caseSensitive, out var tableError);
            if (table is null)
            {
                errors.Add($"{entity}: table {info.QuotedTableName} {tableError ?? "was not found in the database schema"} (service '{extension.Name}').");
                continue;
            }

            if (replaced.ContainsKey(table))
            {
                errors.Add($"{entity}: table '{table.ExposedName}' is already mapped by another entity.");
                continue;
            }

            var byColumn = new Dictionary<string, SimpleCrudPropertyInfo>(StringComparer.Ordinal);
            foreach (var property in info.Properties)
            {
                var column = Match(table.Columns, c => c.DbName, property.ColumnName, caseSensitive, out var columnError);
                if (column is null)
                {
                    errors.Add($"{entity}.{property.Property.Name}: column {property.QuotedColumnName} {columnError ?? $"does not exist in '{table.DbName}'"}.");
                    continue;
                }

                byColumn[column.ExposedName] = property;
            }

            if (info.Keys.Count != 1 || table.PrimaryKey.Count != 1)
            {
                errors.Add($"{entity}: needs exactly one key property and a single-column primary key (entity keys: {info.Keys.Count}, table PK columns: {table.PrimaryKey.Count}). Composite keys are not supported yet.");
                continue;
            }

            var keyColumn = byColumn.FirstOrDefault(kv => kv.Value.IsKey).Key;
            if (keyColumn != table.PrimaryKey[0])
            {
                errors.Add($"{entity}: key property {info.Keys[0].Property.Name} maps to '{keyColumn}', but the table's primary key is '{table.PrimaryKey[0]}'.");
                continue;
            }

            var keyType = Nullable.GetUnderlyingType(info.Keys[0].Property.PropertyType) ?? info.Keys[0].Property.PropertyType;
            if (keyType != typeof(int) && keyType != typeof(long) && keyType != typeof(short) && keyType != typeof(uint)
                && keyType != typeof(ulong) && keyType != typeof(ushort) && keyType != typeof(Guid) && keyType != typeof(string))
            {
                errors.Add($"{entity}: SimpleCRUD inserts support int/long/short/Guid/string keys, not {keyType.Name}.");
                continue;
            }

            // Overlay: columns this entity cannot write become read-only (computed) in the API, so the
            // engine rejects writes to them up front instead of SimpleCRUD silently dropping them.
            var columns = table.Columns.Select(c =>
            {
                if (c.IsPrimaryKey || c.IsComputed) return c;
                var writable = byColumn.TryGetValue(c.ExposedName, out var p) && (p.IsInsertable || p.IsUpdatable);
                return writable ? c : c with { IsComputed = true };
            }).ToList();

            replaced[table] = table with { Columns = columns };
            bound.Add((registration, info, replaced[table], byColumn));
        }

        if (errors.Count > 0)
        {
            throw new EzExtensionConfigurationException(extension.Name, errors);
        }

        var schema = runtime.Schema with
        {
            Tables = runtime.Schema.Tables.Select(t => replaced.TryGetValue(t, out var r) ? r : t).ToList(),
        };

        var byTable = bound.ToDictionary(
            b => b.Table.ExposedName,
            b => new BoundTable(b.Registration, new EntityBinding(b.Info, b.Table, b.ByColumn)),
            StringComparer.Ordinal);

        return new ServiceModel(extension, runtime, dialect, engine, schema, byTable);
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
            // Same name in several schemas: prefer the engine's default schema.
            var preferred = loose.OfType<TableModel>().Where(t => t.DbSchema is "" or "public" or "dbo" or "main").ToList();
            if (preferred.Count == 1) return preferred[0] as TItem;
            error = $"is ambiguous ({loose.Count} matches); set [Table(Schema = ...)]";
            return null;
        }

        if (caseSensitive)
        {
            error = $"differs only by case from '{name(loose[0])}' (PostgreSQL identifiers are case-sensitive when quoted, as SimpleCRUD does)";
            return null;
        }

        return loose[0];
    }
}

/// <summary>Entity ↔ table mapping for one bound table (API column names are the table's exposed names).</summary>
internal sealed class EntityBinding
{
    private readonly Dictionary<string, string> _columnByProperty;

    public EntityBinding(SimpleCrudEntityInfo info, TableModel table, Dictionary<string, SimpleCrudPropertyInfo> byColumn)
    {
        Info = info;
        Table = table;
        ByColumn = byColumn;
        Key = info.Keys[0];
        KeyColumn = table.PrimaryKey[0];
        _columnByProperty = byColumn.ToDictionary(kv => kv.Value.Property.Name, kv => kv.Key, StringComparer.Ordinal);
    }

    public SimpleCrudEntityInfo Info { get; }
    public TableModel Table { get; }
    public IReadOnlyDictionary<string, SimpleCrudPropertyInfo> ByColumn { get; }
    public SimpleCrudPropertyInfo Key { get; }
    public string KeyColumn { get; }

    public string? ColumnForProperty(string propertyName) => _columnByProperty.TryGetValue(propertyName, out var c) ? c : null;
}

/// <summary>Entity/table mismatches found when binding (fails startup rather than the first request).</summary>
public sealed class EzExtensionConfigurationException : InvalidOperationException
{
    public EzExtensionConfigurationException(string service, IReadOnlyList<string> problems)
        : base($"ez-odata SimpleCRUD extension for service '{service}' does not match the database:{Environment.NewLine} - "
               + string.Join(Environment.NewLine + " - ", problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}
