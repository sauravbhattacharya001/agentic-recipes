using Prompt;
using System.Text;

// ──────────────────────────────────────────────────────────────
// Memory-Augmented Chain Recipe
// Pattern: Context accumulation across turns (retrieve → augment → generate → remember)
//
// A plain chain forgets everything between turns. This recipe gives
// the agent a working memory: before each turn it RETRIEVES the most
// relevant facts it has stored, AUGMENTS the prompt with them,
// GENERATES a response, then writes new facts back and decides — on
// its own — what to keep and what to forget.
//
// The memory is self-managing:
//   • relevance + recency + salience scoring picks what to recall
//   • salience decays a little every turn, so stale facts fade
//   • recalling a fact refreshes it (it was useful → keep it longer)
//   • near-duplicate writes reinforce an existing memory instead of
//     piling up redundant copies
//   • when the store exceeds its budget, the lowest-value memories
//     are evicted automatically
//
// This is the learning/adaptation flavour of agency: the app gets
// better across a conversation because it remembers, and it manages
// that memory without being told what matters.
// ──────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;

// 1. Configure the memory-augmented agent.
var agent = new MemoryAugmentedAgent(new MemoryOptions
{
    MaxItems = 6,                 // budget: evict the weakest beyond this
    TopK = 3,                     // recall at most this many facts per turn
    MinRelevanceToRecall = 0.05,  // ignore facts with no meaningful overlap
    RelevanceWeight = 1.0,        // how strongly query-overlap drives recall
    RecencyWeight = 0.35,         // freshly-used facts rank a little higher
    SalienceWeight = 0.25,        // important facts rank a little higher
    DecayPerTurn = 0.12,          // every fact loses this much salience/turn
    OnTurn = turn =>
    {
        Console.WriteLine($"  ── Turn {turn.TurnNumber} ──");
        Console.WriteLine($"  User    : {turn.UserInput}");
        Console.WriteLine(turn.Recalled.Count == 0
            ? "  Recalled: (nothing relevant yet)"
            : $"  Recalled: {string.Join(" | ", turn.Recalled.Select(m => m.Text))}");
        Console.WriteLine($"  Reply   : {turn.Response}");
        if (turn.Written.Count > 0)
            Console.WriteLine($"  Learned : {string.Join(" | ", turn.Written.Select(m => m.Text))}");
        if (turn.Reinforced.Count > 0)
            Console.WriteLine($"  Reinforced: {string.Join(" | ", turn.Reinforced.Select(m => m.Text))}");
        if (turn.Evicted.Count > 0)
            Console.WriteLine($"  Forgot  : {string.Join(" | ", turn.Evicted.Select(m => m.Text))}");
        Console.WriteLine();
    }
});

// 2. A simulated responder. In a real recipe this is an LLM call that
//    receives the augmented prompt (user input + recalled memories) and
//    returns BOTH a reply and the new facts worth remembering. Here we
//    model a trip-planning assistant deterministically so the memory
//    machinery is easy to follow offline.
TurnResult Respond(string userInput, IReadOnlyList<MemoryItem> recalled, int turn)
{
    var known = string.Join(" ", recalled.Select(m => m.Text)).ToLowerInvariant();
    var facts = new List<NewFact>();
    string reply;
    var words = userInput.ToLowerInvariant().Split(
        new[] { ' ', ',', '.', '?', '!', ';', ':', '\'', '\"', '-' },
        StringSplitOptions.RemoveEmptyEntries);
    bool Has(params string[] any) => any.Any(w => words.Contains(w));

    if (Has("tokyo"))
    {
        reply = "Great — noting Tokyo as your destination.";
        facts.Add(new NewFact("Destination is Tokyo.", 1.0, new[] { "destination", "tokyo", "trip", "travel" }));
    }
    else if (Has("vegetarian", "veggie"))
    {
        reply = "Got it, I'll keep restaurant picks vegetarian.";
        facts.Add(new NewFact("Traveler is vegetarian.", 0.9,
            new[] { "vegetarian", "diet", "food", "eat", "restaurant", "dinner" }));
    }
    else if (Has("budget", "cheap", "affordable"))
    {
        reply = "Understood — planning around a modest budget.";
        facts.Add(new NewFact("Budget-conscious trip.", 0.8,
            new[] { "budget", "money", "cheap", "affordable", "restaurant", "eat" }));
    }
    else if (Has("restaurant", "restaurants", "eat", "dinner", "dining", "food"))
    {
        // The payoff: the agent answers using what it remembered.
        var dest = known.Contains("tokyo") ? "Tokyo" : "your destination";
        reply = known.Contains("vegetarian")
            ? $"For {dest} I'd suggest vegetarian-friendly spots like a shojin-ryori place."
            : $"Here are some popular restaurants in {dest}.";
        if (known.Contains("budget"))
            reply += " I'll favour affordable options.";
    }
    else if (Has("forget") && known.Contains("vegetarian"))
    {
        reply = "Okay, I'll stop assuming vegetarian.";
    }
    else
    {
        reply = "Noted.";
    }

    return new TurnResult(reply, facts);
}

