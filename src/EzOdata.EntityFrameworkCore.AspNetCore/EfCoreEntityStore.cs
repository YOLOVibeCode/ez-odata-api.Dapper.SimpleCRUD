using System.Data;
using System.Data.Common;
using EzOdata.Entities.AspNetCore;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.EntityFrameworkCore.AspNetCore;

internal sealed class EfCoreEntityStore<TContext> : IEntityStore where TContext : DbContext
{
    private readonly IReadOnlyDictionary<Type, EntityMap> _maps;
    private bool _completed;

    public EfCoreEntityStore(TContext context, IDbTransaction transaction, IReadOnlyDictionary<Type, EntityMap> maps)
    {
        Context = context;
        Transaction = transaction;
        _maps = maps;
        Connection = (DbConnection)transaction.Connection!;
    }

    public TContext Context { get; }
    public DbConnection Connection { get; }
    public IDbTransaction? Transaction { get; }

    public async Task<T?> GetAsync<T>(object key) where T : class
    {
        var keys = KeyValues<T>(key);
        var entity = await Context.Set<T>().FindAsync(keys);
        Context.ChangeTracker.Clear();
        return entity;
    }

    public async Task<object?> InsertAsync<T>(T entity) where T : class
    {
        Context.Add(entity);
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();
        var map = Map<T>();
        return map.Keys.Count == 1 ? map.Keys[0].Property.GetValue(entity) : null;
    }

    public async Task<int> UpdateAsync<T>(T entity, T? original = null) where T : class
    {
        if (original is null)
        {
            Context.Update(entity);
        }
        else
        {
            Context.Attach(entity);
            var entry = Context.Entry(entity);
            foreach (var property in Map<T>().Properties.Where(p => p.IsUpdatable))
            {
                var current = property.Property.GetValue(entity);
                var prior = property.Property.GetValue(original);
                entry.Property(property.Property.Name).IsModified = !Equals(current, prior);
            }
        }

        var affected = await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();
        // A no-op PATCH/PUT (values already match) still found the row; HTTP treats that as 200, not 404.
        return affected == 0 ? 1 : affected;
    }

    public async Task<int> DeleteAsync<T>(T entity) where T : class
    {
        Context.Attach(entity);
        Context.Remove(entity);
        var affected = await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();
        return affected;
    }

    public void Commit()
    {
        Transaction?.Commit();
        _completed = true;
    }

    public void Rollback()
    {
        if (_completed) return;
        try { Transaction?.Rollback(); }
        catch (InvalidOperationException) { /* already completed */ }
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try { Transaction?.Rollback(); }
            catch (InvalidOperationException) { }
        }

        Transaction?.Dispose();
        await Context.DisposeAsync();
    }

    private object[] KeyValues<T>(object key) where T : class
    {
        var map = Map<T>();
        if (key is T entity)
        {
            return map.Keys.Select(k => k.Property.GetValue(entity)
                ?? throw new InvalidOperationException($"Key '{k.Property.Name}' is null.")).ToArray()!;
        }

        return [key];
    }

    private EntityMap Map<T>() =>
        _maps.TryGetValue(typeof(T), out var map) ? map : EfCoreEntityEngine<TContext>.MapType(Context.Model, typeof(T));
}
