# Research → Summarize → Format

A linear `PromptChain` that demonstrates the most common agentic pattern: sequential processing where each step's output feeds the next.

## Pattern: Linear Chain

```
[Research] → [Summarize] → [Format]
     │              │             │
     ▼              ▼             ▼
  raw_data      summary     final_report
```

## What It Does

1. **Research** — Takes a topic and produces raw information/findings
2. **Summarize** — Distills the research into key bullet points
3. **Format** — Produces a polished, structured report from the summary

## Key Concepts

- `PromptChain` — Sequential execution with variable passing between steps
- `PromptTemplate` — Parameterized prompts with `{{variable}}` interpolation
- `ChainResult` — Access outputs from any step, not just the final one
- Chain validation — Static analysis of variable dependencies before execution

## Usage

```bash
dotnet run --project . -- "quantum computing advances in 2025"
```

## How It Leverages prompt-lib

```csharp
var chain = new PromptChain()
    .WithSystemPrompt("You are a research analyst.")
    .AddStep("research", researchTemplate, "raw_data")
    .AddStep("summarize", summarizeTemplate, "summary")
    .AddStep("format", formatTemplate, "final_report");

// Validate before running (no API calls)
var errors = chain.Validate(new() { ["topic"] = topic });

// Execute — each step auto-feeds the next
var result = await chain.RunAsync(new() { ["topic"] = topic });
```
