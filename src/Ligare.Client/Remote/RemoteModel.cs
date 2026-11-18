using System.Collections;
using System.Reflection;
using System.Text.Json;
using Ligare.Wire;

namespace Ligare.Remote;

/// <summary>The server's model, bound to the CLR types of this process.</summary>
internal sealed class RemoteModel
{
    private readonly Dictionary<Type, RemoteEntityType> _byType = [];
    private readonly Dictionary<string, RemoteEntityType> _byClrName = [];

    public RemoteModel(ModelDescription description)
    {
        foreach (var entityType in description.EntityTypes)
        {
            var clrType = ResolveType(entityType.ClrName)
                ?? throw new InvalidOperationException($"The server exposes '{entityType.ClrName}', but no such type is loaded in this process.");
            var bound = new RemoteEntityType(this, clrType, entityType);
            _byType[clrType] = bound;
            _byClrName[entityType.ClrName] = bound;
        }
    }

    public RemoteEntityType this[Type clrType] =>
        _byType.TryGetValue(clrType, out var entityType)
            ? entityType
            : throw new InvalidOperationException($"'{clrType.Name}' is not an entity type of the Ligare server.");

    public RemoteEntityType this[string clrName] =>
        _byClrName.TryGetValue(clrName, out var entityType)
            ? entityType
            : throw new InvalidOperationException($"'{clrName}' is not an entity type of the Ligare server.");

    public bool IsEntityType(Type clrType) => _byType.ContainsKey(clrType);

    private static Type? ResolveType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Select(a => a.GetType(fullName, throwOnError: false))
            .FirstOrDefault(t => t is not null);
}

internal sealed class RemoteEntityType
{
    private readonly Dictionary<string, RemoteMember> _members;

    public RemoteEntityType(RemoteModel model, Type clrType, EntityTypeDescription description)
    {
        Model = model;
        ClrType = clrType;
        Description = description;
        _members = description.Members.ToDictionary(m => m.Name, m => new RemoteMember(clrType, m));
        Key = description.Key.Select(k => _members[k]).ToArray();
        Members = [.. _members.Values];
    }

    public RemoteModel Model { get; }

    public Type ClrType { get; }

    public EntityTypeDescription Description { get; }

    public string Name => Description.Name;

    public string ClrName => Description.ClrName;

    public IReadOnlyList<RemoteMember> Key { get; }

    public IReadOnlyList<RemoteMember> Members { get; }

    public IEnumerable<RemoteMember> Values => Members.Where(m => m.Kind is MemberKind.Property or MemberKind.Value);

    public RemoteMember this[string name] =>
        _members.TryGetValue(name, out var member)
            ? member
            : throw new InvalidOperationException($"'{name}' is not a member of {Name} on the Ligare server.");

    public RemoteMember? Find(string name) => _members.GetValueOrDefault(name);
}

/// <summary>A member of an entity type, with access to its CLR property, backing field or shadow value.</summary>
internal sealed class RemoteMember
{
    private readonly PropertyInfo? _property;
    private readonly FieldInfo? _field;

    public RemoteMember(Type declaringType, MemberDescription description)
    {
        Description = description;
        if (!description.IsShadow)
        {
            _property = FindProperty(declaringType, description.Name)
                ?? throw new InvalidOperationException($"{declaringType.Name}.{description.Name} exists on the server but not in this process.");
            _field = _property.SetMethod is null ? FindBackingField(declaringType, description.Name) : null;
        }

        ValueType = _property?.PropertyType ?? typeof(object);
    }

    public MemberDescription Description { get; }

    public string Name => Description.Name;

    public MemberKind Kind => Description.Kind;

    public bool IsShadow => Description.IsShadow;

    public Type ValueType { get; }

    /// <summary>The element type of a collection navigation.</summary>
    public Type ElementType =>
        ValueType.GetInterfaces().Append(ValueType)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0]
        ?? throw new InvalidOperationException($"{Name} is not a collection.");

    public bool CanSet => _property?.SetMethod is not null || _field is not null;

    public object? Get(object entity, IReadOnlyDictionary<string, object?>? shadow) =>
        IsShadow ? shadow?.GetValueOrDefault(Name) : _property!.GetValue(entity);

    public void Set(object entity, object? value, IDictionary<string, object?>? shadow)
    {
        if (IsShadow)
        {
            if (shadow is not null)
            {
                shadow[Name] = value;
            }
        }
        else if (_property!.SetMethod is not null)
        {
            _property.SetValue(entity, value);
        }
        else if (_field is not null)
        {
            _field.SetValue(entity, value);
        }
        else
        {
            throw new InvalidOperationException($"{_property.DeclaringType?.Name}.{Name} cannot be set.");
        }
    }

    /// <summary>The collection of a collection navigation, created if it is null and settable.</summary>
    public IList? GetOrCreateCollection(object entity)
    {
        if (_property!.GetValue(entity) is IList existing)
        {
            return existing;
        }

        if (!CanSet)
        {
            return null;
        }

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ElementType))!;
        Set(entity, list, null);
        return list;
    }

    public object? Read(JsonElement element) => element.Deserialize(ValueType, WireJson.Options);

    /// <summary>Shadow values may still be the JSON they arrived as; everything else is serialized by its CLR type.</summary>
    public JsonElement Write(object? value) => value switch
    {
        JsonElement json => json,
        _ => JsonSerializer.SerializeToElement(value, IsShadow && value is not null ? value.GetType() : ValueType, WireJson.Options),
    };

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } property)
            {
                return property;
            }
        }

        return null;
    }

    private static FieldInfo? FindBackingField(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field)
            {
                return field;
            }
        }

        return null;
    }
}
