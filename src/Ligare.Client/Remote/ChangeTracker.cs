using System.Collections;
using System.Text.Json;
using Ligare.Wire;

namespace Ligare.Remote;

internal enum TrackedState
{
    Unchanged,
    Added,
    Modified,
    Deleted,
}

internal sealed class TrackedEntity(object entity, RemoteEntityType type, Dictionary<string, object?> shadow)
{
    public object Entity { get; } = entity;

    public RemoteEntityType Type { get; } = type;

    public Dictionary<string, object?> Shadow { get; } = shadow;

    public TrackedState State { get; set; }

    /// <summary>Values as loaded, serialized: compared as JSON, so value objects compare by content.</summary>
    public Dictionary<string, string> Values { get; } = [];

    public Dictionary<string, object?> References { get; } = [];

    public Dictionary<string, HashSet<object>> Collections { get; } = [];

    public string Key => ChangeTracker.KeyOf(Type, Entity, Shadow);
}

internal sealed record DetectedChange(TrackedEntity Entry, WireChange Wire);

/// <summary>
/// Snapshot change tracking, as EF Core does it: compares entities with their state as loaded,
/// discovers new entities through navigations and keeps foreign keys in step with navigations.
/// </summary>
internal sealed class ChangeTracker
{
    private readonly Dictionary<(Type, string), TrackedEntity> _byKey = [];
    private readonly Dictionary<object, TrackedEntity> _byInstance = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _pendingAdds = [];
    private readonly List<object> _pendingRemoves = [];

    public static string KeyOf(RemoteEntityType type, JsonElement json) =>
        string.Join("|", type.Key.Select(k => json.TryGetProperty(k.Name, out var v)
            ? v.GetRawText()
            : throw new InvalidOperationException($"The server sent {type.Name} without its key {k.Name}.")));

    public static string KeyOf(RemoteEntityType type, object entity, IReadOnlyDictionary<string, object?>? shadow) =>
        string.Join("|", type.Key.Select(k => k.Write(k.Get(entity, shadow)).GetRawText()));

    public static string KeyOf(RemoteEntityType type, Dictionary<string, JsonElement> key) =>
        string.Join("|", type.Key.Select(k => key[k.Name].GetRawText()));

    public TrackedEntity? Find(RemoteEntityType type, string key) => _byKey.GetValueOrDefault((type.ClrType, key));

    public TrackedEntity? Find(RemoteEntityType type, Dictionary<string, JsonElement> key) => Find(type, KeyOf(type, key));

    public TrackedEntity Attach(object entity, RemoteEntityType type, string key, Dictionary<string, object?> shadow)
    {
        var entry = new TrackedEntity(entity, type, shadow);
        _byKey[(type.ClrType, key)] = entry;
        _byInstance[entity] = entry;
        return entry;
    }

    public void AddPending(object entity)
    {
        if (_byInstance.TryGetValue(entity, out var entry) && entry.State == TrackedState.Deleted)
        {
            entry.State = TrackedState.Modified;
            return;
        }

        _pendingAdds.Add(entity);
    }

    public void RemovePending(object entity) => _pendingRemoves.Add(entity);

    public static void TakeSnapshot(TrackedEntity entry)
    {
        var shadow = entry.Shadow;
        foreach (var member in entry.Type.Members)
        {
            switch (member.Kind)
            {
                case MemberKind.Property or MemberKind.Value:
                    entry.Values[member.Name] = member.Write(member.Get(entry.Entity, shadow)).GetRawText();
                    break;
                case MemberKind.Reference when !member.IsShadow:
                    entry.References[member.Name] = member.Get(entry.Entity, shadow);
                    break;
                case MemberKind.Collection when member.Get(entry.Entity, shadow) is IEnumerable items:
                    entry.Collections[member.Name] = new HashSet<object>(items.Cast<object>(), ReferenceEqualityComparer.Instance);
                    break;
            }
        }
    }

