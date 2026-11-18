using Microsoft.EntityFrameworkCore;

namespace Ligare.Tests;

public sealed class DirectModeTests : IAsyncLifetime
{
    private readonly TestDatabase _database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task Queries_support_include_and_where()
    {
        await using var db = _database.CreateContext();

        var blocked = await db.Set<Tool>()
            .Include(t => t.Status)
            .Where(t => t.Status.Name == "Blocked")
            .ToListAsync(Ct);

        var tool = Assert.Single(blocked);
        Assert.Equal("Caliper", tool.Name);
        Assert.Equal("Blocked", tool.Status.Name);
    }

    [Fact]
    public async Task Changed_properties_are_saved()
    {
        await using (var db = _database.CreateContext())
        {
            var tool = await db.Set<Tool>().FirstAsync(t => t.Id == _database.Wrench.Id, Ct);
            tool.Name = "Torque wrench";
            await db.SaveChangesAsync(Ct);
        }

        await using var check = _database.CreateContext();
        Assert.Equal("Torque wrench", (await check.Set<Tool>().FirstAsync(t => t.Id == _database.Wrench.Id, Ct)).Name);
    }

    [Fact]
    public async Task Adding_to_a_collection_runs_the_prepare_rules()
    {
        await using (var db = _database.CreateContext())
        {
            var tool = await db.Set<Tool>().FirstAsync(t => t.Id == _database.Wrench.Id, Ct);
            var blocked = await db.Set<Status>().FirstAsync(s => s.Id == _database.Blocked.Id, Ct);

            tool.StatusEntries.Add(new StatusEntry { Status = blocked, Comment = "Dropped" });
            await db.SaveChangesAsync(Ct);
        }

        await using var check = _database.CreateContext();
        var saved = await check.Set<Tool>().Include(t => t.StatusEntries).FirstAsync(t => t.Id == _database.Wrench.Id, Ct);
        Assert.Equal(_database.Blocked.Id, saved.StatusId);
        Assert.Equal("tester", Assert.Single(saved.StatusEntries).ChangedBy);
    }

    [Fact]
    public async Task Rejected_changes_report_their_property_and_store_nothing()
    {
        await using (var db = _database.CreateContext())
        {
            var tool = await db.Set<Tool>().FirstAsync(t => t.Id == _database.Wrench.Id, Ct);
            var blocked = await db.Set<Status>().FirstAsync(s => s.Id == _database.Blocked.Id, Ct);
            tool.Name = " ";
            tool.Status = blocked;

            var error = await Assert.ThrowsAsync<LigareValidationException>(() => db.SaveChangesAsync(Ct));

            var byProperty = error.For(tool);
            Assert.Equal(["Name is required."], byProperty["Name"]);
            Assert.Equal(["Change the status by adding a status entry."], byProperty["Status"]);
        }

        await using var check = _database.CreateContext();
        var stored = await check.Set<Tool>().FirstAsync(t => t.Id == _database.Wrench.Id, Ct);
        Assert.Equal("Wrench", stored.Name);
        Assert.Equal(_database.Released.Id, stored.StatusId);
    }

    [Fact]
    public async Task Reference_navigations_and_new_entities_are_saved()
    {
        await using (var db = _database.CreateContext())
        {
            var tool = await db.Set<Tool>().FirstAsync(t => t.Id == _database.Wrench.Id, Ct);
            db.Add(new Customer { Name = "ACME", FavoriteTool = tool });
            await db.SaveChangesAsync(Ct);
        }

        await using var check = _database.CreateContext();
        var customer = await check.Set<Customer>().Include(c => c.FavoriteTool).SingleAsync(Ct);
        Assert.Equal("Wrench", customer.FavoriteTool?.Name);
    }
}
