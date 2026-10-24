using System.Text.Json;

namespace DotnetAgentFramework.Tests;

public class OpenAIWireFormatTests
{
    [Theory]
    [InlineData(MessageRole.System, "system")]
    [InlineData(MessageRole.User, "user")]
    [InlineData(MessageRole.Assistant, "assistant")]
    [InlineData(MessageRole.Tool, "tool")]
    public void RoleName_KnownRole_ReturnsWireName(MessageRole role, string expected)
    {
        Assert.Equal(expected, OpenAIWireFormat.RoleName(role));
    }

    [Fact]
    public void RoleName_UndefinedRole_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenAIWireFormat.RoleName((MessageRole)99));
    }

    [Fact]
    public void ToArgumentsString_Object_ReturnsRawJson()
    {
        using var doc = JsonDocument.Parse("""{"city":"Kyiv","days":2}""");

        var text = OpenAIWireFormat.ToArgumentsString(doc.RootElement);

        Assert.Equal("""{"city":"Kyiv","days":2}""", text);
    }

    [Fact]
    public void ToArgumentsString_Undefined_ReturnsEmptyObject()
    {
        Assert.Equal("{}", OpenAIWireFormat.ToArgumentsString(default));
    }

    [Fact]
    public void ParseArguments_ValidObject_ReturnsObject()
    {
        var element = OpenAIWireFormat.ParseArguments("""{"city":"Kyiv"}""");

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("Kyiv", element.GetProperty("city").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"city":"Ky""")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void ParseArguments_UnusableText_ReturnsEmptyObject(string? text)
    {
        var element = OpenAIWireFormat.ParseArguments(text);

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Empty(element.EnumerateObject());
    }
}
