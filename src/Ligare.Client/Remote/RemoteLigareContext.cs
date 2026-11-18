using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ligare.Wire;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace Ligare.Remote;

/// <summary>
/// Remote mode: an <see cref="ILigareContext"/> whose queries and saves go to a Ligare server. Tracks
/// what it loaded the way EF Core does — by snapshot — and sends only what changed.
/// </summary>
internal sealed class RemoteLigareContext : ILigareContext
{
    private readonly HttpClient _http;
    private readonly RemoteModelSource _modelSource;
    private readonly EntityQueryProvider _provider;
    private readonly ChangeTracker _tracker = new();

    public RemoteLigareContext(HttpClient http, RemoteModelSource modelSource)
    {
        _http = http;
        _modelSource = modelSource;
        _provider = new EntityQueryProvider(new RemoteQueryCompiler(this));
    }

    public bool TrackingByDefault => true;

    public IQueryable<T> Set<T>() where T : class => new EntityQueryable<T>(_provider, new RemoteRootExpression(typeof(T)));

    public T Add<T>(T entity) where T : class
    {
        _tracker.AddPending(entity);
        return entity;
    }

    public void Remove<T>(T entity) where T : class => _tracker.RemovePending(entity);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var model = await _modelSource.GetAsync(_http, cancellationToken);
        var changes = _tracker.DetectChanges(model);
        if (changes.Count == 0)
        {
            return 0;
        }

