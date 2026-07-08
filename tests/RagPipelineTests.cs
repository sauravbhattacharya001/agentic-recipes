using Xunit;

namespace AgenticRecipes.Tests;

public class RagPipelineTests
{
    private static readonly Document[] Corpus =
    {
        new("returns", "Our return policy allows returns within 30 days of delivery for a full refund. " +
                       "Items must be unused and in original packaging. Final-sale items cannot be returned."),
        new("shipping", "Standard shipping takes 3 to 5 business days and is free on orders over fifty dollars. " +
                        "Express shipping arrives in 1 to 2 business days for an extra charge. " +
                        "We currently ship to the United States and Canada only."),
        new("warranty", "All electronics include a one-year limited warranty covering manufacturing defects. " +
                        "The warranty does not cover accidental or water damage. " +
                        "Warranty claims require the original order number as proof of purchase."),
    };

    private static RagPipeline Build(RagOptions? options = null)
    {
        var rag = new RagPipeline(options ?? new RagOptions());
        rag.Ingest(Corpus);
        return rag;
    }

    private static string CountingGenerator(string q, IReadOnlyList<RetrievedChunk> ctx)
        => $"answer from {ctx.Count} chunks";

    // ── Ingestion / chunking ─────────────────────────────────

    [Fact]
    public void Ingest_ProducesChunks() => Assert.True(Build().ChunkCount > 0);

    [Fact]
    public void Ingest_LargeChunkSize_ProducesOneChunkPerDoc()
    {
        var rag = Build(new RagOptions { ChunkSize = 1000, ChunkOverlap = 0 });
        Assert.Equal(Corpus.Length, rag.ChunkCount);
    }

    [Fact]
    public void Ingest_SmallChunkSize_ProducesManyChunks()
    {
        var rag = Build(new RagOptions { ChunkSize = 5, ChunkOverlap = 1 });
        Assert.True(rag.ChunkCount > Corpus.Length);
    }

    [Fact]
    public void Ingest_SkipsEmptyAndNullDocuments()
    {
        var rag = new RagPipeline(new RagOptions());
        rag.Ingest(new[] { new Document("a", ""), new Document("b", "   "), null! });
        Assert.Equal(0, rag.ChunkCount);
    }

    [Fact]
    public void Ingest_NullCollection_DoesNotThrow()
    {
        var rag = new RagPipeline(new RagOptions());
        rag.Ingest(null!);
        Assert.Equal(0, rag.ChunkCount);
    }

    [Fact]
    public void Ingest_CanBeCalledMultipleTimes_GrowsCorpus()
    {
        var rag = new RagPipeline(new RagOptions { ChunkSize = 1000 });
        rag.Ingest(new[] { new Document("a", "alpha beta gamma") });
        var afterFirst = rag.ChunkCount;
        rag.Ingest(new[] { new Document("b", "delta epsilon zeta") });
        Assert.True(rag.ChunkCount > afterFirst);
    }

    [Fact]
    public void Chunk_IndicesAreContiguousPerDocument()
    {
        // A chunk's Index is its ordinal WITHIN its own document, restarting at 0
        // for each document — so the "doc#N" citation names a real in-document position.
        var rag = Build(new RagOptions { ChunkSize = 6, ChunkOverlap = 2 });
        foreach (var group in rag.Chunks.GroupBy(c => c.DocumentId))
        {
            var expected = 0;
            foreach (var chunk in group)
                Assert.Equal(expected++, chunk.Index);
        }
    }

    [Fact]
    public void Chunk_EachDocumentStartsAtIndexZero()
    {
        // Regression: Index must not be a corpus-global counter. Every document's
        // first surviving chunk is #0, so citations like "warranty#0" are truthful
        // even when the doc is indexed after others.
        var rag = Build(new RagOptions { ChunkSize = 6, ChunkOverlap = 2 });
        foreach (var group in rag.Chunks.GroupBy(c => c.DocumentId))
            Assert.Equal(0, group.Min(c => c.Index));
    }

    // ── Retrieval ────────────────────────────────────────────

    [Fact]
    public void Retrieve_EmptyCorpus_ReturnsNothing()
        => Assert.Empty(new RagPipeline(new RagOptions()).Retrieve("anything"));

