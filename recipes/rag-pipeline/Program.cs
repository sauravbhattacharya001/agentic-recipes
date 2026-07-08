using Prompt;
using System.Text;

// ──────────────────────────────────────────────────────────────
// RAG Pipeline Recipe
// Pattern: Retrieve → Augment → Generate (grounded answers over a corpus)
//
// A bare prompt answers from the model's own weights — it can't cite a
// source and it will happily make something up when it doesn't know.
// Retrieval-Augmented Generation fixes that by grounding every answer
// in a document corpus the app actually controls:
//
//   INGEST   split each document into overlapping chunks and index them
//   RETRIEVE rank chunks against the question with TF-IDF cosine
//            similarity (rare, informative terms dominate the score)
//   AUGMENT  build a prompt that carries only the top-K chunks as
//            numbered, quotable context
//   GENERATE compose an answer that cites the chunks it used — and,
//            crucially, ABSTAINS when nothing clears the relevance
//            floor instead of hallucinating
//
// The agency here is epistemic: the pipeline decides, on its own,
// whether it knows enough to answer. "I don't have enough information"
// is a first-class outcome, not a failure — that refusal is what makes
// a grounded agent trustworthy.
//
// Unlike the Memory-Augmented Chain (which accumulates conversational
// facts across turns), RAG retrieves from a fixed knowledge base on a
// single turn and always attributes its answer back to the sources.
// ──────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;

// 1. A tiny knowledge base. In a real recipe these are docs/wiki pages/
//    support articles; here we keep a handful inline so retrieval is easy
//    to follow offline.
var corpus = new[]
{
    new Document("returns", """
        Our return policy allows returns within 30 days of delivery for a full refund.
        Items must be unused and in original packaging. Final-sale items cannot be returned.
        To start a return, open the Orders page and select "Return or replace items".
        """),
    new Document("shipping", """
        Standard shipping takes 3 to 5 business days and is free on orders over fifty dollars.
        Express shipping arrives in 1 to 2 business days for an extra charge.
        We currently ship to the United States and Canada only.
        """),
    new Document("warranty", """
        All electronics include a one-year limited warranty covering manufacturing defects.
        The warranty does not cover accidental or water damage.
        Warranty claims require the original order number as proof of purchase.
        """),
    new Document("accounts", """
        You can reset your password from the sign-in page using the "Forgot password" link.
        Two-factor authentication can be enabled under Security settings.
        Deleting your account is permanent and erases your order history.
        """),
};

// 2. Configure the pipeline. The MinRelevance floor is the abstention
//    dial: questions whose best chunk scores below it get "I don't know"
//    rather than a guessed answer.
var rag = new RagPipeline(new RagOptions
{
    ChunkSize = 24,          // ~tokens per chunk
    ChunkOverlap = 6,        // sliding-window overlap so facts aren't split
    TopK = 3,                // chunks carried into the prompt
    MinRelevance = 0.08,     // below this → abstain
});

rag.Ingest(corpus);
Console.WriteLine($"Indexed {corpus.Length} documents into {rag.ChunkCount} chunks.\n");

// 3. The grounded generator. In production this is an LLM call that
//    receives the augmented prompt and is instructed to answer ONLY from
//    the provided context and to cite chunk numbers. Here we compose a
//    deterministic extractive answer so the retrieval machinery is the
//    star of the show offline.
string Generate(string question, IReadOnlyList<RetrievedChunk> context)
{
    // The pipeline already decided this is answerable (context non-empty).
    // Stitch the most relevant sentences together with citations, skipping
    // fragments already covered by a higher-ranked chunk (overlap artifacts).
    var sentences = new List<string>();
    foreach (var c in context)
    {
        var best = RagPipeline.BestSentence(question, c.Chunk.Text);
        if (best is null) continue;
        var alreadyCovered = sentences.Any(s =>
            s.Contains(best, StringComparison.OrdinalIgnoreCase) ||
            best.Contains(s, StringComparison.OrdinalIgnoreCase));
        if (alreadyCovered) continue;
        sentences.Add($"{best} [{c.Citation}]");
    }
    return sentences.Count > 0
        ? string.Join(" ", sentences)
        : "I don't have enough information to answer that.";
}