// 3. Walk a multi-turn conversation. Notice the agent never re-asks
//    facts it has already been told — it pulls them from memory.
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Memory-Augmented Chain Recipe");
Console.WriteLine("  (retrieve → augment → generate → remember)");
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine();

var conversation = new[]
{
    "I'm planning a trip to Tokyo.",
    "By the way, I'm vegetarian.",
    "Trying to keep it on a tight budget.",
    "Where should I eat dinner?",          // ← recalls vegetarian + budget (the food-relevant facts)
    "What's the weather like there usually?",
    "Any museums worth seeing?",
    "Remind me — any good restaurant picks again?"  // memory still has the food prefs
};

foreach (var (line, i) in conversation.Select((l, idx) => (l, idx + 1)))
    await agent.ChatAsync(line, Respond);

// 4. Inspect the final memory state.
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine($"  Working memory ({agent.Memory.Count}/{agent.Options.MaxItems} slots):");
foreach (var m in agent.Memory.OrderByDescending(m => m.Salience))
    Console.WriteLine($"    • [{m.Salience,4:F2}] {m.Text}  (used turn {m.LastUsedTurn})");
Console.WriteLine();
Console.WriteLine("Pattern: every turn retrieves relevant memories, augments the");
Console.WriteLine("prompt, generates a reply, then writes + decays + evicts memory");
Console.WriteLine("autonomously — context accumulates without unbounded growth.");

// ── Supporting types ────────────────────────────────────────

/// <summary>A fact the responder wants to remember after a turn.</summary>
/// <param name="Text">The fact, phrased so it reads well when injected into a later prompt.</param>
/// <param name="Salience">Initial importance 0–1; higher survives decay longer.</param>
/// <param name="Tags">Optional keywords that also count toward relevance matching.</param>
record NewFact(string Text, double Salience, IReadOnlyList<string> Tags);

/// <summary>What the responder returns for a single turn.</summary>
/// <param name="Response">The reply shown to the user.</param>
/// <param name="Facts">New facts to commit to memory (may be empty).</param>
record TurnResult(string Response, IReadOnlyList<NewFact> Facts);

/// <summary>One stored memory and its bookkeeping.</summary>
record MemoryItem(string Id, string Text, double Salience, int CreatedTurn, int LastUsedTurn, IReadOnlyList<string> Tags);

/// <summary>A full record of what happened during one turn — for observability.</summary>
record MemoryTurn(
    int TurnNumber,
    string UserInput,
    IReadOnlyList<MemoryItem> Recalled,
    string Response,
    IReadOnlyList<MemoryItem> Written,
    IReadOnlyList<MemoryItem> Reinforced,
    IReadOnlyList<MemoryItem> Evicted);

/// <summary>Configuration for <see cref="MemoryAugmentedAgent"/>.</summary>
record MemoryOptions
{
    /// <summary>Maximum memories to retain; the weakest are evicted beyond this. At least 1.</summary>
    public int MaxItems { get; init; } = 8;
    /// <summary>Maximum memories to recall (and inject) per turn. At least 0.</summary>
    public int TopK { get; init; } = 3;
    /// <summary>Memories scoring below this relevance are not recalled.</summary>
    public double MinRelevanceToRecall { get; init; } = 0.05;
    /// <summary>Weight on query↔memory text overlap when ranking recall.</summary>
    public double RelevanceWeight { get; init; } = 1.0;
    /// <summary>Weight on how recently a memory was used when ranking recall.</summary>
    public double RecencyWeight { get; init; } = 0.3;
    /// <summary>Weight on a memory's salience when ranking recall.</summary>
    public double SalienceWeight { get; init; } = 0.25;
    /// <summary>How much salience every memory loses each turn (0 disables decay).</summary>
    public double DecayPerTurn { get; init; } = 0.1;
    /// <summary>Jaccard overlap at/above which a new fact reinforces an existing memory instead of adding a duplicate.</summary>
    public double DuplicateThreshold { get; init; } = 0.6;
    /// <summary>Observability hook fired once per completed turn.</summary>
    public Action<MemoryTurn>? OnTurn { get; init; }
}