    [Fact]
    public void Retrieve_FindsRelevantDocument()
    {
        var hits = Build().Retrieve("water damage warranty");
        Assert.NotEmpty(hits);
        Assert.Equal("warranty", hits[0].Chunk.DocumentId);
    }

    [Fact]
    public void Retrieve_RanksTermSpecificChunkFirst()
    {
        var hits = Build().Retrieve("how long for express shipping");
        Assert.Equal("shipping", hits[0].Chunk.DocumentId);
    }

    [Fact]
    public void Retrieve_RespectsTopK()
    {
        var rag = Build(new RagOptions { ChunkSize = 5, ChunkOverlap = 1, TopK = 2 });
        Assert.True(rag.Retrieve("shipping return warranty").Count <= 2);
    }

    [Fact]
    public void Retrieve_TopKZero_ReturnsNothing()
        => Assert.Empty(Build(new RagOptions { TopK = 0 }).Retrieve("returns"));

    [Fact]
    public void Retrieve_QueryWithOnlyStopWords_ReturnsNothing()
        => Assert.Empty(Build().Retrieve("what is the of and to"));

    [Fact]
    public void Retrieve_QueryWithNoCorpusOverlap_ReturnsNothing()
        => Assert.Empty(Build().Retrieve("dinosaur helicopter saxophone"));

    [Fact]
    public void Retrieve_AssignsSequentialCitations()
    {
        var hits = Build(new RagOptions { ChunkSize = 6, ChunkOverlap = 1, TopK = 3 })
            .Retrieve("shipping warranty returns refund");
        for (var i = 0; i < hits.Count; i++)
            Assert.Equal(i + 1, hits[i].Citation);
    }

    [Fact]
    public void Retrieve_ScoresAreDescending()
    {
        var hits = Build(new RagOptions { ChunkSize = 6, ChunkOverlap = 1, TopK = 5 })
            .Retrieve("shipping warranty returns refund days");
        for (var i = 1; i < hits.Count; i++)
            Assert.True(hits[i - 1].Score >= hits[i].Score);
    }

    [Fact]
    public void Retrieve_IsDeterministic()
    {
        var rag = Build(new RagOptions { ChunkSize = 6, ChunkOverlap = 2, TopK = 4 });
        string Key() => string.Join(",", rag.Retrieve("shipping warranty days")
            .Select(h => $"{h.Chunk.DocumentId}#{h.Chunk.Index}"));
        Assert.Equal(Key(), Key());
    }

    [Fact]
    public void Retrieve_RareTermOutranksCommonTerm()
    {
        // "warranty" appears in one doc (high IDF); "days" appears in two.
        var hits = Build(new RagOptions { ChunkSize = 1000, ChunkOverlap = 0 }).Retrieve("warranty days");
        Assert.Equal("warranty", hits[0].Chunk.DocumentId);
    }

    // ── Ask: grounded answers + abstention ───────────────────

    [Fact]
    public async Task Ask_AnswerableQuestion_DoesNotAbstain()
    {
        var rag = Build();
        var ans = await rag.AskAsync("Does the warranty cover water damage?", CountingGenerator);
        Assert.False(ans.Abstained);
        Assert.NotEmpty(ans.Context);
        Assert.True(ans.TopScore >= rag.Options.MinRelevance);
    }

    [Fact]
    public async Task Ask_OutOfCorpusQuestion_Abstains()
    {
        var ans = await Build().AskAsync("What is your CEO's favourite colour?", CountingGenerator);
        Assert.True(ans.Abstained);
        Assert.Empty(ans.Context);
        Assert.Contains("don't have enough information", ans.Text);
    }

    [Fact]
    public async Task Ask_EmptyCorpus_Abstains()
    {
        var ans = await new RagPipeline(new RagOptions()).AskAsync("anything", CountingGenerator);
        Assert.True(ans.Abstained);
        Assert.Equal(0.0, ans.TopScore, 6);
    }

    [Fact]
    public async Task Ask_HighRelevanceFloor_ForcesAbstention()
    {
        var ans = await Build(new RagOptions { MinRelevance = 0.99 })
            .AskAsync("Does the warranty cover water damage?", CountingGenerator);
        Assert.True(ans.Abstained);
    }

    [Fact]
    public async Task Ask_DoesNotCallGenerator_WhenAbstaining()
    {
        var called = false;
        await Build().AskAsync("dinosaur helicopter saxophone", (q, ctx) => { called = true; return "x"; });
        Assert.False(called);
    }

