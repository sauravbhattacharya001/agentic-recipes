# Multi-Agent Debate

**Argue → rebut (with opponents in view) → judge → converge or decide.**

Two or more agents take opposing positions on a question and argue it out across
several rounds. Crucially, on every round each debater **sees the opponents'
latest arguments** and must rebut or revise. A neutral **judge** scores the
standings after each round, and an orchestrator decides — on its own terms —
when the debate is over.

This is the pattern behind *"Improving Factuality and Reasoning in Language
Models through Multiagent Debate"* (Du et al., 2023): letting models critique
each other tends to wash out individual mistakes and surface the stronger case.

## Why not just sample, or just ask different personas?

| Recipe | Interaction between agents? | How it resolves |
| --- | --- | --- |
| [Self-Consistency](../self-consistency) | ❌ identical samples, no contact | majority **vote** |
| [Multi-Perspective](../multi-perspective) | ❌ parallel personas, no contact | **synthesis** of all views |
| [Iterative Refinement](../iterative-refinement) | one author + one critic | critic loop on **one** artifact |
| **Multi-Agent Debate** | ✅ debaters **rebut each other** each round | **convergence** or a judged **decision** |

Debate adds the missing ingredient the others lack: the agents actually *engage*
with one another's reasoning, round after round.

## The agency: knowing when to stop

The orchestrator watches the debate and ends it as soon as continuing stops
being productive:

- **Converged** 🤝 — the debaters now agree on the same answer. There's nothing
  left to argue, so it settles immediately and returns the agreed answer (no
  "winner": it's a consensus).
- **Decided** ⚖️ — nobody concedes, but the judge has given one side a *clear,
  stable* lead (a margin held for several rounds). The orchestrator calls a
  winner instead of looping to the cap.
- **Hung** 🛑 — neither convergence nor a decisive lead emerges within the round
  budget. The orchestrator refuses to fake a verdict and reports the debate as
  hung, so a human (or a higher-authority agent) can take the call.

It therefore debates *exactly as long as the disagreement is still useful* —
short-circuiting easy agreements, pressing genuine disputes, and abstaining on
the ones with no clean answer.

## How it works

```
for each round (up to MaxRounds):
    every debater argues, seeing the full transcript so far   # rebuttals
    the judge scores each debater's latest argument in [0,1]
    accumulate judge weight per debater and per answer

    if all debaters now share one answer        -> Converged (stop)
    if the leader's margin >= DecisiveMargin
       and it has held for StableLeadRounds      -> Decided  (stop)

# budget exhausted with no resolution           -> Hung
```

`DecisiveMargin` is the leader's edge over the runner-up **as a share of their
head-to-head weight** — `(leader − runner-up) / (leader + runner-up)`. Because
it's a ratio between the two top debaters rather than a fraction of all points
awarded, a steady per-round gap produces a steady margin instead of being
diluted as the rounds (and the running total) pile up.

## Run it

```bash
dotnet run --project recipes/multi-agent-debate
```

The demo runs three debates: a factual one that **converges** (the wrong side
concedes), a trade-off the judge **decides** on a stable lead, and a values
question that ends **hung** and escalates.

## Key types

- `Debater` — a name + a delegate `(context, ct) -> DebateArgument`. Plug in a
  model-backed persona, or script it (as the demo does) for deterministic tests.
- `DebateArgument` — the move a debater makes: `Reasoning`, the `Answer` it
  supports, and a self-reported `Confidence`.
- `DebateOrchestrator` / `DebateOptions` — runs the rounds and owns the
  stop logic (`MaxRounds`, `DecisiveMargin`, `StableLeadRounds`,
  `NormalizeAnswer`, `OnExchange`).
- `DebateResult` — the outcome: `Verdict` (`Converged` / `Decided` / `Hung`),
  the settled `Answer` (null when hung), the `Winner` (when decided), the final
  `Standings`, and the full round-by-round `Transcript`.

The `judge` is a delegate `(question, argument, transcript) -> double` in
`[0, 1]`. Swap the scripted rubric for a model-graded judge in real use.

## Where this shines

- **High-stakes factual calls** where a single pass is risky — let two agents
  stress-test each other before you trust the answer.
- **Design / trade-off decisions** with a defensible "winner" — capture the
  argument *and* the adjudication, not just a verdict.
- **Routing to humans** — the `Hung` verdict is a first-class "I shouldn't
  decide this alone" signal.
