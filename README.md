# dotnet-agent-framework

[![CI](https://github.com/Sarmkadan/dotnet-agent-framework/actions/workflows/ci.yml/badge.svg)](https://github.com/Sarmkadan/dotnet-agent-framework/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/DotnetAgentFramework.svg)](https://www.nuget.org/packages/DotnetAgentFramework)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Lightweight framework for building AI agents with tool calling, memory and multi-step reasoning.

No opinionated pipeline, no plugin marketplace, no YAML. Four interfaces and one loop: give the agent a model, some tools and a memory, and it runs the reason-act cycle until the model produces a final answer.

## Features

- **Provider-agnostic model abstraction** - `IChatModel` wraps any chat API (OpenAI, Anthropic, Azure OpenAI, Ollama). Bring your own HTTP client.
- **Typed tools** - implement `ITool` directly, or derive from `Tool<TArgs>` and get argument deserialization and JSON Schema generation for free.
- **Pluggable memory** - `IAgentMemory` decides what the model sees each step: full history, sliding window, summary, or vector recall.
- **Bounded reasoning loop** - `MaxSteps`, per-tool timeouts and parallel tool execution are options, not forks of the framework.
- **Streaming** - stream the final answer token by token after tool steps complete.
- **Zero heavy dependencies** - the core targets `net9.0` and uses only `System.Text.Json`.

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
IChatModel model = new OpenAIChatModel(apiKey, "gpt-4o");

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

## Installation

```bash
dotnet add package DotnetAgentFramework
```

Requires .NET 9.0 or later.

## API overview

| Type | Role |
|------|------|
| `Agent` | The reason-act loop. Takes a model, tools, memory and options; exposes `RunAsync` and `StreamAsync`. |
| `AgentOptions` | `SystemPrompt`, `MaxSteps`, `ParallelToolCalls`, `ToolTimeout`. |
| `IChatModel` | One completion or one stream from a chat model, with optional tool definitions. |
| `ITool` / `Tool<TArgs>` | A named, described, schema'd function the model can invoke. |
| `IAgentMemory` / `InMemoryAgentMemory` | Stores messages and decides which ones the model sees next. |
| `ChatMessage`, `ToolCall`, `ToolDefinition` | Wire-format-neutral conversation primitives. |
| `AgentResult`, `TokenUsage` | What a run returns: final answer, transcript, step count, token totals. |
| `AgentStepLimitExceededException` | Thrown when `MaxSteps` is hit without a final answer. |

### Lifecycle of one `RunAsync`

1. Append the user message to memory.
2. `memory.RecallAsync(input)` builds the prompt.
3. `model.CompleteAsync(messages, tools)` returns an assistant message.
4. If it has no tool calls, that is the final answer. Done.
5. Otherwise run each `ToolCall` through the matching `ITool`, append the results as `MessageRole.Tool` messages, and go to step 2.
6. After `MaxSteps` iterations without a final answer, throw `AgentStepLimitExceededException`.

## Status

Public API shape is defined; implementations are in progress. See [issues](https://github.com/Sarmkadan/dotnet-agent-framework/issues) for the roadmap.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE) - Copyright (c) 2026 Vladyslav Zaiets
