namespace DotnetAgentFramework;

/// <summary>
/// Configuration for <see cref="OpenAIChatModel"/>. <see cref="OpenAIChatModel"/> validates these on construction;
/// call <see cref="Validate"/> earlier to fail sooner.
/// </summary>
public sealed class OpenAIChatModelOptions
{
    /// <summary>
    /// Model identifier sent with each request, for example "gpt-4o" or "llama3.1". Required; there is no default.
    /// </summary>
    public required string ModelId { get; set; }

    /// <summary>
    /// Base of the endpoint. <c>chat/completions</c> is resolved beneath it, so it must be absolute and end with '/'.
    /// Defaults to <c>https://api.openai.com/v1/</c>.
    /// </summary>
    public Uri BaseAddress { get; set; } = new("https://api.openai.com/v1/");

    /// <summary>
    /// Sent as a Bearer token on each request. Null for servers without authentication, such as a local Ollama.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Sampling temperature, from 0 to 2 inclusive. Null omits the field and leaves the server default in place.
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    /// Upper bound on generated tokens, greater than 0. Null omits the field and leaves the server default in place.
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// Time limit for each request, covering the send and the reading of the response body. Must be positive.
    /// Defaults to 100 seconds, the same as <see cref="HttpClient.Timeout"/>.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Headers added to every request, for example <c>api-key</c> for Azure OpenAI. The Bearer token from
    /// <see cref="ApiKey"/> is set after these, so it takes precedence if both name <c>Authorization</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> DefaultHeaders { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks every property and throws for the first rule that is broken. The exception names the property.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="ModelId"/> or <see cref="ApiKey"/> is empty or whitespace, <see cref="BaseAddress"/> is not absolute
    /// or does not end with '/', or a <see cref="DefaultHeaders"/> entry has an empty name.
    /// </exception>
    /// <exception cref="ArgumentNullException"><see cref="BaseAddress"/> or <see cref="DefaultHeaders"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="Temperature"/> is outside 0 to 2, <see cref="MaxTokens"/> is not positive, or <see cref="RequestTimeout"/>
    /// is not positive.
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelId))
        {
            throw new ArgumentException("ModelId must be a non-empty, non-whitespace string.", nameof(ModelId));
        }

        ArgumentNullException.ThrowIfNull(BaseAddress, nameof(BaseAddress));
        if (!BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException($"BaseAddress must be an absolute URI, but was '{BaseAddress}'.", nameof(BaseAddress));
        }

        if (!BaseAddress.AbsoluteUri.EndsWith('/'))
        {
            throw new ArgumentException(
                $"BaseAddress must end with '/' so that 'chat/completions' resolves beneath it, but was '{BaseAddress}'.",
                nameof(BaseAddress));
        }

        if (ApiKey is not null && string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("ApiKey must be null or a non-whitespace string.", nameof(ApiKey));
        }

        if (Temperature is { } temperature && (double.IsNaN(temperature) || temperature < 0 || temperature > 2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Temperature), temperature, "Temperature must be between 0 and 2 inclusive.");
        }

        if (MaxTokens is { } maxTokens && maxTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTokens), maxTokens, "MaxTokens must be greater than 0 when set.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RequestTimeout), RequestTimeout, "RequestTimeout must be positive.");
        }

        ArgumentNullException.ThrowIfNull(DefaultHeaders, nameof(DefaultHeaders));
        foreach (var (name, value) in DefaultHeaders)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("DefaultHeaders must not contain a null or whitespace header name.", nameof(DefaultHeaders));
            }

            if (value is null)
            {
                throw new ArgumentException($"DefaultHeaders value for '{name}' must not be null.", nameof(DefaultHeaders));
            }
        }
    }
}
