using Tallyhouse.Core.Schemas;

namespace Tallyhouse.UnitTests;

public sealed class CompiledSchemaTests
{
    private static readonly CompiledSchema Purchase = CompiledSchema.Compile("purchase", 1, Samples.Purchase);
    private static readonly HashSet<string> NoRedaction = [];

    private static (IReadOnlyList<string>? Violations, Dictionary<string, string> Values) Run(string json, IReadOnlySet<string>? redact = null)
    {
        Dictionary<string, string> values = [];
        IReadOnlyList<string>? violations = Purchase.Normalize(Samples.Json(json), redact ?? NoRedaction, values);
        return (violations, values);
    }

    [Fact]
    public void Values_are_stored_in_one_canonical_text_form()
    {
        (IReadOnlyList<string>? violations, Dictionary<string, string> values) =
            Run("""{"amount": 12.50, "quantity": 3, "currency": "EUR", "gift": true}""");

        violations.ShouldBeNull();
        values["amount"].ShouldBe("12.5");
        values["quantity"].ShouldBe("3");
        values["currency"].ShouldBe("EUR");
        values["gift"].ShouldBe("true");
    }

    [Fact]
    public void An_undeclared_property_is_a_violation_so_typos_surface_instead_of_becoming_new_columns() =>
        Run("""{"amount": 1, "currency": "EUR", "ammount": 1}""").Violations!
            .ShouldHaveSingleItem().ShouldContain("'ammount' is not declared");

    [Theory]
    [InlineData("""{"amount": "12", "currency": "EUR"}""", "'amount' must be a finite number")]
    [InlineData("""{"amount": 1, "currency": "EUR", "quantity": 1.5}""", "'quantity' must be an integer")]
    [InlineData("""{"amount": 1, "currency": "EUR", "gift": "yes"}""", "'gift' must be a boolean")]
    [InlineData("""{"amount": 1, "currency": 978}""", "'currency' must be a string")]
    [InlineData("""{"amount": 1, "currency": "GBP"}""", "'currency' has a value that is not one of its allowed values")]
    public void Each_type_is_checked(string json, string expected) =>
        Run(json).Violations!.ShouldHaveSingleItem().ShouldContain(expected);

    [Fact]
    public void A_missing_required_property_is_reported_by_name() =>
        Run("""{"amount": 1}""").Violations!.ShouldHaveSingleItem().ShouldBe("required property 'currency' is missing");

    [Fact]
    public void Null_means_not_set_so_it_passes_for_optional_and_fails_for_required()
    {
        Run("""{"amount": 1, "currency": "EUR", "coupon": null}""").Violations.ShouldBeNull();
        Run("""{"amount": null, "currency": "EUR"}""").Violations!.ShouldHaveSingleItem().ShouldContain("'amount' is missing");
    }

    [Fact]
    public void Every_violation_is_reported_not_only_the_first() =>
        Run("""{"amount": "x", "gift": 1}""").Violations!.Count.ShouldBe(3);

    [Fact]
    public void A_repeated_key_is_refused_rather_than_silently_keeping_one_of_them() =>
        Run("""{"amount": 1, "amount": 2, "currency": "EUR"}""").Violations!.ShouldHaveSingleItem().ShouldContain("more than once");

    [Fact]
    public void Strings_are_bounded() =>
        Run($$"""{"amount": 1, "currency": "EUR", "coupon": "{{new string('x', CompiledSchema.MaxStringLength + 1)}}"}""")
            .Violations!.ShouldHaveSingleItem().ShouldContain("longer than");

    [Theory]
    [InlineData("[]")]
    [InlineData("\"amount\"")]
    public void Properties_must_be_an_object(string json) =>
        Run(json).Violations!.ShouldHaveSingleItem().ShouldBe("properties must be a JSON object");

    [Fact]
    public void Redacted_properties_are_validated_and_then_never_stored()
    {
        (IReadOnlyList<string>? violations, Dictionary<string, string> values) =
            Run("""{"amount": 1, "currency": "EUR", "email": "ada@example.com"}""", new HashSet<string> { "email" });

        violations.ShouldBeNull();
        values["email"].ShouldBe(CompiledSchema.RedactedMarker);
        values.Values.ShouldNotContain("ada@example.com");
    }

    [Fact]
    public void A_replayed_event_whose_typed_property_was_redacted_still_validates()
    {
        (IReadOnlyList<string>? violations, Dictionary<string, string> values) =
            Run("""{"amount": "[redacted]", "currency": "EUR"}""", new HashSet<string> { "amount" });

        violations.ShouldBeNull();
        values["amount"].ShouldBe(CompiledSchema.RedactedMarker);
    }

    [Fact]
    public void The_redaction_marker_is_no_escape_hatch_for_properties_that_are_not_redacted() =>
        Run("""{"amount": "[redacted]", "currency": "EUR"}""").Violations!.ShouldHaveSingleItem().ShouldContain("finite number");
}
