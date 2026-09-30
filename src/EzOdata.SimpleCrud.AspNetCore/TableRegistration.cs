using EzOdata.Connectors.Abstractions;
using EzOdata.Core;
using EzOdata.Core.Query;

namespace EzOdata.SimpleCrud.AspNetCore;

/// <summary>Non-generic handle on a <c>Table&lt;T&gt;</c> registration.</summary>
internal abstract class TableRegistration
{
    public abstract Type EntityType { get; }

    /// <summary>A per-operation adapter around a freshly resolved handler.</summary>
    public abstract ITableOperation Begin(IServiceProvider services, EntityBinding binding);
}

internal interface ITableOperation
{
    Task<QueryRequest> BeforeReadAsync(QueryRequest query, EzHookContext ctx);
    Task AfterReadAsync(IReadOnlyList<Row> rows, EzHookContext ctx);
    Task<WriteResult> WriteAsync(WriteExecution execution, FilterNode? readFilter, WriteToolkit kit, EzHookContext ctx);
}

internal sealed class TableRegistration<T> : TableRegistration where T : class, new()
{
    private readonly Func<IServiceProvider, EzTableHandler<T>> _handlerFactory;

    public TableRegistration(Func<IServiceProvider, EzTableHandler<T>> handlerFactory) => _handlerFactory = handlerFactory;

    public override Type EntityType => typeof(T);

    public override ITableOperation Begin(IServiceProvider services, EntityBinding binding) =>
        new Operation(_handlerFactory(services), binding);

    private sealed class Operation(EzTableHandler<T> handler, EntityBinding binding) : ITableOperation
    {
        public Task<QueryRequest> BeforeReadAsync(QueryRequest query, EzHookContext ctx) => handler.BeforeReadAsync(query, ctx);

        public Task AfterReadAsync(IReadOnlyList<Row> rows, EzHookContext ctx) => handler.AfterReadAsync(rows, ctx);

        public async Task<WriteResult> WriteAsync(WriteExecution execution, FilterNode? readFilter, WriteToolkit kit, EzHookContext ctx)
        {
            var write = execution.Write;
            var session = ctx.Session!;

            switch (write.Kind)
            {
                case WriteKind.Insert:
                {
                    var rows = new List<Row>(write.Records.Count);
                    foreach (var record in write.Records)
                    {
                        if (record.Children.Count > 0)
                        {
                            throw new NotSupportedQueryException("Deep insert into a SimpleCRUD-extended table is not supported yet.");
                        }

                        var entity = new T();
                        Apply(entity, record.Values, WriteKind.Insert);
                        await handler.BeforeInsertAsync(entity, ctx);

                        var inserted = await handler.InsertAsync(entity, ctx);
                        var keyValue = AdoptKey(entity, inserted);

                        var row = await kit.ReadByKeyAsync(execution, binding, keyValue)
                            ?? throw new ConnectorException(ErrorCodes.InternalUnmapped, "The inserted row could not be read back.");

                        if (write.InsertVisibilityFilter is { } visibility && !await kit.ExistsAsync(execution, binding, keyValue, visibility))
                        {
                            throw new ConnectorException(ErrorCodes.ForbiddenRowFilter, "Inserted record does not satisfy the role's row filter.");
                        }

                        await handler.AfterInsertAsync(entity, ctx);
                        rows.Add(row);
                    }

                    return new WriteResult(rows.Count, rows);
                }

                case WriteKind.Update:
                case WriteKind.Replace:
                {
                    var (keyFilterValue, key) = Key(write);
                    if (!await VisibleAsync(execution, kit, keyFilterValue, write.Precondition, readFilter)) return new WriteResult(0, []);

                    var original = await session.GetAsync<T>(key);
                    if (original is null) return new WriteResult(0, []);

                    var entity = Clone(original);
                    Apply(entity, write.Records[0].Values, write.Kind);
                    await handler.BeforeUpdateAsync(entity, original, ctx);

                    var affected = await handler.UpdateAsync(entity, ctx);
                    if (affected == 0) return new WriteResult(0, []);

                    var row = await kit.ReadByKeyAsync(execution, binding, keyFilterValue);
                    await handler.AfterUpdateAsync(entity, ctx);
                    return new WriteResult(affected, row is null ? [] : [row]);
                }

                case WriteKind.Delete:
                {
                    var (keyFilterValue, key) = Key(write);
                    if (!await VisibleAsync(execution, kit, keyFilterValue, write.Precondition, readFilter)) return new WriteResult(0, []);

                    var entity = await session.GetAsync<T>(key);
                    if (entity is null) return new WriteResult(0, []);

                    await handler.BeforeDeleteAsync(entity, ctx);
                    var affected = await handler.DeleteAsync(entity, ctx);
                    await handler.AfterDeleteAsync(entity, ctx);
                    return new WriteResult(affected, []);
                }

                default:
                    throw new NotSupportedQueryException($"Unsupported write kind {write.Kind}.");
            }
        }

