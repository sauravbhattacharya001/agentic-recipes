# Conditional Router

**Pattern:** Classify → Branch → Handle

## Overview

The Conditional Router pattern demonstrates **agentic decision-making** in prompt pipelines. Instead of processing every input through the same pipeline, the router:

1. **Classifies** the input into a category using a specialized classifier prompt
2. **Routes** it to the appropriate handler based on the classification
3. **Executes** a branch-specific pipeline with its own system prompt and strategy
4. **Falls back** gracefully when classification confidence is low

This is the basis for building autonomous support systems, content processing pipelines, and any workflow where different inputs need fundamentally different handling.

## Architecture

```
                    ┌─────────────────┐
                    │   User Input    │
                    └────────┬────────┘
                             │
                    ┌────────▼────────┐
                    │   Classifier    │
                    │  (confidence +  │
                    │   reasoning)    │
                    └────────┬────────┘
                             │
           ┌─────────┬──────┼──────┬───────────┐
           │         │      │      │           │
     ┌─────▼─────┐ ┌▼────┐ ┌▼───┐ ┌▼─────────▼┐
     │ Technical │ │Bill-│ │Gen-│ │ Escalation │
     │  Handler  │ │ing  │ │eral│ │  Handler   │
     └───────────┘ └─────┘ └────┘ └────────────┘
           │         │      │      │
           └─────────┴──────┼──────┘
                            │
                   ┌────────▼────────┐
                   │  Unified Output │
                   └─────────────────┘
```

## Key Concepts

### Classifier with Confidence

The classifier doesn't just pick a route — it returns a confidence score and reasoning. If confidence is below the threshold (`MinConfidence`), the router falls back to a safe default route.

```csharp
var router = new PromptRouter(new RouterOptions
{
    Routes = new List<string> { "technical", "billing", "general", "escalation" },
    MinConfidence = 0.6,    // Anything below this → fallback
    FallbackRoute = "general"
});
```

### Specialized Branch Handlers

Each route has its own:
- **System prompt** — tailored for the domain
- **Response format** — structured output appropriate to the category
- **Priority** — for triage and ordering

### Graceful Degradation

- Invalid JSON from classifier → fallback route
- Unknown route name → fallback route  
- Low confidence → fallback route
- Chosen route has no registered handler → fallback handler
- All failures are logged, never crash

## Running

```bash
dotnet run --project recipes/conditional-router
```

## When to Use This Pattern

| Use Case | Why |
|----------|-----|
| Customer support triage | Different teams handle different issue types |
| Content moderation | Severity determines response (warn vs. remove vs. escalate) |
| Multi-language routing | Detect language → route to appropriate model/prompt |
| Risk assessment | Classify risk level → apply proportional review |
| Query optimization | Simple queries → fast path; complex → thorough pipeline |

## Extending

- Add **multi-label routing** (message belongs to multiple categories)
- Add **confidence calibration** (track prediction accuracy over time)
- Add **route priority queuing** (escalations always processed first)
- Add **A/B routing** (test new handlers against baseline)
- Chain with **Tool Agent Loop** for routes that need tool access

## Related Patterns

- [Multi-Perspective](../multi-perspective/) — fan-out to multiple handlers simultaneously
- [Code Review Pipeline](../code-review-pipeline/) — sequential middleware pipeline
- [Tool Agent Loop](../tool-agent-loop/) — when a branch needs to call tools
