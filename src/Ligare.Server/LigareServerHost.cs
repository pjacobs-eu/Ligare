using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Ligare.EntityFrameworkCore;
using Ligare.Wire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace Ligare.Server;

/// <summary>Handles the requests of Ligare clients against one <typeparamref name="TContext"/>.</summary>
internal sealed class LigareServerHost<TContext>(IOptions<LigareServerOptions> options) : ILigareServerHost
    where TContext : DbContext
{
    private static readonly MethodInfo QueryMethod = typeof(LigareServerHost<TContext>).GetMethod(nameof(QueryAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly LigareServerOptions _options = options.Value;
    private ServerModel? _model;

    public async Task ModelAsync(HttpContext http)
    {
        await using var db = CreateContext(http);
        await Results.Json(Model(db).Description, WireJson.Options).ExecuteAsync(http);
    }

    public async Task QueryAsync(HttpContext http, string set, bool count)
    {
        await using var db = CreateContext(http);
        var entityType = Model(db).FindByName(set);
        if (entityType is null)
        {
            await Results.Problem($"There is no entity set '{set}'.", statusCode: StatusCodes.Status404NotFound).ExecuteAsync(http);
            return;
        }

        try
        {
            await (Task)QueryMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [http, db, entityType, count])!;
        }
        catch (Microsoft.OData.ODataException exception)
        {
            await Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(http);
        }
        catch (LigareQueryException exception)
        {
            await Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(http);
        }
    }

    public async Task SaveAsync(HttpContext http)
    {
        var request = await http.Request.ReadFromJsonAsync<SaveRequest>(WireJson.Options, http.RequestAborted);
        await using var db = CreateContext(http);
        var model = Model(db);

        try
        {
            foreach (var change in request?.Changes ?? [])
            {
                await ApplyAsync(db, model, change, http.RequestAborted);
            }
        }
        catch (LigareQueryException exception)
        {
            await Results.Problem(exception.Message, statusCode: exception.StatusCode).ExecuteAsync(http);
            return;
        }

        await LoadReferencesAsync(db, http.RequestAborted);

        var ligare = new EfLigareContext(db, http.RequestServices);
        IReadOnlyList<object> written;
        try
        {
            written = await ligare.SaveChangesAndListAsync(http.RequestAborted);
        }
        catch (LigareValidationException exception)
        {
            var errors = exception.Errors
                .Select(e => db.Entry(e.Entity) is var entry
                    ? new WireError(entry.Metadata.ClrType.FullName!, EntityWriter.Key(entry), e.PropertyName, e.Code)
                    : null)
                .ToList();
            await Results.Json(new SaveErrorResponse(errors!), WireJson.Options, statusCode: StatusCodes.Status422UnprocessableEntity).ExecuteAsync(http);
            return;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            await Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict).ExecuteAsync(http);
            return;
        }

        var saved = written
            .Select(db.Entry)
            .Select(entry => new WireEntity(entry.Metadata.ClrType.FullName!, EntityWriter.Key(entry), EntityWriter.Values(entry)))
            .ToList();

        var writtenSet = new HashSet<object>(written, ReferenceEqualityComparer.Instance);
        var related = new Dictionary<object, WireEntity>(ReferenceEqualityComparer.Instance);
        foreach (var entry in written.Select(db.Entry))
        {
            foreach (var reference in entry.References)
            {
                if (reference.Metadata is INavigation { IsOnDependent: true, ForeignKey.IsOwnership: false } navigation
                    && model.IsExposed(navigation.TargetEntityType)
                    && reference.CurrentValue is { } target
                    && !writtenSet.Contains(target) && !related.ContainsKey(target))
                {
                    var targetEntry = db.Entry(target);
                    related[target] = new WireEntity(targetEntry.Metadata.ClrType.FullName!, EntityWriter.Key(targetEntry), EntityWriter.Values(targetEntry));
                }
            }
        }

        await Results.Json(new SaveResponse(saved, [.. related.Values]), WireJson.Options).ExecuteAsync(http);
    }

    private static TContext CreateContext(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDbContextFactory<TContext>>().CreateDbContext();

    private ServerModel Model(DbContext db) => _model ??= new ServerModel(db.Model, _options.Excluded);

    private async Task QueryAsync<T>(HttpContext http, DbContext db, IEntityType entityType, bool count) where T : class
    {
        var query = http.Request.Query;
        IQueryable<T> source = db.Set<T>();

        if (query.TryGetValue("$filter", out var filter) && !string.IsNullOrWhiteSpace(filter))
        {
            source = ApplyFilter(source, filter!);
        }

        if (count)
        {
            var total = await source.LongCountAsync(http.RequestAborted);
            await Results.Text(total.ToString(CultureInfo.InvariantCulture)).ExecuteAsync(http);
            return;
        }

        foreach (var path in ExpandParser.Parse(query["$expand"]))
        {
            ValidateInclude(entityType, path);
            source = source.Include(path);
        }

        if (query.TryGetValue("$orderby", out var orderBy) && !string.IsNullOrWhiteSpace(orderBy))
        {
            source = ApplyOrderBy(source, orderBy!);
        }

        if (query.TryGetValue("$skip", out var skip))
        {
            source = source.Skip(int.Parse(skip!, CultureInfo.InvariantCulture));
        }

        var top = query.TryGetValue("$top", out var topText) ? int.Parse(topText!, CultureInfo.InvariantCulture) : (int?)null;
        if (top > _options.MaxPageSize)
        {
            throw new LigareQueryException($"$top may be at most {_options.MaxPageSize}.");
        }

        // Without $top, more than a page is an error rather than a silently cut result.
        source = source.Take((top ?? _options.MaxPageSize) + (top is null ? 1 : 0));
        var results = await source.ToListAsync(http.RequestAborted);
        if (top is null && results.Count > _options.MaxPageSize)
        {
            throw new LigareQueryException($"The query returns more than {_options.MaxPageSize} entities; page it with $top and $skip.");
        }

        http.Response.ContentType = "application/json; charset=utf-8";
        await using var json = new Utf8JsonWriter(http.Response.BodyWriter);
        var writer = new EntityWriter(db, json);
        json.WriteStartObject();
        json.WriteStartArray("value");
        foreach (var entity in results)
        {
            writer.WriteEntity(entity, navigations: true);
        }

        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(http.RequestAborted);
        await http.Response.BodyWriter.FlushAsync(http.RequestAborted);
    }

    private IQueryable<T> ApplyFilter<T>(IQueryable<T> source, string filter)
    {
        var edm = _model!.Edm;
        var entitySet = edm.EntityContainer.FindEntitySet(typeof(T).Name);
        var parser = new ODataQueryOptionParser(edm, entitySet.EntityType, entitySet, new Dictionary<string, string> { ["$filter"] = filter });
        var clause = parser.ParseFilter();

        var settings = new ODataQuerySettings { HandleNullPropagation = HandleNullPropagationOption.False };
        var predicate = (Expression<Func<T, bool>>)new FilterBinder().BindFilter(clause, new QueryBinderContext(edm, settings, typeof(T)));
        return source.Where(predicate);
    }

    /// <summary><c>Status/Name desc,InventoryNumber</c>: member paths, optionally descending.</summary>
    private static IQueryable<T> ApplyOrderBy<T>(IQueryable<T> source, string orderBy)
    {
        var first = true;
        foreach (var clause in orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var descending = parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);

            var parameter = Expression.Parameter(typeof(T), "e");
            Expression body = parameter;
            foreach (var member in parts[0].Split('/'))
            {
                body = Expression.PropertyOrField(body, member);
            }

            var method = (first, descending) switch
            {
                (true, false) => nameof(Queryable.OrderBy),
                (true, true) => nameof(Queryable.OrderByDescending),
                (false, false) => nameof(Queryable.ThenBy),
                (false, true) => nameof(Queryable.ThenByDescending),
            };

            source = source.Provider.CreateQuery<T>(Expression.Call(
                typeof(Queryable), method, [typeof(T), body.Type], source.Expression, Expression.Quote(Expression.Lambda(body, parameter))));
            first = false;
        }

        return source;
    }

    private void ValidateInclude(IEntityType entityType, string path)
    {
        var navigations = path.Split('.');
        if (navigations.Length > _options.MaxExpansionDepth)
        {
            throw new LigareQueryException($"$expand may be at most {_options.MaxExpansionDepth} levels deep.");
        }

        var current = entityType;
        foreach (var name in navigations)
        {
            var navigation = current.FindNavigation(name);
            if (navigation is null || navigation.ForeignKey.IsOwnership || !_model!.IsExposed(navigation.TargetEntityType))
            {
                throw new LigareQueryException($"{current.ClrType.Name} has no navigation '{name}'.");
            }

            current = navigation.TargetEntityType;
        }
    }

    private static async Task ApplyAsync(DbContext db, ServerModel model, WireChange change, CancellationToken cancellationToken)
    {
        var entityType = model.FindByClrName(change.Type) ?? throw new LigareQueryException($"There is no entity type '{change.Type}'.");
        var keyProperties = entityType.FindPrimaryKey()!.Properties;
        var keyValues = keyProperties
            .Select(p => change.Key.TryGetValue(p.Name, out var v)
                ? v.Deserialize(p.ClrType, WireJson.Options)
                : throw new LigareQueryException($"The change of {entityType.ClrType.Name} lacks its key {p.Name}."))
            .ToArray();

        if (change.State == WireChangeState.Added)
        {
            var entity = Activator.CreateInstance(entityType.ClrType, nonPublic: true)!;
            var entry = db.Entry(entity);
            for (var i = 0; i < keyProperties.Count; i++)
            {
                entry.Property(keyProperties[i].Name).CurrentValue = keyValues[i];
            }

            // CLR values first, then Add, which also tracks owned data; shadow values exist once tracked.
            var values = change.Values ?? [];
            SetValues(entry, values.Where(v => entityType.FindProperty(v.Key)?.IsShadowProperty() != true).ToDictionary(), keyProperties);
            db.Add(entity);
            SetValues(entry, values.Where(v => entityType.FindProperty(v.Key)?.IsShadowProperty() == true).ToDictionary(), keyProperties);
            return;
        }

        var existing = await db.FindAsync(entityType.ClrType, keyValues, cancellationToken)
            ?? throw new LigareQueryException($"{entityType.ClrType.Name} {string.Join(", ", keyValues)} does not exist (any more).", StatusCodes.Status404NotFound);

        if (change.State == WireChangeState.Deleted)
        {
            db.Remove(existing);
        }
        else
        {
            SetValues(db.Entry(existing), change.Values, keyProperties);
        }
    }

    private static void SetValues(EntityEntry entry, Dictionary<string, JsonElement>? values, IReadOnlyList<IProperty> key)
    {
        foreach (var (name, json) in values ?? [])
        {
            var type = entry.Metadata;
            if (type.FindProperty(name) is { } property)
            {
                if (key.Contains(property))
                {
                    if (entry.State is not (EntityState.Detached or EntityState.Added))
                    {
                        throw new LigareQueryException($"The key {type.ClrType.Name}.{name} cannot be changed.");
                    }

                    continue;
                }

                entry.Property(name).CurrentValue = json.Deserialize(property.ClrType, WireJson.Options);
            }
            else if (type.FindComplexProperty(name) is { } complex)
            {
                entry.ComplexProperty(name).CurrentValue = json.Deserialize(complex.ClrType, WireJson.Options);
            }
            else if (type.FindNavigation(name) is { ForeignKey.IsOwnership: true, IsOnDependent: false } owned)
            {
                entry.Navigation(name).CurrentValue = json.Deserialize(owned.ClrType, WireJson.Options);
            }
            else
            {
                throw new LigareQueryException($"{type.ClrType.Name} has no value '{name}'.");
            }
        }
    }

    /// <summary>Rules look at related entities (e.g. the equipment of a new status change): load what the keys point to.</summary>
    private static async Task LoadReferencesAsync(DbContext db, CancellationToken cancellationToken)
    {
        db.ChangeTracker.DetectChanges();
        var entries = db.ChangeTracker.Entries()
            .Where(e => !e.Metadata.IsOwned() && e.State is EntityState.Added or EntityState.Modified)
            .ToList();

        foreach (var entry in entries)
        {
            foreach (var reference in entry.References)
            {
                if (reference.Metadata is INavigation { IsOnDependent: true, ForeignKey.IsOwnership: false } && reference.CurrentValue is null && !reference.IsLoaded)
                {
                    await reference.LoadAsync(cancellationToken);
                }
            }
        }
    }
}

