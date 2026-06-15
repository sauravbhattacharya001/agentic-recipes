# Tree-of-Thoughts

**Pattern:** Expand → Evaluate → Search (branch, score, prune, backtrack)
**Building block:** `promptlib` prompt composition + a best-first frontier search with a beam

A single chain of thought is one path through the space of reasoning. It commits
to the first step, then the next, and if an early step was a wrong turn the whole
chain inherits the mistake — there is no way back. **Tree-of-Thoughts** (Yao et
al., 2023) reframes reasoning as **deliberate search**: from a partial solution
(a "thought") the agent proposes several candidate next steps, an evaluator
**scores how promising each resulting state looks**, and the search keeps only the
best handful (the **beam**) to grow further. Weak branches are pruned; when the
most promising branch dead-ends, the frontier hands the lead to the next-best
shelved node — the search **backtracks** instead of marching a doomed path to the
end.

## How it works

```
                 root ""  (score 0.18)
                   │  EXPAND
          ┌────────┼────────┐
       +9 →9     +5 →5     *2 →0           ← EVALUATE each candidate state
      (0.48)    (0.35)    (0.18)
        │  keep top-2 (BEAM) ─ drop *2 →0
        │  EXPAND best (+9 →9)
   ┌────┼────┬────┐
 +9→18 +5→14 *2→18 -1→8
 (0.78)(0.65)(0.78)(0.45)
   │  keep top-2 ─ both 18s
   │  EXPAND an 18
 ┌─┴──┐
+9→27 +5→23
(0.82)(1.00) ★ SOLVED — stop
```

1. **Expand** — `expand(thought, depth)` proposes candidate next thoughts. With a
   real model this is "given the work so far, suggest k next steps"; here a
   deterministic delegate appends an arithmetic move so the search is reproducible.
2. **Evaluate** — `evaluate(state, depth)` returns a **score in `[0, 1]`** and a
   `Solved` flag. This is the value function that decides which states are worth
   the agent's limited expansion budget.
3. **Search** — the frontier is re-ranked after **every** expansion. Best-first
   always grows the highest-scoring open node; the **beam** caps how many open
   nodes survive so the tree stays bounded; states below `PruneThreshold` are
   dropped outright.

## The agency: look-ahead with pruning and backtracking

A greedy chain takes the locally-best next token and never reconsiders. The
agency in this recipe is that the agent **spends compute where the value function
says the payoff is highest, abandons branches that score below a floor, and
reconsiders** — because the frontier is re-ranked globally, a branch that stalls
below its siblings is quietly overtaken by a previously-shelved node. That is
backtracking that falls out of the data structure, not a special case.

And it **stops on its own**, reporting *why*:

| Outcome | Trigger | Meaning for the caller |
|---------|---------|------------------------|
| **Solved** | a state hit `SolvedThreshold` (or was flagged solved) | The search found an answer — use `SolutionPath`. |
| **FrontierExhausted** | the frontier emptied (everything pruned/dead-ended) | No reachable state survived; widen the beam or lower the prune floor. |
| **DepthLimited** | every open branch hit `MaxDepth` first | The answer (if any) is deeper than allowed; raise `MaxDepth`. |
| **BudgetExhausted** | `MaxExpansions` was spent | Ran out of compute; raise the budget or sharpen the evaluator. |

Even when it does **not** solve, the agent returns the **best partial state it
found** (`BestState` / `BestScore`) with the reasoning steps that reached it — so
a bounded search still yields its most promising lead rather than nothing.

## How this differs from the other recipes

| | Tree-of-Thoughts | [Self-Consistency](../self-consistency/) | [Reflexion](../reflexion/) | [Plan-and-Execute](../plan-and-execute/) |
|---|---|---|---|---|
| Shape | **Tree** searched with a beam | Flat fan-out of N samples | Linear retry loop | One ordered plan |
| State | **Partial** solutions, grown step by step | Whole independent answers | Whole task, re-attempted | Whole task, decomposed once |
| Selection | **Score states, keep the beam** | Majority vote over answers | Carry verbal lessons forward | Follow the dependency order |
| Signature move | **Prune + backtrack** | Abstain when split | Self-reflect after failure | Commit then execute |

