using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace Ligare.Remote;

/// <summary>
/// Executes EF Core query expressions against a Ligare server. Plugged into EF Core's
/// <see cref="EntityQueryProvider"/>, so EF Core's own operators (Include, ToListAsync, FirstAsync, …)
/// build the expressions this class receives.
/// </summary>
internal sealed class RemoteQueryCompiler(RemoteLigareContext context) : IQueryCompiler
{
    private static readonly MethodInfo EnumerateMethod = typeof(RemoteLigareContext).GetMethod(nameof(RemoteLigareContext.EnumerateAsync))!;
    private static readonly MethodInfo ExecuteMethod = typeof(RemoteLigareContext).GetMethod(nameof(RemoteLigareContext.ExecuteAsync))!;

    public TResult Execute<TResult>(Expression query) =>
        throw new NotSupportedException("Queries against a Ligare server are asynchronous: use ToListAsync, FirstAsync, CountAsync and so on.");

    public TResult ExecuteAsync<TResult>(Expression query, CancellationToken cancellationToken)
    {
        var remote = QueryTranslator.Translate(query, context.TrackingByDefault);
        var resultType = typeof(TResult);

        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            return (TResult)EnumerateMethod.MakeGenericMethod(resultType.GetGenericArguments()[0])
                .Invoke(context, [remote, cancellationToken])!;
        }

        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return (TResult)ExecuteMethod.MakeGenericMethod(resultType.GetGenericArguments()[0])
                .Invoke(context, [remote, cancellationToken])!;
        }

        throw new NotSupportedException($"Ligare cannot return {resultType.Name} from a query.");
    }

    public Func<QueryContext, TResult> CreateCompiledQuery<TResult>(Expression query) =>
        throw new NotSupportedException("Compiled queries are not supported against a Ligare server.");

    public Func<QueryContext, TResult> CreateCompiledAsyncQuery<TResult>(Expression query) =>
        throw new NotSupportedException("Compiled queries are not supported against a Ligare server.");

    public Expression<Func<QueryContext, TResult>> PrecompileQuery<TResult>(Expression query, bool async) =>
        throw new NotSupportedException("Precompiled queries are not supported against a Ligare server.");
}
