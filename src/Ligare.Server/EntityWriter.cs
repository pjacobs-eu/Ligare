using System.Text.Json;
using Ligare.Wire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Ligare.Server;

/// <summary>
/// Writes tracked entities as JSON, from the EF Core model: mapped values (shadow properties included),
/// owned and complex values, and the navigations that were loaded. An entity that appears twice is
/// written once; later occurrences only carry its key and <see cref="WireJson.ReferenceMarker"/>.
/// </summary>
internal sealed class EntityWriter(DbContext db, Utf8JsonWriter json)
{
    private readonly HashSet<object> _written = new(ReferenceEqualityComparer.Instance);

    public void WriteEntity(object entity, bool navigations)
    {
        var entry = db.Entry(entity);
        json.WriteStartObject();

        if (!_written.Add(entity))
        {
            json.WriteBoolean(WireJson.ReferenceMarker, true);
            foreach (var key in entry.Metadata.FindPrimaryKey()!.Properties)
            {
                WriteValue(key.Name, entry.Property(key.Name).CurrentValue, key.ClrType);
            }

            json.WriteEndObject();
            return;
        }

        WriteValues(entry);

        if (navigations)
        {
            foreach (var navigation in entry.Navigations)
            {
                var metadata = navigation.Metadata as Microsoft.EntityFrameworkCore.Metadata.INavigation;
                if (metadata is null || metadata.ForeignKey.IsOwnership || !navigation.IsLoaded)
                {
                    continue;
                }

                json.WritePropertyName(metadata.Name);
                if (navigation is CollectionEntry collection)
                {
                    json.WriteStartArray();
                    foreach (var item in collection.CurrentValue ?? Array.Empty<object>())
                    {
                        WriteEntity(item, navigations: true);
                    }

                    json.WriteEndArray();
                }
                else if (navigation.CurrentValue is { } related)
                {
                    WriteEntity(related, navigations: true);
                }
                else
                {
                    json.WriteNullValue();
                }
            }
        }

        json.WriteEndObject();
    }

    /// <summary>The stored values of an entity, without navigations, as the save response sends them.</summary>
    public static Dictionary<string, JsonElement> Values(EntityEntry entry)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            new EntityWriter(entry.Context, json).WriteValues(entry);
            json.WriteEndObject();
        }

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(buffer.ToArray())!;
    }

    public static Dictionary<string, JsonElement> Key(EntityEntry entry) =>
        entry.Metadata.FindPrimaryKey()!.Properties.ToDictionary(
            p => p.Name,
            p => JsonSerializer.SerializeToElement(entry.Property(p.Name).CurrentValue, p.ClrType, WireJson.Options));

    private void WriteValues(EntityEntry entry)
    {
        foreach (var property in entry.Metadata.GetProperties())
        {
            WriteValue(property.Name, entry.Property(property.Name).CurrentValue, property.ClrType);
        }

        foreach (var complex in entry.Metadata.GetComplexProperties())
        {
            WriteValue(complex.Name, entry.ComplexProperty(complex.Name).CurrentValue, complex.ClrType);
        }

        foreach (var navigation in entry.Metadata.GetNavigations().Where(n => n.ForeignKey.IsOwnership && !n.IsOnDependent))
        {
            WriteValue(navigation.Name, entry.Navigation(navigation.Name).CurrentValue, navigation.ClrType);
        }
    }

    private void WriteValue(string name, object? value, Type type)
    {
        json.WritePropertyName(name);
        JsonSerializer.Serialize(json, value, type, WireJson.Options);
    }
}
