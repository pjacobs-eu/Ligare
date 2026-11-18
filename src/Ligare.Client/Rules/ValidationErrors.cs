using System.Linq.Expressions;

namespace Ligare;

/// <summary>
/// A rejected change: which entity, which property (if any), and why — as a code such as
/// <c>equipment.name.required</c>, which the user interface translates.
/// </summary>
public sealed record LigareValidationError(object Entity, string? PropertyName, string Code);

/// <summary>Collects the errors of one save.</summary>
public sealed class ValidationErrors
{
    private readonly List<LigareValidationError> _errors = [];

    public IReadOnlyList<LigareValidationError> All => _errors;

    public bool Any => _errors.Count > 0;

    public void Add(object entity, string code) => _errors.Add(new LigareValidationError(entity, null, code));

    public void Add<T, TProperty>(T entity, Expression<Func<T, TProperty>> property, string code) where T : class =>
        _errors.Add(new LigareValidationError(entity, PropertyName.Of(property), code));

    public void Add<T, TProperty>(EntityChange<T> change, Expression<Func<T, TProperty>> property, string code) where T : class =>
        Add(change.Entity, property, code);

    public void Add<T>(EntityChange<T> change, string code) where T : class => Add(change.Entity, code);
}

/// <summary>A save was rejected by its rules; nothing was stored.</summary>
public sealed class LigareValidationException(IReadOnlyList<LigareValidationError> errors)
    : Exception("The save was rejected: " + string.Join(", ", errors.Select(e => e.PropertyName is null ? e.Code : $"{e.PropertyName}: {e.Code}")))
{
    public IReadOnlyList<LigareValidationError> Errors { get; } = errors;

    /// <summary>The error codes of one entity by property name; codes without a property are keyed by <see cref="string.Empty"/>.</summary>
    public IReadOnlyDictionary<string, string[]> For(object entity) =>
        Errors.Where(e => ReferenceEquals(e.Entity, entity))
            .GroupBy(e => e.PropertyName ?? "")
            .ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray());

    /// <summary>All error codes by property name, e.g. for a form that edits one entity.</summary>
    public IReadOnlyDictionary<string, string[]> ByProperty() =>
        Errors.GroupBy(e => e.PropertyName ?? "").ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray());
}
