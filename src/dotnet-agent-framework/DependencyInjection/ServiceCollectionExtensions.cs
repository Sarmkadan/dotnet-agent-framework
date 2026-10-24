using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DotnetAgentFramework;

/// <summary>
/// Registers the framework's services with a dependency injection container.
/// </summary>
/// <remarks>
/// Lifetimes: <see cref="Agent"/> and <see cref="IAgentMemory"/> are scoped, so each scope (for example each HTTP
/// request) gets its own conversation. <see cref="IChatModel"/> and <see cref="ITool"/> are singletons, which
/// requires them to be thread-safe.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AgentOptions"/> and a scoped <see cref="Agent"/>. The agent is built from the registered
    /// <see cref="IChatModel"/>, every registered <see cref="ITool"/>, the <see cref="IAgentMemory"/> and the options.
    /// </summary>
    /// <remarks>
    /// The options are validated when they are first resolved, so an invalid configuration throws
    /// <see cref="OptionsValidationException"/> at that point. <see cref="IAgentMemory"/> defaults to
    /// <see cref="InMemoryAgentMemory"/>; use <see cref="AddAgentMemory{TMemory}"/> to replace it. Register the
    /// <see cref="IChatModel"/> separately, for example with <see cref="AddOpenAIChatModel"/>.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Sets the loop options. <see langword="null"/> keeps the <see cref="AgentOptions"/> defaults.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentFramework(
        this IServiceCollection services,
        Action<AgentOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<AgentOptions>()
            .Configure(configure ?? (_ => { }))
            .Validate(IsValid, "AgentOptions is invalid");

        services.TryAddScoped<IAgentMemory, InMemoryAgentMemory>();
        services.AddScoped(sp => new Agent(
            sp.GetRequiredService<IChatModel>(),
            sp.GetServices<ITool>(),
            sp.GetRequiredService<IAgentMemory>(),
            sp.GetRequiredService<IOptions<AgentOptions>>().Value));

        return services;

        static bool IsValid(AgentOptions options)
        {
            try
            {
                options.Validate();
                return true;
            }
            catch (ArgumentException)
            {
                // Validate reports its failures as ArgumentOutOfRangeException; OptionsValidationException carries the message.
                return false;
            }
        }
    }

    /// <summary>
    /// Registers a tool that the container constructs. Registered as a singleton, so the tool must be thread-safe.
    /// </summary>
    /// <typeparam name="TTool">Tool type to construct through the container.</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddTool<TTool>(this IServiceCollection services)
        where TTool : class, ITool
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddSingleton<ITool, TTool>();
    }

    /// <summary>
    /// Registers an existing tool instance. The instance is shared by every agent, so it must be thread-safe.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="instance">Tool to register.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="instance"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddTool(this IServiceCollection services, ITool instance)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(instance);

        return services.AddSingleton(instance);
    }

    /// <summary>
    /// Registers an <see cref="OpenAIChatModel"/> as the singleton <see cref="IChatModel"/>. The options are applied and
    /// validated immediately, so an invalid configuration throws during registration rather than at first use.
    /// </summary>
    /// <remarks>
    /// The model owns a new <see cref="HttpClient"/> that is never disposed, because the singleton lives as long as the
    /// container. The client does not come from <c>IHttpClientFactory</c>. To use the factory, register
    /// <see cref="IChatModel"/> yourself with an <see cref="OpenAIChatModel"/> built from a factory-created client.
    /// <para>
    /// The client's <see cref="HttpClient.Timeout"/> is infinite. <see cref="OpenAIChatModelOptions.RequestTimeout"/>
    /// already bounds each request, and a finite client timeout would also cut off long streamed answers.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Sets the model, endpoint and request options. <see cref="OpenAIChatModelOptions.ModelId"/> must be set.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The options are invalid, for example <see cref="OpenAIChatModelOptions.ModelId"/> is not set.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static IServiceCollection AddOpenAIChatModel(
        this IServiceCollection services,
        Action<OpenAIChatModelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // ModelId is required, so the initializer needs a value. Validate rejects the empty string if configure leaves it unset.
        var options = new OpenAIChatModelOptions { ModelId = string.Empty };
        configure(options);
        options.Validate();

        // The client is owned by the singleton model below, which lives as long as the container.
#pragma warning disable CA2000 // Ownership moves into the OpenAIChatModel singleton, which is never disposed.
        var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var model = new OpenAIChatModel(httpClient, options);
#pragma warning restore CA2000

        return services.AddSingleton<IChatModel>(model);
    }

    /// <summary>
    /// Replaces the default <see cref="IAgentMemory"/> with a scoped <typeparamref name="TMemory"/>. Works whether it is
    /// called before or after <see cref="AddAgentFramework"/>.
    /// </summary>
    /// <typeparam name="TMemory">Memory type to construct through the container for each scope.</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentMemory<TMemory>(this IServiceCollection services)
        where TMemory : class, IAgentMemory
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.Replace(ServiceDescriptor.Scoped<IAgentMemory, TMemory>());
    }
}
