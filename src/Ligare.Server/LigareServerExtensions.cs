using Ligare.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ligare;

public sealed class LigareServerOptions
{
    /// <summary>The most entities one query may return; larger results must be paged with <c>$top</c>/<c>$skip</c>.</summary>
    public int MaxPageSize { get; set; } = 1000;

    /// <summary>The deepest <c>Include</c>/<c>ThenInclude</c> chain a client may request.</summary>
    public int MaxExpansionDepth { get; set; } = 3;
}

public static class LigareServerExtensions
{
    /// <summary>
    /// Serves <typeparamref name="TContext"/> to Ligare clients. Requires an
    /// <c>IDbContextFactory&lt;TContext&gt;</c>; the context's query filters (e.g. per tenant) apply to
    /// every request, and every save runs the registered save rules.
    /// </summary>
    public static IServiceCollection AddLigareServer<TContext>(this IServiceCollection services, Action<LigareServerOptions>? configure = null)
        where TContext : DbContext
    {
        services.AddOptions<LigareServerOptions>().Configure(o => configure?.Invoke(o));
        services.AddSingleton<ILigareServerHost, LigareServerHost<TContext>>();
        return services;
    }

    /// <summary>
    /// Maps the Ligare endpoints under <paramref name="prefix"/>: <c>$model</c>, <c>{set}</c> (OData
    /// queries), <c>{set}/$count</c> and <c>$save</c>. Returns the group, e.g. to require authorization.
    /// </summary>
    public static RouteGroupBuilder MapLigare(this IEndpointRouteBuilder endpoints, string prefix)
    {
        var group = endpoints.MapGroup(prefix);

        group.MapGet("$model", (HttpContext http, ILigareServerHost host) => host.ModelAsync(http));
        group.MapGet("{set}/$count", (HttpContext http, string set, ILigareServerHost host) => host.QueryAsync(http, set, count: true));
        group.MapGet("{set}", (HttpContext http, string set, ILigareServerHost host) => host.QueryAsync(http, set, count: false));
        group.MapPost("$save", (HttpContext http, ILigareServerHost host) => host.SaveAsync(http));

        return group;
    }
}
