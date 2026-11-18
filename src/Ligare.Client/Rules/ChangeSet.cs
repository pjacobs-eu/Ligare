namespace Ligare;

/// <summary>Everything one <see cref="ILigareContext.SaveChangesAsync"/> is about to store.</summary>
public sealed class ChangeSet
{
    private readonly List<EntityChange> _changes = [];

    public ChangeSet(ILigareContext context)
    {
        Context = context;
    }

    /// <summary>The saving context; rules may query it, e.g. to look up related data.</summary>
    public ILigareContext Context { get; }

    public IReadOnlyList<EntityChange> Changes => _changes;

    public IEnumerable<T> Added<T>() where T : class => Entities<T>(EntityChangeState.Added);

    public IEnumerable<T> Modified<T>() where T : class => Entities<T>(EntityChangeState.Modified);

    public IEnumerable<T> Deleted<T>() where T : class => Entities<T>(EntityChangeState.Deleted);

    /// <summary>For backends: adds one change while building the set.</summary>
    public void Include(EntityChange change) => _changes.Add(change);

    private IEnumerable<T> Entities<T>(EntityChangeState state) where T : class =>
        _changes.Where(c => c.State == state).Select(c => c.Entity).OfType<T>();
}