/// <summary>
/// A conversational agent with self-managing working memory. Each turn it
/// retrieves the most relevant stored facts, lets an injected responder use
/// them, writes new facts back, refreshes recalled ones, decays salience, and
/// evicts the weakest memories when the store is over budget.
///
/// The responder is an injected delegate so the loop runs deterministically in
/// tests and wires to real LLM calls in production.
/// </summary>
class MemoryAugmentedAgent
{
    private readonly List<MemoryItem> _memory = new();
    private int _turn;
    private int _idSeq;

    public MemoryOptions Options { get; }

    /// <summary>The current memory snapshot (read-only view).</summary>
    public IReadOnlyList<MemoryItem> Memory => _memory.AsReadOnly();

    public MemoryAugmentedAgent(MemoryOptions options) => Options = options;

    /// <summary>Run one conversational turn with a synchronous responder.</summary>
    public Task<MemoryTurn> ChatAsync(
        string userInput,
        Func<string, IReadOnlyList<MemoryItem>, int, TurnResult> respond,
        CancellationToken ct = default)
        => ChatAsync(userInput, (input, recalled, turn, _) => Task.FromResult(respond(input, recalled, turn)), ct);

    /// <summary>Run one conversational turn with an async responder (e.g. a real model call).</summary>
    public async Task<MemoryTurn> ChatAsync(
        string userInput,
        Func<string, IReadOnlyList<MemoryItem>, int, CancellationToken, Task<TurnResult>> respond,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _turn++;

        // ── RETRIEVE: rank memories and pick the most useful for this turn. ──
        var recalled = Retrieve(userInput);

        // ── AUGMENT + GENERATE: the responder sees the recalled context. ──
        var result = await respond(userInput, recalled, _turn, ct);

        // Recalling a memory refreshes it — it just proved useful.
        foreach (var item in recalled)
            Touch(item.Id);

        // ── REMEMBER: write new facts, reinforcing near-duplicates. ──
        var written = new List<MemoryItem>();
        var reinforced = new List<MemoryItem>();
        foreach (var fact in result.Facts ?? Array.Empty<NewFact>())
        {
            var dup = FindDuplicate(fact.Text);
            if (dup is not null)
            {
                // Reinforce: bump salience and recency instead of storing a copy.
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

        // ── MAINTAIN: decay everything, then evict the weakest over budget. ──
        Decay();
        var evicted = EvictOverBudget();

        var turnRecord = new MemoryTurn(_turn, userInput, recalled, result.Response, written, reinforced, evicted);
        Options.OnTurn?.Invoke(turnRecord);
        return turnRecord;
    }

    /// <summary>Rank memories for a query and return the top-K above the relevance floor.</summary>
    public IReadOnlyList<MemoryItem> Retrieve(string query)
    {
        var topK = Math.Max(0, Options.TopK);
        if (topK == 0 || _memory.Count == 0) return Array.Empty<MemoryItem>();

        var queryTokens = Tokenize(query);

        var ranked = _memory
            .Select(m => new { Item = m, Relevance = Relevance(queryTokens, m) })
            .Where(x => x.Relevance >= Options.MinRelevanceToRecall)
            .Select(x => new
            {
                x.Item,
                Score = Options.RelevanceWeight * x.Relevance
                      + Options.RecencyWeight * Recency(x.Item)
                      + Options.SalienceWeight * x.Item.Salience
            })
            // Deterministic tie-break: score, then most-recent, then id.
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.LastUsedTurn)
            .ThenBy(x => x.Item.Id, StringComparer.Ordinal)
            .Take(topK)
            .Select(x => x.Item)
            .ToList();

        return ranked;
    }

    // ── Memory mechanics ─────────────────────────────────────

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

        // Keep-score blends salience and recency; lowest is evicted first.
        // Deterministic: keep-score asc, then oldest use, then id.
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
        // Adding the tag verbatim instead would glue the words into one token
        // that no tokenized query term can ever match, silently dropping the
        // tag from recall.
        foreach (var tag in m.Tags) itemTokens.UnionWith(Tokenize(tag));
        return Jaccard(queryTokens, itemTokens);
    }

    // ── Pure helpers ─────────────────────────────────────────

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
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else FlushToken(sb, set);
        }
        FlushToken(sb, set);
        return set;
    }

    private static void FlushToken(StringBuilder sb, HashSet<string> set)
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
