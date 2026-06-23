using Xunit;

namespace AgenticRecipes.Tests;

public class MemoryAugmentedChainTests
{
    // A responder that commits a single fact and never recalls anything itself —
    // keeps the memory machinery under test rather than the LLM.
    private static TurnResult StoreFact(string text, double salience = 0.8, params string[] tags)
        => new("ok", new List<NewFact> { new(text, salience, tags) });

    [Fact]
    public void Retrieve_EmptyMemory_ReturnsNothing()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions());
        Assert.Empty(agent.Retrieve("anything at all"));
    }

    [Fact]
    public async Task Chat_StoresNewFact()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions());

        var turn = await agent.ChatAsync(
            "I love hiking",
            (input, recalled, t) => new TurnResult("noted",
                new List<NewFact> { new("User likes hiking.", 0.9, new[] { "hiking" }) }));

        Assert.Single(agent.Memory);
        Assert.Single(turn.Written);
        Assert.Equal("User likes hiking.", agent.Memory[0].Text);
        Assert.Equal(1, agent.Memory[0].CreatedTurn);
    }

    [Fact]
    public async Task Retrieve_OnlyReturnsRelevantMemories()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0 });
        await agent.ChatAsync("a", (i, r, t) => StoreFact("User enjoys hiking trails.", 0.8, "hiking", "outdoors"));
        await agent.ChatAsync("b", (i, r, t) => StoreFact("User drives an electric car.", 0.8, "car", "electric"));

        var recalled = agent.Retrieve("recommend a hiking route");

        Assert.Single(recalled);
        Assert.Contains("hiking", recalled[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retrieve_MatchesTagsNotJustText()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0 });
        // "vegetarian" is not in the stored text, only in the tag.
        await agent.ChatAsync("a", (i, r, t) => StoreFact("Dietary preference recorded.", 0.8, "vegetarian", "food"));

        Assert.Single(agent.Retrieve("any good vegetarian spots"));
    }

    [Fact]
    public async Task Retrieve_MatchesMultiWordTag_ByEitherWord()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0 });
        // The tag is two words; neither word appears in the stored text. A query
        // that mentions just ONE of the words must still recall the memory — tags
        // are tokenized like text, so each word is independently matchable.
        await agent.ChatAsync("a", (i, r, t) => StoreFact("Vehicle preference recorded.", 0.8, "electric car"));

        Assert.Single(agent.Retrieve("recommend a fast car"));      // matches "car"
        Assert.Single(agent.Retrieve("is it fully electric"));      // matches "electric"
    }

    [Fact]
    public async Task Retrieve_RespectsTopK()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0, TopK = 2, MaxItems = 10 });
        await agent.ChatAsync("a", (i, r, t) => StoreFact("hiking trip planning notes", 0.8, "trip"));
        await agent.ChatAsync("b", (i, r, t) => StoreFact("hiking gear checklist", 0.8, "trip"));
        await agent.ChatAsync("c", (i, r, t) => StoreFact("hiking weather forecast", 0.8, "trip"));

        Assert.Equal(2, agent.Retrieve("hiking trip").Count);
    }

    [Fact]
    public async Task Retrieve_TopKZero_ReturnsNothing()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { TopK = 0, DecayPerTurn = 0 });
        await agent.ChatAsync("a", (i, r, t) => StoreFact("hiking notes", 0.8, "hiking"));

        Assert.Empty(agent.Retrieve("hiking"));
    }

    [Fact]
    public async Task Retrieve_RanksHigherSalienceFirst_WhenRelevanceTies()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions
        {
            DecayPerTurn = 0,
            TopK = 2,
            RelevanceWeight = 1.0,
            RecencyWeight = 0,      // isolate salience as the tie-breaker
            SalienceWeight = 1.0
        });
        await agent.ChatAsync("a", (i, r, t) => StoreFact("hiking option alpha", 0.2, "hiking"));
        await agent.ChatAsync("b", (i, r, t) => StoreFact("hiking option bravo", 0.9, "hiking"));

        Assert.Equal("hiking option bravo", agent.Retrieve("hiking option")[0].Text);
    }

    [Fact]
    public async Task DuplicateFact_ReinforcesInsteadOfAddingCopy()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0, DuplicateThreshold = 0.6 });

        await agent.ChatAsync("a", (i, r, t) => StoreFact("User is vegetarian.", 0.5, "vegetarian"));
        var second = await agent.ChatAsync("b", (i, r, t) => StoreFact("User is vegetarian.", 0.9, "vegetarian", "diet"));

        Assert.Single(agent.Memory);                 // no duplicate copy stored
        Assert.Empty(second.Written);
        Assert.Single(second.Reinforced);
        Assert.True(agent.Memory[0].Salience >= 0.9); // reinforced to the higher salience (+ bump)
        Assert.Contains("diet", agent.Memory[0].Tags); // tags merged
    }

    [Fact]
    public async Task EvictsWeakestMemory_WhenOverBudget()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions
        {
            MaxItems = 2,
            DecayPerTurn = 0,
            TopK = 0   // never recall, so nothing gets refreshed
        });

        await agent.ChatAsync("a", (i, r, t) => StoreFact("low value fact", 0.1, "alpha"));
        await agent.ChatAsync("b", (i, r, t) => StoreFact("medium value fact", 0.5, "bravo"));
        var third = await agent.ChatAsync("c", (i, r, t) => StoreFact("high value fact", 0.9, "charlie"));

        Assert.Equal(2, agent.Memory.Count);
        Assert.Single(third.Evicted);
        Assert.Equal("low value fact", third.Evicted[0].Text);   // lowest keep-score evicted
        Assert.DoesNotContain(agent.Memory, m => m.Text == "low value fact");
    }

    [Fact]
    public async Task Decay_ReducesSalienceEachTurn()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0.2, TopK = 0, MaxItems = 10 });

        await agent.ChatAsync("a", (i, r, t) => StoreFact("fact", 1.0, "x")); // → 0.8 after this turn
        var s1 = agent.Memory[0].Salience;
        await agent.ChatAsync("b", (i, r, t) => new TurnResult("noop", new List<NewFact>())); // → 0.6
        var s2 = agent.Memory[0].Salience;

        Assert.True(s2 < s1);
        Assert.Equal(0.8, s1, 3);
        Assert.Equal(0.6, s2, 3);
    }

    [Fact]
    public async Task Decay_FloorsAtZero_NeverNegative()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0.5, TopK = 0, MaxItems = 10 });
        await agent.ChatAsync("a", (i, r, t) => StoreFact("fact", 0.3, "x"));
        await agent.ChatAsync("b", (i, r, t) => new TurnResult("noop", new List<NewFact>()));
        await agent.ChatAsync("c", (i, r, t) => new TurnResult("noop", new List<NewFact>()));

        Assert.True(agent.Memory[0].Salience >= 0);
    }

    [Fact]
    public async Task RecallingMemory_RefreshesRecencyAndSalience()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0, TopK = 3, MaxItems = 10 });
        await agent.ChatAsync("seed", (i, r, t) => StoreFact("hiking notes here", 0.5, "hiking"));
        var before = agent.Memory[0];

        var turn = await agent.ChatAsync("tell me about hiking", (i, recalled, t) => new TurnResult("sure", new List<NewFact>()));

        Assert.Single(turn.Recalled);
        var after = agent.Memory.Single(m => m.Id == before.Id);
        Assert.Equal(2, after.LastUsedTurn);            // refreshed to current turn
        Assert.True(after.Salience > before.Salience);  // small reinforcement bump
    }

    [Fact]
    public async Task TurnRecord_ReportsRecalledWrittenEvicted()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0, MaxItems = 10, TopK = 3 });
        await agent.ChatAsync("seed", (i, r, t) => StoreFact("coffee preferences noted", 0.6, "coffee"));

        var turn = await agent.ChatAsync(
            "what about coffee",
            (i, recalled, t) => new TurnResult("here",
                new List<NewFact> { new("Likes espresso.", 0.7, new[] { "coffee", "espresso" }) }));

        Assert.Equal(2, turn.TurnNumber);
        Assert.Single(turn.Recalled);
        Assert.Single(turn.Written);
        Assert.Empty(turn.Evicted);
        Assert.Equal("what about coffee", turn.UserInput);
        Assert.Equal("here", turn.Response);
    }

    [Fact]
    public async Task OnTurn_FiresOncePerTurn()
    {
        var seen = new List<int>();
        var agent = new MemoryAugmentedAgent(new MemoryOptions { OnTurn = t => seen.Add(t.TurnNumber) });

        await agent.ChatAsync("a", (i, r, t) => StoreFact("fa", 0.5, "x"));
        await agent.ChatAsync("b", (i, r, t) => StoreFact("fb", 0.5, "y"));

        Assert.Equal(new[] { 1, 2 }, seen);
    }

    [Fact]
    public async Task Salience_ClampedIntoZeroOneRange_OnWrite()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0 });
        await agent.ChatAsync("a", (i, r, t) => StoreFact("over the top", 5.0, "x"));
        Assert.Equal(1.0, agent.Memory[0].Salience, 3);
    }

    [Fact]
    public async Task MaxItemsZero_KeepsAtLeastOne()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { MaxItems = 0, DecayPerTurn = 0, TopK = 0 });
        await agent.ChatAsync("a", (i, r, t) => StoreFact("only fact", 0.5, "x"));
        Assert.Single(agent.Memory);
    }

    [Fact]
    public async Task NullFactsList_IsHandledGracefully()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions());
        var turn = await agent.ChatAsync("a", (i, r, t) => new TurnResult("no facts", null!));
        Assert.Empty(turn.Written);
        Assert.Empty(agent.Memory);
    }

    [Fact]
    public async Task AlreadyCancelled_Throws()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions());
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await agent.ChatAsync(
                "a",
                (i, r, t, ct) => Task.FromResult(new TurnResult("x", new List<NewFact>())),
                cts.Token));
    }

    [Fact]
    public async Task AsyncResponder_IsAwaited()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0 });

        var turn = await agent.ChatAsync(
            "a",
            async (input, recalled, t, ct) =>
            {
                await Task.Yield();
                return new TurnResult("async reply", new List<NewFact> { new("async fact", 0.6, new[] { "async" }) });
            });

        Assert.Equal("async reply", turn.Response);
        Assert.Single(agent.Memory);
    }

    [Fact]
    public async Task Retrieve_IsDeterministic_AcrossRepeatedCalls()
    {
        var agent = new MemoryAugmentedAgent(new MemoryOptions { DecayPerTurn = 0, TopK = 3, MaxItems = 10 });
        await agent.ChatAsync("a", (i, r, t) => StoreFact("hiking trail map", 0.4, "trip"));
        await agent.ChatAsync("b", (i, r, t) => StoreFact("hiking trail notes", 0.4, "trip"));
        await agent.ChatAsync("c", (i, r, t) => StoreFact("hiking trail photos", 0.4, "trip"));

        var first = string.Join(",", agent.Retrieve("hiking trail").Select(m => m.Id));
        var second = string.Join(",", agent.Retrieve("hiking trail").Select(m => m.Id));

        Assert.Equal(first, second);
    }
}

