namespace Tallyhouse.Domain.Schemas;

/// <summary>
/// Decides whether a schema may be changed in place. The rule is the one that keeps every event already
/// stored valid and every query already written correct: a version may only grow. Anything else is a new
/// version, which instrumented clients opt into explicitly.
/// </summary>
public static class SchemaEvolution
{
    public static IReadOnlyList<string> BreakingChanges(SchemaSpec current, SchemaSpec proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);

        List<string> changes = [];

        foreach ((string name, FieldSpec before) in current.Properties)
        {
            if (!proposed.Properties.TryGetValue(name, out FieldSpec? after))
            {
                changes.Add($"property '{name}' was removed");
                continue;
            }

            if (before.Type != after.Type)
            {
                changes.Add($"property '{name}' changed type from {before.Type} to {after.Type}");
            }

            // Both directions break somebody. Making a field required rejects events clients already send;
            // making it optional breaks queries that assumed every event carries it.
            if (before.Required != after.Required)
            {
                changes.Add($"property '{name}' changed from {Describe(before.Required)} to {Describe(after.Required)}");
            }

            switch (before.Enum, after.Enum)
            {
                case (null, not null):
                    changes.Add($"property '{name}' now restricts its values, which rejects values it used to accept");
                    break;

                case (not null, null):
                    changes.Add($"property '{name}' no longer restricts its values, which breaks queries that enumerate them");
                    break;

                case (not null, not null):
                    foreach (string value in before.Enum.Except(after.Enum, StringComparer.Ordinal))
                    {
                        changes.Add($"property '{name}' no longer allows '{value}'");
                    }

                    break;
            }
        }

        foreach ((string name, FieldSpec added) in proposed.Properties)
        {
            if (!current.Properties.ContainsKey(name) && added.Required)
            {
                changes.Add($"new property '{name}' is required, but a property added to an existing version must be optional");
            }
        }

        return changes;
    }

    private static string Describe(bool required) => required ? "required" : "optional";
}
