# 🧪 Agentic Recipes

[![CI](https://github.com/sauravbhattacharya001/agentic-recipes/actions/workflows/ci.yml/badge.svg)](https://github.com/sauravbhattacharya001/agentic-recipes/actions/workflows/ci.yml)
[![CodeQL](https://github.com/sauravbhattacharya001/agentic-recipes/actions/workflows/codeql.yml/badge.svg)](https://github.com/sauravbhattacharya001/agentic-recipes/actions/workflows/codeql.yml)
[![codecov](https://codecov.io/gh/sauravbhattacharya001/agentic-recipes/branch/master/graph/badge.svg)](https://codecov.io/gh/sauravbhattacharya001/agentic-recipes)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

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
| [Iterative Refinement](recipes/iterative-refinement/) | Critic Loop | Generate a draft, self-critique with a score, revise until good enough or plateaued |
| [Guardrailed Pipeline](recipes/guardrailed-pipeline/) | Inspect → Decide → Act | Screen input for injection, PII/secrets, and disallowed content, then allow / sanitize / block |
| [Memory-Augmented Chain](recipes/memory-augmented/) | Retrieve → Augment → Generate → Remember | Carry context across turns with self-managing working memory: relevance recall, salience decay, eviction |
| [RAG Pipeline](recipes/rag-pipeline/) | Retrieve → Augment → Generate | Ground answers in a document corpus with TF-IDF retrieval, inline citations, and autonomous abstention |
| [Plan-and-Execute](recipes/plan-and-execute/) | Decompose → Execute → Adapt | Decompose a goal into a dependency-ordered plan, execute it, and adapt to step failures with retry → fallback → skip → abort |
| [Self-Consistency](recipes/self-consistency/) | Sample → Vote → Decide | Sample the same question N times, majority-vote over the answers, report consensus, and autonomously abstain when too split to trust |
| [Multi-Agent Debate](recipes/multi-agent-debate/) | Argue → Rebut → Judge | Opposing agents debate across rounds, rebutting each other while a judge scores; stop early on convergence or a clear lead, escalate when hung |
| [Reflexion](recipes/reflexion/) | Attempt → Evaluate → Reflect → Retry | Attempt a task, grade the outcome, write a verbal lesson from each failure into episodic memory, and retry wiser; stop on success, budget, or a stuck loop |
| [Tree-of-Thoughts](recipes/tree-of-thoughts/) | Expand → Evaluate → Search | Branch a tree of partial solutions, score each state, keep the best beam, and backtrack on dead ends; stop on solved, frontier-exhausted, depth-limited, or budget-exhausted |

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

269 tests total: 264 pass out of the box, 5 skipped (require Azure OpenAI credentials).

### Coverage

Each recipe is a standalone top-level program, so the logic under test is
mirrored into the test assembly. Coverage is collected with
[Coverlet](https://github.com/coverlet-coverage/coverlet) using the checked-in
`coverlet.runsettings` (which enables `IncludeTestAssembly` and filters out the
test frameworks), then reported to [Codecov](https://codecov.io) by CI:

```bash
dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings
```

The Cobertura report is written under `TestResults/`. Current coverage is ~99%
line / ~94% branch over the recipe logic.
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
- [x] RAG Pipeline (retrieve → augment → generate)
- [x] Iterative Refinement (critic loop)
- [x] Conditional Router (classify → branch)
- [x] Guardrailed Pipeline (injection detection + content filtering)
- [x] Memory-Augmented Chain (context accumulation across turns)
- [x] Plan-and-Execute (decompose → execute → adapt: retry / fallback / skip / abort)
- [x] Self-Consistency (sample N → majority vote → consensus-gated abstention)
- [x] Multi-Agent Debate (argue → rebut → judge → converge or decide, escalate when hung)
- [x] Reflexion (attempt → evaluate → verbal self-reflection → retry, learning from failure across trials)
- [x] Tree-of-Thoughts (expand → evaluate → beam search with pruning and backtracking, autonomous stop on solved / exhausted / depth / budget)

## License

MIT
