# Self-Consistency / Ensemble Voting

**Pattern:** Sample N → Vote → Decide (fan-out + aggregation)
**Building block:** `promptlib` prompt composition + a confidence-aware vote aggregator

A single model call is one roll of the dice. Greedy decoding commits to the
first plausible chain of thought, and if that chain takes a wrong turn early, the
whole answer is wrong — confidently, with no signal that anything went sideways.
**Self-Consistency** (Wang et al., 2022) trades a little extra compute for a lot
of robustness: ask the *same* question several independent times, let each run
reason on its own, then take a **vote** over the final answers. The majority
answer is returned — and, just as importantly, **how strongly the runs agreed**
becomes an honest confidence score.

## How it works

```
                         ┌── path 1 → reason → "9"  (0.86) ─┐
                         │                                   │
            ┌─────────┐  ├── path 2 → reason → "9"  (0.74) ─┤   ┌──────────────┐
   question ┤ FAN-OUT ├──┼── path 3 → reason → "9"  (0.91) ─┼──▶│     VOTE     │
            │ sample  │  ├── path 4 → reason → "9"  (0.80) ─┤   │ normalize +  │
            │   ×N    │  └── path 5 → reason → "8"  (0.55) ─┘   │ tally + rank │
            └─────────┘                                         └──────┬───────┘
                                                                       │
                                       ┌───────────────────────────────▼─────────┐
                                       │                 DECIDE                    │
                                       │  consensus ≥ confident  → return answer   │
                                       │  consensus ≥ min        → tentative       │
                                       │  consensus <  min       → ABSTAIN, escalate│
                                       └───────────────────────────────────────────┘
```

1. **Sample** — the same prompt is drawn `N` times. With a real model you'd raise
   the temperature so each path explores independently; here a `sampler(index)`
   delegate stands in so the recipe is deterministic and testable.
2. **Vote** — each path's `Answer` is **normalized** (so `"4"`, `"Four"`, and
   `"four."` count as the same vote) and bucketed. Buckets are ranked by vote
   mass, ties broken by first appearance, so the result is fully deterministic.
3. **Decide** — the winner's share of the total vote is the **consensus**. Two
   thresholds turn that number into a verdict (below).

## The agency: knowing when *not* to answer

A naïve majority vote always returns *something* — even a 2-vs-2 coin flip comes
back wearing a confident face. The agency in this recipe is that the ensemble
**reads its own consensus and refuses to fake certainty.** A winning answer that
only barely edged out the field is reported as **Tentative**; a genuinely split
ensemble **Abstains** and hands the question off rather than guessing.

| Verdict | Trigger | What it means for the caller |
|---------|---------|------------------------------|
| **Confident** | consensus ≥ `ConfidentConsensus` (default 2/3) | The runs strongly agree — use the answer. |
| **Tentative** | `MinConsensus` ≤ consensus < `ConfidentConsensus` | A plurality winner, but weak — use with a second check. |
| **Abstained** | consensus < `MinConsensus` (default 0.40) | Too divided to trust — `Answer` is `null`; escalate to a human, a tie-breaker model, or more samples. |

```csharp
var verdict = consensus >= _options.ConfidentConsensus ? EnsembleVerdict.Confident
            : consensus >= _options.MinConsensus       ? EnsembleVerdict.Tentative
            :                                             EnsembleVerdict.Abstained;
```

That abstention is the whole point: an ensemble that says *"I'm split, don't
trust me here"* is far more useful in an autonomous pipeline than one that always
sounds sure. **It would rather flag the hard cases than launder a guess as a
fact.**

### Conviction vs. headcount (weighted voting)

By default every path casts one equal vote. Flip `WeightByConfidence = true` and
each vote is scaled by that path's self-reported confidence — so five wishy-washy
votes can lose to two emphatic ones. In the demo's Q3, a bare head-count would
crown `"B"` (3 vs 2), but the two `"A"` paths are near-certain while the `"B"`
bloc is hedging, so weighting lets **conviction outvote raw count.**

