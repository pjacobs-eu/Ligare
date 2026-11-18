using System.Linq.Expressions;

namespace Ligare;

/// <summary>A rejected change: which entity, which property (if any), and why.</summary>
public sealed record LigareValidationError(object Entity, string? PropertyName, string Message);

/// <summary>Collects the errors of one save.</summary>
public sealed class ValidationErrors
{
    private readonly List<LigareValidationError> _errors = [];

    public IReadOnlyList<LigareValidationError> All => _errors;

    public bool Any => _errors.Count > 0;

    public void Add(object entity, string message) => _errors.Add(new LigareValidationError(entity, null, message));

    public void Add<T, TProperty>(T entity, Expression<Func<T, TProperty>> property, string message) where T : class =>
        _errors.Add(new LigareValidationError(entity, PropertyName.Of(property), message));

    public void Add<T, TProperty>(EntityChange<T> change, Expression<Func<T, TProperty>> property, string message) where T : class =>
        Add(change.Entity, property, message);

    public void Add<T>(EntityChange<T> change, string message) where T : class => Add(change.Entity, message);
}

/// <summary>A save was rejected by its rules; nothing was stored.</summary>
public sealed class LigareValidationException(IReadOnlyList<LigareValidationError> errors)
    : Exception(errors.Count == 1 ? errors[0].Message : $"{errors.Count} changes were rejected: " + string.Join(" ", errors.Select(e => e.Message)))
{
    public IReadOnlyList<LigareValidationError> Errors { get; } = errors;

    /// <summary>The errors of one entity by property name; errors without a property are keyed by <see cref="string.Empty"/>.</summary>
    public IReadOnlyDictionary<string, string[]> For(object entity) =>
        Errors.Where(e => ReferenceEquals(e.Entity, entity))
            .GroupBy(e => e.PropertyName ?? "")
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());
}
