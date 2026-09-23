using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Tallyhouse.Core.Schemas;

/// <summary>
/// A schema turned into the shape the ingest path wants: a frozen lookup from property name to rule, and a
/// bitmask of required properties. Validating an event is one pass over its properties with no reflection
/// and no per-event allocation beyond the normalised values themselves.
/// </summary>
public sealed class CompiledSchema
{
    public const int MaxStringLength = 1024;

    /// <summary>What a redacted value is replaced with before anything is stored.</summary>
    public const string RedactedMarker = "[redacted]";

    // A client can send hundreds of bad properties; the quarantine reason only needs enough to act on.
    private const int MaxReportedViolations = 10;

    private readonly FrozenDictionary<string, Field> fields;
    private readonly ulong requiredMask;

    private CompiledSchema(string eventName, int version, SchemaSpec spec, FrozenDictionary<string, Field> fields, ulong requiredMask)
    {
        EventName = eventName;
        Version = version;
        Spec = spec;
        this.fields = fields;
        this.requiredMask = requiredMask;
    }

    public string EventName { get; }

    public int Version { get; }

    public SchemaSpec Spec { get; }

    public static CompiledSchema Compile(string eventName, int version, SchemaSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Problems() is { Count: > 0 } problems)
        {
            throw new ArgumentException($"Schema {eventName}@{version} is not valid: {string.Join("; ", problems)}", nameof(spec));
        }

        Dictionary<string, Field> compiled = new(StringComparer.Ordinal);
        ulong required = 0;
        int bit = 0;

        foreach ((string name, FieldSpec field) in spec.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ulong mask = 1UL << bit++;
            FrozenSet<string>? allowed = field.Enum?.ToFrozenSet(StringComparer.Ordinal);
            compiled[name] = new Field(name, field.Type, field.Required, mask, allowed);

            if (field.Required)
            {
                required |= mask;
            }
        }

        return new CompiledSchema(eventName, version, spec, compiled.ToFrozenDictionary(StringComparer.Ordinal), required);
    }

    /// <summary>
    /// Checks <paramref name="properties"/> against the schema and writes each value, as the string ClickHouse
    /// stores, into <paramref name="output"/>. Returns null when the event is valid and the list of
    /// violations when it is not.
    /// </summary>
    /// <remarks>
    /// Keys in <paramref name="redact"/> are type-checked like any other and then stored as
    /// <see cref="RedactedMarker"/>. A value that already is the marker passes without a type check, because
    /// that is what a quarantined event looks like when it is replayed: its personal data was removed before
    /// it was ever written down, and it must not fail validation for that.
    /// </remarks>
    public IReadOnlyList<string>? Normalize(JsonElement properties, IReadOnlySet<string> redact, Dictionary<string, string> output)
    {
        ArgumentNullException.ThrowIfNull(redact);
        ArgumentNullException.ThrowIfNull(output);

        List<string>? violations = null;
        ulong seen = 0;

        if (properties.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (!fields.TryGetValue(property.Name, out Field? field))
                {
                    Report(ref violations, $"property '{Clip(property.Name)}' is not declared in {EventName}@{Version.ToString(CultureInfo.InvariantCulture)}");
                    continue;
                }

                JsonElement value = property.Value;

                // An explicit null is how most SDKs spell "not set". Treating it as absent keeps optional
                // properties optional, and a required one still fails the presence check below.
                if (value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if ((seen & field.Bit) != 0)
                {
                    Report(ref violations, $"property '{field.Name}' appears more than once");
                    continue;
                }

                seen |= field.Bit;
                bool redacted = redact.Contains(field.Name);

                if (redacted && value.ValueKind == JsonValueKind.String && value.ValueEquals(RedactedMarker))
                {
                    output[field.Name] = RedactedMarker;
                    continue;
                }

                string? normalized = field.Normalize(value, out string? problem);

                if (problem is not null)
                {
                    Report(ref violations, problem);
                    continue;
                }

                output[field.Name] = redacted ? RedactedMarker : normalized!;
            }
        }
        else if (properties.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return ["properties must be a JSON object"];
        }

        ulong missing = requiredMask & ~seen;

        if (missing != 0)
        {
            foreach (Field field in fields.Values)
            {
                if ((field.Bit & missing) != 0)
                {
                    Report(ref violations, $"required property '{field.Name}' is missing");
                }
            }
        }

        return violations;
    }

    private static void Report(ref List<string>? violations, string violation)
    {
        violations ??= [];

        if (violations.Count < MaxReportedViolations)
        {
            violations.Add(violation);
        }
    }

    private static string Clip(string name) => name.Length <= Names.MaxPropertyNameLength ? name : string.Concat(name.AsSpan(0, Names.MaxPropertyNameLength), "...");

    private sealed record Field(string Name, FieldType Type, bool Required, ulong Bit, FrozenSet<string>? Allowed)
    {
        public string? Normalize(JsonElement value, out string? problem)
        {
            problem = null;

            switch (Type)
            {
                case FieldType.String when value.ValueKind == JsonValueKind.String:
                    string text = value.GetString()!;

                    if (text.Length > MaxStringLength)
                    {
                        problem = $"property '{Name}' is longer than {MaxStringLength} characters";
                        return null;
                    }

                    if (Allowed is not null && !Allowed.Contains(text))
                    {
                        problem = $"property '{Name}' has a value that is not one of its allowed values";
                        return null;
                    }

                    return text;

                // Numbers are stored in their shortest round-trip form, so "12.50" and "12.5" are the same
                // value to an equality filter later.
                case FieldType.Number when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number):
                    return number.ToString(CultureInfo.InvariantCulture);

                case FieldType.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long integer):
                    return integer.ToString(CultureInfo.InvariantCulture);

                case FieldType.Boolean when value.ValueKind is JsonValueKind.True:
                    return "true";

                case FieldType.Boolean when value.ValueKind is JsonValueKind.False:
                    return "false";

                default:
                    problem = $"property '{Name}' must be {Article(Type)}";
                    return null;
            }
        }

        private static string Article(FieldType type) => type switch
        {
            FieldType.Integer => "an integer",
            FieldType.Boolean => "a boolean",
            FieldType.Number => "a finite number",
            _ => "a string",
        };
    }
}
