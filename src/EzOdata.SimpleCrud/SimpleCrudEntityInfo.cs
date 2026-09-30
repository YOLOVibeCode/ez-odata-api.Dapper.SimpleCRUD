using System.Reflection;
using System.Text;

namespace EzOdata.SimpleCrud;

/// <summary>How one engine's SimpleCRUD maps an entity type (see <see cref="SimpleCrudEngine.Describe(Type)"/>).</summary>
public sealed class SimpleCrudEntityInfo
{
    private SimpleCrudEntityInfo(Type entityType, string quotedTable, string? schema, string table,
        IReadOnlyList<SimpleCrudPropertyInfo> properties)
    {
        EntityType = entityType;
        QuotedTableName = quotedTable;
        Schema = schema;
        TableName = table;
        Properties = properties;
        Keys = properties.Where(p => p.IsKey).ToList();
    }

    public Type EntityType { get; }

    /// <summary>Exactly what SimpleCRUD emits, e.g. <c>[dbo].[Orders]</c>.</summary>
    public string QuotedTableName { get; }

    /// <summary>Unquoted schema from <c>[Table(Schema = ...)]</c> or the naming convention; null when unspecified.</summary>
    public string? Schema { get; }

    /// <summary>Unquoted table name.</summary>
    public string TableName { get; }

    /// <summary>Every property SimpleCRUD reads or writes (its "scaffoldable" properties that it selects).</summary>
    public IReadOnlyList<SimpleCrudPropertyInfo> Properties { get; }

    /// <summary><c>[Key]</c> properties, else the property named <c>Id</c>.</summary>
    public IReadOnlyList<SimpleCrudPropertyInfo> Keys { get; }

    internal static SimpleCrudEntityInfo Read(SimpleCrudEngine engine, Type entityType)
    {
        // These are SimpleCRUD's private helpers — the same code paths Insert/Update/Get use —
        // invoked on the engine's own copy, so quoting, resolvers and version-specific rules all match.
        var crud = engine.CrudType;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;

        string Invoke(string name, object arg, Type parameterType) =>
            (string)crud.GetMethod(name, flags, null, [parameterType], null)!.Invoke(null, [arg])!;

        try
        {
            var quotedTable = Invoke("GetTableName", entityType, typeof(Type));
            var ids = new HashSet<string>(((IEnumerable<PropertyInfo>)crud.GetMethod("GetIdProperties", flags, null, [typeof(Type)], null)!
                .Invoke(null, [entityType])!).Select(p => p.Name));

            var scaffoldable = ((IEnumerable<PropertyInfo>)crud.GetMethod("GetScaffoldableProperties", flags)!
                .MakeGenericMethod(entityType).Invoke(null, null)!).ToList();

            var updatable = new HashSet<string>(((IEnumerable<PropertyInfo>)crud.GetMethod("GetUpdateableProperties", flags)!
                .MakeGenericMethod(entityType).Invoke(null, [null])!).Select(p => p.Name));

            var insertSql = new StringBuilder();
            crud.GetMethod("BuildInsertParameters", flags)!.MakeGenericMethod(entityType).Invoke(null, [insertSql]);
            var insertColumns = new HashSet<string>(insertSql.ToString().Split([", "], StringSplitOptions.RemoveEmptyEntries));

            var properties = new List<SimpleCrudPropertyInfo>();
            foreach (var property in scaffoldable)
            {
                var names = property.GetCustomAttributes(true).Select(a => a.GetType().Name).ToList();
                var selectable = !names.Contains("IgnoreSelectAttribute") && !names.Contains("NotMappedAttribute");
                if (!selectable && !ids.Contains(property.Name)) continue;

                var quotedColumn = Invoke("GetColumnName", property, typeof(PropertyInfo));
                properties.Add(new SimpleCrudPropertyInfo(
                    property,
                    quotedColumn,
                    SimpleCrudNaming.Unquote(engine.Dialect, quotedColumn).Name,
                    isKey: ids.Contains(property.Name),
                    isInsertable: insertColumns.Contains(quotedColumn),
                    isUpdatable: updatable.Contains(property.Name),
                    isSelectable: selectable));
            }

            var (schema, table) = SimpleCrudNaming.Unquote(engine.Dialect, quotedTable);
            return new SimpleCrudEntityInfo(entityType, quotedTable, schema, table, properties);
        }
        catch (Exception ex) when (ex is NullReferenceException or TargetInvocationException or AmbiguousMatchException)
        {
            throw new NotSupportedException(
                $"Could not read SimpleCRUD's mapping for {entityType.Name} from {engine.Name}. This facade reads SimpleCRUD 2.3.x internals.", ex);
        }
    }
}

public sealed class SimpleCrudPropertyInfo
{
    internal SimpleCrudPropertyInfo(PropertyInfo property, string quotedColumn, string column,
        bool isKey, bool isInsertable, bool isUpdatable, bool isSelectable)
    {
        Property = property;
        QuotedColumnName = quotedColumn;
        ColumnName = column;
        IsKey = isKey;
        IsInsertable = isInsertable;
        IsUpdatable = isUpdatable;
        IsSelectable = isSelectable;
    }

    public PropertyInfo Property { get; }
    public string QuotedColumnName { get; }
    public string ColumnName { get; }
    public bool IsKey { get; }

    /// <summary>SimpleCRUD writes this column on insert (identity keys, <c>[IgnoreInsert]</c>, <c>[ReadOnly(true)]</c> are excluded).</summary>
    public bool IsInsertable { get; }

    /// <summary>SimpleCRUD writes this column on update (keys, <c>[IgnoreUpdate]</c>, <c>[ReadOnly(true)]</c> are excluded).</summary>
    public bool IsUpdatable { get; }

    public bool IsSelectable { get; }

    public override string ToString() => $"{Property.Name} → {ColumnName}";
}
