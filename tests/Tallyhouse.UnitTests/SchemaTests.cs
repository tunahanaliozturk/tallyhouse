using Tallyhouse.Kernel.Schemas;

namespace Tallyhouse.UnitTests;

public sealed class SchemaSpecTests
{
    [Fact]
    public void A_sensible_spec_has_no_problems() =>
        Samples.Purchase.Problems().ShouldBeEmpty();

    [Theory]
    [InlineData("1amount")]
    [InlineData("amount-total")]
    [InlineData("")]
    [InlineData("a b")]
    public void Property_names_must_be_plain_identifiers(string name)
    {
        SchemaSpec spec = new(new Dictionary<string, FieldSpec> { [name] = new(FieldType.String) });

        spec.Problems().ShouldHaveSingleItem().ShouldContain("property name");
    }

    [Fact]
    public void Only_strings_may_declare_allowed_values()
    {
        SchemaSpec spec = new(new Dictionary<string, FieldSpec> { ["tier"] = new(FieldType.Integer, Enum: ["1", "2"]) });

        spec.Problems().ShouldHaveSingleItem().ShouldContain("only string properties");
    }

    [Fact]
    public void Allowed_values_must_be_distinct()
    {
        SchemaSpec spec = new(new Dictionary<string, FieldSpec> { ["plan"] = new(FieldType.String, Enum: ["pro", "pro"]) });

        spec.Problems().ShouldHaveSingleItem().ShouldContain("same allowed value twice");
    }

    [Fact]
    public void More_properties_than_the_required_mask_can_hold_are_refused()
    {
        Dictionary<string, FieldSpec> properties = Enumerable.Range(0, SchemaSpec.MaxProperties + 1)
            .ToDictionary(i => $"p{i}", _ => new FieldSpec(FieldType.String));

        new SchemaSpec(properties).Problems().ShouldContain(problem => problem.Contains("at most 64"));
    }

    [Fact]
    public void Compiling_an_invalid_spec_throws_rather_than_validating_against_nonsense()
    {
        SchemaSpec spec = new(new Dictionary<string, FieldSpec> { ["bad name"] = new(FieldType.String) });

        Should.Throw<ArgumentException>(() => CompiledSchema.Compile("purchase", 1, spec));
    }
}

public sealed class SchemaEvolutionTests
{
    private static SchemaSpec With(params (string Name, FieldSpec Field)[] fields) =>
        new(fields.ToDictionary(field => field.Name, field => field.Field));

    [Fact]
    public void Adding_an_optional_property_is_additive() =>
        SchemaEvolution.BreakingChanges(
            With(("plan", new FieldSpec(FieldType.String, true))),
            With(("plan", new FieldSpec(FieldType.String, true)), ("seats", new FieldSpec(FieldType.Integer))))
            .ShouldBeEmpty();

    [Fact]
    public void Growing_the_allowed_values_is_additive() =>
        SchemaEvolution.BreakingChanges(
            With(("plan", new FieldSpec(FieldType.String, Enum: ["free", "pro"]))),
            With(("plan", new FieldSpec(FieldType.String, Enum: ["free", "pro", "team"]))))
            .ShouldBeEmpty();

    [Fact]
    public void Adding_a_required_property_breaks_clients_that_do_not_send_it() =>
        SchemaEvolution.BreakingChanges(
            With(("plan", new FieldSpec(FieldType.String))),
            With(("plan", new FieldSpec(FieldType.String)), ("seats", new FieldSpec(FieldType.Integer, true))))
            .ShouldHaveSingleItem().ShouldContain("must be optional");

    [Fact]
    public void Removing_a_property_is_breaking() =>
        SchemaEvolution.BreakingChanges(
            With(("plan", new FieldSpec(FieldType.String)), ("seats", new FieldSpec(FieldType.Integer))),
            With(("plan", new FieldSpec(FieldType.String))))
            .ShouldHaveSingleItem().ShouldContain("'seats' was removed");

    [Fact]
    public void Changing_a_type_is_breaking() =>
        SchemaEvolution.BreakingChanges(
            With(("seats", new FieldSpec(FieldType.Integer))),
            With(("seats", new FieldSpec(FieldType.Number))))
            .ShouldHaveSingleItem().ShouldContain("changed type");

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Changing_whether_a_property_is_required_is_breaking_in_both_directions(bool before, bool after) =>
        SchemaEvolution.BreakingChanges(
            With(("plan", new FieldSpec(FieldType.String, before))),
            With(("plan", new FieldSpec(FieldType.String, after))))
            .ShouldHaveSingleItem().ShouldContain("changed from");

    [Fact]
    public void Removing_an_allowed_value_is_breaking() =>
        SchemaEvolution.BreakingChanges(
            With(("plan", new FieldSpec(FieldType.String, Enum: ["free", "pro"]))),
            With(("plan", new FieldSpec(FieldType.String, Enum: ["pro"]))))
            .ShouldHaveSingleItem().ShouldContain("no longer allows 'free'");

    [Fact]
    public void Introducing_or_dropping_a_closed_set_of_values_is_breaking()
    {
        SchemaEvolution.BreakingChanges(
            With(("plan", new FieldSpec(FieldType.String))),
            With(("plan", new FieldSpec(FieldType.String, Enum: ["pro"])))).ShouldHaveSingleItem();

        SchemaEvolution.BreakingChanges(
            With(("plan", new FieldSpec(FieldType.String, Enum: ["pro"]))),
            With(("plan", new FieldSpec(FieldType.String)))).ShouldHaveSingleItem();
    }
}
