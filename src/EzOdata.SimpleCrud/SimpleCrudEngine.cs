using System.Collections.Concurrent;
using System.Data;
using System.Linq.Expressions;
using System.Reflection;
using Dapper;

namespace EzOdata.SimpleCrud;

/// <summary>
/// One SimpleCRUD "instance": a copy of <c>Dapper.SimpleCRUD</c> with its own dialect, identifier
/// quoting, SQL caches and name resolvers. Obtain engines from <see cref="SimpleCrudEngines"/>; they are
/// process-lifetime singletons (one per dialect + naming), created on first use.
/// </summary>
/// <remarks>
/// The API mirrors SimpleCRUD's extension methods one-for-one, with the connection passed first.
/// Calls go through delegates compiled once per (operation, entity type), so after warm-up the cost
/// is a delegate invocation; exceptions surface unwrapped, exactly as SimpleCRUD throws them.
/// </remarks>
public sealed class SimpleCrudEngine
{
    private static readonly Type G = typeof(GenericArg);
    private static readonly MethodInfo BoxTaskMethod =
        typeof(SimpleCrudEngine).GetMethod(nameof(BoxTask), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly Type _crud;
    private readonly Dictionary<string, MethodInfo> _methods;
    private readonly ConcurrentDictionary<(string Op, Type T, Type? K), Delegate> _delegates = new();
    private readonly ConcurrentDictionary<Type, SimpleCrudEntityInfo> _entityInfo = new();

    internal SimpleCrudEngine(Type crudType, SimpleCRUD.Dialect dialect, bool isolated, string name, SimpleCrudNaming? naming)
    {
        _crud = crudType;
        Dialect = dialect;
        IsIsolated = isolated;
        Name = name;
        Naming = naming;
        _methods = ResolveMethods(crudType);
    }

    /// <summary>The dialect this engine was created for.</summary>
    public SimpleCRUD.Dialect Dialect { get; }

    /// <summary>True when this engine runs on its own isolated copy of SimpleCRUD.</summary>
    public bool IsIsolated { get; }

    /// <summary>Diagnostic name, e.g. <c>SimpleCRUD[PostgreSQL]</c>.</summary>
    public string Name { get; }

    /// <summary>Naming conventions installed into this engine, if any.</summary>
    public SimpleCrudNaming? Naming { get; }

    /// <summary>The dialect SimpleCRUD reports inside this engine's copy (diagnostics / tests).</summary>
    public string EffectiveDialect => (string)_crud.GetMethod("GetDialect")!.Invoke(null, null)!;

    /// <summary>The <c>Dapper.SimpleCRUD</c> type this engine drives (the isolated copy, or the process-wide one).</summary>
    internal Type CrudType => _crud;

    /// <summary>Delegates compiled so far (tests: proves the per-call path is cached).</summary>
    internal int CompiledDelegateCount => _delegates.Count;

    public override string ToString() => Name;

    // ---- Get ---------------------------------------------------------------------------------

    public T? Get<T>(IDbConnection connection, object id, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, object, IDbTransaction?, int?, T?>>("Get", typeof(T))(connection, id, transaction, commandTimeout);

    public Task<T?> GetAsync<T>(IDbConnection connection, object id, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, object, IDbTransaction?, int?, Task<T?>>>("GetAsync", typeof(T))(connection, id, transaction, commandTimeout);

    // ---- GetList -----------------------------------------------------------------------------

    public IEnumerable<T> GetList<T>(IDbConnection connection) =>
        Bind<Func<IDbConnection, IEnumerable<T>>>("GetList:all", typeof(T))(connection);

    public IEnumerable<T> GetList<T>(IDbConnection connection, object whereConditions, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, object, IDbTransaction?, int?, IEnumerable<T>>>("GetList:obj", typeof(T))(connection, whereConditions, transaction, commandTimeout);

    public IEnumerable<T> GetList<T>(IDbConnection connection, string conditions, object? parameters = null, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, string, object?, IDbTransaction?, int?, IEnumerable<T>>>("GetList:str", typeof(T))(connection, conditions, parameters, transaction, commandTimeout);

    public Task<IEnumerable<T>> GetListAsync<T>(IDbConnection connection) =>
        Bind<Func<IDbConnection, Task<IEnumerable<T>>>>("GetListAsync:all", typeof(T))(connection);

    public Task<IEnumerable<T>> GetListAsync<T>(IDbConnection connection, object whereConditions, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, object, IDbTransaction?, int?, Task<IEnumerable<T>>>>("GetListAsync:obj", typeof(T))(connection, whereConditions, transaction, commandTimeout);

    public Task<IEnumerable<T>> GetListAsync<T>(IDbConnection connection, string conditions, object? parameters = null, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, string, object?, IDbTransaction?, int?, Task<IEnumerable<T>>>>("GetListAsync:str", typeof(T))(connection, conditions, parameters, transaction, commandTimeout);

    public IEnumerable<T> GetListPaged<T>(IDbConnection connection, int pageNumber, int rowsPerPage, string conditions, string orderby, object? parameters = null, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, int, int, string, string, object?, IDbTransaction?, int?, IEnumerable<T>>>("GetListPaged", typeof(T))(connection, pageNumber, rowsPerPage, conditions, orderby, parameters, transaction, commandTimeout);

    public Task<IEnumerable<T>> GetListPagedAsync<T>(IDbConnection connection, int pageNumber, int rowsPerPage, string conditions, string orderby, object? parameters = null, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, int, int, string, string, object?, IDbTransaction?, int?, Task<IEnumerable<T>>>>("GetListPagedAsync", typeof(T))(connection, pageNumber, rowsPerPage, conditions, orderby, parameters, transaction, commandTimeout);

    // ---- Insert ------------------------------------------------------------------------------

    public int? Insert<TEntity>(IDbConnection connection, TEntity entityToInsert, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, TEntity, IDbTransaction?, int?, int?>>("Insert:1", typeof(TEntity))(connection, entityToInsert, transaction, commandTimeout);

    public TKey Insert<TKey, TEntity>(IDbConnection connection, TEntity entityToInsert, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, TEntity, IDbTransaction?, int?, TKey>>("Insert:2", typeof(TEntity), typeof(TKey))(connection, entityToInsert, transaction, commandTimeout);

    public Task<int?> InsertAsync<TEntity>(IDbConnection connection, TEntity entityToInsert, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, TEntity, IDbTransaction?, int?, Task<int?>>>("InsertAsync:1", typeof(TEntity))(connection, entityToInsert, transaction, commandTimeout);

    public Task<TKey> InsertAsync<TKey, TEntity>(IDbConnection connection, TEntity entityToInsert, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, TEntity, IDbTransaction?, int?, Task<TKey>>>("InsertAsync:2", typeof(TEntity), typeof(TKey))(connection, entityToInsert, transaction, commandTimeout);

    /// <summary>
    /// <c>InsertAsync&lt;TKey, TEntity&gt;</c> with the key type chosen at runtime (e.g. from the entity's key
    /// property). Returns the new key boxed.
    /// </summary>
    public Task<object?> InsertForKeyAsync<TEntity>(IDbConnection connection, TEntity entityToInsert, Type keyType, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, TEntity, IDbTransaction?, int?, Task<object?>>>("InsertAsync:2:boxed", typeof(TEntity), keyType)(connection, entityToInsert, transaction, commandTimeout);

    // ---- Update ------------------------------------------------------------------------------

    public int Update<TEntity>(IDbConnection connection, TEntity entityToUpdate, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, TEntity, IDbTransaction?, int?, int>>("Update", typeof(TEntity))(connection, entityToUpdate, transaction, commandTimeout);

    public Task<int> UpdateAsync<TEntity>(IDbConnection connection, TEntity entityToUpdate, IDbTransaction? transaction = null, int? commandTimeout = null, CancellationToken? token = null) =>
        Bind<Func<IDbConnection, TEntity, IDbTransaction?, int?, CancellationToken?, Task<int>>>("UpdateAsync", typeof(TEntity))(connection, entityToUpdate, transaction, commandTimeout, token);

    // ---- Delete ------------------------------------------------------------------------------

    public int Delete<T>(IDbConnection connection, T entityToDelete, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, T, IDbTransaction?, int?, int>>("Delete:entity", typeof(T))(connection, entityToDelete, transaction, commandTimeout);

    public int Delete<T>(IDbConnection connection, object id, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, object, IDbTransaction?, int?, int>>("Delete:id", typeof(T))(connection, id, transaction, commandTimeout);

    public Task<int> DeleteAsync<T>(IDbConnection connection, T entityToDelete, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, T, IDbTransaction?, int?, Task<int>>>("DeleteAsync:entity", typeof(T))(connection, entityToDelete, transaction, commandTimeout);

    public Task<int> DeleteAsync<T>(IDbConnection connection, object id, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, object, IDbTransaction?, int?, Task<int>>>("DeleteAsync:id", typeof(T))(connection, id, transaction, commandTimeout);

    public int DeleteList<T>(IDbConnection connection, object whereConditions, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, object, IDbTransaction?, int?, int>>("DeleteList:obj", typeof(T))(connection, whereConditions, transaction, commandTimeout);

    public int DeleteList<T>(IDbConnection connection, string conditions, object? parameters = null, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, string, object?, IDbTransaction?, int?, int>>("DeleteList:str", typeof(T))(connection, conditions, parameters, transaction, commandTimeout);

    public Task<int> DeleteListAsync<T>(IDbConnection connection, object whereConditions, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, object, IDbTransaction?, int?, Task<int>>>("DeleteListAsync:obj", typeof(T))(connection, whereConditions, transaction, commandTimeout);

    public Task<int> DeleteListAsync<T>(IDbConnection connection, string conditions, object? parameters = null, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, string, object?, IDbTransaction?, int?, Task<int>>>("DeleteListAsync:str", typeof(T))(connection, conditions, parameters, transaction, commandTimeout);

    // ---- RecordCount -------------------------------------------------------------------------

    public int RecordCount<T>(IDbConnection connection, string conditions = "", object? parameters = null, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, string, object?, IDbTransaction?, int?, int>>("RecordCount:str", typeof(T))(connection, conditions, parameters, transaction, commandTimeout);

    public int RecordCount<T>(IDbConnection connection, object whereConditions, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, object, IDbTransaction?, int?, int>>("RecordCount:obj", typeof(T))(connection, whereConditions, transaction, commandTimeout);

    public Task<int> RecordCountAsync<T>(IDbConnection connection, string conditions = "", object? parameters = null, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, string, object?, IDbTransaction?, int?, Task<int>>>("RecordCountAsync:str", typeof(T))(connection, conditions, parameters, transaction, commandTimeout);

    public Task<int> RecordCountAsync<T>(IDbConnection connection, object whereConditions, IDbTransaction? transaction = null, int? commandTimeout = null) =>
        Bind<Func<IDbConnection, object, IDbTransaction?, int?, Task<int>>>("RecordCountAsync:obj", typeof(T))(connection, whereConditions, transaction, commandTimeout);

    // ---- Metadata ----------------------------------------------------------------------------

    /// <summary>
    /// How THIS engine's SimpleCRUD sees <paramref name="entityType"/>: table name, key, and which
    /// properties it selects / inserts / updates — read from SimpleCRUD itself, so integrations
    /// (like the ez-odata extension) can never drift from what SimpleCRUD will actually emit.
    /// </summary>
    public SimpleCrudEntityInfo Describe(Type entityType) => _entityInfo.GetOrAdd(entityType, t => SimpleCrudEntityInfo.Read(this, t));

    /// <inheritdoc cref="Describe(Type)"/>
    public SimpleCrudEntityInfo Describe<T>() => Describe(typeof(T));

    // ---- delegate plumbing ---------------------------------------------------------------------

    private TDelegate Bind<TDelegate>(string op, Type t, Type? k = null) where TDelegate : Delegate =>
        (TDelegate)_delegates.GetOrAdd((op, t, k), key => Compile(typeof(TDelegate), key));

    private Delegate Compile(Type delegateType, (string Op, Type T, Type? K) key)
    {
        var boxed = key.Op.EndsWith(":boxed", StringComparison.Ordinal);
        var open = _methods[boxed ? key.Op.Substring(0, key.Op.Length - ":boxed".Length) : key.Op];
        var closed = open.GetGenericArguments().Length == 2
            ? open.MakeGenericMethod(key.K!, key.T)
            : open.MakeGenericMethod(key.T);

        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        Expression body = Expression.Call(closed, parameters);

        if (boxed)
        {
            body = Expression.Call(BoxTaskMethod.MakeGenericMethod(key.K!), body);
        }
        else if (body.Type != invoke.ReturnType)
        {
            body = Expression.Convert(body, invoke.ReturnType);
        }

        return Expression.Lambda(delegateType, body, parameters).Compile();
    }

    private static async Task<object?> BoxTask<TKey>(Task<TKey> task) => await task.ConfigureAwait(false);

    private static Dictionary<string, MethodInfo> ResolveMethods(Type crud)
    {
        var conn = typeof(IDbConnection);
        var tx = typeof(IDbTransaction);
        var timeout = typeof(int?);
        var obj = typeof(object);
        var str = typeof(string);
        var i32 = typeof(int);

        var map = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        void Add(string key, string name, int arity, params Type[] ps) => map[key] = Find(crud, name, arity, ps);

        foreach (var suffix in new[] { "", "Async" })
        {
            Add("Get" + suffix, "Get" + suffix, 1, conn, obj, tx, timeout);
            Add($"GetList{suffix}:all", "GetList" + suffix, 1, conn);
            Add($"GetList{suffix}:obj", "GetList" + suffix, 1, conn, obj, tx, timeout);
            Add($"GetList{suffix}:str", "GetList" + suffix, 1, conn, str, obj, tx, timeout);
            Add("GetListPaged" + suffix, "GetListPaged" + suffix, 1, conn, i32, i32, str, str, obj, tx, timeout);
            Add($"Insert{suffix}:1", "Insert" + suffix, 1, conn, G, tx, timeout);
            Add($"Insert{suffix}:2", "Insert" + suffix, 2, conn, G, tx, timeout);
            Add($"Delete{suffix}:entity", "Delete" + suffix, 1, conn, G, tx, timeout);
            Add($"Delete{suffix}:id", "Delete" + suffix, 1, conn, obj, tx, timeout);
            Add($"DeleteList{suffix}:obj", "DeleteList" + suffix, 1, conn, obj, tx, timeout);
            Add($"DeleteList{suffix}:str", "DeleteList" + suffix, 1, conn, str, obj, tx, timeout);
            Add($"RecordCount{suffix}:str", "RecordCount" + suffix, 1, conn, str, obj, tx, timeout);
            Add($"RecordCount{suffix}:obj", "RecordCount" + suffix, 1, conn, obj, tx, timeout);
        }

        Add("Update", "Update", 1, conn, G, tx, timeout);
        Add("UpdateAsync", "UpdateAsync", 1, conn, G, tx, timeout, typeof(CancellationToken?));
        return map;
    }

    private static MethodInfo Find(Type crud, string name, int arity, Type[] parameterTypes)
    {
        foreach (var method in crud.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != name || method.GetGenericArguments().Length != arity) continue;
            var ps = method.GetParameters();
            if (ps.Length != parameterTypes.Length) continue;

            var match = true;
            for (var i = 0; i < ps.Length && match; i++)
            {
                var actual = ps[i].ParameterType;
                match = parameterTypes[i] == G
                    ? actual.IsGenericParameter
                    : !actual.IsGenericParameter && actual == parameterTypes[i];
            }

            if (match) return method;
        }

        throw new MissingMethodException(
            $"Dapper.SimpleCRUD does not expose {name} with the expected signature. This facade targets SimpleCRUD 2.3.x.");
    }

    /// <summary>Placeholder for "the method's generic parameter" in signature matching.</summary>
    private sealed class GenericArg;
}
