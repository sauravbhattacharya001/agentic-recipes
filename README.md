# 🧪 Agentic Recipes

Canonical agentic pipeline examples built on [`promptlib`](https://www.nuget.org/packages/promptlib) ([source](https://github.com/sauravbhattacharya001/prompt)).

Each recipe demonstrates a different orchestration pattern — from simple linear chains to tool-calling agent loops — showing how composable prompt building blocks become autonomous multi-step agents.

## Recipes

| Recipe | Pattern | Description |
|--------|---------|-------------|
| [Research → Summarize → Format](recipes/research-summarize-format/) | `PromptChain` | Gather information, distill key points, output formatted report |
| [Multi-Perspective Analysis](recipes/multi-perspective/) | `PromptOrchestrator` | Run same input through multiple persona prompts, synthesize insights |
| [Code Review Pipeline](recipes/code-review-pipeline/) | `PromptPipeline` | Analyze code through validation, review, and fix stages with retry |
| [Tool Agent Loop](recipes/tool-agent-loop/) | `PromptToolAgent` | ReAct loop: call tools, observe results, iterate to final answer |
| [Conditional Router](recipes/conditional-router/) | `PromptRouter` | Classify input, branch to specialized handlers, fall back gracefully |

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│                       promptlib                            │
│  PromptTemplate · PromptChain · PromptPipeline           │
│  PromptOrchestrator · PromptToolAgent · Middleware       │
└────────────────────────────┬─────────────────────────────┘
                             │
         ┌───────────┬───────┼───────┬────────────┐
         │           │       │       │            │
    ┌────▼────┐ ┌────▼────┐ ┌▼─────┐ ┌▼──────────▼┐
    │ Linear  │ │ Fan-Out │ │Pipe- │ │ Tool Agent │
    │ Chain   │ │ Fan-In  │ │line  │ │   Loop     │
    └─────────┘ └─────────┘ └──────┘ └────────────┘
```

## Getting Started

```bash
# Clone
git clone https://github.com/sauravbhattacharya001/agentic-recipes.git
cd agentic-recipes

# Restore & build
dotnet restore
dotnet build

# Run a recipe
dotnet run --project recipes/tool-agent-loop
```

### Prerequisites

- .NET 8 SDK
- [`promptlib`](https://www.nuget.org/packages/promptlib) NuGet package (auto-restored)
- Azure OpenAI endpoint + API key (for integration tests only)

### Environment Variables (optional — for integration tests)

```bash
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_API_KEY="your-key"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o"  # optional, defaults to gpt-4o
```

## Tests

```bash
dotnet test
```

61 tests total: 56 pass out of the box, 5 skipped (require Azure OpenAI credentials).

## Adding a Recipe

1. Create a folder under `recipes/`
2. Add a `.csproj` referencing `promptlib`
3. Add a `README.md` explaining the pattern
4. Add `Program.cs` with a runnable example
5. Add tests in `tests/`

## Patterns Roadmap

- [x] Linear Chain (`PromptChain`)
- [x] Fan-Out / Fan-In (`PromptOrchestrator`)
- [x] Middleware Pipeline (`PromptPipeline`)
- [x] Tool Agent Loop (`PromptToolAgent`)
- [ ] RAG Pipeline (retrieve → augment → generate)
- [ ] Iterative Refinement (critic loop)
- [x] Conditional Router (classify → branch)
- [ ] Guardrailed Pipeline (injection detection + content filtering)
- [ ] Memory-Augmented Chain (context accumulation across turns)

## License

MIT
