# Ligare

*Latin: to bind, to connect.*

Ligare lets you write data access exactly like EF Core — `Include`, `Where`, setting properties,
adding to navigation collections, `SaveChangesAsync` — and decide by configuration where the data
actually lives: in a `DbContext` in the same process, or behind a Ligare server over OData.

```csharp
var tool = await db.Set<Equipment>()
    .Include(e => e.Status)
    .FirstAsync(e => e.InventoryNumber == "PM-0003");

tool.Name = "Torque wrench 20–100 Nm";
tool.StatusChanges.Add(new StatusChange { Status = blocked, Comment = "Dropped" });
customer.FavoriteTool = tool;

await db.SaveChangesAsync();
```

```csharp
// Direct: a DbContext in this process (server-side Blazor, desktop app with a local database)
services.AddLigareClient(o => o.UseEntityFrameworkCore<AppDbContext>());

// Remote: the same code, over OData (Blazor WebAssembly, MAUI against a server)
services.AddLigareClient(o => o.UseServer(new Uri("https://example.com/data")));
```

Your EF Core entities are the model everywhere — no DTOs, no generated client, no hand-written API.

## Packages

| Package | Purpose |
|---|---|
| `Ligare.Client` | EF Core-style API, snapshot change tracking, remote mode over OData |
| `Ligare.Client.EntityFrameworkCore` | Direct mode against a `DbContext` |
| `Ligare.Server` | Exposes a `DbContext` as OData for Ligare clients: `$batch` saves, validation, tenancy, audit |

## Status

Early development. The API will change.
