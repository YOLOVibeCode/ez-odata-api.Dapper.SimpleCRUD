using System.Security.Claims;
using EzOdata.Core;
using EzOdata.SimpleCrud;

namespace EzOdata.SimpleCrud.AspNetCore;

public enum EzOperation { Read, Insert, Update, Delete }

/// <summary>What a hook sees: who is calling, which table, and SimpleCRUD bound to the right database.</summary>
public sealed class EzHookContext
{
    private readonly EntityBinding _binding;
    private readonly Func<ISimpleCrudOperations> _client;

    internal EzHookContext(string serviceName, EntityBinding binding, EzOperation operation, ClaimsPrincipal user,
        IServiceProvider services, SimpleCrudEngine engine, SimpleCrudSession? session, Func<ISimpleCrudOperations> client,
        CancellationToken cancellationToken)
    {
        ServiceName = serviceName;
        _binding = binding;
        Operation = operation;
        User = user;
        Services = services;
        Engine = engine;
        Session = session;
        _client = client;
        CancellationToken = cancellationToken;
    }

    public string ServiceName { get; }

    /// <summary>The API entity set name (the table's exposed name).</summary>
    public string Table => _binding.Table.ExposedName;

    public EzOperation Operation { get; }

    /// <summary>The caller (from the host's authentication); an anonymous principal when there is none.</summary>
    public ClaimsPrincipal User { get; }

    /// <summary><c>sub</c> or NameIdentifier claim.</summary>
    public string? UserId => User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    /// <summary>Request-scoped services when in an HTTP request, else the root provider.</summary>
    public IServiceProvider Services { get; }

    public SimpleCrudEngine Engine { get; }

    /// <summary>For writes: the API write's own connection + transaction. Null for reads.</summary>
    public SimpleCrudSession? Session { get; }

    /// <summary>
    /// SimpleCRUD for this service's database. During writes it enlists in the API write's transaction
    /// (your extra inserts/updates commit or roll back with it); during reads each call opens a connection.
    /// </summary>
    public ISimpleCrudOperations Crud => Session ?? _client();

    public CancellationToken CancellationToken { get; }

    /// <summary>Per-operation scratch space shared between Before/After hooks.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>The API column name for an entity property (e.g. <c>nameof(Customer.IsDeleted)</c> → <c>is_deleted</c>).</summary>
    public string Column(string propertyName) =>
        _binding.ColumnForProperty(propertyName)
        ?? throw new ArgumentException($"'{propertyName}' is not a mapped property of {_binding.Info.EntityType.Name}.", nameof(propertyName));

    /// <summary>Fail the request with 400 and this message; the transaction rolls back.</summary>
    public void Reject(string message) => throw new EzHookException(ErrorCodes.ValidationInvalidValue, message);

    /// <summary>Fail the request with 403 and this message; the transaction rolls back.</summary>
    public void Forbid(string message) => throw new EzHookException(ErrorCodes.ForbiddenRowFilter, message);
}

/// <summary>Thrown by <see cref="EzHookContext.Reject"/> / <see cref="EzHookContext.Forbid"/>; mapped to an API error.</summary>
public sealed class EzHookException : Exception
{
    public EzHookException(string errorCode, string message) : base(message) => ErrorCode = errorCode;

    public string ErrorCode { get; }
}
