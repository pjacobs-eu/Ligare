using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Ligare;

/// <summary>Chooses where the data of a Ligare client lives.</summary>
public sealed class LigareClientBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;
}

public static class LigareServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ILigareContextFactory"/> and a scoped <see cref="ILigareContext"/>. Choose
    /// the backend in <paramref name="configure"/>, e.g. <c>UseEntityFrameworkCore&lt;AppDbContext&gt;()</c>.
    /// </summary>
    public static IServiceCollection AddLigareClient(this IServiceCollection services, Action<LigareClientBuilder> configure)
    {
        configure(new LigareClientBuilder(services));

        if (!services.Any(s => s.ServiceType == typeof(ILigareContextFactory)))
        {
            throw new InvalidOperationException("No Ligare backend was configured. Call e.g. UseEntityFrameworkCore<TContext>() in AddLigareClient.");
        }

        services.AddScoped(sp => sp.GetRequiredService<ILigareContextFactory>().CreateContext());
        return services;
    }

    /// <summary>Registers every <see cref="IEntityRule{T}"/> implementation of an assembly (scoped).</summary>
    public static IServiceCollection AddLigareRules(this IServiceCollection services, Assembly assembly)
    {
        var rules =
            from type in assembly.GetTypes()
            where type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            from contract in type.GetInterfaces()
            where contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEntityRule<>)
            select (contract, type);

        foreach (var (contract, type) in rules)
        {
            services.AddScoped(contract, type);
        }

        return services;
    }
}
