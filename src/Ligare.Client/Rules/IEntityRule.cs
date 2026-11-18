namespace Ligare;

/// <summary>
/// Business rules for saving entities of type <typeparamref name="T"/>. They run on every save, in
/// direct mode in the saving process and in remote mode on the server, so a client cannot skip them.
/// Register implementations with <c>AddLigareRules</c>; they are resolved from the scope of the context.
/// </summary>
public interface IEntityRule<T> where T : class
{
    /// <summary>
    /// Runs first, for every change: complete or derive values, e.g. stamp who changed something or keep
    /// a denormalized field in sync. May change any tracked entity.
    /// </summary>
    ValueTask PrepareAsync(EntityChange<T> change, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Runs after every <see cref="PrepareAsync"/>: report what is not allowed. Must not change entities.</summary>
    void Validate(EntityChange<T> change, ValidationErrors errors)
    {
    }
}