// 4. Ask the corpus a series of questions — including one it should
//    refuse, because the answer simply isn't in the knowledge base.
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  RAG Pipeline Recipe");
Console.WriteLine("  (retrieve → augment → generate, with citations)");
Console.WriteLine("═══════════════════════════════════════════════════════\n");

var questions = new[]
{
    "How long do I have to return something?",
    "Is shipping free and where do you ship to?",
    "Does the warranty cover water damage?",
    "How do I turn on two-factor authentication?",
    "What is your CEO's favourite colour?",   // ← not in the corpus → abstain
};

foreach (var question in questions)
{
    var answer = await rag.AskAsync(question, Generate);

    Console.WriteLine($"Q: {question}");
    if (answer.Abstained)
    {
        Console.WriteLine($"A: {answer.Text}");
        Console.WriteLine($"   (best relevance {answer.TopScore:F3} < floor {rag.Options.MinRelevance:F3} — refused)\n");
        continue;
    }

    Console.WriteLine($"A: {answer.Text}");
    Console.WriteLine("   Sources:");
    foreach (var c in answer.Context)
        Console.WriteLine($"     [{c.Citation}] {c.Chunk.DocumentId}#{c.Chunk.Index} (score {c.Score:F3})");
    Console.WriteLine();
}

Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine("Pattern: retrieval grounds generation in a controlled corpus,");
Console.WriteLine("answers cite their sources, and the pipeline abstains when the");
Console.WriteLine("knowledge base doesn't contain the answer — no hallucination.");

// ── Supporting types ────────────────────────────────────────

/// <summary>A source document to be ingested into the corpus.</summary>
/// <param name="Id">Stable identifier used in citations.</param>
/// <param name="Text">Raw document body; split into chunks on ingest.</param>
record Document(string Id, string Text);

/// <summary>One indexed slice of a document plus its term-frequency vector.</summary>
/// <param name="DocumentId">The id of the source document this slice came from.</param>
/// <param name="Index">This slice's 0-based ordinal <b>within its own document</b>, so a
/// citation like <c>warranty#0</c> names the first chunk of the warranty doc. (It is not a
/// corpus-global counter — that would make <c>warranty#4</c> point past a two-chunk document.)</param>
record Chunk(string DocumentId, int Index, string Text, IReadOnlyDictionary<string, int> TermFreq);

/// <summary>A chunk retrieved for a query, with its similarity score and citation number.</summary>
record RetrievedChunk(Chunk Chunk, double Score, int Citation);

/// <summary>The outcome of one RAG turn.</summary>
/// <param name="Text">The grounded answer, or an abstention message.</param>
/// <param name="Abstained">True when nothing cleared the relevance floor.</param>
/// <param name="TopScore">The best chunk score seen (0 when the corpus is empty).</param>
/// <param name="Context">The chunks carried into generation (empty when abstained).</param>
record RagAnswer(string Text, bool Abstained, double TopScore, IReadOnlyList<RetrievedChunk> Context);

/// <summary>Configuration for <see cref="RagPipeline"/>.</summary>
record RagOptions
{
    /// <summary>Target number of tokens per chunk. At least 1.</summary>
    public int ChunkSize { get; init; } = 24;
    /// <summary>Tokens shared between adjacent chunks so facts aren't split. Clamped to [0, ChunkSize-1].</summary>
    public int ChunkOverlap { get; init; } = 6;
    /// <summary>Maximum chunks retrieved and carried into the prompt. At least 0.</summary>
    public int TopK { get; init; } = 3;
    /// <summary>Cosine similarity at/above which a chunk is considered relevant; below it the pipeline abstains.</summary>
    public double MinRelevance { get; init; } = 0.08;
}

/// <summary>
/// A retrieval-augmented generation pipeline over an in-memory corpus.
/// Documents are chunked and indexed on <see cref="Ingest"/>; each
/// <see cref="AskAsync"/> ranks chunks with TF-IDF cosine similarity,
/// carries the top-K into an injected generator, and abstains when the
/// best score is below <see cref="RagOptions.MinRelevance"/>.
///
/// The generator is an injected delegate so the loop runs deterministically
/// in tests and wires to real LLM calls in production.
/// </summary>
class RagPipeline
{
    private readonly List<Chunk> _chunks = new();
    private readonly Dictionary<string, int> _docFreq = new(StringComparer.Ordinal); // term → #chunks containing it

