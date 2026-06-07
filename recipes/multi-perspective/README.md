# Multi-Perspective Analysis

A fan-out/fan-in pattern using `PromptOrchestrator` that runs the same input through multiple persona prompts in parallel, then synthesizes their insights.

## Pattern: Fan-Out / Fan-In

```
                    ┌─── [Optimist] ───┐
                    │                   │
[Analyze Topic] ───┼─── [Skeptic]  ───┼──→ [Synthesizer]
                    │                   │
                    └─── [Pragmatist] ──┘
```

## What It Does

1. **Analyze** — Frames the topic/proposal for evaluation
2. **Fan-Out** — Three parallel perspectives analyze independently:
   - **Optimist** — Best-case scenario, opportunities, upside
   - **Skeptic** — Risks, failure modes, blind spots
   - **Pragmatist** — Realistic assessment, resource requirements, timeline
3. **Synthesize** — Combines all three perspectives into a balanced recommendation

## Key Concepts

- `PromptOrchestrator` — DAG-based execution with dependency resolution
- `BuildFanOutFanIn` — Static builder for parallel execution patterns
- Parallel execution — Fan-out nodes run concurrently (not sequentially)
- `OrchestratorReport` — Execution reports in text, Markdown, JSON, Mermaid

## Usage

```bash
dotnet run --project . -- "Should we migrate our monolith to microservices?"
```

## How It Leverages prompt-lib

```csharp
var plan = PromptOrchestrator.BuildFanOutFanIn(
    inputPrompt: "Analyze: {topic}",
    parallelPrompts: new[]
    {
        "As an optimist, evaluate: {input}",
        "As a skeptic, evaluate: {input}",
        "As a pragmatist, evaluate: {input}"
    },
    aggregatorPrompt: "Synthesize: {parallel_0} | {parallel_1} | {parallel_2}"
);

var execution = await orchestrator.ExecuteAsync(plan, variables);
```
