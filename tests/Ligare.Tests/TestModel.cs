using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ligare.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Ligare.Tests;

public sealed class Status
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>Set once, like a system flag; EF Core maps it through the private setter.</summary>
    public bool IsDefault { get; private set; }

    public static Status CreateDefault(string name) => new() { Name = name, IsDefault = true };
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

/// <summary>Names are required, and the status only changes through a status entry; new tools get their first one.</summary>
public sealed class ToolRule : IEntityRule<Tool>
{
    public ValueTask PrepareAsync(EntityChange<Tool> change, CancellationToken cancellationToken)
    {
        if (change.IsAdded && change.Entity.StatusEntries.Count == 0)
        {
            change.Entity.StatusEntries.Add(new StatusEntry { Status = change.Entity.Status, Comment = "New" });
        }

        return ValueTask.CompletedTask;
    }

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

public enum Mode
{
    Direct,
    Remote,
}

/// <summary>
/// An SQLite in-memory database and a Ligare client for one test: in direct mode on the database, in
/// remote mode through a Ligare server on a test host, which runs the save rules.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _client;
    private readonly IServiceScope _scope;
    private readonly WebApplication? _server;

    private TestDatabase(Mode mode, Action<LigareServerOptions>? configureServer)
    {
        _connection.Open();

        var client = new ServiceCollection();
        if (mode == Mode.Direct)
        {
            AddData(client);
            client.AddLigareClient(l => l.UseEntityFrameworkCore<TestDbContext>());
        }
        else
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            AddData(builder.Services);
            builder.Services.AddLigareServer<TestDbContext>(o =>
            {
                o.MaxPageSize = 50;
                configureServer?.Invoke(o);
            });
            _server = builder.Build();
            _server.MapLigare("/data");
            _server.Start();

            var testServer = _server.GetTestServer();
            client.AddLigareClient(l => l.UseServer(_ =>
            {
                var http = testServer.CreateClient();
                http.BaseAddress = new Uri("http://localhost/data/");
                return http;
            }));
        }

        _client = client.BuildServiceProvider();
        _scope = _client.CreateScope();

        using var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options);
        db.Database.EnsureCreated();

        Released = Status.CreateDefault("Released");
        Blocked = new Status { Name = "Blocked" };
        Wrench = new Tool { Name = "Wrench", Status = Released };
        db.AddRange(Released, Blocked, Wrench, new Tool { Name = "Caliper", Status = Blocked });
        db.SaveChanges();
    }

    public Status Released { get; }

    public Status Blocked { get; }

    public Tool Wrench { get; }

    public static TestDatabase Create(Mode mode, Action<LigareServerOptions>? configureServer = null) => new(mode, configureServer);

    public ILigareContext CreateContext() => _scope.ServiceProvider.GetRequiredService<ILigareContextFactory>().CreateContext();

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _client.DisposeAsync();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }

    private void AddData(IServiceCollection services)
    {
        services.AddDbContextFactory<TestDbContext>(o => o.UseSqlite(_connection));
        services.AddLigareRules(typeof(TestDatabase).Assembly);
    }
}
