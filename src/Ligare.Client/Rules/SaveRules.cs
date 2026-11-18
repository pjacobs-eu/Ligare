using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Ligare;

/// <summary>
/// Runs the <see cref="IEntityRule{T}"/>s of a save. Used by every backend, so rules behave the same
/// in direct and remote mode.
/// </summary>
public static class SaveRules
{
    private static readonly ConcurrentDictionary<Type, IRuleInvoker> Invokers = new();

    /// <summary>
    /// Prepares every changed entity once — including entities that preparing others added — then
    /// validates every change of the final change set.
    /// </summary>
    /// <param name="services">The scope the rules are resolved from.</param>
    /// <param name="detectChanges">Builds the change set from the current state of the context.</param>
    /// <param name="cancellationToken">Cancels the rules.</param>
    /// <exception cref="LigareValidationException">A rule rejected a change.</exception>
    public static async Task RunAsync(IServiceProvider services, Func<ChangeSet> detectChanges, CancellationToken cancellationToken)
    {
        var prepared = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (detectChanges().Changes.Where(c => !prepared.Contains(c.Entity)).ToList() is { Count: > 0 } pending)
        {
            foreach (var change in pending)
            {
                prepared.Add(change.Entity);
                await Invoker(change.Entity.GetType()).PrepareAsync(services, change, cancellationToken);
            }
        }

        var errors = new ValidationErrors();
        foreach (var change in detectChanges().Changes)
        {
            await Invoker(change.Entity.GetType()).ValidateAsync(services, change, errors, cancellationToken);
        }

        if (errors.Any)
        {
            throw new LigareValidationException(errors.All);
        }
    }

    private static IRuleInvoker Invoker(Type entityType) =>
        Invokers.GetOrAdd(entityType, t => (IRuleInvoker)Activator.CreateInstance(typeof(RuleInvoker<>).MakeGenericType(t))!);

    private interface IRuleInvoker
    {
        ValueTask PrepareAsync(IServiceProvider services, EntityChange change, CancellationToken cancellationToken);

        ValueTask ValidateAsync(IServiceProvider services, EntityChange change, ValidationErrors errors, CancellationToken cancellationToken);
    }

    private sealed class RuleInvoker<T> : IRuleInvoker where T : class
    {
        public async ValueTask PrepareAsync(IServiceProvider services, EntityChange change, CancellationToken cancellationToken)
        {
            foreach (var rule in services.GetServices<IEntityRule<T>>())
            {
                await rule.PrepareAsync(new EntityChange<T>(change), cancellationToken);
            }
        }

        public async ValueTask ValidateAsync(IServiceProvider services, EntityChange change, ValidationErrors errors, CancellationToken cancellationToken)
        {
            foreach (var rule in services.GetServices<IEntityRule<T>>())
            {
                await rule.ValidateAsync(new EntityChange<T>(change), errors, cancellationToken);
            }
        }
    }
}