Self-Consistency samples the *same* question many times and never branches;
Reflexion re-runs the *whole* task and learns in words; Plan-and-Execute commits
to a plan up front. Tree-of-Thoughts is the only one that **searches a tree of
partial solutions** — comparing rival half-built answers and expanding the
winners.

## Configuration (`TreeOfThoughtsOptions`)

| Option | Default | Meaning |
|--------|---------|---------|
| `BeamWidth` | `3` | How many of the best open nodes survive after each expansion. |
| `MaxDepth` | `4` | Deepest reasoning level explored (root = depth 0). |
| `MaxExpansions` | `30` | Hard ceiling on nodes expanded — the compute budget. |
| `SolvedThreshold` | `1.0` | Score at/above which a state counts as solved. |
| `PruneThreshold` | `0.0` | Candidate states scoring below this are dropped, never enqueued. |
| `Strategy` | `BestFirst` | `BestFirst` (re-rank by score) or `BreadthFirst` (FIFO per level). |
| `OnNode` | `null` | Observability hook fired once per scored node, including the root. |

A `ThoughtExpansion` carries the `Step` (a short reasoning move) and the resulting
`State`; a `ThoughtEvaluation` carries the `Score`, the `Solved` flag, and a
one-line `Rationale` for transparency.

## Run it

```bash
dotnet run --project recipes/tree-of-thoughts
```

Expected (abridged):

```
Goal: build an expression that reaches 23 (moves: +9 +5 *2 -1)

Searching the thought tree (best-first, beam width 2)...

  d0 [0.18]
    d1 [0.48] 0 +9 = 9
    d1 [0.35] 0 +5 = 5
    d1 [0.18] 0 *2 = 0
      d2 [0.78] 0 +9 = 9 +9 = 18
      d2 [0.78] 0 +9 = 9 *2 = 18
      ...
        d3 [1.00] 0 +9 = 9 +9 = 18 +5 = 23  * SOLVED

  Outcome        : Solved
  Best score     : 1.00
  Nodes expanded : 3      (only 3 of the 25-node budget)

== WINNING PATH (root -> solution) ==
  1. apply +9 -> 9
  2. apply +9 -> 18
  3. apply +5 -> 23

  Bonus: budget-bounded search (a faraway target)
  Outcome        : BudgetExhausted
  Nodes expanded : 10 (budget 10)
```

The headline run **solves** the puzzle after expanding just 3 nodes — it never
needed its full budget because the value function steered it straight to the
goal. The bonus run chases a target far beyond what its small budget can build
and **stops itself at the budget** instead of searching forever.

## Wiring to a real model

The two seams are the `expand` and `evaluate` delegates. Point the expander at a
model that proposes k next steps and the evaluator at a model (or a tool/verifier)
that scores a partial solution:

```csharp
var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
{
    BeamWidth = 3,
    MaxDepth = 6,
    MaxExpansions = 40,
    SolvedThreshold = 0.9,
    PruneThreshold = 0.2,
});

var result = await agent.SearchAsync(
    rootThought: problem,
    expand: async (thought, depth, ct) =>
    {
        // Ask the model for a few candidate NEXT steps given the work so far.
        var prompt = new PromptTemplate("""
            Problem: {{p}}
            Work so far:
            {{w}}
            Propose {{k}} distinct next steps. One per line.
            """).Set("p", problem).Set("w", thought).Set("k", "3").Render();

        var lines = await model.CompleteLinesAsync(prompt, temperature: 0.7, ct);
        return lines.Select(step => new ThoughtExpansion(step, thought + "\n" + step)).ToList();
    },
    evaluate: async (state, depth, ct) =>
    {
        // Ask the model (or a verifier) to score this partial solution in [0, 1].
        var (score, solved) = await ScoreStateAsync(problem, state, ct);
        return new ThoughtEvaluation(score, solved, "model value estimate");
    });

if (result.Solved)
    Use(result.SolutionPath, result.BestState);
else
    Escalate(result.Outcome, result.BestState);   // best partial lead + why it stopped
```

Because the beam, the pruning floor, the budget, and the backtracking all live in
the search, the model only ever does two dumb things — *propose next steps* and
*score a state* — while the deliberate, bounded **search** sits in one reusable
place.
