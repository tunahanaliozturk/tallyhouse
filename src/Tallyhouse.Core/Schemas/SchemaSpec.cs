using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Tallyhouse.Core.Schemas;

[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "These are the JSON type names a schema author writes, and they appear on the wire exactly as spelled.")]
public enum FieldType
{
    [JsonStringEnumMemberName("string")]
    String,

    [JsonStringEnumMemberName("number")]
    Number,

    [JsonStringEnumMemberName("integer")]
    Integer,

    [JsonStringEnumMemberName("boolean")]
    Boolean,
}

/// <summary>One property an event of this schema may carry.</summary>
/// <param name="Type">The JSON type the value must have.</param>
/// <param name="Required">Whether every event must carry it.</param>
/// <param name="Enum">For strings only: the closed set of values allowed, or null for any value.</param>
public sealed record FieldSpec(FieldType Type, bool Required = false, IReadOnlyList<string>? Enum = null);

/// <summary>
/// The registered shape of one event at one version. Registration is where a mistake is cheapest to catch,
/// so the spec is checked for sanity here before anything is compiled from it.
/// </summary>
public sealed record SchemaSpec(IReadOnlyDictionary<string, FieldSpec> Properties)
{
    // A required-field bitmask is one ulong. Sixty-four properties is well past what a behavioural event
    // should carry; wider payloads belong in a different system.
    public const int MaxProperties = 64;
    public const int MaxEnumValues = 256;
    public const int MaxEnumValueLength = 256;

    public IReadOnlyList<string> Problems()
    {
        List<string> problems = [];

        if (Properties is null)
        {
            problems.Add("properties is required (use an empty object for an event without properties)");
            return problems;
        }

        if (Properties.Count > MaxProperties)
        {
            problems.Add($"a schema may declare at most {MaxProperties} properties, this one declares {Properties.Count}");
        }

        foreach ((string name, FieldSpec field) in Properties)
        {
            if (!Names.IsValidPropertyName(name))
            {
                problems.Add($"property name '{name}' must start with a letter or underscore and use only letters, digits and underscores (max {Names.MaxPropertyNameLength})");
            }

            if (field is null)
            {
                problems.Add($"property '{name}' has no definition");
                continue;
            }

            if (!Enum.IsDefined(field.Type))
            {
                problems.Add($"property '{name}' has an unknown type");
            }

            if (field.Enum is null)
            {
                continue;
            }

            if (field.Type != FieldType.String)
            {
                problems.Add($"property '{name}' declares allowed values, which only string properties may do");
            }

            if (field.Enum.Count == 0 || field.Enum.Count > MaxEnumValues)
            {
                problems.Add($"property '{name}' must list between 1 and {MaxEnumValues} allowed values");
            }

            if (field.Enum.Any(value => value is null || value.Length > MaxEnumValueLength))
            {
                problems.Add($"property '{name}' has an allowed value that is null or longer than {MaxEnumValueLength} characters");
            }
            else if (field.Enum.Distinct(StringComparer.Ordinal).Count() != field.Enum.Count)
            {
                problems.Add($"property '{name}' lists the same allowed value twice");
            }
        }

        return problems;
    }
}
