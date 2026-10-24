using System.Text.Json;

namespace DotnetAgentFramework;

/// <summary>
/// A tool defined by a delegate instead of a class. Use the <c>Create</c> factories for the common cases,
/// or the constructor when the <see cref="ToolDefinition"/> is built by hand.
/// </summary>
public sealed class DelegateTool : ITool
{
    private readonly Func<JsonElement, CancellationToken, Task<string>> _handler;

    /// <summary>
    /// Creates a tool from a definition and a handler that receives the raw JSON arguments.
    /// </summary>
    /// <param name="definition">Name, description and argument schema presented to the model.</param>
    /// <param name="handler">Executes the tool. Receives the arguments chosen by the model.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    public DelegateTool(ToolDefinition definition, Func<JsonElement, CancellationToken, Task<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);

        Definition = definition;
        _handler = handler;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; }

    /// <inheritdoc />
    /// <remarks>Forwards the arguments and cancellation token to the handler unchanged.</remarks>
    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        => _handler(arguments, cancellationToken);

    /// <summary>
    /// Creates a tool whose arguments are deserialized into <typeparamref name="TArgs"/> and whose schema is derived from its shape.
    /// Arguments the model got wrong are returned to the model as an <c>"Error: ..."</c> text, as with <see cref="Tool{TArgs}"/>.
    /// </summary>
    /// <typeparam name="TArgs">POCO describing the tool's arguments.</typeparam>
    /// <param name="name">Unique tool name presented to the model. Must not be empty or whitespace.</param>
    /// <param name="description">Natural-language description presented to the model. Must not be empty or whitespace.</param>
    /// <param name="handler">Executes the tool with deserialized arguments.</param>
    /// <returns>The tool.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="description"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>, <paramref name="description"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    public static DelegateTool Create<TArgs>(
        string name,
        string description,
        Func<TArgs, CancellationToken, Task<string>> handler)
        where TArgs : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(handler);

        var definition = new ToolDefinition(name, description, JsonSchemaGenerator.Generate(typeof(TArgs)));

        return new DelegateTool(definition, (arguments, cancellationToken) =>
        {
            if (!ToolArguments.TryDeserialize(arguments, out TArgs? args, out string? error))
            {
                return Task.FromResult($"Error: tool '{name}' {error}");
            }

            return handler(args!, cancellationToken);
        });
    }

    /// <summary>
    /// Synchronous convenience form of <see cref="Create{TArgs}(string, string, Func{TArgs, CancellationToken, Task{string}})"/>.
    /// </summary>
    /// <typeparam name="TArgs">POCO describing the tool's arguments.</typeparam>
    /// <param name="name">Unique tool name presented to the model. Must not be empty or whitespace.</param>
    /// <param name="description">Natural-language description presented to the model. Must not be empty or whitespace.</param>
    /// <param name="handler">Executes the tool synchronously with deserialized arguments.</param>
    /// <returns>The tool.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="description"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>, <paramref name="description"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    public static DelegateTool Create<TArgs>(string name, string description, Func<TArgs, string> handler)
        where TArgs : class
    {
        ArgumentNullException.ThrowIfNull(handler);

        return Create<TArgs>(name, description, (args, _) => Task.FromResult(handler(args)));
    }

    /// <summary>
    /// Creates a tool that takes no arguments. The schema is an object with no properties.
    /// </summary>
    /// <param name="name">Unique tool name presented to the model. Must not be empty or whitespace.</param>
    /// <param name="description">Natural-language description presented to the model. Must not be empty or whitespace.</param>
    /// <param name="handler">Executes the tool.</param>
    /// <returns>The tool.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="description"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>, <paramref name="description"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    public static DelegateTool Create(string name, string description, Func<CancellationToken, Task<string>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(handler);

        var definition = new ToolDefinition(name, description, JsonSchemaGenerator.EmptyObjectSchema);

        return new DelegateTool(definition, (_, cancellationToken) => handler(cancellationToken));
    }
}
