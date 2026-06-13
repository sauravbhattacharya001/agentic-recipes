# Plan-and-Execute

**Pattern:** Decompose → Execute → Adapt (planner / executor with replanning)
**Building block:** `promptlib` prompt composition + a dependency-ordered executor

The [Tool Agent Loop](../tool-agent-loop/) reacts one step at a time — look,
decide, act, repeat. That is great for open-ended exploration, but it has no map:
it can wander, repeat work, or lose the thread on a long task. **Plan-and-Execute**
flips the order. The agent first writes a **plan** (an ordered list of concrete
steps with dependencies), then an executor runs the plan in dependency order —
**and adapts when a step fails** instead of giving up or charging blindly ahead.

## How it works

```
         ┌──────────┐      ┌───────────────────────────────────────────┐
  goal → │   PLAN   │  →   │                 EXECUTE                     │
         │ decompose│      │  topo-order steps, feed outputs downstream  │
         │ into     │      │                                             │
         │ steps +  │      │   each step that fails →  ┌──────────────┐  │
         │ deps     │      │                           │   ADAPT      │  │
         └──────────┘      │                           │ retry        │  │
                           │                           │ ↓ fallback   │  │
                           │                           │ ↓ skip       │  │
                           │                           │ ↓ abort      │  │
                           │                           └──────────────┘  │
                           └───────────────────────────────────────────┘
```

1. **Plan** — the goal is decomposed into `PlanStep`s. Each step declares the
   steps it `DependsOn` (its inputs), whether it is `Critical` to the goal, and
   optionally a primary `Run` and a `Fallback` approach. `Plan.ExecutionOrder()`
   **topologically sorts** the steps (ties broken by authoring order) so every
   step runs after the steps it depends on, and rejects cycles, duplicate ids,
   and dangling dependencies up front.
2. **Execute** — steps run in that order. Each finished step's output is placed
   in an `ExecutionContext` the downstream steps read from, so data flows along
   the dependency edges.
3. **Adapt** — see below. This is the whole point.

## The agency: adapting to failure

A reactive loop stops at the first error. A planner with no adaptation marches
on and fails three steps later for a reason no one can see. This executor does
neither — when a step fails it applies a **graduated policy**, deciding on its
own how far to escalate:

| Tier | Trigger | What the executor does |
|------|---------|------------------------|
| **Retry** | A step throws | Re-run the primary approach, up to `RetryBudget` extra times (transient errors often clear on a second try) |
| **Fallback** | Retries exhausted **and** the step has a `Fallback` | Switch to the alternate approach; if it works the step is marked **Recovered** |
| **Skip** | Out of options **and** the step is non-critical | Drop the step, **cascade-skip** anything that depended on it, and keep going |
| **Abort** | Out of options **and** the step is **critical** | Stop the run — the goal is no longer reachable, so don't burn effort on steps that can never succeed |

```csharp
// Non-critical and unrecoverable → drop it and keep going.
if (!step.Critical) { /* mark Skipped, dependents cascade-skip */ continue; }

// Critical and unrecoverable → the goal is gone.
if (_options.StopOnCriticalFailure) { /* mark Failed, emit Aborted */ break; }
```

That refusal to either quit early *or* push blindly is what makes the run
trustworthy: the agent **re-routes around closed roads but won't drive off a
cliff.**

In the demo (`Publish a launch-day blog post`):

- `make_hero_img` fails with no fallback → **skipped**, and `social_card` (which
  needed the image) **cascade-skips**.
- `seo_pass` fails its primary approach but its **fallback** succeeds → **recovered**.
- `publish` (critical) runs because both its dependencies completed → **goal reached**.

The bonus scenario shows the hard stop: a critical `build` step can't recover, so
`ship` never runs and the whole plan **aborts** rather than attempting an
impossible upload.

## How this differs from the other loop recipes

| | Plan-and-Execute | [Tool Agent Loop](../tool-agent-loop/) | [Iterative Refinement](../iterative-refinement/) |
|---|---|---|---|
| Shape | Plan up front, then execute | React step-by-step, no plan | Improve **one** artifact in a loop |
| Unit of work | Steps with dependencies | Tool calls chosen on the fly | Drafts of a single output |
| Signature move | **Retry → fallback → skip → abort** | Observe-then-decide | Critic score + plateau stop |
| Stops when | Goal reached / critical step dies | Final answer or turn budget | Target hit / budget / plateau |

## Configuration (`PlanExecutorOptions`)

| Option | Default | Meaning |
|--------|---------|---------|
| `RetryBudget` | `1` | Extra attempts at a step's primary approach after the first failure, before falling back (clamped to ≥ 0) |
| `StopOnCriticalFailure` | `true` | `true`: an unrecoverable **critical** step aborts the run. `false`: record it and keep going (best-effort mode) |
| `OnEvent` | `null` | Observability hook fired as steps start, retry, fall back, succeed, skip, or fail |

A `PlanStep` carries: `Id`, `Description`, `DependsOn`, `Critical`, an optional
`Run` (primary work), and an optional `Fallback` (alternate approach).

## Run it

```bash
dotnet run --project recipes/plan-and-execute
```

Expected (abridged):

```
Plan (6 steps, execution order shown):
  1. gather_notes   after: —                          ...
  ...
  6. publish        after: draft_post+seo_pass        Publish the post [critical]

Executing…
  ✓ gather_notes   ...
  ✓ draft_post     ...
  ↻ make_hero_img  retry 1/1 after: image service timed out
  ⤼ make_hero_img  non-critical and out of options ...; skipped
  ⇄ seo_pass       primary failed (...); recovered via fallback
  ⤼ social_card    skipped — depends on 'make_hero_img' which did not complete
  ✓ publish        ...

  Goal reached : yes ✓
  Outcome      : Completed
  Succeeded    : 3/6  (gather_notes, draft_post, publish)
  Recovered    : seo_pass  (failed first, then fell back)
  Skipped      : make_hero_img, social_card
```

## Wiring to a real model

Two seams plug into real LLM / tool calls. **First, the planner** — ask a model
to decompose the goal into steps, then build a `Plan` from its answer:

```csharp
// planJson = await model.CompleteAsync("Decompose this goal into ordered steps " +
//                                      "with dependencies, as JSON: " + goal);
var plan = new Plan(goal, ParseSteps(planJson));   // your JSON → PlanStep[] mapping
```

**Second, each step's work** — pass an async default delegate (and/or per-step
`Run` / `Fallback` delegates) that call tools or the model:

```csharp
var result = await executor.ExecuteAsync(plan, async (step, ctx, ct) =>
{
    var prompt = new PromptTemplate("""
        Goal: {{goal}}
        Task: {{task}}
        Inputs so far:
        {{inputs}}
        Do the task and return only its result.
        """)
        .Set("goal", ctx.Goal)
        .Set("task", step.Description)
        .Set("inputs", string.Join("\n", step.DependsOn.Select(d => $"- {d}: {ctx.Outputs[d]}")));

    return await model.CompleteAsync(prompt.Render(), ct);
});
```

The retry / fallback / skip / abort policy runs around whatever those delegates
do, so a flaky tool call is retried, a recoverable step falls back, and a dead
critical step stops the run — without any of that logic leaking into the step
implementations themselves.