## How this differs from the other recipes

| | Self-Consistency | [Multi-Perspective](../multi-perspective/) | [Iterative Refinement](../iterative-refinement/) |
|---|---|---|---|
| Inputs | **Same** prompt sampled `N`× | **Different** persona prompts | One prompt, improved over time |
| Aim | Outvote a wrong reasoning path | Combine complementary viewpoints | Polish a single artifact |
| Aggregation | **Majority vote + consensus** | Synthesis into one narrative | Critic score + plateau stop |
| Signature move | **Abstain when split** | Merge distinct angles | Revise until good enough |

Multi-Perspective wants *diversity of view* and keeps all of it; Self-Consistency
wants *agreement* and uses disagreement as a stop signal.

## Configuration (`EnsembleOptions`)

| Option | Default | Meaning |
|--------|---------|---------|
| `ConfidentConsensus` | `0.66` | Consensus ratio at/above which the verdict is `Confident`. |
| `MinConsensus` | `0.40` | Consensus ratio below which the ensemble `Abstains`. |
| `WeightByConfidence` | `false` | Weight each vote by the sample's confidence instead of 1-per-sample. |
| `NormalizeAnswer` | trim + lowercase | Canonicalizes answers so equivalent spellings vote together. |
| `OnSample` | `null` | Observability hook fired as each sample completes: `(index, sample)`. |

A `ReasoningSample` carries the path's `Reasoning` (for transparency), its final
`Answer`, and a `Confidence` in `[0, 1]`.

## Run it

```bash
dotnet run --project recipes/self-consistency
```

Expected (abridged):

```
Q1: "A farmer has 17 sheep. All but 9 run away. How many remain?"
  🎲 path 1: answer=9    conf=86%  ⟨'all but 9 run away' means 9 stay behind⟩
  ...
  🎲 path 5: answer=8    conf=55%  ⟨17 - 9 = 8 run away so 8 remain⟩

  ✅ Verdict : CONFIDENT
     Answer  : 9
     Consensus: 80%  (4/5 weight, 5 paths)
     Tally   : 9=4  8=1

Q2: "Is a hot dog a sandwich?" (intentionally divisive)
  ...
  🛑 Verdict : ABSTAINED
     Answer  : — (abstained, needs escalation)
     Consensus: 50%  (2/4 weight, 4 paths)
     Tally   : no=2  yes=2

Q3: "Which algorithm is asymptotically faster?"  (weighted vote)
  ...
  ✅ Verdict : CONFIDENT
     Answer  : A
     ...
  (head-count winner would have been 'B' with 3/5 — weighting chose conviction over count)
```

## Wiring to a real model

The one seam is the `sampler` delegate. Point it at a real model with a non-zero
temperature so every draw is an independent reasoning path, and parse each
completion into a `ReasoningSample`:

```csharp
var voter = new EnsembleVoter(new EnsembleOptions
{
    ConfidentConsensus = 0.66,
    MinConsensus = 0.40,
    NormalizeAnswer = a => a.Trim().TrimEnd('.').ToLowerInvariant(),
});

var prompt = new PromptTemplate("""
    Solve step by step, then end with a line:  ANSWER: <value>
    Question: {{q}}
    """, new Dictionary<string, string>()).Render(new Dictionary<string, string> { ["q"] = question });

var result = await voter.RunAsync(samples: 5, async (i, ct) =>
{
    // Independent draw — raise temperature so paths diverge.
    var text = await model.CompleteAsync(prompt, temperature: 0.8, ct);
    var (reasoning, answer) = SplitOnAnswerLine(text);   // your parser
    return new ReasoningSample(reasoning, answer, ConfidenceOf(text));
});

if (result.Verdict == EnsembleVerdict.Abstained)
    await EscalateAsync(question, result);   // don't ship a coin-flip
else
    Use(result.Answer);
```

Because the vote and the abstention policy live entirely in the aggregator, the
sampling delegate stays a dumb "call the model once" — all the
robustness-through-redundancy logic sits in one reusable place.
