#if NET
using System.ComponentModel;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Dapper;

namespace EzOdata.SimpleCrud;

/// <summary>
/// Implements an isolated SimpleCRUD copy's <c>ITableNameResolver</c> / <c>IColumnNameResolver</c>
/// (distinct types from the process-wide ones) by forwarding to a <see cref="SimpleCrudNaming"/>,
/// quoting with the engine's dialect.
/// </summary>
/// <remarks>
/// <c>DispatchProxy</c> cannot be used here: it emits every proxy into ONE dynamic assembly per base
/// type, whose reference to <c>Dapper.SimpleCRUD</c> binds to the first copy it sees, so the second
/// isolated engine's interface no longer matches. Instead each engine gets its own tiny dynamic
/// assembly, created inside that engine's load context.
/// </remarks>
internal static class NameResolverProxy
{
    internal static void Install(Type crud, SimpleCRUD.Dialect dialect, SimpleCrudNaming naming, AssemblyLoadContext context)
    {
        using var scope = context.EnterContextualReflection();
        var module = AssemblyBuilder
            .DefineDynamicAssembly(new AssemblyName($"EzOdata.SimpleCrud.Resolvers.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("resolvers");

        var table = Create(module, crud.GetNestedType("ITableNameResolver")!,
            t => SimpleCrudNaming.QuoteQualified(dialect, naming.ResolveTable((Type)t)));
        var column = Create(module, crud.GetNestedType("IColumnNameResolver")!,
            p => SimpleCrudNaming.Quote(dialect, naming.ResolveColumn((PropertyInfo)p)));

        crud.GetMethod("SetTableNameResolver")!.Invoke(null, [table]);
        crud.GetMethod("SetColumnNameResolver")!.Invoke(null, [column]);
    }

    private static object Create(ModuleBuilder module, Type resolverInterface, Func<object, string> resolve)
    {
        var target = resolverInterface.GetMethods().Single();
        var builder = module.DefineType($"{resolverInterface.Name}Impl", TypeAttributes.Public | TypeAttributes.Sealed, typeof(ForwardingResolver));
        builder.AddInterfaceImplementation(resolverInterface);

        var method = builder.DefineMethod(target.Name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(string), [target.GetParameters()[0].ParameterType]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, typeof(ForwardingResolver).GetMethod(nameof(ForwardingResolver.Resolve))!);
        il.Emit(OpCodes.Ret);
        builder.DefineMethodOverride(method, target);

        var instance = (ForwardingResolver)Activator.CreateInstance(builder.CreateType()!)!;
        instance.Resolver = resolve;
        return instance;
    }
}

/// <summary>Base class of the emitted resolvers. Infrastructure; not for direct use.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class ForwardingResolver
{
    internal Func<object, string>? Resolver { get; set; }

    public string Resolve(object argument) => Resolver!(argument);
}
#endif
