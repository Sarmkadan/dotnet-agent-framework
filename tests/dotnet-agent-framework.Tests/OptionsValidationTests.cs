namespace DotnetAgentFramework.Tests;

public class OpenAIChatModelOptionsValidationTests
{
    [Fact]
    public void Validate_FullyPopulatedOptions_DoesNotThrow()
    {
        var options = new OpenAIChatModelOptions
        {
            ModelId = "gpt-4o",
            BaseAddress = new Uri("https://example.test/openai/deployments/chat/"),
            ApiKey = "sk-test",
            Temperature = 0.7,
            MaxTokens = 256,
            RequestTimeout = TimeSpan.FromSeconds(30),
            DefaultHeaders = new Dictionary<string, string> { ["api-key"] = "az-key" },
        };

        options.Validate();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyModelId_ThrowsArgumentNamingModelId(string? modelId)
    {
        var options = new OpenAIChatModelOptions { ModelId = modelId! };

        var ex = Assert.Throws<ArgumentException>(options.Validate);

        Assert.Equal("ModelId", ex.ParamName);
        Assert.Contains("ModelId", ex.Message);
    }

    [Theory]
    [InlineData("v1/")]
    [InlineData("https://example.test/v1")]
    public void Validate_BaseAddressRelativeOrWithoutTrailingSlash_ThrowsArgumentNamingBaseAddress(string address)
    {
        var options = new OpenAIChatModelOptions
        {
            ModelId = "gpt-4o",
            BaseAddress = new Uri(address, UriKind.RelativeOrAbsolute),
        };

        var ex = Assert.Throws<ArgumentException>(options.Validate);

        Assert.Equal("BaseAddress", ex.ParamName);
        Assert.Contains("BaseAddress", ex.Message);
    }

    [Fact]
    public void Validate_NullBaseAddress_ThrowsArgumentNullNamingBaseAddress()
    {
        var options = new OpenAIChatModelOptions { ModelId = "gpt-4o", BaseAddress = null! };

        var ex = Assert.Throws<ArgumentNullException>(options.Validate);

        Assert.Equal("BaseAddress", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_EmptyApiKey_ThrowsArgumentNamingApiKey(string apiKey)
    {
        var options = new OpenAIChatModelOptions { ModelId = "gpt-4o", ApiKey = apiKey };

        var ex = Assert.Throws<ArgumentException>(options.Validate);

        Assert.Equal("ApiKey", ex.ParamName);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(2.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Validate_TemperatureOutsideZeroToTwo_ThrowsArgumentOutOfRangeNamingTemperature(double temperature)
    {
        var options = new OpenAIChatModelOptions { ModelId = "gpt-4o", Temperature = temperature };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Equal("Temperature", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMaxTokens_ThrowsArgumentOutOfRangeNamingMaxTokens(int maxTokens)
    {
        var options = new OpenAIChatModelOptions { ModelId = "gpt-4o", MaxTokens = maxTokens };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Equal("MaxTokens", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveRequestTimeout_ThrowsArgumentOutOfRangeNamingRequestTimeout(int seconds)
    {
        var options = new OpenAIChatModelOptions { ModelId = "gpt-4o", RequestTimeout = TimeSpan.FromSeconds(seconds) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Equal("RequestTimeout", ex.ParamName);
    }

    [Fact]
    public void Validate_NullDefaultHeaders_ThrowsArgumentNullNamingDefaultHeaders()
    {
        var options = new OpenAIChatModelOptions { ModelId = "gpt-4o", DefaultHeaders = null! };

        var ex = Assert.Throws<ArgumentNullException>(options.Validate);

        Assert.Equal("DefaultHeaders", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_DefaultHeaderWithBlankName_ThrowsArgumentNamingDefaultHeaders(string name)
    {
        var options = new OpenAIChatModelOptions
        {
            ModelId = "gpt-4o",
            DefaultHeaders = new Dictionary<string, string> { [name] = "value" },
        };

        var ex = Assert.Throws<ArgumentException>(options.Validate);

        Assert.Equal("DefaultHeaders", ex.ParamName);
    }

    [Fact]
    public void Validate_DefaultHeaderWithNullValue_ThrowsArgumentNamingDefaultHeaders()
    {
        var options = new OpenAIChatModelOptions
        {
            ModelId = "gpt-4o",
            DefaultHeaders = new Dictionary<string, string> { ["api-key"] = null! },
        };

        var ex = Assert.Throws<ArgumentException>(options.Validate);

        Assert.Equal("DefaultHeaders", ex.ParamName);
        Assert.Contains("api-key", ex.Message);
    }
}

public class AgentOptionsValidationTests
{
    [Fact]
    public void Validate_NullToolTimeoutAndSingleStep_DoesNotThrow()
    {
        var options = new AgentOptions { MaxSteps = 1, ToolTimeout = null, ParallelToolCalls = false };

        options.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_MaxStepsBelowOne_ThrowsArgumentOutOfRangeNamingOptions(int maxSteps)
    {
        var options = new AgentOptions { MaxSteps = maxSteps };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Equal("options", ex.ParamName);
        Assert.Contains("MaxSteps must be at least 1.", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveToolTimeout_ThrowsArgumentOutOfRangeNamingOptions(int seconds)
    {
        var options = new AgentOptions { ToolTimeout = TimeSpan.FromSeconds(seconds) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Equal("options", ex.ParamName);
        Assert.Contains("ToolTimeout must be positive when set.", ex.Message);
    }
}
