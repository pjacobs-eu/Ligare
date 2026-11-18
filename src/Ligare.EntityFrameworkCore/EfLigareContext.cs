using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Ligare.EntityFrameworkCore;

/// <summary>Direct mode: an <see cref="ILigareContext"/> over a <see cref="DbContext"/> in this process.</summary>
public sealed class EfLigareContext(DbContext dbContext, IServiceProvider services) : ILigareContext
{
    /// <summary>The underlying context, for what Ligare does not cover. Code using it only works in direct mode.</summary>
    public DbContext DbContext { get; } = dbContext;

    public IQueryable<T> Set<T>() where T : class => DbContext.Set<T>();

    public T Add<T>(T entity) where T : class
    {
        DbContext.Add(entity);
        return entity;
    }

    public void Remove<T>(T entity) where T : class => DbContext.Remove(entity);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        (await SaveChangesAndListAsync(cancellationToken)).Count;

    /// <summary>Like <see cref="SaveChangesAsync"/>, returning every entity written, including those the rules changed.</summary>
    public async Task<IReadOnlyList<object>> SaveChangesAndListAsync(CancellationToken cancellationToken = default)
    {
        await SaveRules.RunAsync(services, DetectChanges, cancellationToken);

        DbContext.ChangeTracker.DetectChanges();
        var written = DbContext.ChangeTracker.Entries()
            .Where(e => !e.Metadata.IsOwned() && e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();

        await DbContext.SaveChangesAsync(cancellationToken);
        return written;
    }

    public void Dispose() => DbContext.Dispose();

    public ValueTask DisposeAsync() => DbContext.DisposeAsync();

    /// <summary>
    /// The change set of the tracked entities. Owned and complex types are part of their owner and
    /// therefore not listed on their own.
    /// </summary>
    internal ChangeSet DetectChanges()
    {
        DbContext.ChangeTracker.DetectChanges();

        var changeSet = new ChangeSet(this);
        foreach (var entry in DbContext.ChangeTracker.Entries())
        {
            if (entry.Metadata.IsOwned())
            {
                continue;
            }

            EntityChangeState? state = entry.State switch
            {
                EntityState.Added => EntityChangeState.Added,
                EntityState.Modified => EntityChangeState.Modified,
                EntityState.Deleted => EntityChangeState.Deleted,
                _ => null,
            };

            if (state is { } s)
            {
                changeSet.Include(new EfEntityChange(entry, s, changeSet));
            }
        }

        return changeSet;
    }
}

internal sealed class EfEntityChange(EntityEntry entry, EntityChangeState state, ChangeSet changeSet) : EntityChange(changeSet)
{
    public override object Entity => entry.Entity;

    public override EntityChangeState State => state;

    public override bool IsModified(string propertyName)
    {
        if (state == EntityChangeState.Added)
        {
            return true;
        }

        if (entry.Metadata.FindProperty(propertyName) is not null)
        {
            return entry.Property(propertyName).IsModified;
        }

        if (entry.Metadata.FindComplexProperty(propertyName) is not null)
        {
            return entry.ComplexProperty(propertyName).IsModified;
        }

        // A reference navigation counts as modified when its foreign key is.
        if (entry.Metadata.FindNavigation(propertyName) is { IsCollection: false } navigation)
        {
            var foreignKey = navigation.ForeignKey;
            if (navigation.IsOnDependent)
            {
                return foreignKey.Properties.Any(p => entry.Property(p.Name).IsModified);
            }
        }

        throw new ArgumentException($"'{propertyName}' is not a property or reference navigation of {entry.Metadata.DisplayName()}.", nameof(propertyName));
    }

    public override object? GetOriginalValue(string propertyName) => entry.Property(propertyName).OriginalValue;
}
