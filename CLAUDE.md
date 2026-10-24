# dotnet-agent-framework

Lightweight framework for building AI agents with tool calling, memory and multi-step reasoning. NuGet package `DotnetAgentFramework`, target `net9.0`.

## Commands

```bash
dotnet build                                  # build everything (warnings are errors)
dotnet test                                   # run xunit tests
dotnet test --filter "FullyQualifiedName~Agent"   # run a subset
dotnet pack src/dotnet-agent-framework -c Release -o artifacts
dotnet format --verify-no-changes             # check .editorconfig compliance
```

Local machine may lack the .NET 9 runtime while having the 10 SDK. Build works regardless (targeting pack comes from NuGet); to run tests use `DOTNET_ROLL_FORWARD=Major dotnet test`. CI installs 9.0.x explicitly.

## Layout

```
src/dotnet-agent-framework/        library, namespace DotnetAgentFramework
  Agent.cs                          Agent, AgentOptions, AgentStepLimitExceededException
  IChatModel.cs                     model provider abstraction (complete + stream)
  ITool.cs                          ITool, Tool<TArgs> typed base
  IAgentMemory.cs                   IAgentMemory, InMemoryAgentMemory
  Models.cs                         ChatMessage, ToolCall, ToolDefinition, ChatCompletion, AgentResult, TokenUsage
  Memory/                           IAgentMemory implementations (namespace DotnetAgentFramework)
    ConversationWindow.cs           internal cut logic shared by window memories (keeps tool round-trips intact)
    SlidingWindowAgentMemory.cs     keeps the last N messages in process
    TokenBudgetAgentMemory.cs       keeps the newest messages that fit a token budget
    SummarizingAgentMemory.cs       folds old messages into a model-written summary
    JsonFileAgentMemory.cs          durable JSON Lines log wrapping an inner memory; OpenAsync replays, Create does not
  Providers/                        IChatModel adapters (namespace DotnetAgentFramework)
    OpenAIChatModel.cs              OpenAI-compatible /chat/completions adapter (non-streaming)
    OpenAIChatModelOptions.cs       configuration for OpenAIChatModel, validated on construction
    OpenAIWireFormat.cs             internal framework <-> OpenAI wire-format conversions
    SseParser.cs                    internal Server-Sent Events reader (data field only)
  DependencyInjection/              AddAgentFramework, AddTool, AddOpenAIChatModel, AddAgentMemory (namespace DotnetAgentFramework)
  Testing/ScriptedChatModel.cs      ScriptedChatModel: deterministic IChatModel for consumers' tests. Namespace
                                    DotnetAgentFramework.Testing, the ONE allowed sub-namespace (everything else is DotnetAgentFramework)
tests/dotnet-agent-framework.Tests/ xunit, references the library
.github/workflows/ci.yml            build + test + pack on push/PR to main
Directory.Build.props               shared MSBuild: Nullable, LangVersion latest, TreatWarningsAsErrors, analyzers
```

Note: directories are kebab-case (`dotnet-agent-framework`), assemblies and namespaces are PascalCase (`DotnetAgentFramework`).

## Architecture

The whole framework is one loop and four seams:

- `IChatModel` - the only thing that talks to an LLM provider. Takes `IReadOnlyList<ChatMessage>` + `IReadOnlyList<ToolDefinition>`, returns `ChatCompletion`. Provider adapters (OpenAI, Anthropic, Ollama) live in separate packages or in `src/.../Providers/` once added; the core must not reference provider SDKs.
- `ITool` - one callable function. `Definition` gives the model name/description/JSON Schema; `InvokeAsync(JsonElement)` runs it. `Tool<TArgs>` handles deserialization and (to be implemented) schema generation from the POCO.
- `IAgentMemory` - owns the transcript and decides what the model sees each step via `RecallAsync(query)`. Truncation, summarization and vector recall are all memory implementations, not agent features.
- `Agent.RunAsync` - append user message -> recall -> complete -> if tool calls: execute, append results, repeat; else return `AgentResult`. Bounded by `AgentOptions.MaxSteps`; throws `AgentStepLimitExceededException` when exceeded.

Current state: public API shape is defined and documented; method bodies throw `NotImplementedException`. Implement in this order: `InMemoryAgentMemory` -> `Agent.RunAsync` (with a fake `IChatModel` in tests) -> `Tool<TArgs>.BuildSchema` -> `Agent.StreamAsync` -> first provider adapter.

## Conventions

- File-scoped namespaces, `sealed` by default, records for immutable data.
- Every public member has XML docs (`GenerateDocumentationFile` is on, missing docs fail the build).
- Async methods end in `Async` and take `CancellationToken cancellationToken = default` as the last parameter.
- Tools return recoverable errors as text (the model should see them), throw only for programmer errors.
- No provider SDK dependencies in the core project. `System.Text.Json` and `Microsoft.Extensions.*` abstractions only.
- Tests: xunit, one class per type under test, method names `Method_Scenario_Expected`. Use hand-written fakes for `IChatModel`, not mocking libraries.
- Conventional commits (`feat:`, `fix:`, `test:`, `docs:`, `chore:`). No `Co-Authored-By` trailers.
- Analyzer level `latest-recommended` with warnings as errors; suppress with a justified `#pragma` or `.editorconfig` entry, not `<NoWarn>`.
