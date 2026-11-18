namespace Ligare;

/// <summary>
/// Business rules for saving entities of type <typeparamref name="T"/>. They run on every save, in
/// direct mode in the saving process and in remote mode on the server, so a client cannot skip them.
/// Register implementations with <c>AddLigareRules</c>; they are resolved from the scope of the context.
/// </summary>
public interface IEntityRule<T> where T : class
{
    /// <summary>
    /// Runs first, once for every changed entity, also for entities another rule added: complete or
    /// derive values, e.g. stamp who changed something or keep a denormalized field in sync. May change
    /// any tracked entity.
    /// </summary>
    ValueTask PrepareAsync(EntityChange<T> change, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Runs after every <see cref="PrepareAsync"/>: report what is not allowed. Must not change entities.</summary>
    void Validate(EntityChange<T> change, ValidationErrors errors)
    {
    }

    /// <summary>
    /// Like <see cref="Validate"/>, for checks that query, e.g. uniqueness through
    /// <c>change.ChangeSet.Context</c>. By default runs <see cref="Validate"/>.
    /// </summary>
    ValueTask ValidateAsync(EntityChange<T> change, ValidationErrors errors, CancellationToken cancellationToken)
    {
        Validate(change, errors);
        return ValueTask.CompletedTask;
    }
}
