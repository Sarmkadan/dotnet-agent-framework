using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetAgentFramework;

/// <summary>
/// Durable conversation log. Every appended message is also written as one JSON line to a file, so a conversation
/// survives process restarts. The file is the record of what was said; the <c>inner</c> memory decides what the model sees.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="OpenAsync"/> to resume a conversation: it reads the file and replays every stored message into
/// <c>inner</c>, which does file I/O. Use <see cref="Create"/> to start from an empty log: it does not read the file, so
/// the first append adds to whatever the file already holds. Pass an empty <c>inner</c> to <see cref="Create"/>; a
/// non-empty one will not match the file.
/// </para>
/// <para>
/// <see cref="AppendAsync"/> stores the message in <c>inner</c>, then appends its line to the file. If the file write
/// fails, the message is already in <c>inner</c> and the exception reaches the caller.
/// <see cref="ClearAsync"/> clears <c>inner</c> and truncates the file.
/// </para>
/// <para>
/// Appends, clears and file writes are serialized by one semaphore. The semaphore is held across the <c>inner</c> call
/// too, so the order of lines in the file matches the order of messages in memory. Otherwise a concurrent append
/// could be written out of order, and a reload would replay a different conversation.
/// <see cref="RecallAsync"/> is forwarded to <c>inner</c>, so it ignores the file and behaves as <c>inner</c> does.
/// </para>
/// </remarks>
// CA1001: SemaphoreSlim is disposable, but this type never touches its AvailableWaitHandle, so it holds no unmanaged
// resources and the memory does not need to be disposable. Keeps the public shape to IAgentMemory alone.
#pragma warning disable CA1001
public sealed class JsonFileAgentMemory : IAgentMemory
#pragma warning restore CA1001
{
    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAgentMemory _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private JsonFileAgentMemory(string filePath, IAgentMemory inner)
    {
        FilePath = filePath;
        _inner = inner;
    }

    /// <summary>
    /// Gets the path of the JSON Lines file that holds the conversation.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Opens a conversation log and replays the stored messages into <paramref name="inner"/>. If the file does not
    /// exist, the log starts empty. This is the preferred way to resume a conversation.
    /// </summary>
    /// <param name="filePath">Path of the JSON Lines file. The file is created on the first append.</param>
    /// <param name="inner">Memory that decides what the model sees. Defaults to a new <see cref="InMemoryAgentMemory"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The opened memory, with every stored message replayed into <paramref name="inner"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidDataException">A non-empty line in the file is not a valid <see cref="ChatMessage"/>.</exception>
    public static async Task<JsonFileAgentMemory> OpenAsync(
        string filePath,
        IAgentMemory? inner = null,
        CancellationToken cancellationToken = default)
    {
        JsonFileAgentMemory memory = Create(filePath, inner);
        await memory.ReplayAsync(cancellationToken).ConfigureAwait(false);
        return memory;
    }

    /// <summary>
    /// Creates a conversation log without reading the file. Use this to start a fresh log. The file is not
    /// read, so any messages already in it are not replayed and will remain in it.
    /// </summary>
    /// <param name="filePath">Path of the JSON Lines file. The file is created on the first append.</param>
    /// <param name="inner">Memory that decides what the model sees. Defaults to a new <see cref="InMemoryAgentMemory"/>.</param>
    /// <returns>The memory.</returns>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is empty or whitespace.</exception>
    public static JsonFileAgentMemory Create(string filePath, IAgentMemory? inner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return new(filePath, inner ?? new InMemoryAgentMemory());
    }

    /// <inheritdoc />
    public async Task AppendAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        string line = JsonSerializer.Serialize(message, _serializerOptions) + "\n";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _inner.AppendAsync(message, cancellationToken).ConfigureAwait(false);
            await File.AppendAllTextAsync(FilePath, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forwards to <c>inner</c>. The file is not read here.
    /// </summary>
    /// <param name="query">Passed through to <c>inner</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The window that <c>inner</c> returns.</returns>
    public Task<IReadOnlyList<ChatMessage>> RecallAsync(string query, CancellationToken cancellationToken = default)
        => _inner.RecallAsync(query, cancellationToken);

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _inner.ClearAsync(cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(FilePath, string.Empty, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReplayAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        string[] lines = await File.ReadAllLinesAsync(FilePath, cancellationToken).ConfigureAwait(false);

        for (int index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            ChatMessage message = ParseLine(lines[index], index + 1);
            await _inner.AppendAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private ChatMessage ParseLine(string line, int lineNumber)
    {
        ChatMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ChatMessage>(line, _serializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Line {lineNumber} of '{FilePath}' is not a valid ChatMessage", ex);
        }

        return message ?? throw new InvalidDataException($"Line {lineNumber} of '{FilePath}' is not a valid ChatMessage");
    }
}
