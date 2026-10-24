using System.Text.Json;

namespace DotnetAgentFramework.Tests;

public class DelegateToolTests
{
    private sealed record GreetArgs(string Name);

    [Fact]
    public async Task InvokeAsync_ConstructorHandler_ForwardsArgumentsAndToken()
    {
        using var cts = new CancellationTokenSource();
        JsonElement received = default;
        CancellationToken receivedToken = default;
        var definition = ToolDefinition.Create("echo", "Echoes.", JsonSchemaGenerator.EmptyObjectSchema);
        var tool = new DelegateTool(definition, (args, ct) =>
        {
            received = args;
            receivedToken = ct;
            return Task.FromResult("ok");
        });

        var result = await tool.InvokeAsync(Parse("""{"x":1}"""), cts.Token);

        Assert.Equal("ok", result);
        Assert.Equal(1, received.GetProperty("x").GetInt32());
        Assert.Equal(cts.Token, receivedToken);
        Assert.Same(definition, tool.Definition);
    }

    [Fact]
    public async Task Create_TypedHandler_DeserializesArguments()
    {
        var tool = DelegateTool.Create<GreetArgs>("greet", "Greets.", (args, _) => Task.FromResult($"Hi {args.Name}"));

        var result = await tool.InvokeAsync(Parse("""{"name":"Ada"}"""));

        Assert.Equal("Hi Ada", result);
    }

    [Fact]
    public void Create_TypedHandler_DerivesSchemaFromType()
    {
        var tool = DelegateTool.Create<GreetArgs>("greet", "Greets.", (args, _) => Task.FromResult(args.Name));

        Assert.Equal("greet", tool.Definition.Name);
        Assert.Equal("Greets.", tool.Definition.Description);
        Assert.Equal(["name"], tool.Definition.ParametersSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Create_TypedHandler_NonObjectArguments_ReturnsErrorText()
    {
        var tool = DelegateTool.Create<GreetArgs>("greet", "Greets.", (args, _) => Task.FromResult(args.Name));

        var result = await tool.InvokeAsync(Parse("[1]"));

        Assert.Equal("Error: tool 'greet' expected a JSON object but received Array.", result);
    }

    [Fact]
    public async Task Create_TypedHandler_WrongPropertyType_ReturnsErrorText()
    {
        var tool = DelegateTool.Create<GreetArgs>("greet", "Greets.", (args, _) => Task.FromResult(args.Name));

        var result = await tool.InvokeAsync(Parse("""{"name":5}"""));

        Assert.StartsWith("Error: tool 'greet' received arguments that could not be deserialized:", result);
    }

    [Fact]
    public async Task Create_SyncHandler_ReturnsHandlerResult()
    {
        var tool = DelegateTool.Create<GreetArgs>("greet", "Greets.", args => $"Hi {args.Name}");

        var result = await tool.InvokeAsync(Parse("""{"name":"Ada"}"""));

        Assert.Equal("Hi Ada", result);
    }

    [Fact]
    public async Task Create_Parameterless_UsesEmptyObjectSchemaAndForwardsToken()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken receivedToken = default;
        var tool = DelegateTool.Create("now", "Current time.", ct =>
        {
            receivedToken = ct;
            return Task.FromResult("noon");
        });

        var result = await tool.InvokeAsync(Parse("{}"), cts.Token);

        Assert.Equal("noon", result);
        Assert.Equal(cts.Token, receivedToken);
        Assert.Equal("object", tool.Definition.ParametersSchema.GetProperty("type").GetString());
        Assert.Empty(tool.Definition.ParametersSchema.GetProperty("properties").EnumerateObject());
        Assert.False(tool.Definition.ParametersSchema.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [InlineData(" ", "desc")]
    [InlineData("", "desc")]
    [InlineData("name", " ")]
    public void Create_BlankNameOrDescription_Throws(string name, string description)
    {
        Assert.Throws<ArgumentException>(() => DelegateTool.Create(name, description, ct => Task.FromResult("x")));
        Assert.Throws<ArgumentException>(() => DelegateTool.Create<GreetArgs>(name, description, (args, _) => Task.FromResult(args.Name)));
    }

    [Fact]
    public void Create_NullHandler_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DelegateTool.Create<GreetArgs>("greet", "Greets.", (Func<GreetArgs, CancellationToken, Task<string>>)null!));
        Assert.Throws<ArgumentNullException>(() => DelegateTool.Create<GreetArgs>("greet", "Greets.", (Func<GreetArgs, string>)null!));
        Assert.Throws<ArgumentNullException>(() => DelegateTool.Create("now", "Current time.", (Func<CancellationToken, Task<string>>)null!));
    }

    [Fact]
    public void Constructor_NullArguments_Throws()
    {
        var definition = ToolDefinition.Create("echo", "Echoes.", JsonSchemaGenerator.EmptyObjectSchema);

        Assert.Throws<ArgumentNullException>(() => new DelegateTool(null!, (_, _) => Task.FromResult("x")));
        Assert.Throws<ArgumentNullException>(() => new DelegateTool(definition, null!));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
