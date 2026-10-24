using DotnetAgentFramework.Testing;
using DotnetAgentFramework.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DotnetAgentFramework.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAgentFramework_ResolvesAgentWithRegisteredTools_ExposesBothTools()
    {
        var model = new ScriptedChatModel().Answer("done");
        using var provider = new ServiceCollection()
            .AddSingleton<IChatModel>(model)
            .AddAgentFramework()
            .AddTool<EchoTool>()
            .AddTool(DelegateTool.Create("clock", "Returns the time.", _ => Task.FromResult("noon")))
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var agent = scope.ServiceProvider.GetRequiredService<Agent>();

        Assert.Equal(2, agent.Tools.Count);
        Assert.Contains("echo", agent.Tools.Keys);
        Assert.Contains("clock", agent.Tools.Keys);
        Assert.Same(model, agent.Model);
    }

    [Fact]
    public async Task AddAgentFramework_ConfigureDelegate_AppliesMaxSteps()
    {
        var model = new ScriptedChatModel()
            .CallTool("echo", new { text = "first" })
            .CallTool("echo", new { text = "second" });
        using var provider = new ServiceCollection()
            .AddSingleton<IChatModel>(model)
            .AddAgentFramework(options => options.MaxSteps = 2)
            .AddTool<EchoTool>()
            .BuildServiceProvider();

        Assert.Equal(2, provider.GetRequiredService<IOptions<AgentOptions>>().Value.MaxSteps);

        using var scope = provider.CreateScope();
        var agent = scope.ServiceProvider.GetRequiredService<Agent>();

        var ex = await Assert.ThrowsAsync<AgentStepLimitExceededException>(() => agent.RunAsync("go"));
        Assert.Equal(2, ex.MaxSteps);
    }

    [Fact]
    public void AddAgentFramework_DefaultMemory_IsScopedAndDiffersBetweenScopes()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IChatModel>(new ScriptedChatModel())
            .AddAgentFramework()
            .BuildServiceProvider();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var firstMemory = first.ServiceProvider.GetRequiredService<IAgentMemory>();
        var secondMemory = second.ServiceProvider.GetRequiredService<IAgentMemory>();

        Assert.IsType<InMemoryAgentMemory>(firstMemory);
        Assert.NotSame(firstMemory, secondMemory);
        Assert.Same(firstMemory, first.ServiceProvider.GetRequiredService<IAgentMemory>());
    }

    [Fact]
    public void AddAgentFramework_InvalidOptions_ThrowsOptionsValidationExceptionOnResolve()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IChatModel>(new ScriptedChatModel())
            .AddAgentFramework(options => options.MaxSteps = 0)
            .BuildServiceProvider();

        using var scope = provider.CreateScope();

        var ex = Assert.Throws<OptionsValidationException>(() => scope.ServiceProvider.GetRequiredService<Agent>());
        Assert.Contains("AgentOptions is invalid", ex.Failures);
    }

    [Fact]
    public void AddAgentMemory_RegisteredBeforeOrAfterFramework_ReplacesDefaultMemory()
    {
        using var before = new ServiceCollection()
            .AddAgentMemory<NoopMemory>()
            .AddAgentFramework()
            .BuildServiceProvider();
        using var after = new ServiceCollection()
            .AddAgentFramework()
            .AddAgentMemory<NoopMemory>()
            .BuildServiceProvider();

        Assert.IsType<NoopMemory>(before.GetRequiredService<IAgentMemory>());
        Assert.IsType<NoopMemory>(after.GetRequiredService<IAgentMemory>());
    }

    [Fact]
    public void AddOpenAIChatModel_ConfiguredModelId_ResolvesSingletonOpenAIChatModel()
    {
        using var provider = new ServiceCollection()
            .AddOpenAIChatModel(options => options.ModelId = "gpt-test")
            .BuildServiceProvider();

        var model = provider.GetRequiredService<IChatModel>();

        Assert.IsType<OpenAIChatModel>(model);
        Assert.Equal("gpt-test", model.ModelId);
        Assert.Same(model, provider.GetRequiredService<IChatModel>());
    }

    [Fact]
    public void AddOpenAIChatModel_ModelIdNotSet_ThrowsArgumentExceptionOnRegistration()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() => services.AddOpenAIChatModel(_ => { }));

        Assert.Equal("ModelId", ex.ParamName);
    }

    private sealed class NoopMemory : IAgentMemory
    {
        public Task AppendAsync(ChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ChatMessage>> RecallAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ChatMessage>>([]);

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
