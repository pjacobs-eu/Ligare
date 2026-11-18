using Ligare.Wire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace Ligare.Server;

/// <summary>The entity types a server exposes, built once from the EF Core model of the context.</summary>
internal sealed class ServerModel
{
    private readonly Dictionary<string, IEntityType> _byName;
    private readonly Dictionary<string, IEntityType> _byClrName;
    private readonly HashSet<IEntityType> _exposedTypes;

    public ServerModel(IModel model, IReadOnlySet<Type> excluded)
    {
        var exposed = model.GetEntityTypes()
            .Where(e => !e.IsOwned() && !e.HasSharedClrType && e.FindPrimaryKey() is not null && !excluded.Contains(e.ClrType))
            .ToList();
        _exposedTypes = exposed.ToHashSet();

        var duplicates = exposed.GroupBy(e => e.ClrType.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException($"Ligare names entity sets after their CLR type, which must be unique: {string.Join(", ", duplicates)}.");
        }

        _byName = exposed.ToDictionary(e => e.ClrType.Name, StringComparer.Ordinal);
        _byClrName = exposed.ToDictionary(e => e.ClrType.FullName!, StringComparer.Ordinal);
        Description = new ModelDescription(exposed.Select(e => Describe(e, _exposedTypes)).ToList());
        Edm = BuildEdm(exposed);
    }

    public ModelDescription Description { get; }

    /// <summary>The OData model, used to parse <c>$filter</c>.</summary>
    public IEdmModel Edm { get; }

    public IEntityType? FindByName(string name) => _byName.GetValueOrDefault(name);

    public IEntityType? FindByClrName(string clrName) => _byClrName.GetValueOrDefault(clrName);

    public bool IsExposed(IEntityType entityType) => _exposedTypes.Contains(entityType);

    private static EntityTypeDescription Describe(IEntityType entityType, HashSet<IEntityType> exposed)
    {
        var members = new List<MemberDescription>();

        foreach (var property in entityType.GetProperties())
        {
            members.Add(new MemberDescription(property.Name, MemberKind.Property, IsShadow: property.IsShadowProperty()));
        }

        foreach (var complex in entityType.GetComplexProperties())
        {
            members.Add(new MemberDescription(complex.Name, MemberKind.Value));
        }

        foreach (var navigation in entityType.GetNavigations())
        {
            var foreignKey = navigation.ForeignKey;
            if (foreignKey.IsOwnership)
            {
                // Owned types are stored with their owner and travel as one value.
                if (!navigation.IsOnDependent)
                {
                    members.Add(new MemberDescription(navigation.Name, MemberKind.Value));
                }

                continue;
            }

            if (!exposed.Contains(navigation.TargetEntityType))
            {
                // Navigations to excluded types do not exist for clients; their foreign keys stay plain values.
                continue;
            }

            if (navigation.IsCollection)
            {
                members.Add(new MemberDescription(
                    navigation.Name,
                    MemberKind.Collection,
                    Target: navigation.TargetEntityType.ClrType.FullName,
                    ForeignKey: foreignKey.Properties.Select(p => p.Name).ToList(),
                    PrincipalKey: foreignKey.PrincipalKey.Properties.Select(p => p.Name).ToList(),
                    IsRequired: foreignKey.IsRequired,
                    Inverse: navigation.Inverse?.Name));
            }
            else if (navigation.IsOnDependent)
            {
                members.Add(new MemberDescription(
                    navigation.Name,
                    MemberKind.Reference,
                    Target: navigation.TargetEntityType.ClrType.FullName,
                    ForeignKey: foreignKey.Properties.Select(p => p.Name).ToList(),
                    PrincipalKey: foreignKey.PrincipalKey.Properties.Select(p => p.Name).ToList(),
                    IsRequired: foreignKey.IsRequired,
                    Inverse: navigation.Inverse?.Name));
            }
        }

        return new EntityTypeDescription(
            entityType.ClrType.Name,
            entityType.ClrType.FullName!,
            entityType.FindPrimaryKey()!.Properties.Select(p => p.Name).ToList(),
            members);
    }

    private static IEdmModel BuildEdm(IReadOnlyList<IEntityType> exposed)
    {
        var builder = new ODataConventionModelBuilder();

        foreach (var entityType in exposed)
        {
            var configuration = builder.AddEntityType(entityType.ClrType);
            builder.AddEntitySet(entityType.ClrType.Name, configuration);

            var keyProperties = entityType.FindPrimaryKey()!.Properties.Select(p => p.PropertyInfo).OfType<System.Reflection.PropertyInfo>();
            foreach (var key in keyProperties)
            {
                configuration.HasKey(key);
            }

            // Only what EF Core maps can be filtered on; computed CLR properties would fail in SQL.
            var mapped = entityType.GetProperties().Select(p => p.Name)
                .Concat(entityType.GetComplexProperties().Select(p => p.Name))
                .Concat(entityType.GetNavigations().Select(n => n.Name))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var property in entityType.ClrType.GetProperties().Where(p => !mapped.Contains(p.Name)))
            {
                configuration.RemoveProperty(property);
            }

            // The conventions skip properties without a public setter; EF Core maps them, so filters may use them.
            var known = configuration.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var property in entityType.GetProperties().Select(p => p.PropertyInfo).OfType<System.Reflection.PropertyInfo>())
            {
                if (known.Contains(property.Name))
                {
                    continue;
                }

                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (type.IsEnum)
                {
                    configuration.AddEnumProperty(property);
                }
                else if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
                         || type == typeof(DateTimeOffset) || type == typeof(DateTime) || type == typeof(DateOnly)
                         || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(byte[]))
                {
                    configuration.AddProperty(property);
                }
                else
                {
                    builder.AddComplexType(type);
                    configuration.AddComplexProperty(property);
                }
            }
        }

        return builder.GetEdmModel();
    }
}