    /// <summary>Finds every change since loading, in the order the server should apply them.</summary>
    public List<DetectedChange> DetectChanges(RemoteModel model)
    {
        foreach (var entity in _pendingAdds)
        {
            Track(model, entity, TrackedState.Added);
        }

        foreach (var entity in _pendingRemoves)
        {
            if (_byInstance.TryGetValue(entity, out var entry))
            {
                if (entry.State == TrackedState.Added)
                {
                    Forget(entry);
                }
                else
                {
                    entry.State = TrackedState.Deleted;
                }
            }
        }

        _pendingAdds.Clear();
        _pendingRemoves.Clear();

        // Navigations first: they discover new entities and set foreign keys, which are then changes too.
        var queue = new Queue<TrackedEntity>(_byInstance.Values.Where(e => e.State != TrackedState.Deleted));
        while (queue.TryDequeue(out var entry))
        {
            FixUpNavigations(model, entry, queue);
        }

        var changes = new List<DetectedChange>();
        foreach (var entry in _byInstance.Values)
        {
            var shadow = entry.Shadow;
            var key = entry.Type.Key.ToDictionary(k => k.Name, k => k.Write(k.Get(entry.Entity, shadow)));

            switch (entry.State)
            {
                case TrackedState.Added:
                    changes.Add(new DetectedChange(entry, new WireChange(entry.Type.ClrName, WireChangeState.Added, key,
                        entry.Type.Values.ToDictionary(m => m.Name, m => m.Write(m.Get(entry.Entity, shadow))))));
                    break;
                case TrackedState.Deleted:
                    changes.Add(new DetectedChange(entry, new WireChange(entry.Type.ClrName, WireChangeState.Deleted, key, null)));
                    break;
                default:
                    var modified = new Dictionary<string, JsonElement>();
                    foreach (var member in entry.Type.Values)
                    {
                        var current = member.Write(member.Get(entry.Entity, shadow));
                        if (current.GetRawText() != entry.Values.GetValueOrDefault(member.Name))
                        {
                            modified[member.Name] = current;
                        }
                    }

                    entry.State = modified.Count > 0 ? TrackedState.Modified : TrackedState.Unchanged;
                    if (modified.Count > 0)
                    {
                        changes.Add(new DetectedChange(entry, new WireChange(entry.Type.ClrName, WireChangeState.Modified, key, modified)));
                    }

                    break;
            }
        }

        return changes;
    }

    /// <summary>Takes over what the server stored and makes it the new baseline.</summary>
    public void AcceptChanges(RemoteModel model, IReadOnlyList<WireEntity> saved)
    {
        foreach (var entity in saved)
        {
            var type = model[entity.Type];
            if (Find(type, entity.Key) is not { } entry)
            {
                continue;
            }

            foreach (var (name, value) in entity.Values)
            {
                if (type.Find(name) is { Kind: MemberKind.Property or MemberKind.Value } member)
                {
                    member.Set(entry.Entity, member.Read(value), entry.Shadow);
                }
            }
        }

        foreach (var entry in _byInstance.Values.ToList())
        {
            if (entry.State == TrackedState.Deleted)
            {
                Forget(entry);
                continue;
            }

            entry.State = TrackedState.Unchanged;
        }

        // Rules on the server may have moved foreign keys: point the navigations at what is loaded.
        foreach (var entry in _byInstance.Values)
        {
            FixUpReferencesFromKeys(model, entry);
            TakeSnapshot(entry);
        }
    }

    private TrackedEntity Track(RemoteModel model, object entity, TrackedState state, Dictionary<string, object?>? shadow = null)
    {
        if (_byInstance.TryGetValue(entity, out var existing))
        {
            return existing;
        }

        var type = model[entity.GetType()];
        shadow ??= [];
        var key = KeyOf(type, entity, shadow);

        if (type.Key.Any(k => IsDefault(k.Get(entity, shadow))))
        {
            throw new InvalidOperationException($"A new {type.Name} needs its key ({string.Join(", ", type.Key.Select(k => k.Name))}) set in code before it is saved.");
        }

        if (_byKey.ContainsKey((type.ClrType, key)))
        {
            throw new InvalidOperationException($"Another {type.Name} with the key {key} is already tracked.");
        }

        var entry = Attach(entity, type, key, shadow);
        entry.State = state;
        return entry;
    }

    private void Forget(TrackedEntity entry)
    {
        _byInstance.Remove(entry.Entity);
        _byKey.Remove((entry.Type.ClrType, entry.Key));
    }

