using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ligare.Wire;

// What a Ligare server and its remote clients exchange. Queries use OData URL conventions
// ($filter, $orderby, $top, $skip, $expand, /$count); entities travel as plain JSON objects.

/// <summary>Response of <c>GET {base}/$model</c>: the entity types a server exposes.</summary>
public sealed record ModelDescription(IReadOnlyList<EntityTypeDescription> EntityTypes);

/// <param name="Name">CLR type name, also the entity set name in URLs.</param>
/// <param name="ClrName">Full CLR type name.</param>
public sealed record EntityTypeDescription(string Name, string ClrName, IReadOnlyList<string> Key, IReadOnlyList<MemberDescription> Members);

public enum MemberKind
{
    /// <summary>A scalar column (including value-converted value objects).</summary>
    Property,

    /// <summary>A complex property or owned type, stored with its owner and sent as one value.</summary>
    Value,

    /// <summary>A reference navigation whose foreign key is on this side.</summary>
    Reference,

    /// <summary>A collection navigation; the foreign key is on the elements.</summary>
    Collection,
}

/// <param name="IsShadow">Exists only in the model (no CLR property); its value is tracked by Ligare.</param>
/// <param name="Target">Full CLR name of the related type (navigations).</param>
/// <param name="ForeignKey">Reference: foreign key properties on this type. Collection: on the element type.</param>
/// <param name="PrincipalKey">The key the foreign key points to, on the principal.</param>
/// <param name="IsRequired">Reference: the relationship is required. Collection: elements cannot exist without the owner.</param>
/// <param name="Inverse">The navigation on the other side, if any.</param>
public sealed record MemberDescription(
    string Name,
    MemberKind Kind,
    bool IsShadow = false,
    string? Target = null,
    IReadOnlyList<string>? ForeignKey = null,
    IReadOnlyList<string>? PrincipalKey = null,
    bool IsRequired = false,
    string? Inverse = null);

public enum WireChangeState
{
    Added,
    Modified,
    Deleted,
}

/// <param name="Type">Full CLR name of the entity type.</param>
/// <param name="Values">Added: all values. Modified: changed values only. Deleted: none.</param>
public sealed record WireChange(string Type, WireChangeState State, Dictionary<string, JsonElement> Key, Dictionary<string, JsonElement>? Values);

/// <summary>Body of <c>POST {base}/$save</c>. Applied in one transaction, after the save rules.</summary>
public sealed record SaveRequest(IReadOnlyList<WireChange> Changes);

/// <summary>
/// Every entity the save wrote, with its stored values, including those the rules changed; and the
/// entities their references point to, so a client can follow navigations the rules set.
/// </summary>
public sealed record SaveResponse(IReadOnlyList<WireEntity> Entities, IReadOnlyList<WireEntity>? Related = null);

public sealed record WireEntity(string Type, Dictionary<string, JsonElement> Key, Dictionary<string, JsonElement> Values);

/// <summary>Body of a 422 response to <c>$save</c>.</summary>
public sealed record SaveErrorResponse(IReadOnlyList<WireError> Errors);

public sealed record WireError(string Type, Dictionary<string, JsonElement> Key, string? Property, string Code);

public static class WireJson
{
    /// <summary>Marks a JSON object that only references an entity written earlier in the same response.</summary>
    public const string ReferenceMarker = "@ref";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        // Member names must match the model exactly, as in EF Core.
        PropertyNamingPolicy = null,
        Converters = { new JsonStringEnumConverter() },
    };
}