// ── Supporting types (mirrors recipes/memory-augmented/Program.cs) ──

record NewFact(string Text, double Salience, IReadOnlyList<string> Tags);

record TurnResult(string Response, IReadOnlyList<NewFact> Facts);

record MemoryItem(string Id, string Text, double Salience, int CreatedTurn, int LastUsedTurn, IReadOnlyList<string> Tags);

record MemoryTurn(
    int TurnNumber,
    string UserInput,
    IReadOnlyList<MemoryItem> Recalled,
    string Response,
    IReadOnlyList<MemoryItem> Written,
    IReadOnlyList<MemoryItem> Reinforced,
    IReadOnlyList<MemoryItem> Evicted);

record MemoryOptions
{
    public int MaxItems { get; init; } = 8;
    public int TopK { get; init; } = 3;
    public double MinRelevanceToRecall { get; init; } = 0.05;
    public double RelevanceWeight { get; init; } = 1.0;
    public double RecencyWeight { get; init; } = 0.3;
    public double SalienceWeight { get; init; } = 0.25;
    public double DecayPerTurn { get; init; } = 0.1;
    public double DuplicateThreshold { get; init; } = 0.6;
    public Action<MemoryTurn>? OnTurn { get; init; }
}

class MemoryAugmentedAgent
{
    private readonly List<MemoryItem> _memory = new();
    private int _turn;
    private int _idSeq;

