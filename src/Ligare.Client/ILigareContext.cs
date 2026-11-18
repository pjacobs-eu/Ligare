namespace Ligare;

/// <summary>
/// A unit of work over your EF Core entities, wherever they live. Use it like a <c>DbContext</c>:
/// query with <see cref="Set{T}"/> and the usual EF Core operators (<c>Include</c>, <c>Where</c>,
/// <c>FirstAsync</c>, <c>ToListAsync</c> from <c>Microsoft.EntityFrameworkCore</c>), change properties
/// and navigations of what you loaded, then call <see cref="SaveChangesAsync"/>.
/// </summary>
/// <remarks>
/// Entities loaded through a context are tracked by it, so keep one context for as long as an edit
/// lasts (a page, a dialog) and create a new one for the next. Not thread-safe, like a <c>DbContext</c>.
/// </remarks>
public interface ILigareContext : IDisposable, IAsyncDisposable
{
    /// <summary>The entities of type <typeparamref name="T"/>, as a query.</summary>
    IQueryable<T> Set<T>() where T : class;

    /// <summary>Starts tracking a new entity; it is inserted on the next save, with every new entity it references.</summary>
    T Add<T>(T entity) where T : class;

    /// <summary>Marks a tracked entity for deletion on the next save.</summary>
    void Remove<T>(T entity) where T : class;

    /// <summary>
    /// Runs the save rules for every change, then stores all changes in one transaction.
    /// </summary>
    /// <exception cref="LigareValidationException">A save rule rejected a change; nothing was stored.</exception>
    /// <returns>The number of entities written.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates <see cref="ILigareContext"/> instances, one per unit of work.</summary>
public interface ILigareContextFactory
{
    ILigareContext CreateContext();
}
