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

            if (bound.Any(b => ReferenceEquals(b.Table, table)))
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

            if (!ValidateKeys(entity, info, table, byColumn, errors)) continue;
            bound.Add((registration, info, table, byColumn));
        }

        if (errors.Count > 0) throw new EzExtensionConfigurationException(extension.Name, errors);

        // Overlay 1: columns an entity cannot write become read-only (computed), so the engine rejects
        // writes to them up front instead of SimpleCRUD silently dropping them.
        // Overlay 2 (opt-in): API names become entity property names; SQL keeps using column names.
        var renames = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal); // table → old → new
        var replaced = new Dictionary<TableModel, TableModel>();
        foreach (var (_, info, table, byColumn) in bound)
        {
            var rename = extension.UsePropertyNames
                ? byColumn.Where(kv => kv.Key != kv.Value.Property.Name).ToDictionary(kv => kv.Key, kv => kv.Value.Property.Name, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            var clashes = rename.Values.Where(n => table.Columns.Any(c => c.ExposedName == n && !rename.ContainsKey(c.ExposedName))).ToList();
            if (clashes.Count > 0)
            {
                errors.Add($"{info.EntityType.Name}: property name(s) {string.Join(", ", clashes)} collide with other columns of '{table.ExposedName}'.");
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

        // Foreign keys name columns by their API names on both ends; keep every table consistent.
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
        var byTable = bound.ToDictionary(
            b => b.Table.ExposedName,
            b =>
            {
                var rename = renames[b.Table.ExposedName];
                var byApiName = b.ByColumn.ToDictionary(kv => rename.TryGetValue(kv.Key, out var n) ? n : kv.Key, kv => kv.Value, StringComparer.Ordinal);
                return new BoundTable(b.Registration, new EntityBinding(b.Info, schema.FindTable(b.Table.ExposedName)!, byApiName));
            },
            StringComparer.Ordinal);

        return new ServiceModel(extension, runtime, dialect, engine, schema, byTable);
    }

    private static readonly HashSet<Type> InsertableKeyTypes =
        [typeof(int), typeof(long), typeof(short), typeof(uint), typeof(ulong), typeof(ushort), typeof(Guid), typeof(string)];

    private static bool ValidateKeys(string entity, SimpleCrudEntityInfo info, TableModel table,
        Dictionary<string, SimpleCrudPropertyInfo> byColumn, List<string> errors)
    {
        var keyColumns = byColumn.Where(kv => kv.Value.IsKey).Select(kv => kv.Key).ToList();
        if (info.Keys.Count == 0 || keyColumns.Count != info.Keys.Count
            || !new HashSet<string>(keyColumns, StringComparer.Ordinal).SetEquals(table.PrimaryKey))
        {
            errors.Add($"{entity}: key properties ({string.Join(", ", info.Keys.Select(k => k.Property.Name))}) must map exactly to the primary key of '{table.ExposedName}' ({string.Join(", ", table.PrimaryKey)}).");
            return false;
        }

        var firstKeyType = Nullable.GetUnderlyingType(info.Keys[0].Property.PropertyType) ?? info.Keys[0].Property.PropertyType;
        if (!InsertableKeyTypes.Contains(firstKeyType))
        {
            errors.Add($"{entity}: SimpleCRUD inserts support int/long/short/Guid/string keys, not {firstKeyType.Name}.");
            return false;
        }

        // SimpleCRUD only inserts a composite key's parts when they are client-supplied ([Required], Guid or string).
        if (info.Keys.Count > 1 && info.Keys.FirstOrDefault(k => !k.IsInsertable) is { } missing)
        {
            errors.Add($"{entity}: composite key part {missing.Property.Name} must be [Required] so SimpleCRUD inserts it.");
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

/// <summary>Entity ↔ table mapping for one bound table (keyed by API column names).</summary>
internal sealed class EntityBinding
{
    private readonly Dictionary<string, string> _columnByProperty;

    public EntityBinding(SimpleCrudEntityInfo info, TableModel table, Dictionary<string, SimpleCrudPropertyInfo> byColumn)
    {
        Info = info;
        Table = table;
        ByColumn = byColumn;
        KeyColumns = table.PrimaryKey;
        Keys = KeyColumns.Select(c => byColumn[c]).ToList();
        _columnByProperty = byColumn.ToDictionary(kv => kv.Value.Property.Name, kv => kv.Key, StringComparer.Ordinal);
    }

    public SimpleCrudEntityInfo Info { get; }
    public TableModel Table { get; }
    public IReadOnlyDictionary<string, SimpleCrudPropertyInfo> ByColumn { get; }

    /// <summary>Primary key columns (API names), aligned with <see cref="Keys"/>.</summary>
    public IReadOnlyList<string> KeyColumns { get; }
    public IReadOnlyList<SimpleCrudPropertyInfo> Keys { get; }
    public bool IsComposite => Keys.Count > 1;

    public string? ColumnForProperty(string propertyName) => _columnByProperty.TryGetValue(propertyName, out var c) ? c : null;
}

/// <summary>Entity/table mismatches found when binding (fails startup rather than the first request).</summary>
public sealed class EzExtensionConfigurationException : InvalidOperationException
{
    /// <summary>Creates the exception listing every mismatch found for <paramref name="service"/>.</summary>
    public EzExtensionConfigurationException(string service, IReadOnlyList<string> problems)
        : base($"ez-odata SimpleCRUD extension for service '{service}' does not match the database:{Environment.NewLine} - "
               + string.Join(Environment.NewLine + " - ", problems))
    {
        Problems = problems;
    }

    /// <summary>Each entity/table mismatch, one per entry.</summary>
    public IReadOnlyList<string> Problems { get; }
}
