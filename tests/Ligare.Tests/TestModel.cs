using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ligare.EntityFrameworkCore;

namespace Ligare.Tests;

public sealed class Status
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";
}

public sealed class Tool
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public Guid StatusId { get; set; }

    public Status Status { get; set; } = null!;

    public ICollection<StatusEntry> StatusEntries { get; } = new List<StatusEntry>();
}

public sealed class StatusEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Tool Tool { get; set; } = null!;

    public Status Status { get; set; } = null!;

    public string? Comment { get; set; }

    public string ChangedBy { get; set; } = "";
}

public sealed class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public Tool? FavoriteTool { get; set; }
}

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Status>();
        modelBuilder.Entity<Tool>().HasOne(t => t.Status).WithMany().HasForeignKey(t => t.StatusId);
        modelBuilder.Entity<Tool>().HasMany(t => t.StatusEntries).WithOne(e => e.Tool);
        modelBuilder.Entity<StatusEntry>().HasOne(e => e.Status).WithMany();
        modelBuilder.Entity<Customer>().HasOne(c => c.FavoriteTool).WithMany();

        // Ids are assigned in code, so EF must not take an entity with an id for an existing one.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            modelBuilder.Entity(entity.ClrType).Property("Id").ValueGeneratedNever();
        }
    }
}

/// <summary>A status entry stamps who made it and moves its tool to the new status.</summary>
public sealed class StatusEntryRule : IEntityRule<StatusEntry>
{
    public ValueTask PrepareAsync(EntityChange<StatusEntry> change, CancellationToken cancellationToken)
    {
        if (change.IsAdded)
        {
            change.Entity.ChangedBy = "tester";
            change.Entity.Tool.Status = change.Entity.Status;
        }

        return ValueTask.CompletedTask;
    }

    public void Validate(EntityChange<StatusEntry> change, ValidationErrors errors)
    {
        if (string.IsNullOrWhiteSpace(change.Entity.Comment))
        {
            errors.Add(change, e => e.Comment, "A reason is required.");
        }
    }
}

/// <summary>Names are required, and the status only changes through a status entry.</summary>
public sealed class ToolRule : IEntityRule<Tool>
{
    public void Validate(EntityChange<Tool> change, ValidationErrors errors)
    {
        if (string.IsNullOrWhiteSpace(change.Entity.Name))
        {
            errors.Add(change, t => t.Name, "Name is required.");
        }

        if (!change.IsAdded && change.IsModified(t => t.Status)
            && !change.ChangeSet.Added<StatusEntry>().Any(e => e.Tool == change.Entity))
        {
            errors.Add(change, t => t.Status, "Change the status by adding a status entry.");
        }
    }
}

/// <summary>An SQLite in-memory database with a direct-mode Ligare client, open for one test.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;

    public TestDatabase()
    {
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContextFactory<TestDbContext>(o => o.UseSqlite(_connection));
        services.AddLigareClient(l => l.UseEntityFrameworkCore<TestDbContext>());
        services.AddLigareRules(typeof(TestDatabase).Assembly);
        _services = services.BuildServiceProvider();
        _scope = _services.CreateScope();

        using var db = _services.GetRequiredService<IDbContextFactory<TestDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();

        Released = new Status { Name = "Released" };
        Blocked = new Status { Name = "Blocked" };
        Wrench = new Tool { Name = "Wrench", Status = Released };
        db.AddRange(Released, Blocked, Wrench, new Tool { Name = "Caliper", Status = Blocked });
        db.SaveChanges();
    }

    public Status Released { get; }

    public Status Blocked { get; }

    public Tool Wrench { get; }

    public ILigareContext CreateContext() => _scope.ServiceProvider.GetRequiredService<ILigareContextFactory>().CreateContext();

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
