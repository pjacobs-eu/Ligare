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

    public ServerModel(IModel model)
    {
        var exposed = model.GetEntityTypes()
            .Where(e => !e.IsOwned() && !e.HasSharedClrType && e.FindPrimaryKey() is not null)
            .ToList();

        var duplicates = exposed.GroupBy(e => e.ClrType.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException($"Ligare names entity sets after their CLR type, which must be unique: {string.Join(", ", duplicates)}.");
        }

        _byName = exposed.ToDictionary(e => e.ClrType.Name, StringComparer.Ordinal);
        _byClrName = exposed.ToDictionary(e => e.ClrType.FullName!, StringComparer.Ordinal);
        Description = new ModelDescription(exposed.Select(Describe).ToList());
        Edm = BuildEdm(exposed);
    }

    public ModelDescription Description { get; }

    /// <summary>The OData model, used to parse <c>$filter</c>.</summary>
    public IEdmModel Edm { get; }

    public IEntityType? FindByName(string name) => _byName.GetValueOrDefault(name);

    public IEntityType? FindByClrName(string clrName) => _byClrName.GetValueOrDefault(clrName);

    private static EntityTypeDescription Describe(IEntityType entityType)
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
        }

        return builder.GetEdmModel();
    }
}
