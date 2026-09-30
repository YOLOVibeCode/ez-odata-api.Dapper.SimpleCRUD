using System.Reflection;
using System.Text;
using Dapper;

namespace EzOdata.SimpleCrud;

/// <summary>
/// Table/column naming for an isolated engine, replacing SimpleCRUD's global resolvers per engine.
/// Functions return <b>unquoted</b> names; the engine quotes them for its dialect.
/// </summary>
public sealed class SimpleCrudNaming
{
    private readonly Func<Type, string>? _table;
    private readonly Func<PropertyInfo, string>? _column;
    private readonly bool _attributesWin;

    /// <param name="name">Diagnostic name, shown in the engine name.</param>
    /// <param name="table">Unquoted table name for an entity type (may be "schema.table").</param>
    /// <param name="column">Unquoted column name for a property.</param>
    /// <param name="attributesWin">When true, <c>[Table]</c>/<c>[Column]</c> attributes override the functions.</param>
    public SimpleCrudNaming(string name, Func<Type, string>? table = null, Func<PropertyInfo, string>? column = null, bool attributesWin = true)
    {
        Name = name;
        _table = table;
        _column = column;
        _attributesWin = attributesWin;
    }

    /// <summary>Diagnostic name of the convention.</summary>
    public string Name { get; }

    /// <summary>snake_case tables and columns (<c>OrderLine.UnitPrice</c> → <c>order_line.unit_price</c>), attributes still win.</summary>
    public static SimpleCrudNaming SnakeCase { get; } = new("snake_case", t => ToSnake(t.Name), p => ToSnake(p.Name));

    internal string ResolveTable(Type type)
    {
        if (_attributesWin && AttributeValue(type.GetCustomAttributes(true), "TableAttribute", "Name") is { } table)
        {
            var schema = AttributeValue(type.GetCustomAttributes(true), "TableAttribute", "Schema");
            return string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
        }

        return _table?.Invoke(type) ?? type.Name;
    }

    internal string ResolveColumn(PropertyInfo property)
    {
        if (_attributesWin && AttributeValue(property.GetCustomAttributes(true), "ColumnAttribute", "Name") is { } column)
        {
            return column;
        }

        return _column?.Invoke(property) ?? property.Name;
    }

    /// <summary>SimpleCRUD's own encapsulation per dialect (DB2 is unquoted).</summary>
    public static string Quote(SimpleCRUD.Dialect dialect, string identifier) => dialect switch
    {
        SimpleCRUD.Dialect.SQLServer => $"[{identifier}]",
        SimpleCRUD.Dialect.MySQL => $"`{identifier}`",
        SimpleCRUD.Dialect.DB2 => identifier,
        _ => $"\"{identifier}\"",
    };

    internal static string QuoteQualified(SimpleCRUD.Dialect dialect, string name) =>
        string.Join(".", name.Split('.').Select(part => Quote(dialect, part)));

    /// <summary>Strips SimpleCRUD's quoting: <c>"sales"."orders"</c> → (sales, orders).</summary>
    public static (string? Schema, string Name) Unquote(SimpleCRUD.Dialect dialect, string quoted)
    {
        var (open, close) = dialect switch
        {
            SimpleCRUD.Dialect.SQLServer => ("[", "]"),
            SimpleCRUD.Dialect.MySQL => ("`", "`"),
            SimpleCRUD.Dialect.DB2 => ("", ""),
            _ => ("\"", "\""),
        };

        string Strip(string s) =>
            open.Length > 0 && s.StartsWith(open, StringComparison.Ordinal) && s.EndsWith(close, StringComparison.Ordinal)
                ? s.Substring(open.Length, s.Length - open.Length - close.Length)
                : s;

        var separator = open.Length > 0 ? close + "." + open : ".";
        var index = quoted.IndexOf(separator, StringComparison.Ordinal);
        if (index < 0) return (null, Strip(quoted));

        return (Strip(quoted.Substring(0, index + close.Length)), Strip(quoted.Substring(index + close.Length + 1)));
    }

    private static string? AttributeValue(object[] attributes, string attributeName, string property)
    {
        // SimpleCRUD matches attributes by type NAME (its own or DataAnnotations'); mirror that.
        var attribute = attributes.FirstOrDefault(a => a.GetType().Name == attributeName);
        return attribute?.GetType().GetProperty(property)?.GetValue(attribute) as string;
    }

    private static string ToSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])))) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