    public RagOptions Options { get; }

    /// <summary>Number of indexed chunks.</summary>
    public int ChunkCount => _chunks.Count;

    /// <summary>The indexed chunks (read-only view).</summary>
    public IReadOnlyList<Chunk> Chunks => _chunks.AsReadOnly();

    public RagPipeline(RagOptions options) => Options = options;

    /// <summary>Chunk and index a batch of documents. Can be called more than once to grow the corpus.</summary>
    public void Ingest(IEnumerable<Document> documents)
    {
        foreach (var doc in documents ?? Array.Empty<Document>())
        {
            if (doc is null || string.IsNullOrWhiteSpace(doc.Text)) continue;
            var words = SplitWords(doc.Text);
            if (words.Count == 0) continue;

            // Number chunks per-document so a citation's "#N" is the slice's position
            // within THIS document, not its slot in the global corpus.
            var indexInDoc = 0;
            foreach (var (text, termFreq) in ChunkWords(words))
            {
                if (termFreq.Count == 0) continue; // skip chunks that are all stop-words
                var chunk = new Chunk(doc.Id, indexInDoc++, text, termFreq);
                _chunks.Add(chunk);
                foreach (var term in termFreq.Keys)
                    _docFreq[term] = _docFreq.GetValueOrDefault(term) + 1;
            }
        }
    }

    /// <summary>Retrieve the top-K chunks for a query, ranked by TF-IDF cosine similarity.</summary>
    public IReadOnlyList<RetrievedChunk> Retrieve(string query)
    {
        var topK = Math.Max(0, Options.TopK);
        if (topK == 0 || _chunks.Count == 0) return Array.Empty<RetrievedChunk>();

        var queryTf = TermFrequencies(Tokenize(query));
        if (queryTf.Count == 0) return Array.Empty<RetrievedChunk>();
        var queryVec = TfIdf(queryTf);

        var ranked = _chunks
            .Select(c => new { Chunk = c, Score = Cosine(queryVec, TfIdf(c.TermFreq)) })
            .Where(x => x.Score > 0)
            // Deterministic tie-break: score desc, then document id, then chunk index.
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.DocumentId, StringComparer.Ordinal)
            .ThenBy(x => x.Chunk.Index)
            .Take(topK)
            .Select((x, i) => new RetrievedChunk(x.Chunk, x.Score, i + 1))
            .ToList();

