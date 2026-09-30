using System.Reflection;

namespace EzOdata.Entities.AspNetCore;

/// <summary>How an <see cref="IEntityEngine"/> maps one entity type to a table.</summary>
public sealed class EntityMap
{
    /// <summary>Creates a map for <paramref name="entityType"/>.</summary>
    public EntityMap(Type entityType, string tableName, string? schema, IReadOnlyList<EntityPropertyMap> properties,
        string? quotedTableName = null)
    {
        EntityType = entityType;
        TableName = tableName;
        Schema = schema;
        QuotedTableName = quotedTableName ?? tableName;
        Properties = properties;
        Keys = properties.Where(p => p.IsKey).ToList();
    }

    /// <summary>The entity CLR type.</summary>
    public Type EntityType { get; }

    /// <summary>Unquoted table name.</summary>
    public string TableName { get; }

    /// <summary>Unquoted schema, or null when unspecified.</summary>
    public string? Schema { get; }

    /// <summary>Quoted table identifier, for error messages.</summary>
    public string QuotedTableName { get; }

    /// <summary>Mapped properties.</summary>
    public IReadOnlyList<EntityPropertyMap> Properties { get; }

    /// <summary>Key properties, in primary-key order when the engine provides it.</summary>
    public IReadOnlyList<EntityPropertyMap> Keys { get; }
}

/// <summary>How one entity property maps to a column.</summary>
public sealed class EntityPropertyMap
{
    /// <summary>Creates a property map.</summary>
    public EntityPropertyMap(PropertyInfo property, string columnName, bool isKey, bool isInsertable, bool isUpdatable,
        bool isSelectable = true)
    {
        Property = property;
        ColumnName = columnName;
        IsKey = isKey;
        IsInsertable = isInsertable;
        IsUpdatable = isUpdatable;
        IsSelectable = isSelectable;
    }

    /// <summary>The entity property.</summary>
    public PropertyInfo Property { get; }

    /// <summary>Unquoted column name.</summary>
    public string ColumnName { get; }

    /// <summary>Part of the primary key.</summary>
    public bool IsKey { get; }

    /// <summary>The engine writes this column on insert.</summary>
    public bool IsInsertable { get; }

    /// <summary>The engine writes this column on update.</summary>
    public bool IsUpdatable { get; }

    /// <summary>The engine reads this column.</summary>
    public bool IsSelectable { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Property.Name} → {ColumnName}";
}
