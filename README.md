# dotnet-agent-framework

[![CI](https://github.com/Sarmkadan/dotnet-agent-framework/actions/workflows/ci.yml/badge.svg)](https://github.com/Sarmkadan/dotnet-agent-framework/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/DotnetAgentFramework.svg)](https://www.nuget.org/packages/DotnetAgentFramework)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Lightweight framework for building AI agents with tool calling, memory and multi-step reasoning.

No opinionated pipeline, no plugin marketplace, no YAML. Four interfaces and one loop: give the agent a model, some tools and a memory, and it runs the reason-act cycle until the model produces a final answer.

## Features

- **Provider-agnostic model abstraction** - `IChatModel` wraps any chat API. `OpenAIChatModel` covers OpenAI-compatible endpoints (OpenAI, Ollama, vLLM, OpenRouter). Bring your own `HttpClient`.
- **Typed tools** - implement `ITool` directly, or derive from `Tool<TArgs>` and get argument deserialization and JSON Schema generation for free.
- **Pluggable memory** - `IAgentMemory` decides what the model sees each step: full history, sliding window, token budget, summary, or a durable JSON Lines log.
- **Bounded reasoning loop** - `MaxSteps`, per-tool timeouts and parallel tool execution are options, not forks of the framework.
- **Streaming** - stream the final answer token by token after tool steps complete.
- **Zero heavy dependencies** - the core targets `net9.0` and uses `System.Text.Json` and the `Microsoft.Extensions` abstractions only.

## Quick start

```csharp
using System.Text.Json;
using DotnetAgentFramework;

// 1. A tool the model can call. Derive from Tool<TArgs> as below, or use DelegateTool.Create<TArgs>(...) for an inline tool without a class.
public sealed record WeatherArgs(string City);

public sealed class WeatherTool : Tool<WeatherArgs>
{
    public override string Name => "get_weather";
    public override string Description => "Returns current weather for a city.";

    protected override Task<string> InvokeAsync(WeatherArgs args, CancellationToken ct)
        => Task.FromResult($"{args.City}: 21 C, clear");
}

// 2. Wire up a model (any IChatModel implementation) and run
IChatModel model = new OpenAIChatModel(new HttpClient(), "gpt-4o", apiKey);

var agent = new Agent(
    model,
    tools: [new WeatherTool()],
    options: new AgentOptions
    {
        SystemPrompt = "You are a concise travel assistant.",
        MaxSteps = 5,
    });

AgentResult result = await agent.RunAsync("Should I bring a jacket to Sofia tonight?");

Console.WriteLine(result.FinalAnswer);
Console.WriteLine($"{result.Steps} steps, {result.Usage.TotalTokens} tokens");
```

Streaming the final answer:

```csharp
await foreach (string delta in agent.StreamAsync("Summarize today's forecast"))
{
    Console.Write(delta);
}
```

## Tools

A tool is a function the model can call. Both forms below produce the same `ToolDefinition` (name, description, JSON Schema) and report failures the same way (see [Error handling contract](#error-handling-contract)).

### Typed tools with `Tool<TArgs>`

Derive from `Tool<TArgs>`, where `TArgs` is a class (a record is the usual choice). Arguments are deserialized with `System.Text.Json`. The schema is generated from `TArgs` by reflection, once per type.

Schema rules:

- Property names are camelCase: `City` becomes `city`. `[JsonPropertyName]` sets the name the model sees and the name arguments are read from.
- Nullable properties (`int?`, and `string?` in a nullable-enabled project) are optional. Other reference and value types are required.
- `[JsonIgnore]` removes a property from the schema. `[JsonIgnore(Condition = WhenWritingNull)]` or `WhenWritingDefault` keeps it but makes it optional.
- `[Description]` on a property adds a `description` to that property's schema.
- Enums are advertised as string names (`"enum": [...]`).
- Nested classes and records become nested object schemas. A type that refers back to itself is emitted as a plain `"object"`.
- Arrays and `IEnumerable<T>` become `array` with `items`. Dictionaries with string keys become objects with `additionalProperties`. Other key types throw `NotSupportedException` when the schema is generated.
- Every object schema sets `additionalProperties: false`.

```csharp
public enum SortOrder { Relevance, Date }

public sealed record SearchArgs(
    string Query,
    [property: Description("Maximum number of hits to return")] int? Limit,
    SortOrder Sort);

public sealed class SearchTool : Tool<SearchArgs>
{
    public override string Name => "search";
    public override string Description => "Searches the document index.";

    protected override Task<string> InvokeAsync(SearchArgs args, CancellationToken ct)
        => Task.FromResult($"{args.Limit ?? 5} hits for '{args.Query}' sorted by {args.Sort}");
}
```

The `SearchTool` schema is:

```json
{"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer","description":"Maximum number of hits to return"},"sort":{"type":"string","enum":["Relevance","Date"]}},"required":["query","sort"],"additionalProperties":false}
```

To hand-write a schema instead, override the protected virtual `BuildSchema()`.

### Inline tools with DelegateTool

Use `DelegateTool` when a tool is a one-off function and a class is overkill.

```csharp
// Typed arguments, synchronous handler. The schema is generated from SearchArgs.
DelegateTool search = DelegateTool.Create<SearchArgs>(
    "search",
    "Searches the document index.",
    args => $"{args.Limit ?? 5} hits for '{args.Query}'");

// No arguments. The schema is an empty object; the handler receives only the cancellation token.
DelegateTool clock = DelegateTool.Create(
    "utc_now",
    "Returns the current UTC time in ISO 8601.",
    ct => Task.FromResult(DateTime.UtcNow.ToString("O")));
```

`DelegateTool.Create<TArgs>` also has an asynchronous overload that takes `Func<TArgs, CancellationToken, Task<string>>`. The `DelegateTool` constructor takes a hand-built `ToolDefinition` and a handler over raw `JsonElement` arguments, for schemas you cannot express as a type.

### Error handling contract

Tools return recoverable problems as text, so the model sees them and can retry. Throw only for programmer errors.

The agent does not let a tool failure end the run. Each failing call becomes a `MessageRole.Tool` result whose text starts with `Error: `. There are three shapes:

```text
Error: unknown tool 'get_wether'. Available tools: get_weather, search
Error: tool 'get_weather' failed: HttpRequestException: No such host is known.
Error: tool 'get_weather' timed out after 30s
```

- An unknown tool name lists the registered tool names.
- An exception thrown by a tool is reported with its type name and message.
- A call that runs past `AgentOptions.ToolTimeout` (30 seconds by default) is reported as a timeout.

Arguments the model got wrong for a typed tool use the same prefix, for example `Error: tool 'search' received arguments that could not be deserialized: ...`. Cancellation from the caller is not converted; it propagates out of `RunAsync`.

## Memory

`IAgentMemory` owns the transcript and decides what the model sees on each step. If you pass no memory, `Agent` uses `InMemoryAgentMemory`.

| Implementation | Keeps | Use when |
|----------------|-------|----------|
| `InMemoryAgentMemory` | Every message, in process | Short sessions and tests. This is the default. |
| `SlidingWindowAgentMemory(maxMessages)` | The last N messages | You want a fixed, cheap bound and old turns can be dropped. |
| `TokenBudgetAgentMemory(maxTokens, counter?)` | The newest messages that fit a token budget | The limit is the model's context size, not a message count. |
| `SummarizingAgentMemory(summarizer, maxTokens, keepRecentMessages, counter?, summaryPrompt?)` | A running summary of old messages plus recent messages verbatim | Long conversations where early facts must survive. Each summarization is an extra model call. |
| `JsonFileAgentMemory` | Every message in a JSON Lines file; the wrapped inner memory decides what the model sees | The conversation must survive process restarts. |

The two budget-based memories measure messages with an `ITokenCounter`. The default, `ApproximateTokenCounter`, estimates `ceil(chars / 4) + 4` tokens per message. Pass your own counter when the budget must be exact.

Bounded history:

```csharp
var agent = new Agent(model, tools: [new WeatherTool()], memory: new SlidingWindowAgentMemory(20));
```

Persistence across runs. `OpenAsync` replays the file into the inner memory, and the file keeps growing; the inner memory only limits what the model sees. Use `JsonFileAgentMemory.Create` to start a fresh log without reading the file.

```csharp
// First process: the file is created on the first append.
var memory = await JsonFileAgentMemory.OpenAsync("conversation.jsonl", new SlidingWindowAgentMemory(20));
var agent = new Agent(model, tools: [new WeatherTool()], memory: memory);
await agent.RunAsync("My name is Vlad.");

// Later, in a new process: the stored messages are replayed before the run.
var resumed = await JsonFileAgentMemory.OpenAsync("conversation.jsonl", new SlidingWindowAgentMemory(20));
var nextAgent = new Agent(model, tools: [new WeatherTool()], memory: resumed);
AgentResult answer = await nextAgent.RunAsync("What is my name?");
```

### Writing your own memory

Implement `IAgentMemory`. The returned list must be a valid conversation: no orphan tool results, meaning every `MessageRole.Tool` message must follow the assistant message whose `ToolCalls` contain its `ToolCallId`. `ConversationWindow` is internal, so your memory has to enforce this itself.

```csharp
// Keeps the last N messages. Leading tool results are skipped because their request was cut away.
public sealed class LastNAgentMemory(int maxMessages) : IAgentMemory
{
    private readonly List<ChatMessage> _messages = [];
    private readonly Lock _gate = new();

    public Task AppendAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        lock (_gate) _messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> RecallAsync(string query, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            int start = Math.Max(0, _messages.Count - maxMessages);
            while (start < _messages.Count && _messages[start].Role == MessageRole.Tool) start++;
            return Task.FromResult<IReadOnlyList<ChatMessage>>(_messages.GetRange(start, _messages.Count - start));
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) _messages.Clear();
        return Task.CompletedTask;
    }
}
```

## Installation

```bash
dotnet add package DotnetAgentFramework
```

Requires .NET 9.0 or later.

## API overview

| Type | Role |
|------|------|
| `Agent` | The reason-act loop. Takes a model, tools, memory and options; exposes `RunAsync` and `StreamAsync`. |
| `AgentOptions` | `SystemPrompt`, `MaxSteps`, `ParallelToolCalls`, `ToolTimeout`, `Observer`. |
| `IChatModel` / `OpenAIChatModel` / `OpenAIChatModelOptions` | One completion or one stream from a chat model, with optional tool definitions. `OpenAIChatModel` adapts OpenAI-compatible `/chat/completions` endpoints. |
| `ITool` / `Tool<TArgs>` / `DelegateTool` | A named, described, schema'd function the model can invoke. `Tool<TArgs>` is the typed base class; `DelegateTool` is the inline form. |
| `IAgentMemory` / `InMemoryAgentMemory` | Stores messages and decides which ones the model sees next. |
| `SlidingWindowAgentMemory`, `TokenBudgetAgentMemory`, `SummarizingAgentMemory`, `JsonFileAgentMemory` | Memory implementations for bounded history, token budgets, summaries and persistence. |
| `ITokenCounter` / `ApproximateTokenCounter` | Estimates the tokens a message occupies, for the budget-based memories. |
| `IAgentObserver` / `ConsoleAgentObserver` / `DelegateAgentObserver` | Hooks for run, step, model and tool events. |
| `ChatMessage`, `ToolCall`, `ToolDefinition` | Wire-format-neutral conversation primitives. |
| `AgentResult`, `TokenUsage` | What a run returns: final answer, transcript, step count, token totals. |
| `AgentStepLimitExceededException` | Thrown when `MaxSteps` is hit without a final answer. |
| `ServiceCollectionExtensions` | `AddAgentFramework`, `AddTool`, `AddOpenAIChatModel`, `AddAgentMemory` for `Microsoft.Extensions.DependencyInjection`. |
| `ScriptedChatModel` | Deterministic `IChatModel` for your tests (namespace `DotnetAgentFramework.Testing`). |

### Lifecycle of one `RunAsync`

1. Append the user message to memory.
2. `memory.RecallAsync(input)` builds the prompt.
3. `model.CompleteAsync(messages, tools)` returns an assistant message.
4. If it has no tool calls, that is the final answer. Done.
5. Otherwise run each `ToolCall` through the matching `ITool`, append the results as `MessageRole.Tool` messages, and go to step 2.
6. After `MaxSteps` iterations without a final answer, throw `AgentStepLimitExceededException`.

## Status

Implemented: the reason-act loop (`Agent.RunAsync`, `Agent.StreamAsync`) with `MaxSteps`, per-tool timeouts and optional parallel tool calls; typed and delegate tools with schema generation; the five memories listed above; `OpenAIChatModel` for OpenAI-compatible endpoints (`CompleteAsync` and SSE `StreamAsync`); observer hooks; DI registration; `ScriptedChatModel` for tests.

Not implemented yet: a native Anthropic adapter and a vector recall memory.

See [issues](https://github.com/Sarmkadan/dotnet-agent-framework/issues) for the roadmap.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE) - Copyright (c) 2026 Vladyslav Zaiets
