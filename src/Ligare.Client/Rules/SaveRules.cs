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
    /// Prepares every change, rebuilds the change set (preparing may change other entities), then
    /// validates every change.
    /// </summary>
    /// <param name="services">The scope the rules are resolved from.</param>
    /// <param name="detectChanges">Builds the change set from the current state of the context.</param>
    /// <param name="cancellationToken">Cancels the rules.</param>
    /// <exception cref="LigareValidationException">A rule rejected a change.</exception>
    public static async Task RunAsync(IServiceProvider services, Func<ChangeSet> detectChanges, CancellationToken cancellationToken)
    {
        foreach (var change in detectChanges().Changes)
        {
            await Invoker(change.Entity.GetType()).PrepareAsync(services, change, cancellationToken);
        }

        var errors = new ValidationErrors();
        foreach (var change in detectChanges().Changes)
        {
            Invoker(change.Entity.GetType()).Validate(services, change, errors);
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

        void Validate(IServiceProvider services, EntityChange change, ValidationErrors errors);
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

        public void Validate(IServiceProvider services, EntityChange change, ValidationErrors errors)
        {
            foreach (var rule in services.GetServices<IEntityRule<T>>())
            {
                rule.Validate(new EntityChange<T>(change), errors);
            }
        }
    }
}
