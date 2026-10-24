using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetAgentFramework.Tests;

public class JsonSchemaGeneratorTests
{
    [Fact]
    public void Schema_SimpleRecord_MapsPrimitiveTypes()
    {
        var schema = Generate<SimpleArgs>();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("string", Prop(schema, "city").GetProperty("type").GetString());
        Assert.Equal("integer", Prop(schema, "days").GetProperty("type").GetString());
        Assert.Equal("boolean", Prop(schema, "metric").GetProperty("type").GetString());
        Assert.Equal(["city", "days", "metric"], RequiredOf(schema));
    }

    [Fact]
    public void Schema_NullableMembers_NotRequired()
    {
        var schema = Generate<OptionalArgs>();

        Assert.Equal(["required"], RequiredOf(schema));
        Assert.Equal("string", Prop(schema, "optional").GetProperty("type").GetString());
        Assert.Equal("integer", Prop(schema, "count").GetProperty("type").GetString());
    }

    [Fact]
    public void Schema_DescriptionAttribute_EmitsDescription()
    {
        var country = Prop(Generate<DescribedArgs>(), "country");

        Assert.Equal("ISO country code", country.GetProperty("description").GetString());
    }

    [Fact]
    public void Schema_JsonPropertyName_OverridesCamelCase()
    {
        var properties = Generate<RenamedArgs>().GetProperty("properties");

        Assert.True(properties.TryGetProperty("user_id", out _));
        Assert.False(properties.TryGetProperty("userId", out _));
        Assert.Equal(["user_id"], RequiredOf(Generate<RenamedArgs>()));
    }

    [Fact]
    public void Schema_Enum_EmitsStringEnumWithNames()
    {
        var unit = Prop(Generate<UnitArgs>(), "unit");

        Assert.Equal("string", unit.GetProperty("type").GetString());
        Assert.Equal(["Celsius", "Fahrenheit"], unit.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void Schema_Collections_EmitArrayWithItems()
    {
        var schema = Generate<ListArgs>();

        var tags = Prop(schema, "tags");
        Assert.Equal("array", tags.GetProperty("type").GetString());
        Assert.Equal("string", tags.GetProperty("items").GetProperty("type").GetString());

        var scores = Prop(schema, "scores");
        Assert.Equal("array", scores.GetProperty("type").GetString());
        Assert.Equal("number", scores.GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public void Schema_NestedRecord_EmitsObjectSchemaRecursively()
    {
        var home = Prop(Generate<NestedArgs>(), "home");

        Assert.Equal("object", home.GetProperty("type").GetString());
        Assert.False(home.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["street", "zip"], RequiredOf(home));
        Assert.Equal("string", Prop(home, "street").GetProperty("type").GetString());
        Assert.Equal("string", Prop(home, "zip").GetProperty("type").GetString());
    }

    [Fact]
    public void Schema_JsonIgnore_PropertyOmitted()
    {
        var schema = Generate<IgnoredArgs>();
        var properties = schema.GetProperty("properties");

        Assert.Equal(["keep"], properties.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["keep"], RequiredOf(schema));
    }

    [Fact]
    public void Schema_DateAndGuid_EmitFormats()
    {
        var schema = Generate<DateArgs>();

        var when = Prop(schema, "when");
        Assert.Equal("string", when.GetProperty("type").GetString());
        Assert.Equal("date-time", when.GetProperty("format").GetString());

        var id = Prop(schema, "id");
        Assert.Equal("string", id.GetProperty("type").GetString());
        Assert.Equal("uuid", id.GetProperty("format").GetString());
    }

    [Fact]
    public void Definition_CalledTwice_ReturnsEquivalentSchema()
    {
        var tool = new ProbeTool();

        var first = tool.Definition.ParametersSchema.GetRawText();
        var second = tool.Definition.ParametersSchema.GetRawText();

        Assert.Equal(first, second);
        Assert.Equal("probe", tool.Definition.Name);
    }

    [Fact]
    public async Task Tool_EnumArgumentSentAsSchemaName_Deserializes()
    {
        using var document = JsonDocument.Parse("""{"unit":"Fahrenheit"}""");

        var result = await new UnitTool().InvokeAsync(document.RootElement);

        Assert.Equal("Fahrenheit", result);
    }

    private static JsonElement Generate<T>()
        => JsonSchemaGenerator.Generate(typeof(T));

    private static JsonElement Prop(JsonElement schema, string name)
        => schema.GetProperty("properties").GetProperty(name);

    private static string[] RequiredOf(JsonElement schema)
        => schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private sealed record SimpleArgs(string City, int Days, bool Metric);

    private sealed class OptionalArgs
    {
        public string Required { get; set; } = "";

        public string? Optional { get; set; }

        public int? Count { get; set; }
    }

    private sealed record DescribedArgs([property: Description("ISO country code")] string Country);

    private sealed record RenamedArgs([property: JsonPropertyName("user_id")] string UserId);

    private enum Unit
    {
        Celsius,
        Fahrenheit,
    }

    private sealed record UnitArgs(Unit Unit);

    private sealed record ListArgs(List<string> Tags, double[] Scores);

    private sealed record Address(string Street, string Zip);

    private sealed record NestedArgs(Address Home);

    private sealed record IgnoredArgs(string Keep, [property: JsonIgnore] string Secret);

    private sealed record DateArgs(DateTimeOffset When, Guid Id);

    private sealed class ProbeTool : Tool<SimpleArgs>
    {
        public override string Name => "probe";

        public override string Description => "Probe tool";

        protected override Task<string> InvokeAsync(SimpleArgs args, CancellationToken cancellationToken)
            => Task.FromResult("ok");
    }

    private sealed class UnitTool : Tool<UnitArgs>
    {
        public override string Name => "unit";

        public override string Description => "Unit tool";

        protected override Task<string> InvokeAsync(UnitArgs args, CancellationToken cancellationToken)
            => Task.FromResult(args.Unit.ToString());
    }
}
