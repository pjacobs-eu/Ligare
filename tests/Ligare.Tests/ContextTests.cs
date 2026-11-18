using Microsoft.EntityFrameworkCore;

namespace Ligare.Tests;

/// <summary>The same behaviour in both modes: that is the point of Ligare.</summary>
public abstract class ContextTests(Mode mode) : IAsyncLifetime
{
    private readonly TestDatabase _database = TestDatabase.Create(mode);

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
    public async Task Queries_support_ordering_paging_and_captured_values()
    {
        await using var db = _database.CreateContext();
        var names = new[] { "Wrench", "Caliper" };
        var prefix = "C";

        var ordered = await db.Set<Tool>().Where(t => names.Contains(t.Name)).OrderByDescending(t => t.Name).ToListAsync(Ct);
        var second = await db.Set<Tool>().OrderBy(t => t.Name).Skip(1).Take(1).SingleAsync(Ct);
        var startingWithC = await db.Set<Tool>().CountAsync(t => t.Name.StartsWith(prefix), Ct);
        var anyBlocked = await db.Set<Tool>().AnyAsync(t => t.StatusId == _database.Blocked.Id, Ct);
        var defaultStatus = await db.Set<Status>().SingleAsync(s => s.IsDefault, Ct);

        Assert.Equal(["Wrench", "Caliper"], ordered.Select(t => t.Name));
        Assert.Equal("Wrench", second.Name);
        Assert.Equal(1, startingWithC);
        Assert.True(anyBlocked);
        Assert.Equal("Released", defaultStatus.Name);

        var projected = await db.Set<Tool>().Where(t => t.Name != "").OrderBy(t => t.Name).Select(t => t.Name.ToUpper()).ToListAsync(Ct);
        Assert.Equal(["CALIPER", "WRENCH"], projected);
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
            var tool = await db.Set<Tool>().Include(t => t.Status).FirstAsync(t => t.Id == _database.Wrench.Id, Ct);
            var blocked = await db.Set<Status>().FirstAsync(s => s.Id == _database.Blocked.Id, Ct);

            tool.StatusEntries.Add(new StatusEntry { Status = blocked, Comment = "Dropped" });
            await db.SaveChangesAsync(Ct);

            // What the rules changed is visible right after saving, also in remote mode.
            Assert.Same(blocked, tool.Status);
            Assert.Equal("tester", tool.StatusEntries.Single().ChangedBy);
        }

        await using var check = _database.CreateContext();
        var saved = await check.Set<Tool>()
            .Include(t => t.StatusEntries).ThenInclude(e => e.Status)
            .FirstAsync(t => t.Id == _database.Wrench.Id, Ct);
        Assert.Equal(_database.Blocked.Id, saved.StatusId);
        var entry = Assert.Single(saved.StatusEntries);
        Assert.Equal("tester", entry.ChangedBy);
        Assert.Equal("Blocked", entry.Status.Name);
    }

    [Fact]
    public async Task Entities_added_by_a_rule_are_prepared_too()
    {
        await using (var db = _database.CreateContext())
        {
            var released = await db.Set<Status>().FirstAsync(s => s.Id == _database.Released.Id, Ct);
            db.Add(new Tool { Name = "Gauge", Status = released });
            await db.SaveChangesAsync(Ct);
        }

        await using var check = _database.CreateContext();
        var gauge = await check.Set<Tool>().Include(t => t.StatusEntries).SingleAsync(t => t.Name == "Gauge", Ct);
        Assert.Equal("tester", Assert.Single(gauge.StatusEntries).ChangedBy);
    }

    [Fact]
    public async Task Navigations_set_by_rules_point_to_entities_the_client_never_loaded()
    {
        await using var db = _database.CreateContext();
        var gauge = db.Add(new Tool { Name = "Gauge" });

        await db.SaveChangesAsync(Ct);

        // The rule chose the default status; the client follows the navigation without loading it.
        Assert.Equal("Released", gauge.Status.Name);
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

        await using (var db = _database.CreateContext())
        {
            var customer = await db.Set<Customer>().Include(c => c.FavoriteTool).SingleAsync(Ct);
            Assert.Equal("Wrench", customer.FavoriteTool?.Name);

            customer.FavoriteTool = null;
            await db.SaveChangesAsync(Ct);
        }

        await using var check = _database.CreateContext();
        Assert.Null((await check.Set<Customer>().Include(c => c.FavoriteTool).SingleAsync(Ct)).FavoriteTool);
    }

    [Fact]
    public async Task Elements_keyed_by_their_owner_get_the_owner_key()
    {
        await using (var db = _database.CreateContext())
        {
            var tool = await db.Set<Tool>().FirstAsync(t => t.Id == _database.Wrench.Id, Ct);
            tool.Notes.Add(new ToolNote { Number = 1, Text = "Calibrate yearly" });
            await db.SaveChangesAsync(Ct);
        }

        await using var check = _database.CreateContext();
        var saved = await check.Set<Tool>().Include(t => t.Notes).FirstAsync(t => t.Id == _database.Wrench.Id, Ct);
        Assert.Equal("Calibrate yearly", Assert.Single(saved.Notes).Text);
    }

    [Fact]
    public async Task Removed_entities_are_deleted()
    {
        await using (var db = _database.CreateContext())
        {
            db.Remove(await db.Set<Tool>().FirstAsync(t => t.Name == "Caliper", Ct));
            await db.SaveChangesAsync(Ct);
        }

        await using var check = _database.CreateContext();
        Assert.Equal(1, await check.Set<Tool>().CountAsync(Ct));
    }
}

public sealed class DirectModeTests() : ContextTests(Mode.Direct);

public sealed class RemoteModeTests() : ContextTests(Mode.Remote);

public sealed class ServerTests
{
    [Fact]
    public async Task Excluded_types_are_not_served()
    {
        await using var database = TestDatabase.Create(Mode.Remote, server => server.Exclude<Customer>());
        await using var db = database.CreateContext();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<Customer>().ToListAsync(TestContext.Current.CancellationToken));

        Assert.Contains("not an entity type of the Ligare server", error.Message);
    }
}
