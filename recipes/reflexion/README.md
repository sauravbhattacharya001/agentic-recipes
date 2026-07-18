# Reflexion

**Pattern:** Attempt → Evaluate → Self-Reflect → Retry

> Based on *Reflexion: Language Agents with Verbal Reinforcement Learning* (Shinn et al., 2023).

## Overview

The Reflexion pattern is the **learn-from-failure loop**. Instead of just retrying a failed task and hoping for a different result, the agent writes a short **verbal self-reflection** about *why* it failed, stores that lesson in a persistent **episodic memory**, and is shown the accumulated lessons on every subsequent attempt. It improves across trials — with no weight updates, no fine-tuning, just language it generated and remembered.

1. **Attempt** the task from the task description plus the lessons currently in episodic memory
2. **Evaluate** the attempt → a scalar reward (0–1) and a list of concrete failures
3. **Reflect** on a failure → a one-line verbal lesson ("verify the input is sorted first") appended to episodic memory
4. **Retry** with the growing memory injected into context
5. **Stop** when the task is solved, the trial budget is spent, **or** reflection stops producing new lessons (a stuck loop)

This is the basis for self-correcting coding agents (attempt → run tests → reflect on failures → re-code), agents that learn a tool's quirks over a session, and any task where an outcome signal — not a human — drives improvement.

## Architecture

```
                    ┌─────────────────┐
                    │      Task       │
                    └────────┬────────┘
                             │
              ┌──────────────▼──────────────┐
              │           Attempt            │◄────────┐
              │  (task + episodic memory     │         │
              │   of lessons → action)       │         │ append
              └──────────────┬──────────────┘         │ lesson
                             │                          │
              ┌──────────────▼──────────────┐         │
              │           Evaluate           │         │
              │   (reward 0–1 + open issues) │         │
              └──────────────┬──────────────┘         │
                             │ fail               ┌────┴─────┐
                             ├───────────────────►│  Reflect │
                             │                    │ (verbal  │
                             │                    │  lesson) │
                             │                    └──────────┘
                   ┌─────────▼─────────┐
                   │   Stop?           │
                   │  • solved         │
                   │  • budget spent   │
                   │  • stuck (no new  │
                   │    lesson)        │
                   └─────────┬─────────┘
                             │ yes
                   ┌─────────▼─────────────────────────┐
                   │  Best action + episodic memory     │
                   └────────────────────────────────────┘
```

## Key Concepts

### Reward, not a quality score

The signal is a **task outcome** — did the attempt pass the tests / reach the goal? — expressed as a reward in `[0, 1]`. The evaluator also returns the concrete open issues so the reflector has something specific to introspect on.

```csharp
record Evaluation(double Reward, bool Succeeded, string Feedback, IReadOnlyList<string> OpenIssues);
```

### Verbal self-reflection stored in episodic memory

On failure, the reflector turns the most important open issue into a short, memorable lesson. A lesson learned at any point is never re-stored — novelty is judged against the *full* history of lessons, not the bounded window — so memory stays honest and the agent can tell when it is stuck. Every future attempt sees the current episodic memory (the most recent `MaxReflections` lessons; oldest are dropped first once the cap is reached):

```csharp
string? Reflect(string task, string action, Evaluation eval, IReadOnlyList<string> priorLessons) =>
    eval.OpenIssues.Count == 0 ? null : $"Lesson: {Distill(eval.OpenIssues[0])}";
```

This is the core difference from a plain retry: the carried state is **what I now know not to do**, not the previous attempt.

### Three autonomous stop conditions

```csharp
var agent = new ReflexionAgent(new ReflexionOptions
{
    MaxTrials = 5,            // hard ceiling on attempts
    RewardThreshold = 1.0,   // reward ≥ this counts as solved
    MaxReflections = 8,      // bound on episodic memory (oldest lesson dropped first)
    StuckPatience = 2        // give up after this many failing trials with no new lesson
});
```

- **Solved** — an attempt reached `RewardThreshold`
- **BudgetExhausted** — `MaxTrials` attempts ran without solving
- **Stuck** — reflection stopped producing a *new* lesson while still failing, so the
  loop bails out instead of repeating a mistake it can't articulate its way out of

### Returns the *best* attempt, not the last

A later trial can regress. The agent tracks the highest-reward attempt across all trials and returns that one (`BestAction` / `BestReward` / `BestTrial`).

### Injected delegates → testable and model-agnostic

The actor, evaluator, and reflector are all passed in as delegates. Tests drive the loop deterministically; production wires them to real LLM calls and a real grader (sync or async overloads are provided).

## Running

```bash
dotnet run --project recipes/reflexion
```

The demo solves a `binary_search` coding task that fails its hidden tests until reflection has taught the agent to sort-check, handle empty input, and return the leftmost duplicate — then shows the stuck-loop stop kicking in when reflection can't produce anything new.

## When to Use This Pattern

| Use Case | Why |
|----------|-----|
| Self-correcting code generation | Attempt → run tests → reflect on failures → re-code |
| Tool / API learning | Remember a tool's quirks (rate limits, arg formats) across a session |
| Multi-step task agents | Carry "what went wrong last time" into the next planning attempt |
| Game / environment agents | Verbal reinforcement when gradient RL is impractical |
| Hard reasoning problems | Let a failed solution attempt inform the next from a clean slate |

## Reflexion vs. Iterative Refinement

Both loop until "good enough," but they carry different state and react to different signals:

| | Reflexion | [Iterative Refinement](../iterative-refinement/) |
|---|---|---|
| Signal | Task **outcome / reward** (did it pass?) | Quality **score** (0–100) on one artifact |
| Carried state | A growing list of **verbal lessons** | The **previous draft** |
| Each new round | Can re-solve from scratch, armed with lessons | Edits the prior draft |
| Stuck stop | No **new lesson** to learn | Score **plateaus** |

## Extending

- Swap the rubric evaluator for a **real grader** — a test runner, a tool result, or an LLM judge
- Have an LLM generate the reflection from the full **failure trace** instead of a single issue
- Persist episodic memory **across sessions** so the agent starts each run already wiser
- Add a **cost budget** alongside the trial budget (stop when $ spent exceeds a cap)
- Weight or summarise old lessons so memory stays compact as it grows
- Combine with [Plan-and-Execute](../plan-and-execute/): reflect on a failed plan, then re-plan

## Related Patterns

- [Iterative Refinement](../iterative-refinement/) — improve one artifact via a critic score
- [Plan-and-Execute](../plan-and-execute/) — decompose → execute → adapt to step failures
- [Tool Agent Loop](../tool-agent-loop/) — observe-and-iterate with tool calls
- [Memory-Augmented Chain](../memory-augmented/) — carry working memory across conversational turns