    public MemoryOptions Options { get; }

    public IReadOnlyList<MemoryItem> Memory => _memory.AsReadOnly();

    public MemoryAugmentedAgent(MemoryOptions options) => Options = options;

    public Task<MemoryTurn> ChatAsync(
        string userInput,
        Func<string, IReadOnlyList<MemoryItem>, int, TurnResult> respond,
        CancellationToken ct = default)
        => ChatAsync(userInput, (input, recalled, turn, _) => Task.FromResult(respond(input, recalled, turn)), ct);

    public async Task<MemoryTurn> ChatAsync(
        string userInput,
        Func<string, IReadOnlyList<MemoryItem>, int, CancellationToken, Task<TurnResult>> respond,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _turn++;

        var recalled = Retrieve(userInput);
        var result = await respond(userInput, recalled, _turn, ct);

        foreach (var item in recalled)
            Touch(item.Id);

        var written = new List<MemoryItem>();
        var reinforced = new List<MemoryItem>();
        foreach (var fact in result.Facts ?? Array.Empty<NewFact>())
        {
            var dup = FindDuplicate(fact.Text);
            if (dup is not null)
            {
                var bumped = dup with
                {
                    Salience = Clamp01(Math.Max(dup.Salience, fact.Salience) + 0.1),
                    LastUsedTurn = _turn,
                    Tags = MergeTags(dup.Tags, fact.Tags ?? Array.Empty<string>())
                };
                Replace(dup.Id, bumped);
                reinforced.Add(bumped);
            }
            else
            {
                var item = new MemoryItem(
                    Id: $"m{++_idSeq}",
                    Text: fact.Text,
                    Salience: Clamp01(fact.Salience),
                    CreatedTurn: _turn,
                    LastUsedTurn: _turn,
                    Tags: fact.Tags ?? Array.Empty<string>());
                _memory.Add(item);
                written.Add(item);
            }
        }

        Decay();
        var evicted = EvictOverBudget();

        var turnRecord = new MemoryTurn(_turn, userInput, recalled, result.Response, written, reinforced, evicted);
        Options.OnTurn?.Invoke(turnRecord);
        return turnRecord;
    }

