using System.Linq.Expressions;

namespace Ligare;

public enum EntityChangeState
{
    Added,
    Modified,
    Deleted,
}

/// <summary>One entity about to be saved, as seen by the save rules.</summary>
public abstract class EntityChange
{
    protected EntityChange(ChangeSet changeSet)
    {
        ChangeSet = changeSet;
    }

    public abstract object Entity { get; }

    public abstract EntityChangeState State { get; }

    /// <summary>All changes of this save, e.g. to check that a related entity was added alongside.</summary>
    public ChangeSet ChangeSet { get; }

    /// <summary>
    /// Whether a property, or the foreign key behind a reference navigation, changed. Always true for
    /// added entities.
    /// </summary>
    public abstract bool IsModified(string propertyName);

    /// <summary>The value as loaded; for added entities the current value.</summary>
    public abstract object? GetOriginalValue(string propertyName);
}

/// <summary>Typed view of an <see cref="EntityChange"/>.</summary>
public sealed class EntityChange<T>(EntityChange inner) where T : class
{
    public T Entity => (T)inner.Entity;

    public EntityChangeState State => inner.State;

    public ChangeSet ChangeSet => inner.ChangeSet;

    public bool IsAdded => State == EntityChangeState.Added;

    public bool IsModified<TProperty>(Expression<Func<T, TProperty>> property) => inner.IsModified(PropertyName.Of(property));

    public TProperty GetOriginalValue<TProperty>(Expression<Func<T, TProperty>> property) =>
        (TProperty)inner.GetOriginalValue(PropertyName.Of(property))!;
}

internal static class PropertyName
{
    public static string Of(LambdaExpression property) =>
        property.Body is MemberExpression { Expression: ParameterExpression } member
            ? member.Member.Name
            : throw new ArgumentException($"'{property}' must be a property of the entity, like e => e.Name.", nameof(property));
}