        return ranked;
    }

    /// <summary>Run one RAG turn with a synchronous generator.</summary>
    public Task<RagAnswer> AskAsync(
        string question,
        Func<string, IReadOnlyList<RetrievedChunk>, string> generate,
        CancellationToken ct = default)
        => AskAsync(question, (q, ctx, _) => Task.FromResult(generate(q, ctx)), ct);

    /// <summary>Run one RAG turn with an async generator (e.g. a real model call).</summary>
    public async Task<RagAnswer> AskAsync(
        string question,
        Func<string, IReadOnlyList<RetrievedChunk>, CancellationToken, Task<string>> generate,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // ── RETRIEVE ──
        var retrieved = Retrieve(question);
        var topScore = retrieved.Count > 0 ? retrieved[0].Score : 0.0;

        // ── DECIDE: abstain if nothing clears the floor. ──
        if (retrieved.Count == 0 || topScore < Options.MinRelevance)
            return new RagAnswer(
                "I don't have enough information to answer that.",
                Abstained: true,
                TopScore: topScore,
                Context: Array.Empty<RetrievedChunk>());

        // ── AUGMENT + GENERATE: only relevant chunks are carried forward. ──
        var context = retrieved.Where(r => r.Score >= Options.MinRelevance).ToList();
        var renumbered = context.Select((r, i) => r with { Citation = i + 1 }).ToList();
        var text = await generate(question, renumbered, ct);

        return new RagAnswer(text, Abstained: false, TopScore: topScore, Context: renumbered);
    }

    /// <summary>Build the augmented context block a real LLM prompt would carry. Exposed for inspection/tests.</summary>
    public static string BuildContextBlock(IReadOnlyList<RetrievedChunk> context)
    {
        var sb = new StringBuilder();
        foreach (var c in context)
            sb.AppendLine($"[{c.Citation}] ({c.Chunk.DocumentId}) {c.Chunk.Text}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Pick the sentence from a chunk most relevant to the question (extractive answer helper).</summary>
    public static string? BestSentence(string question, string chunkText)
    {
        var q = TermFrequencies(Tokenize(question));
        if (q.Count == 0) return null;

        string? best = null;
        var bestScore = 0;
        foreach (var raw in SplitSentences(chunkText))
        {
            var sentence = raw.Trim();
            if (sentence.Length == 0) continue;
            var overlap = Tokenize(sentence).Count(t => q.ContainsKey(t));
            if (overlap > bestScore)
            {
                bestScore = overlap;
                best = sentence;
            }
        }
        return bestScore > 0 ? best : null;
    }

    // ── Chunking ─────────────────────────────────────────────

    // Chunk over the ORIGINAL words (so chunk text stays human-readable for
    // display + extractive answers), and build the term-frequency vector from
    // the normalized form of those same words (so scoring ignores case,
    // punctuation, and stop-words).
    private IEnumerable<(string Text, IReadOnlyDictionary<string, int> TermFreq)> ChunkWords(List<string> words)
    {
        var size = Math.Max(1, Options.ChunkSize);
        var overlap = Math.Min(Math.Max(0, Options.ChunkOverlap), size - 1);
        var step = size - overlap;

        for (var start = 0; start < words.Count; start += step)
        {
            var slice = words.Skip(start).Take(size).ToList();
            if (slice.Count == 0) break;
            yield return (string.Join(" ", slice), TermFrequencies(Normalize(slice)));
            if (start + size >= words.Count) break; // last window covered the tail
        }
    }

    // ── TF-IDF / similarity ──────────────────────────────────

    private Dictionary<string, double> TfIdf(IReadOnlyDictionary<string, int> termFreq)
    {
        // IDF uses the chunk count as the document count; smoothed so a term
        // present in every chunk still contributes a little.
        var n = Math.Max(1, _chunks.Count);
        var vec = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (term, tf) in termFreq)
        {
            var df = _docFreq.GetValueOrDefault(term, 0);
            var idf = Math.Log((n + 1.0) / (df + 1.0)) + 1.0;
            vec[term] = tf * idf;
        }
        return vec;
    }

    private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        // Iterate the smaller vector for the dot product.
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        double dot = 0;
        foreach (var (term, w) in small)
            if (large.TryGetValue(term, out var w2)) dot += w * w2;
        if (dot == 0) return 0;
        return dot / (Norm(a) * Norm(b));
    }

    private static double Norm(Dictionary<string, double> v)
    {
        double sum = 0;
        foreach (var w in v.Values) sum += w * w;
        return Math.Sqrt(sum);
    }

    private static Dictionary<string, int> TermFrequencies(IEnumerable<string> tokens)
    {
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens) tf[t] = tf.GetValueOrDefault(t) + 1;
        return tf;
    }

    // ── Tokenization ─────────────────────────────────────────

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the","a","an","is","are","was","were","be","been","being","do","does","did",
        "to","of","in","on","for","and","or","but","with","at","by","from","as","that",
        "this","these","those","it","its","you","your","i","my","me","we","our","they",
        "their","he","she","his","her","how","what","when","where","why","which","who",
        "can","could","will","would","should","may","might","must","have","has","had",
        "if","so","than","then","there","here","into","out","up","down","over","under",
        "s","t","not","no","yes","get","got"
    };

    // Split on whitespace, preserving each original word (case + punctuation)
    // so retrieved chunks read naturally when shown or quoted.
    private static List<string> SplitWords(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    // Reduce raw words to scoring terms using the same rules as Tokenize
    // (lower-case, alphanumeric-only, drop stop-words and single chars).
    private static IEnumerable<string> Normalize(IEnumerable<string> words) =>
        words.SelectMany(Tokenize);

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return tokens;
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else FlushToken(sb, tokens);
        }
        FlushToken(sb, tokens);
        return tokens;
    }

    private static void FlushToken(StringBuilder sb, List<string> tokens)
    {
        if (sb.Length == 0) return;
        var token = sb.ToString();
        sb.Clear();
        if (token.Length > 1 && !StopWords.Contains(token)) tokens.Add(token);
    }

    private static IEnumerable<string> SplitSentences(string text) =>
        text.Split(new[] { '.', '!', '?', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
}
