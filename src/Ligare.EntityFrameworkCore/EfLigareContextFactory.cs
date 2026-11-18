using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ligare.EntityFrameworkCore;

/// <summary>Creates direct-mode contexts from the application's <see cref="IDbContextFactory{TContext}"/>.</summary>
public sealed class EfLigareContextFactory<TContext>(IDbContextFactory<TContext> dbContextFactory, IServiceProvider services)
    : ILigareContextFactory
    where TContext : DbContext
{
    public ILigareContext CreateContext() => new EfLigareContext(dbContextFactory.CreateDbContext(), services);
}

public static class LigareClientBuilderExtensions
{
    /// <summary>
    /// Direct mode: queries and saves go to <typeparamref name="TContext"/> in this process. Register the
    /// context with <c>AddDbContextFactory</c>; use <c>ServiceLifetime.Scoped</c> when it depends on scoped
    /// services such as the current tenant.
    /// </summary>
    public static LigareClientBuilder UseEntityFrameworkCore<TContext>(this LigareClientBuilder builder)
        where TContext : DbContext
    {
        builder.Services.AddScoped<ILigareContextFactory, EfLigareContextFactory<TContext>>();
        return builder;
    }
}
