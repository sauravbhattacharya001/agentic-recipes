# RAG Pipeline

**Pattern:** Retrieve → Augment → Generate (grounded answers over a corpus)
**Building block:** `promptlib` prompt composition + a TF-IDF retriever

A bare prompt answers from the model's own weights — it can't cite a source, and
it will happily make something up when it doesn't know. **Retrieval-Augmented
Generation** grounds every answer in a document corpus the app actually
controls, and refuses to answer when the corpus comes up empty.

## How it works

```
            ┌─────────┐   ┌──────────┐   ┌─────────┐   ┌──────────┐
 documents →│ INGEST  │ → │ RETRIEVE │ → │ AUGMENT │ → │ GENERATE │ → answer + citations
            │ chunk + │   │ TF-IDF   │   │ top-K   │   │ grounded │
            │ index   │   │ cosine   │   │ context │   │ or abstain│
            └─────────┘   └──────────┘   └─────────┘   └──────────┘
```

1. **Ingest** — each document is split into **overlapping token chunks** (a
   sliding window, so a fact never falls in the crack between two chunks) and
   indexed. Document frequencies are tracked per term for IDF weighting.
2. **Retrieve** — the question is scored against every chunk with **TF-IDF
   cosine similarity**, so rare, informative terms (*"warranty"*, *"water"*)
   dominate over common ones. The top-K chunks are returned with scores.
3. **Augment** — only the relevant chunks are carried forward as numbered,
   quotable context (see `BuildContextBlock`). A real prompt instructs the model
   to answer *only* from this context and to cite chunk numbers.
4. **Generate** — the answer is composed with inline `[1] [2]` citations back to
   the chunks it used.

## The agency: knowing when *not* to answer

The interesting decision is **abstention**. Before generating, the pipeline
checks whether the best chunk clears `MinRelevance`:

```csharp
if (retrieved.Count == 0 || topScore < Options.MinRelevance)
    return new RagAnswer("I don't have enough information to answer that.",
                         Abstained: true, ...);
```

`"I don't have enough information"` is a **first-class outcome, not a failure**.
That refusal is what makes a grounded agent trustworthy — it decides on its own
whether the knowledge base actually supports an answer instead of hallucinating
a confident guess.

In the demo, four questions are answered with citations and one
(*"What is your CEO's favourite colour?"*) is refused, because the answer simply
isn't in the corpus.

## How this differs from the Memory-Augmented Chain

| | RAG Pipeline | [Memory-Augmented Chain](../memory-augmented/) |
|---|---|---|
| Knowledge source | Fixed document corpus | Conversational facts learned over turns |
| Scope | Single turn, stateless | Multi-turn, accumulating |
| Retrieval | TF-IDF cosine over chunks | Relevance + recency + salience |
| Signature move | **Citations + abstention** | Salience decay + eviction |

## Configuration (`RagOptions`)

| Option | Default | Meaning |
|--------|---------|---------|
| `ChunkSize` | `24` | Target tokens per chunk |
| `ChunkOverlap` | `6` | Tokens shared between adjacent chunks (clamped to `[0, ChunkSize-1]`) |
| `TopK` | `3` | Max chunks retrieved and carried into the prompt |
| `MinRelevance` | `0.08` | Cosine floor; below it the pipeline abstains |

## Run it

```bash
dotnet run --project recipes/rag-pipeline
```

Expected (abridged):

```
Indexed 4 documents into N chunks.

Q: Does the warranty cover water damage?
A: The warranty does not cover accidental or water damage. [1]
   Sources:
     [1] warranty#... (score 0.4xx)

Q: What is your CEO's favourite colour?
A: I don't have enough information to answer that.
   (best relevance 0.0xx < floor 0.080 — refused)
```

## Wiring to a real model

Replace the deterministic `Generate` delegate with an LLM call. Build the prompt
from the retrieved context and instruct the model to stay grounded:

```csharp
var answer = await rag.AskAsync(question, async (q, context, ct) =>
{
    var prompt = new PromptTemplate("""
        Answer the question using ONLY the context below.
        Cite sources as [n]. If the context does not contain the answer,
        say you don't have enough information.

        Context:
        {{context}}

        Question: {{question}}
        """)
        .Set("context", RagPipeline.BuildContextBlock(context))
        .Set("question", q);

    return await model.CompleteAsync(prompt.Render(), ct);
});
```

The abstention guard still runs *before* the model is ever called, so you never
pay for — or risk a hallucination from — a question your corpus can't support.
