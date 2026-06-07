# 🧪 Agentic Recipes

Canonical agentic pipeline examples built on top of [prompt-lib](https://github.com/sauravbhattacharya001/prompt).

Each recipe demonstrates a different orchestration pattern — from simple linear chains to parallel fan-out/fan-in workflows — showing how composable prompt building blocks become autonomous multi-step agents.

## Recipes

| Recipe | Pattern | Description |
|--------|---------|-------------|
| [Research → Summarize → Format](recipes/research-summarize-format/) | Linear Chain | Gather information, distill key points, output formatted report |
| [Multi-Perspective Analysis](recipes/multi-perspective/) | Fan-Out / Fan-In | Run same input through multiple persona prompts, synthesize insights |
| [Code Review Pipeline](recipes/code-review-pipeline/) | Middleware Pipeline | Analyze code through validation, review, and fix stages with retry |

## Architecture

```
┌─────────────────────────────────────────────────┐
│                  prompt-lib                       │
│  PromptTemplate · PromptChain · PromptPipeline  │
│  PromptOrchestrator · Middleware                │
└──────────────────────┬──────────────────────────┘
                       │
         ┌─────────────┼─────────────┐
         │             │             │
    ┌────▼────┐  ┌─────▼─────┐  ┌───▼────┐
    │ Linear  │  │  Fan-Out  │  │ Pipe-  │
    │ Chain   │  │  Fan-In   │  │ line   │
    └─────────┘  └───────────┘  └────────┘
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
dotnet run --project recipes/research-summarize-format
```

### Prerequisites

- .NET 8 SDK
- Azure OpenAI endpoint + API key (set in environment variables)
- [prompt-lib](https://www.nuget.org/packages/prompt-llm-aoi) NuGet package (auto-restored)

### Environment Variables

```bash
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_API_KEY="your-key"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o"  # optional, defaults to gpt-4o
```

## Adding a Recipe

1. Create a folder under `recipes/`
2. Add a `.csproj` referencing `prompt-llm-aoi`
3. Add a `README.md` explaining the pattern
4. Add `Program.cs` with a runnable example
5. Submit a PR

## Patterns Roadmap

- [ ] Plan → Execute → Verify (task decomposition)
- [ ] Iterative Refinement (critic loop)
- [ ] Conditional Router (classify → branch)
- [ ] Guardrailed Pipeline (injection detection + content filtering)
- [ ] Memory-Augmented Chain (context accumulation across turns)

## License

MIT