internal interface ILigareServerHost
{
    Task ModelAsync(HttpContext http);

    Task QueryAsync(HttpContext http, string set, bool count);

    Task SaveAsync(HttpContext http);
}

internal sealed class LigareQueryException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Turns <c>Status,StatusChanges($expand=Status)</c> into include paths: <c>Status</c>, <c>StatusChanges.Status</c>.</summary>
internal static class ExpandParser
{
    public static IEnumerable<string> Parse(string? expand)
    {
        if (string.IsNullOrWhiteSpace(expand))
        {
            yield break;
        }

        foreach (var item in SplitTopLevel(expand))
        {
            var open = item.IndexOf('(');
            var navigation = (open < 0 ? item : item[..open]).Trim();
            var nested = open < 0 ? null : item[(open + 1)..item.LastIndexOf(')')];
            var nestedExpand = nested is null
                ? null
                : SplitTopLevel(nested, ';').Select(o => o.Trim()).FirstOrDefault(o => o.StartsWith("$expand=", StringComparison.Ordinal))?["$expand=".Length..];

            var children = Parse(nestedExpand).ToList();
            if (children.Count == 0)
            {
                yield return navigation;
            }

            foreach (var child in children)
            {
                yield return navigation + "." + child;
            }
        }
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator = ',')
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (text[i] == separator && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }
}
