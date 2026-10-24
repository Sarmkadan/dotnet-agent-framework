using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetAgentFramework.Tests;

public class ToolTests
{
    [Fact]
    public void Definition_PrimitiveProperties_MapsTypesAndMarksAllRequired()
    {
        var schema = SchemaOf<PrimitiveArgs>();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("string", Prop(schema, "text").GetProperty("type").GetString());
        Assert.Equal("boolean", Prop(schema, "flag").GetProperty("type").GetString());
        Assert.Equal("integer", Prop(schema, "count").GetProperty("type").GetString());
        Assert.Equal("number", Prop(schema, "ratio").GetProperty("type").GetString());
        Assert.Equal("uuid", Prop(schema, "id").GetProperty("format").GetString());
        Assert.Equal("date-time", Prop(schema, "when").GetProperty("format").GetString());
        Assert.Equal(["text", "flag", "count", "ratio", "id", "when"], RequiredOf(schema));
    }

    [Fact]
    public void Definition_PropertyNames_UseCamelCaseOrJsonPropertyName()
    {
        var schema = SchemaOf<NamedArgs>();

        Assert.True(schema.GetProperty("properties").TryGetProperty("firstName", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("q", out _));
        Assert.False(schema.GetProperty("properties").TryGetProperty("FirstName", out _));
        Assert.False(schema.GetProperty("properties").TryGetProperty("Query", out _));
    }

    [Fact]
    public void Definition_IgnoredAndUnreadableMembers_AreOmitted()
    {
        var properties = SchemaOf<HiddenArgs>().GetProperty("properties");

        Assert.Equal(["visible"], properties.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Definition_NullableAndConditionallyIgnoredMembers_AreNotRequired()
    {
        var schema = SchemaOf<OptionalArgs>();

        Assert.Equal(["title"], RequiredOf(schema));
        Assert.True(schema.GetProperty("properties").TryGetProperty("nickname", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("age", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("note", out _));
    }

    [Fact]
    public void Definition_DescriptionAttribute_EmitsDescription()
    {
        var city = Prop(SchemaOf<DescribedArgs>(), "city");

        Assert.Equal("The city to search", city.GetProperty("description").GetString());
    }

    [Fact]
    public void Definition_EnumProperty_EmitsStringEnumOfNames()
    {
        var shade = Prop(SchemaOf<EnumArgs>(), "shade");

        Assert.Equal("string", shade.GetProperty("type").GetString());
        Assert.Equal(["Red", "Green"], shade.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void Definition_ListAndDictionaryProperties_EmitItemAndValueSchemas()
    {
        var schema = SchemaOf<CollectionArgs>();

        var numbers = Prop(schema, "numbers");
        Assert.Equal("array", numbers.GetProperty("type").GetString());
        Assert.Equal("integer", numbers.GetProperty("items").GetProperty("type").GetString());

        var scores = Prop(schema, "scores");
        Assert.Equal("object", scores.GetProperty("type").GetString());
        Assert.Equal("integer", scores.GetProperty("additionalProperties").GetProperty("type").GetString());
    }

    [Fact]
    public void Definition_NestedObject_EmitsNestedObjectSchema()
    {
        var home = Prop(SchemaOf<NestedArgs>(), "home");

        Assert.Equal("object", home.GetProperty("type").GetString());
        Assert.Equal(["city"], RequiredOf(home));
        Assert.Equal("string", Prop(home, "city").GetProperty("type").GetString());
    }

    [Fact]
    public void Definition_SelfReferencingType_EmitsObjectOnCycle()
    {
        var next = Prop(SchemaOf<NodeArgs>(), "next");

        Assert.Equal("object", next.GetProperty("type").GetString());
        Assert.False(next.TryGetProperty("properties", out _));
    }

    private static JsonElement SchemaOf<T>() where T : class
        => new TestTool<T>().Definition.ParametersSchema;

    private static JsonElement Prop(JsonElement schema, string name)
        => schema.GetProperty("properties").GetProperty(name);

    private static string[] RequiredOf(JsonElement schema)
        => schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private sealed class TestTool<T> : Tool<T>
        where T : class
    {
        public override string Name => "test";

        public override string Description => "Test tool";

        protected override Task<string> InvokeAsync(T args, CancellationToken cancellationToken)
            => Task.FromResult("ok");
    }

    private sealed class PrimitiveArgs
    {
        public string Text { get; set; } = "";

        public bool Flag { get; set; }

        public int Count { get; set; }

        public double Ratio { get; set; }

        public Guid Id { get; set; }

        public DateTime When { get; set; }
    }

    private sealed class NamedArgs
    {
        public string FirstName { get; set; } = "";

        [JsonPropertyName("q")]
        public string Query { get; set; } = "";
    }

    private sealed class HiddenArgs
    {
        public string Visible { get; set; } = "";

        [JsonIgnore]
        public string Secret { get; set; } = "";

        private string _lastWritten = "";

        public string WriteOnly
        {
            set => _lastWritten = value;
        }

        public string PrivateGetter { private get; set; } = "";
    }

    private sealed class OptionalArgs
    {
        public string Title { get; set; } = "";

        public string? Nickname { get; set; }

        public int? Age { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Note { get; set; } = "";
    }

    private sealed class DescribedArgs
    {
        [Description("The city to search")]
        public string City { get; set; } = "";
    }

    private enum Color
    {
        Red,
        Green,
    }

    private sealed class EnumArgs
    {
        public Color Shade { get; set; }
    }

    private sealed class CollectionArgs
    {
        public List<int> Numbers { get; } = [];

        public Dictionary<string, int> Scores { get; } = [];
    }

    private sealed class Address
    {
        public string City { get; set; } = "";
    }

    private sealed class NestedArgs
    {
        public Address Home { get; set; } = new();
    }

    private sealed class NodeArgs
    {
        public string Name { get; set; } = "";

        public NodeArgs? Next { get; set; }
    }
}