    private void FixUpNavigations(RemoteModel model, TrackedEntity entry, Queue<TrackedEntity> queue)
    {
        var shadow = entry.Shadow;
        foreach (var member in entry.Type.Members)
        {
            if (member.Kind == MemberKind.Reference && !member.IsShadow)
            {
                var target = member.Get(entry.Entity, shadow);
                var targetEntry = target is null ? null : TrackOrQueue(model, target, queue);
                var changed = entry.State == TrackedState.Added || !ReferenceEquals(target, entry.References.GetValueOrDefault(member.Name));

                if (changed && (targetEntry is not null || entry.References.GetValueOrDefault(member.Name) is not null))
                {
                    SetForeignKey(entry, member.Description.ForeignKey!, targetEntry, member.Description.PrincipalKey!);
                }
            }
            else if (member.Kind == MemberKind.Collection && member.Get(entry.Entity, shadow) is IEnumerable items)
            {
                var current = new HashSet<object>(items.Cast<object>(), ReferenceEqualityComparer.Instance);
                foreach (var item in current)
                {
                    // The foreign key first: it may be part of the element's key (e.g. owner id + sequence).
                    if (!_byInstance.TryGetValue(item, out var itemEntry))
                    {
                        var itemShadow = new Dictionary<string, object?>();
                        SetForeignKey(model[item.GetType()], item, itemShadow, member.Description.ForeignKey!, entry, member.Description.PrincipalKey!);
                        itemEntry = Track(model, item, TrackedState.Added, itemShadow);
                        queue.Enqueue(itemEntry);
                    }
                    else
                    {
                        SetForeignKey(itemEntry, member.Description.ForeignKey!, entry, member.Description.PrincipalKey!);
                    }

                    if (member.Description.Inverse is { } inverse && itemEntry.Type.Find(inverse) is { IsShadow: false } back
                        && back.Get(item, null) is null)
                    {
                        back.Set(item, entry.Entity, null);
                    }
                }

                foreach (var removed in entry.Collections.GetValueOrDefault(member.Name) ?? [])
                {
                    if (!current.Contains(removed) && _byInstance.TryGetValue(removed, out var removedEntry))
                    {
                        if (member.Description.IsRequired)
                        {
                            removedEntry.State = TrackedState.Deleted;
                        }
                        else
                        {
                            SetForeignKey(removedEntry, member.Description.ForeignKey!, null, member.Description.PrincipalKey!);
                        }
                    }
                }
            }
        }
    }

    private TrackedEntity TrackOrQueue(RemoteModel model, object entity, Queue<TrackedEntity> queue)
    {
        if (_byInstance.TryGetValue(entity, out var entry))
        {
            return entry;
        }

        entry = Track(model, entity, TrackedState.Added);
        queue.Enqueue(entry);
        return entry;
    }

    private static void SetForeignKey(TrackedEntity dependent, IReadOnlyList<string> foreignKey, TrackedEntity? principal, IReadOnlyList<string> principalKey) =>
        SetForeignKey(dependent.Type, dependent.Entity, dependent.Shadow, foreignKey, principal, principalKey);

    private static void SetForeignKey(RemoteEntityType type, object dependent, Dictionary<string, object?> shadow, IReadOnlyList<string> foreignKey, TrackedEntity? principal, IReadOnlyList<string> principalKey)
    {
        for (var i = 0; i < foreignKey.Count; i++)
        {
            var value = principal is null
                ? null
                : principal.Type[principalKey[i]].Get(principal.Entity, principal.Shadow);
            type[foreignKey[i]].Set(dependent, value, shadow);
        }
    }

    private void FixUpReferencesFromKeys(RemoteModel model, TrackedEntity entry)
    {
        var shadow = entry.Shadow;
        foreach (var member in entry.Type.Members.Where(m => m.Kind == MemberKind.Reference && !m.IsShadow && m.CanSet))
        {
            var target = model[member.Description.Target!];
            var foreignKey = member.Description.ForeignKey!;
            var principalKey = member.Description.PrincipalKey!;

            var keyValues = foreignKey.Select(fk => entry.Type[fk].Get(entry.Entity, shadow)).ToList();
            var current = member.Get(entry.Entity, shadow);

            if (keyValues.Any(v => v is null))
            {
                continue;
            }

            if (current is not null && _byInstance.TryGetValue(current, out var currentEntry)
                && principalKey.Select(pk => currentEntry.Type[pk].Get(current, currentEntry.Shadow)).SequenceEqual(keyValues))
            {
                continue;
            }

            var key = string.Join("|", principalKey.Select((pk, i) => target[pk].Write(keyValues[i]).GetRawText()));
            if (Find(target, key) is { } principal)
            {
                member.Set(entry.Entity, principal.Entity, entry.Shadow);
            }
        }
    }

    private static bool IsDefault(object? value) =>
        value is null || (value.GetType().IsValueType && value.Equals(Activator.CreateInstance(value.GetType())));
}