    [Fact]
    public async Task Ask_CallsGenerator_WhenAnswerable()
    {
        var called = false;
        await Build().AskAsync("water damage warranty", (q, ctx) => { called = true; return "x"; });
        Assert.True(called);
    }

    [Fact]
    public async Task Ask_ContextCitationsAreRenumberedFromOne()
    {
        var ans = await Build(new RagOptions { ChunkSize = 6, ChunkOverlap = 1, TopK = 3 })
            .AskAsync("shipping warranty returns refund", CountingGenerator);
        Assert.False(ans.Abstained);
        for (var i = 0; i < ans.Context.Count; i++)
            Assert.Equal(i + 1, ans.Context[i].Citation);
    }

    [Fact]
    public async Task Ask_PassesContextToGenerator()
    {
        var ans = await Build().AskAsync("water damage warranty", (q, ctx) => $"used {ctx.Count}");
        Assert.Equal($"used {ans.Context.Count}", ans.Text);
    }

    [Fact]
    public async Task Ask_AsyncGenerator_IsAwaited()
    {
        var ans = await Build().AskAsync("water damage warranty", async (q, ctx, ct) =>
        {
            await Task.Yield();
            return "async grounded answer";
        });
        Assert.Equal("async grounded answer", ans.Text);
    }

    [Fact]
    public async Task Ask_AlreadyCancelled_Throws()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Build().AskAsync("water damage", (q, ctx, ct) => Task.FromResult("x"), cts.Token));
    }

    // ── Context block / extractive helpers ───────────────────

    [Fact]
    public void BuildContextBlock_IncludesCitationsAndDocIds()
    {
        var hits = Build().Retrieve("water damage warranty");
        var block = RagPipeline.BuildContextBlock(hits);
        Assert.Contains("[1]", block);
        Assert.Contains(hits[0].Chunk.DocumentId, block);
    }

    [Fact]
    public void BuildContextBlock_EmptyContext_IsEmptyString()
        => Assert.Equal(string.Empty, RagPipeline.BuildContextBlock(Array.Empty<RetrievedChunk>()));

    [Fact]
    public void BestSentence_PicksMostRelevantSentence()
    {
        var text = "Standard shipping is free. The warranty does not cover water damage. Returns take 30 days.";
        var sentence = RagPipeline.BestSentence("does warranty cover water damage", text);
        Assert.NotNull(sentence);
        Assert.Contains("water damage", sentence!);
    }

    [Fact]
    public void BestSentence_NoOverlap_ReturnsNull()
        => Assert.Null(RagPipeline.BestSentence("dinosaur helicopter", "Shipping is free on large orders."));

    [Fact]
    public void BestSentence_StopWordOnlyQuestion_ReturnsNull()
        => Assert.Null(RagPipeline.BestSentence("what is the of", "Shipping is free on large orders."));

    // ── Options clamping ─────────────────────────────────────

    [Fact]
    public void Ingest_OverlapClampedBelowChunkSize_DoesNotHang()
    {
        // Overlap >= ChunkSize would make step <= 0 and loop forever if unclamped.
        Assert.True(Build(new RagOptions { ChunkSize = 5, ChunkOverlap = 99 }).ChunkCount > 0);
    }

    [Fact]
    public void Ingest_ChunkSizeZero_TreatedAsAtLeastOne()
        => Assert.True(Build(new RagOptions { ChunkSize = 0, ChunkOverlap = 0 }).ChunkCount > 0);

    [Fact]
    public async Task Ask_NegativeTopK_TreatedAsZero_Abstains()
    {
        var ans = await Build(new RagOptions { TopK = -3 }).AskAsync("water damage warranty", CountingGenerator);
        Assert.True(ans.Abstained);
    }
}

// ── Supporting types (mirrors recipes/rag-pipeline/Program.cs) ──

record Document(string Id, string Text);

record Chunk(string DocumentId, int Index, string Text, IReadOnlyDictionary<string, int> TermFreq);

record RetrievedChunk(Chunk Chunk, double Score, int Citation);

record RagAnswer(string Text, bool Abstained, double TopScore, IReadOnlyList<RetrievedChunk> Context);

