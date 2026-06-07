# Code Review Pipeline

A middleware-based `PromptPipeline` that processes code through analysis, review, and fix stages — with validation, caching, retry, and metrics built in.

## Pattern: Middleware Pipeline

```
[Validation] → [Logging] → [Retry] → [Caching] → [Metrics]
       │                                                │
       └──── wraps ─────── Model Call ──── wraps ───────┘

Code → [Analyze] → [Review] → [Fix] → Result
```

## What It Does

1. **Analyze** — Identifies code patterns, complexity, potential issues
2. **Review** — Produces structured review with severity ratings
3. **Fix** — Generates corrected version with explanations

Each stage runs through the full middleware pipeline (validation, retry, caching, metrics).

## Key Concepts

- `PromptPipeline` — Middleware-based execution with cross-cutting concerns
- `IPromptMiddleware` — Composable behaviors (logging, caching, retry, validation)
- `PromptPipelineContext` — Rich context with metadata, errors, warnings
- `MetricsMiddleware` — Collect execution stats across all pipeline runs
- `CachingMiddleware` — Deduplicate identical prompts (same code = same review)

## Usage

```bash
dotnet run --project . -- path/to/file.cs
# or pipe code via stdin
echo "public void Bad() { Thread.Sleep(10000); }" | dotnet run --project .
```

## How It Leverages prompt-lib

```csharp
var pipeline = new PromptPipeline()
    .Use(new ValidationMiddleware(maxTokens: 16000))
    .Use(new LoggingMiddleware(Console.WriteLine))
    .Use(new RetryMiddleware(maxRetries: 2))
    .Use(new CachingMiddleware(TimeSpan.FromMinutes(10)))
    .Use(new MetricsMiddleware());

// Each stage runs through the same pipeline
await pipeline.ExecuteAsync(analyzeContext, ModelCall);
await pipeline.ExecuteAsync(reviewContext, ModelCall);
await pipeline.ExecuteAsync(fixContext, ModelCall);
```
