# Memory-Augmented Chain

**Pattern:** Context accumulation across turns — `retrieve → augment → generate → remember`

A plain chain forgets everything between calls. This recipe gives an agent a
**self-managing working memory** so a conversation actually *accumulates*
context: the agent recalls what it has been told, uses it, and stores anything
new — without growing memory unbounded or re-asking facts it already knows.

```
                    ┌───────────────────────────────────────┐
   user input  ───▶ │ 1. RETRIEVE  rank stored memories by    │
                    │              relevance + recency +      │
                    │              salience, take top-K       │
                    └───────────────┬───────────────────────┘
                                    │ recalled facts
                    ┌───────────────▼───────────────────────┐
                    │ 2. AUGMENT   inject recalled facts into │
                    │              the prompt as context      │
                    └───────────────┬───────────────────────┘
                                    │
                    ┌───────────────▼───────────────────────┐
                    │ 3. GENERATE  responder produces a reply │
                    │              + any new facts to keep     │
                    └───────────────┬───────────────────────┘
                                    │
                    ┌───────────────▼───────────────────────┐
                    │ 4. REMEMBER  write new facts (reinforce │
                    │              near-duplicates), refresh   │
                    │              recalled ones, decay all,   │
                    │              evict the weakest over      │
                    │              budget                      │
                    └─────────────────────────────────────────┘
```

## Why this is "agentic"

This is the **learning / adaptation** flavour of agency. The agent gets better
over a conversation because it *remembers*, and it manages that memory on its
own:

- **Relevance-gated recall** — it surfaces only memories that actually relate to
  the current turn (token overlap against fact text *and* tags), so the prompt
  stays focused.
- **Recency + salience ranking** — important and recently-useful facts rank
  higher; using a memory refreshes it.
- **Salience decay** — every fact loses a little importance each turn, so stale
  context fades instead of lingering forever.
- **Duplicate reinforcement** — telling the agent something it already knows
  bumps the existing memory instead of storing a redundant copy.
- **Budget-bounded eviction** — when the store exceeds `MaxItems`, the
  lowest-value memories are dropped automatically. Memory never grows without
  limit.

No one tells the agent what to keep — it decides from relevance, recency, and
salience.

## Run it

```bash
dotnet run --project recipes/memory-augmented
```

The demo walks a 7-turn trip-planning conversation. The traveler mentions their
destination, diet, and budget in turns 1–3; at turn 4 ("Where should I eat
dinner?") the agent **recalls the vegetarian + budget facts from memory** — the
ones whose tags overlap the dining query — and tailors its answer, and it still
has them at turn 7, all without re-asking. (The destination fact stays in memory
but isn't recalled for a dining question: keyword recall surfaces only what's
relevant to *this* turn, which is exactly the focus the pattern is meant to give.)

## When to use it

| Use it when… | Reach for something else when… |
|---|---|
| A multi-turn conversation needs to carry context forward | A single stateless call is enough → plain `PromptTemplate` |
| You want bounded memory, not an ever-growing transcript | You need exhaustive recall over huge corpora → a vector DB / RAG pipeline |
| Relevance can be judged by keyword/tag overlap | Semantic recall across paraphrases is critical → embed + similarity search |
| You want the agent to forget stale facts on its own | Every fact must be retained verbatim forever → an append-only log |

The retrieval here is deliberately **dependency-free** (token/tag overlap), which
keeps the recipe runnable offline and deterministic. The shape is identical to a
production memory layer — swap `Retrieve` for an embedding-similarity search and
the rest of the loop is unchanged.

## Extending

- **Real model:** replace the injected `Respond` delegate with a call to your
  chat endpoint. Give it the recalled memories in the system/context block, and
  have it return a reply plus the facts worth keeping (`TurnResult`).
- **Semantic recall:** swap the `Jaccard` relevance for cosine similarity over
  embeddings; everything else (recency, salience, decay, eviction) stays.
- **Tunable forgetting:** adjust `DecayPerTurn`, `MaxItems`, and the
  recall weights (`RelevanceWeight` / `RecencyWeight` / `SalienceWeight`) to
  trade focus against long-term retention.
- **Pinned facts:** seed high-salience memories with `DecayPerTurn` effectively
  disabled for them (e.g. clamp their salience floor) to keep durable
  preferences from ever being evicted.

## Key types

| Type | Role |
|---|---|
| `MemoryAugmentedAgent` | Orchestrates the retrieve→augment→generate→remember loop |
| `MemoryOptions` | Budget, top-K, recall weights, decay, duplicate threshold |
| `MemoryItem` | A stored fact + salience/recency bookkeeping |
| `NewFact` / `TurnResult` | What the responder returns each turn |
| `MemoryTurn` | Per-turn observability record (recalled / written / reinforced / evicted) |
