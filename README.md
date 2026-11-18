# Ligare

*Latin: to bind, to connect.*

Ligare lets you write data access exactly like EF Core — `Include`, `Where`, setting properties,
adding to navigation collections, `SaveChangesAsync` — and decide by configuration where the data
actually lives: in a `DbContext` in the same process, or behind a Ligare server over OData.

```csharp
await using var db = contextFactory.CreateContext();

var tool = await db.Set<Tool>()
    .Include(t => t.Status)
    .FirstAsync(t => t.InventoryNumber == "PM-0003");

tool.Name = "Torque wrench 20–100 Nm";
tool.StatusChanges.Add(new StatusChange { Status = blocked, Comment = "Dropped" });
customer.FavoriteTool = tool;

await db.SaveChangesAsync();
```

Your EF Core entities are the model everywhere — no DTOs, no generated client, no hand-written API.
Queries use the operators you know from `Microsoft.EntityFrameworkCore`.

## Packages

| Package | Purpose |
|---|---|
| `Ligare.Client` | `ILigareContext`, save rules, remote mode over OData |
| `Ligare.EntityFrameworkCore` | Direct mode against a `DbContext`; the save pipeline shared with the server |
| `Ligare.Server` | Exposes a `DbContext` as OData for remote clients, running the same save rules |

## Setup

### Direct mode — the data is in this process

Server-side Blazor, a desktop app with a local database, a background worker:

```csharp
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddLigareClient(l => l.UseEntityFrameworkCore<AppDbContext>());
builder.Services.AddLigareRules(typeof(ToolRules).Assembly);
```

If the context depends on scoped services (the current tenant, the current user), register the
factory with `ServiceLifetime.Scoped`.

### Server — share a DbContext with remote clients

The server hosts the same `DbContext`, rules and query filters, and serves them
over OData:

```csharp
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString), ServiceLifetime.Scoped);
builder.Services.AddLigareRules(typeof(ToolRules).Assembly);
builder.Services.AddLigareServer<AppDbContext>(server =>
{
    server.MaxPageSize = 500;        // more results must be paged with Skip/Take
    server.MaxExpansionDepth = 3;    // deepest Include chain
});

var app = builder.Build();
app.MapLigare("/data");
```

Only entity types of the `DbContext` are exposed, global query filters (for example per tenant)
always apply, and every save runs the save rules on the server — a client cannot skip them. Secure
the endpoints like any other: `app.MapLigare("/data").RequireAuthorization()`.

Endpoints: `GET {prefix}/$model`, `GET {prefix}/{Type}?$filter=…&$orderby=…&$skip=…&$top=…&$expand=…`,
`GET {prefix}/{Type}/$count`, `POST {prefix}/$save`. Queries use OData URL conventions, so `$filter`
expressions from other tools work too; entities travel as plain JSON.

### Remote mode — the data is on a Ligare server

Blazor WebAssembly, a MAUI app against a server — the same code as in direct mode:

```csharp
builder.Services.AddLigareClient(l => l.UseServer(new Uri("https://example.com/data")));
```

## Save rules

Business rules live in one place and run on every save — in direct mode in the saving process, in
remote mode on the server:

```csharp
public sealed class StatusChangeRules(TimeProvider clock) : IEntityRule<StatusChange>
{
    // Complete or derive values; may change any tracked entity.
    public ValueTask PrepareAsync(EntityChange<StatusChange> change, CancellationToken ct)
    {
        if (change.IsAdded)
        {
            change.Entity.ChangedAt = clock.GetUtcNow();
            change.Entity.Tool.Status = change.Entity.Status;
        }

        return ValueTask.CompletedTask;
    }

    // Report what is not allowed; nothing is stored if any rule reports an error.
    public void Validate(EntityChange<StatusChange> change, ValidationErrors errors)
    {
        if (string.IsNullOrWhiteSpace(change.Entity.Comment))
        {
            errors.Add(change, c => c.Comment, "A reason is required.");
        }
    }
}
```

A rejected save throws `LigareValidationException`; `exception.For(entity)` returns the messages by
property name, ready for a form.

## Remote mode: what runs on the server

`Where`, `OrderBy`/`ThenBy` (descending), `Skip`, `Take`, `Include`/`ThenInclude` (with `OrderBy` inside),
`AsNoTracking`, and `ToListAsync`, `FirstAsync`, `SingleAsync`, `AnyAsync`, `CountAsync` and their
`OrDefault` variants. Inside `Where`: comparisons, `&&`, `||`, `!`, arithmetic, `string.Contains`/
`StartsWith`/`EndsWith`/`ToLower`/`ToUpper`/`Trim`, `list.Contains(x)`, and `Any`/`All` on collections.
Everything else throws `NotSupportedException` naming the operator — nothing silently runs in memory.
Queries are asynchronous only.

## Things to know

- **Ids assigned in code:** configure such keys with `ValueGeneratedNever()`. Otherwise EF Core takes
  a new entity that already has an id, found through a navigation, for an existing one and updates
  instead of inserting it.
- **Unit of work:** a context tracks what it loaded. Keep one per edit (a page, a dialog), not one
  per application.

## Status

Early development: direct mode, server and remote mode work; concurrency tokens and file streams are
next. The API will change.

## License

Copyright © 2026 Philip Jacobs.

Ligare is licensed under the [GNU Lesser General Public License v3.0](COPYING.LESSER), which builds on
the [GNU General Public License v3.0](COPYING). You can use Ligare in proprietary applications;
changes to Ligare itself must be published under the same license.
