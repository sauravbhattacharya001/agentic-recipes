# Iterative Refinement

**Pattern:** Generate → Critique → Revise → Repeat

## Overview

The Iterative Refinement pattern is the **self-improvement loop**: instead of accepting a first draft, the agent grades its own work and revises it until the work clears a quality bar. It demonstrates **autonomous decision-making** — the agent decides, on its own, when the output is good enough or when further effort is no longer paying off.

1. **Generate** a draft from the task (plus any feedback gathered so far)
2. **Critique** the draft with a scoring rubric, producing a 0–100 score and a list of concrete, actionable issues
3. **Revise** by feeding the top open issue back into the next draft
4. **Stop** when the score reaches the target, the iteration budget is spent, **or** the score plateaus (diminishing returns)

This is the basis for self-editing writers, self-correcting code generators, and any workflow where a critic can judge quality and drive improvement without a human in the loop.

## Architecture

```
                    ┌─────────────────┐
                    │      Task       │
                    └────────┬────────┘
                             │
              ┌──────────────▼──────────────┐
              │          Generate           │◄────────┐
              │  (task + accumulated         │         │
              │   feedback → draft)          │         │ feedback
              └──────────────┬──────────────┘         │ (top issue)
                             │                          │
              ┌──────────────▼──────────────┐         │
              │           Critique           │         │
              │  (score 0–100 + issues)      │─────────┘
              └──────────────┬──────────────┘
                             │
                   ┌─────────▼─────────┐
                   │   Stop?           │
                   │  • target reached │
                   │  • budget spent   │
                   │  • plateaued      │
                   └─────────┬─────────┘
                             │ yes
                   ┌─────────▼─────────┐
                   │    Best Draft     │  (the peak score, not just the last)
                   └───────────────────┘
```

## Key Concepts

### Critic with a score and actionable issues

The critic doesn't just say "good" or "bad" — it returns a numeric score and an ordered list of the specific problems to fix. The most important open issue becomes the feedback that steers the next draft.

```csharp
record Critique(double Score, string Feedback, IReadOnlyList<string> Issues);
```

### Three autonomous stop conditions

```csharp
var refiner = new IterativeRefiner(new RefinerOptions
{
    TargetScore = 85,      // stop early once the critic is this happy
    MaxIterations = 5,     // hard ceiling on generate→critique rounds
    MinImprovement = 3.0,  // a round must gain at least this to count as progress
    PlateauPatience = 2    // give up after this many non-improving rounds
});
```

- **TargetReached** — a draft scored at or above `TargetScore`
- **BudgetExhausted** — `MaxIterations` rounds ran without hitting the target
- **Plateaued** — the score stopped improving by `MinImprovement` for `PlateauPatience`
  rounds in a row, so the loop bails out instead of burning calls on a score it
  is never going to reach

### Returns the *best* draft, not the last

Revisions can regress. The refiner tracks the highest-scoring draft across all rounds and returns that one (`BestDraft` / `BestScore` / `BestIteration`), so a later, worse rewrite never overwrites a good earlier one.

### Injected delegates → testable and model-agnostic

Both the generator and critic are passed in as delegates. Tests drive the loop deterministically; production wires them to real LLM calls (sync or async overloads are provided).

## Running

```bash
dotnet run --project recipes/iterative-refinement
```

The demo refines a product blurb to a target score, then shows the plateau stop kicking in when a deliberately capped generator can't reach an unreachable target.

## When to Use This Pattern

| Use Case | Why |
|----------|-----|
| Draft writing / copy editing | Self-edit until tone, length, and CTA all land |
| Code generation | Generate → run critic (lint/review) → fix → repeat |
| Summarisation | Tighten until it covers the key points within a length budget |
| Data extraction | Re-prompt until the schema validates and fields are complete |
| Answer quality gating | Don't return an answer until it clears a rubric bar |

## Extending

- Swap the rubric critic for an **LLM-as-judge** that returns JSON `{score, issues}`
- Add a **diff view** so each revision shows exactly what changed
- Add a **cost budget** alongside the iteration budget (stop when $ spent exceeds a cap)
- Feed **multiple** open issues per round instead of just the top one
- Persist the **score trail** as a quality metric for offline evaluation
- Chain with [Code Review Pipeline](../code-review-pipeline/) so the critic *is* the reviewer

## Related Patterns

- [Code Review Pipeline](../code-review-pipeline/) — analyze → review → fix in one pass
- [Conditional Router](../conditional-router/) — classify then branch to a handler
- [Tool Agent Loop](../tool-agent-loop/) — observe-and-iterate, but with tool calls
- [Multi-Perspective](../multi-perspective/) — fan-out to several critics at once