record RagOptions
{
    public int ChunkSize { get; init; } = 24;
    public int ChunkOverlap { get; init; } = 6;
    public int TopK { get; init; } = 3;
    public double MinRelevance { get; init; } = 0.08;
}

class RagPipeline
{
    private readonly List<Chunk> _chunks = new();
    private readonly Dictionary<string, int> _docFreq = new(StringComparer.Ordinal);

    public RagOptions Options { get; }

    public int ChunkCount => _chunks.Count;

    public IReadOnlyList<Chunk> Chunks => _chunks.AsReadOnly();

    public RagPipeline(RagOptions options) => Options = options;

    public void Ingest(IEnumerable<Document> documents)
    {
        foreach (var doc in documents ?? Array.Empty<Document>())
        {
            if (doc is null || string.IsNullOrWhiteSpace(doc.Text)) continue;
            var words = SplitWords(doc.Text);
            if (words.Count == 0) continue;

            var indexInDoc = 0;
            foreach (var (text, termFreq) in ChunkWords(words))
            {
                if (termFreq.Count == 0) continue;
                var chunk = new Chunk(doc.Id, indexInDoc++, text, termFreq);
                _chunks.Add(chunk);
                foreach (var term in termFreq.Keys)
                    _docFreq[term] = _docFreq.GetValueOrDefault(term) + 1;
            }
        }
    }

    public IReadOnlyList<RetrievedChunk> Retrieve(string query)
    {
        var topK = Math.Max(0, Options.TopK);
        if (topK == 0 || _chunks.Count == 0) return Array.Empty<RetrievedChunk>();

        var queryTf = TermFrequencies(Tokenize(query));
        if (queryTf.Count == 0) return Array.Empty<RetrievedChunk>();
        var queryVec = TfIdf(queryTf);

        return _chunks
            .Select(c => new { Chunk = c, Score = Cosine(queryVec, TfIdf(c.TermFreq)) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.DocumentId, StringComparer.Ordinal)
            .ThenBy(x => x.Chunk.Index)
            .Take(topK)
            .Select((x, i) => new RetrievedChunk(x.Chunk, x.Score, i + 1))
            .ToList();
    }

    public Task<RagAnswer> AskAsync(
        string question,
        Func<string, IReadOnlyList<RetrievedChunk>, string> generate,
        CancellationToken ct = default)
        => AskAsync(question, (q, ctx, _) => Task.FromResult(generate(q, ctx)), ct);

    public async Task<RagAnswer> AskAsync(
        string question,
        Func<string, IReadOnlyList<RetrievedChunk>, CancellationToken, Task<string>> generate,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var retrieved = Retrieve(question);
        var topScore = retrieved.Count > 0 ? retrieved[0].Score : 0.0;

        if (retrieved.Count == 0 || topScore < Options.MinRelevance)
            return new RagAnswer(
                "I don't have enough information to answer that.",
                Abstained: true,
                TopScore: topScore,
                Context: Array.Empty<RetrievedChunk>());

        var context = retrieved.Where(r => r.Score >= Options.MinRelevance).ToList();
        var renumbered = context.Select((r, i) => r with { Citation = i + 1 }).ToList();
        var text = await generate(question, renumbered, ct);

        return new RagAnswer(text, Abstained: false, TopScore: topScore, Context: renumbered);
    }

    public static string BuildContextBlock(IReadOnlyList<RetrievedChunk> context)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in context)
            sb.AppendLine($"[{c.Citation}] ({c.Chunk.DocumentId}) {c.Chunk.Text}");
        return sb.ToString().TrimEnd();
    }

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
            if (start + size >= words.Count) break;
        }
    }

    private Dictionary<string, double> TfIdf(IReadOnlyDictionary<string, int> termFreq)
    {
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

    private static List<string> SplitWords(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static IEnumerable<string> Normalize(IEnumerable<string> words) =>
        words.SelectMany(Tokenize);

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return tokens;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else FlushToken(sb, tokens);
        }
        FlushToken(sb, tokens);
        return tokens;
    }

    private static void FlushToken(System.Text.StringBuilder sb, List<string> tokens)
    {
        if (sb.Length == 0) return;
        var token = sb.ToString();
        sb.Clear();
        if (token.Length > 1 && !StopWords.Contains(token)) tokens.Add(token);
    }

    private static IEnumerable<string> SplitSentences(string text) =>
        text.Split(new[] { '.', '!', '?', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
}