        var request = new SaveRequest(changes.Select(c => c.Wire).ToList());
        using var response = await _http.PostAsJsonAsync("$save", request, WireJson.Options, cancellationToken);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var rejected = await response.Content.ReadFromJsonAsync<SaveErrorResponse>(WireJson.Options, cancellationToken);
            throw new LigareValidationException(rejected!.Errors
                .Select(e => new LigareValidationError(_tracker.Find(model[e.Type], e.Key)?.Entity ?? (object)e.Key, e.Property, e.Message))
                .ToList());
        }

        await EnsureSuccessAsync(response, cancellationToken);
        var saved = await response.Content.ReadFromJsonAsync<SaveResponse>(WireJson.Options, cancellationToken);
        _tracker.AcceptChanges(model, saved!.Entities, saved.Related ?? []);
        return saved.Entities.Count;
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async IAsyncEnumerable<T> EnumerateAsync<T>(RemoteQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var entity in await LoadAsync(query, cancellationToken))
        {
            yield return (T)entity;
        }
    }

    public async Task<TResult> ExecuteAsync<TResult>(RemoteQuery query, CancellationToken cancellationToken)
    {
        var model = await _modelSource.GetAsync(_http, cancellationToken);

        if (query.Terminal is QueryTerminal.Count or QueryTerminal.LongCount)
        {
            var path = $"{model[query.ElementType].Name}/$count?{query.ToQueryString(forCount: true)}";
            using var response = await _http.GetAsync(path, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var count = long.Parse(await response.Content.ReadAsStringAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            return query.Terminal == QueryTerminal.Count ? (TResult)(object)checked((int)count) : (TResult)(object)count;
        }

        var results = await LoadAsync(query, cancellationToken);
        object? result = query.Terminal switch
        {
            QueryTerminal.Any => results.Count > 0,
            QueryTerminal.First => results.Count > 0 ? results[0] : throw new InvalidOperationException("Sequence contains no elements"),
            QueryTerminal.FirstOrDefault => results.FirstOrDefault(),
            QueryTerminal.Single => results.Count == 1 ? results[0] : throw new InvalidOperationException(results.Count == 0 ? "Sequence contains no elements" : "Sequence contains more than one element"),
            QueryTerminal.SingleOrDefault => results.Count <= 1 ? results.FirstOrDefault() : throw new InvalidOperationException("Sequence contains more than one element"),
            _ => throw new NotSupportedException(query.Terminal.ToString()),
        };

        return (TResult)result!;
    }

    private async Task<IReadOnlyList<object>> LoadAsync(RemoteQuery query, CancellationToken cancellationToken)
    {
        var model = await _modelSource.GetAsync(_http, cancellationToken);
        var entityType = model[query.ElementType];

        using var response = await _http.GetAsync($"{entityType.Name}?{query.ToQueryString()}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        var materializer = new Materializer(model, query.Tracking ? _tracker : null);
        var entities = document.RootElement.GetProperty("value").EnumerateArray()
            .Select(json => materializer.Materialize(json, entityType, query.Includes));

        if (query.Projection is { } projection)
        {
            var select = projection.Compile();
            return entities.Select(e => select.DynamicInvoke(e)!).ToList();
        }

        return entities.ToList();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"The Ligare server answered {(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
        }
    }
}

/// <summary>Loads the server's model once and shares it between contexts.</summary>
internal sealed class RemoteModelSource
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private RemoteModel? _model;

    public async ValueTask<RemoteModel> GetAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (_model is not null)
        {
            return _model;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            return _model ??= new RemoteModel((await http.GetFromJsonAsync<ModelDescription>("$model", WireJson.Options, cancellationToken))!);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Builds entities from query results, resolving each key to one instance.</summary>
internal sealed class Materializer(RemoteModel model, ChangeTracker? tracker)
{
    private readonly Dictionary<(Type, string), object> _untracked = [];

    public object Materialize(JsonElement json, RemoteEntityType type, IncludeNode? include)
    {
        var key = ChangeTracker.KeyOf(type, json);
        var tracked = tracker?.Find(type, key);
        object entity;
        Dictionary<string, object?>? shadow;
        var fresh = false;

        if (tracked is not null)
        {
            entity = tracked.Entity;
            shadow = tracked.Shadow;
        }
        else if (tracker is null && _untracked.TryGetValue((type.ClrType, key), out var known))
        {
            entity = known;
            shadow = null;
        }
        else
        {
            if (json.TryGetProperty(WireJson.ReferenceMarker, out _))
            {
                throw new InvalidOperationException($"The server referenced {type.Name} {key} before sending it.");
            }

            entity = Create(type.ClrType);
            shadow = new Dictionary<string, object?>();
            foreach (var member in type.Values)
            {
                if (json.TryGetProperty(member.Name, out var value))
                {
                    member.Set(entity, member.Read(value), shadow);
                }
            }

            fresh = true;
            if (tracker is null)
            {
                _untracked[(type.ClrType, key)] = entity;
            }
            else
            {
                tracked = tracker.Attach(entity, type, key, shadow);
            }
        }

        foreach (var member in type.Members)
        {
            if (!json.TryGetProperty(member.Name, out var value) || member.Kind is MemberKind.Property or MemberKind.Value)
            {
                continue;
            }

            var target = model[member.Description.Target!];
            var child = include?.Children.GetValueOrDefault(member.Name);

            if (member.Kind == MemberKind.Reference)
            {
                var related = value.ValueKind == JsonValueKind.Null ? null : Materialize(value, target, child);
                if (fresh || member.Get(entity, null) is null)
                {
                    member.Set(entity, related, shadow);
                }
            }
            else if (member.GetOrCreateCollection(entity) is { } collection)
            {
                foreach (var item in value.EnumerateArray())
                {
                    var element = Materialize(item, target, child);
                    if (!collection.Contains(element))
                    {
                        collection.Add(element);
                    }
                }

                if (child is { Orderings.Count: > 0 })
                {
                    Sort(collection, child.Orderings);
                }
            }
        }

        if (tracked is not null)
        {
            ChangeTracker.TakeSnapshot(tracked);
        }

        return entity;
    }

    internal static object Create(Type type) =>
        type.GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes) is { } constructor
            ? constructor.Invoke(null)
            : RuntimeHelpers.GetUninitializedObject(type);

    private static void Sort(IList collection, IReadOnlyList<(System.Linq.Expressions.LambdaExpression Key, bool Descending)> orderings)
    {
        IEnumerable<object> items = collection.Cast<object>().ToList();
        IOrderedEnumerable<object>? ordered = null;
        foreach (var (key, descending) in orderings)
        {
            var compiled = key.Compile();
            Func<object, object?> selector = item => compiled.DynamicInvoke(item);
            ordered = ordered is null
                ? descending ? items.OrderByDescending(selector, Comparer<object?>.Default) : items.OrderBy(selector, Comparer<object?>.Default)
                : descending ? ordered.ThenByDescending(selector, Comparer<object?>.Default) : ordered.ThenBy(selector, Comparer<object?>.Default);
        }

        var sorted = ordered!.ToList();
        collection.Clear();
        foreach (var item in sorted)
        {
            collection.Add(item);
        }
    }
}