        /// <summary>Row filters (write precondition) and BeforeRead filters both gate writes: a row you cannot see, you cannot change.</summary>
        private async Task<bool> VisibleAsync(WriteExecution execution, WriteToolkit kit, IReadOnlyDictionary<string, object?> key, FilterNode? precondition, FilterNode? readFilter)
        {
            var predicate = precondition is null ? readFilter : readFilter is null ? precondition : EzFilter.And(precondition, readFilter);
            return predicate is null || await kit.ExistsAsync(execution, binding, key, predicate);
        }

        /// <summary>
        /// The URL key as (column → value) for SQL, plus what SimpleCRUD's <c>Get</c> expects: the value for a
        /// single key, or an instance carrying the key properties for a composite key.
        /// </summary>
        private (IReadOnlyDictionary<string, object?> FilterValues, object EntityKey) Key(WriteRequest write)
        {
            var keys = write.Key?.Values ?? throw new QueryValidationException(ErrorCodes.ValidationInvalidValue, "A key is required.");
            var filter = new Dictionary<string, object?>(StringComparer.Ordinal);
            var probe = new T();
            for (var i = 0; i < binding.KeyColumns.Count; i++)
            {
                var column = binding.KeyColumns[i];
                if (!keys.TryGetValue(column, out var value) || value is null)
                {
                    throw new QueryValidationException(ErrorCodes.ValidationInvalidValue, $"Key '{column}' is required.");
                }

                filter[column] = value;
                binding.Keys[i].Property.SetValue(probe, ValueConverter.To(value, binding.Keys[i].Property.PropertyType, column));
            }

            return (filter, binding.IsComposite ? probe : binding.Keys[0].Property.GetValue(probe)!);
        }

        private void Apply(T entity, IReadOnlyDictionary<string, object?> values, WriteKind kind)
        {
            foreach (var pair in values)
            {
                if (!binding.ByColumn.TryGetValue(pair.Key, out var property))
                {
                    throw new ConnectorException(ErrorCodes.ValidationInvalidValue,
                        $"Property '{pair.Key}' is not writable through {typeof(T).Name}.");
                }

                var allowed = kind == WriteKind.Insert ? property.IsInsertable : property.IsUpdatable;
                if (!allowed)
                {
                    // PUT null-fills columns the client omitted; skip those, but never silently drop a real value.
                    if (kind == WriteKind.Replace && pair.Value is null) continue;
                    throw new ConnectorException(ErrorCodes.ValidationInvalidValue, kind == WriteKind.Insert
                        ? $"Property '{pair.Key}' cannot be set on insert."
                        : $"Property '{pair.Key}' cannot be changed.");
                }

                property.Property.SetValue(entity, ValueConverter.To(pair.Value, property.Property.PropertyType, pair.Key));
            }
        }

        /// <summary>Applies a generated single key to the entity; returns the row's key values (column → value).</summary>
        private IReadOnlyDictionary<string, object?> AdoptKey(T entity, object? insertedKey)
        {
            if (!binding.IsComposite && insertedKey is not null)
            {
                var property = binding.Keys[0].Property;
                var converted = ValueConverter.To(insertedKey, property.PropertyType, binding.KeyColumns[0]);
                if (!Equals(property.GetValue(entity), converted)) property.SetValue(entity, converted);
            }

            var key = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < binding.KeyColumns.Count; i++)
            {
                key[binding.KeyColumns[i]] = binding.Keys[i].Property.GetValue(entity)
                    ?? throw new ConnectorException(ErrorCodes.InternalUnmapped, "Insert did not produce a key.");
            }

            return key;
        }

        private static T Clone(T source)
        {
            var copy = new T();
            foreach (var property in typeof(T).GetProperties())
            {
                if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                {
                    property.SetValue(copy, property.GetValue(source));
                }
            }

            return copy;
        }
    }
}