    public IReadOnlyList<MemoryItem> Retrieve(string query)
    {
        var topK = Math.Max(0, Options.TopK);
        if (topK == 0 || _memory.Count == 0) return Array.Empty<MemoryItem>();

        var queryTokens = Tokenize(query);

        return _memory
            .Select(m => new { Item = m, Relevance = Relevance(queryTokens, m) })
            .Where(x => x.Relevance >= Options.MinRelevanceToRecall)
            .Select(x => new
            {
                x.Item,
                Score = Options.RelevanceWeight * x.Relevance
                      + Options.RecencyWeight * Recency(x.Item)
                      + Options.SalienceWeight * x.Item.Salience
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.LastUsedTurn)
            .ThenBy(x => x.Item.Id, StringComparer.Ordinal)
            .Take(topK)
            .Select(x => x.Item)
            .ToList();
    }

    private void Touch(string id)
    {
        var idx = _memory.FindIndex(m => m.Id == id);
        if (idx >= 0)
            _memory[idx] = _memory[idx] with
            {
                LastUsedTurn = _turn,
                Salience = Clamp01(_memory[idx].Salience + 0.05)
            };
    }

    private MemoryItem? FindDuplicate(string text)
    {
        var tokens = Tokenize(text);
        foreach (var m in _memory)
            if (Jaccard(tokens, Tokenize(m.Text)) >= Options.DuplicateThreshold)
                return m;
        return null;
    }

    private void Replace(string id, MemoryItem updated)
    {
        var idx = _memory.FindIndex(m => m.Id == id);
        if (idx >= 0) _memory[idx] = updated;
    }

    private void Decay()
    {
        if (Options.DecayPerTurn <= 0) return;
        for (var i = 0; i < _memory.Count; i++)
            _memory[i] = _memory[i] with { Salience = Clamp01(_memory[i].Salience - Options.DecayPerTurn) };
    }

    private List<MemoryItem> EvictOverBudget()
    {
        var max = Math.Max(1, Options.MaxItems);
        var evicted = new List<MemoryItem>();
        if (_memory.Count <= max) return evicted;

        var ordered = _memory
            .OrderBy(KeepScore)
            .ThenBy(m => m.LastUsedTurn)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        var toRemove = _memory.Count - max;
        for (var i = 0; i < toRemove; i++)
        {
            evicted.Add(ordered[i]);
            _memory.Remove(ordered[i]);
        }
        return evicted;
    }

    private double KeepScore(MemoryItem m) => m.Salience + 0.1 * Recency(m);

    private double Recency(MemoryItem m) =>
        _turn <= 0 ? 0 : Clamp01(1.0 - (_turn - m.LastUsedTurn) / (double)Math.Max(1, _turn));

    private double Relevance(HashSet<string> queryTokens, MemoryItem m)
    {
        var itemTokens = Tokenize(m.Text);
        // Tokenize tags the same way as query/fact text so a multi-word tag
        // ("electric car") contributes each of its words as a matchable token.
        foreach (var tag in m.Tags) itemTokens.UnionWith(Tokenize(tag));
        return Jaccard(queryTokens, itemTokens);
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = 0;
        foreach (var t in a) if (b.Contains(t)) intersection++;
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : intersection / (double)union;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the","a","an","is","are","i","im","my","me","to","of","in","on","for","and",
        "you","your","it","its","that","this","what","where","when","how","s","by","be",
        "with","at","do","does","again","like","usually","about","any","worth","there"
    };

    private static HashSet<string> Tokenize(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return set;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else FlushToken(sb, set);
        }
        FlushToken(sb, set);
        return set;
    }

    private static void FlushToken(System.Text.StringBuilder sb, HashSet<string> set)
    {
        if (sb.Length == 0) return;
        var token = sb.ToString();
        sb.Clear();
        if (token.Length > 1 && !StopWords.Contains(token)) set.Add(token);
    }

    private static IReadOnlyList<string> MergeTags(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var merged = new List<string>(a);
        foreach (var t in b)
            if (!merged.Contains(t, StringComparer.OrdinalIgnoreCase))
                merged.Add(t);
        return merged;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}