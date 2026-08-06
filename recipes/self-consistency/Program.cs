using Prompt;

// ──────────────────────────────────────────────────────────────
// Self-Consistency / Ensemble Voting Recipe
// Pattern: Sample N → Vote → Decide (fan-out + aggregation)
//
// A single model call is a single roll of the dice — one greedy
// chain of thought that can quietly go wrong. Self-Consistency
// samples the SAME question several independent times, lets each
// run reason on its own, then takes a VOTE over the final answers.
// The agency is in the aggregation: the ensemble reports how much
// it agreed with itself and AUTONOMOUSLY ABSTAINS when consensus
// is too weak to trust, instead of confidently returning a guess.
// ──────────────────────────────────────────────────────────────

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Self-Consistency / Ensemble Voting Recipe");
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine();

// ── Scenario 1: a strong majority → Confident ──────────────────
// Five independent reasoning paths on the same word problem. Four
// land on "9", one slips to "8". The ensemble outvotes the slip.
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine("Q1: \"A farmer has 17 sheep. All but 9 run away. How many remain?\"");
Console.WriteLine();

var arithmeticSamples = new[]
{
    new ReasoningSample("'all but 9 run away' means 9 stay behind", "9", 0.86),
    new ReasoningSample("17 - 8 = 9 ran, so 9 are left... the survivors are 9", "9", 0.74),
    new ReasoningSample("trick question; 'all but 9' = 9 remain", "9", 0.91),
    new ReasoningSample("counted the ones that left: 8 left, 9 remain", "9", 0.80),
    new ReasoningSample("17 - 9 = 8 run away so 8 remain", "8", 0.55), // the slip
};

var voter = new EnsembleVoter(new EnsembleOptions
{
    ConfidentConsensus = 0.66,   // ≥2/3 agree → Confident
    MinConsensus = 0.40,         // below this → Abstain
    NormalizeAnswer = a => a.Trim().TrimEnd('.').ToLowerInvariant(),
    OnSample = (i, s) =>
        Console.WriteLine($"  🎲 path {i + 1}: answer={s.Answer,-4} conf={s.Confidence:P0}  ⟨{s.Reasoning}⟩"),
});

var r1 = await voter.RunAsync(arithmeticSamples.Length,
    (i, ct) => Task.FromResult(arithmeticSamples[i]));
PrintVerdict(r1);

// ── Scenario 2: a dead heat → Abstain ──────────────────────────
// An ambiguous question splits the ensemble. No answer clears the
// confidence bar, so the agent refuses to fabricate certainty and
// escalates instead.
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine("Q2: \"Is a hot dog a sandwich?\" (intentionally divisive)");
Console.WriteLine();

var opinionSamples = new[]
{
    new ReasoningSample("bread + filling → yes, structurally a sandwich", "yes", 0.60),
    new ReasoningSample("culturally nobody orders a 'hot dog sandwich' → no", "no", 0.62),
    new ReasoningSample("single hinged bun, not two slices → no", "no", 0.58),
    new ReasoningSample("USDA-ish definition leans sandwich → yes", "yes", 0.57),
};

// A divisive question deserves a stricter floor: require a real majority
// (>50%) to commit, so a dead 2-2 tie abstains instead of flipping a coin.
var strictVoter = new EnsembleVoter(new EnsembleOptions
{
    ConfidentConsensus = 0.66,
    MinConsensus = 0.51,
    OnSample = (i, s) =>
        Console.WriteLine($"  \ud83c\udfb2 path {i + 1}: answer={s.Answer,-4} conf={s.Confidence:P0}  ⟨{s.Reasoning}⟩"),
});

var r2 = await strictVoter.RunAsync(opinionSamples.Length,
    (i, ct) => Task.FromResult(opinionSamples[i]));
PrintVerdict(r2);

// ── Scenario 3: confidence-weighted voting flips a bare plurality ─
// Raw head-count says "B" (3 vs 2), but the two "A" voters are far
// more confident than the wishy-washy "B" bloc. With weighting on,
// conviction — not just headcount — carries the decision.
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine("Q3: \"Which algorithm is asymptotically faster?\"  (weighted vote)");
Console.WriteLine();

var weightedSamples = new[]
{
    new ReasoningSample("merge sort is O(n log n), insertion is O(n^2)", "A", 0.97),
    new ReasoningSample("for large n the n log n curve wins decisively", "A", 0.95),
    new ReasoningSample("not sure, maybe B is fine for small inputs", "B", 0.41),
    new ReasoningSample("B felt faster in one tiny test I imagined", "B", 0.38),
    new ReasoningSample("could be B if the data is nearly sorted?", "B", 0.44),
};

var weightedVoter = new EnsembleVoter(new EnsembleOptions
{
    WeightByConfidence = true,
    ConfidentConsensus = 0.60,
    MinConsensus = 0.40,
    OnSample = (i, s) =>
        Console.WriteLine($"  🎲 path {i + 1}: answer={s.Answer,-4} conf={s.Confidence:P0}  ⟨{s.Reasoning}⟩"),
});

var r3 = await weightedVoter.RunAsync(weightedSamples.Length,
    (i, ct) => Task.FromResult(weightedSamples[i]));
PrintVerdict(r3);
Console.WriteLine($"  (head-count winner would have been 'B' with 3/5 — weighting chose conviction over count)");
Console.WriteLine();

Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("Pattern: sample N → normalize → vote → gauge consensus → decide/abstain");
Console.WriteLine("More independent samples = more robust answer + an honest confidence signal.");

// ── Console rendering helper ───────────────────────────────────
static void PrintVerdict(EnsembleResult r)
{
    Console.WriteLine();
    var icon = r.Verdict switch
    {
        EnsembleVerdict.Confident => "✅",
        EnsembleVerdict.Tentative => "🤔",
        _ => "🛑",
    };
    Console.WriteLine($"  {icon} Verdict : {r.Verdict.ToString().ToUpperInvariant()}");
    Console.WriteLine($"     Answer  : {(r.Answer is null ? "— (abstained, needs escalation)" : r.Answer)}");
    Console.WriteLine($"     Consensus: {r.Consensus:P0}  ({r.WinningVotes:0.##}/{r.TotalWeight:0.##} weight, {r.Samples.Count} paths)");
    Console.WriteLine("     Tally   : " + string.Join("  ", r.Tally.Select(t => $"{t.Answer}={t.Votes:0.##}")));
    Console.WriteLine();
}

// ══════════════════════════════════════════════════════════════
//  Supporting types — the reusable recipe logic
// ══════════════════════════════════════════════════════════════

/// <summary>One independent reasoning path's outcome.</summary>
/// <param name="Reasoning">The path's chain-of-thought (for transparency / debugging).</param>
/// <param name="Answer">The final answer this path settled on.</param>
/// <param name="Confidence">The path's self-reported confidence in [0, 1].</param>
record ReasoningSample(string Reasoning, string Answer, double Confidence = 1.0);

/// <summary>Votes accumulated for one distinct (normalized) answer.</summary>
/// <param name="Answer">A representative original (un-normalized) form of the answer.</param>
/// <param name="Votes">Vote mass — a raw count, or summed confidence when weighting.</param>
/// <param name="Count">How many samples produced this answer.</param>
record VoteTally(string Answer, double Votes, int Count);

/// <summary>The ensemble's decision after voting.</summary>
record EnsembleResult(
    EnsembleVerdict Verdict,
    string? Answer,
    double Consensus,
    double WinningVotes,
    double TotalWeight,
    IReadOnlyList<VoteTally> Tally,
    IReadOnlyList<ReasoningSample> Samples);

/// <summary>How much the ensemble trusts its own vote.</summary>
enum EnsembleVerdict
{
    /// <summary>Consensus met the confident bar — return the answer.</summary>
    Confident,
    /// <summary>A winner emerged but below the confident bar — usable with caution.</summary>
    Tentative,
    /// <summary>Consensus too weak to trust — refuse and escalate.</summary>
    Abstained,
}

/// <summary>Configuration for <see cref="EnsembleVoter"/>.</summary>
record EnsembleOptions
{
    /// <summary>Consensus ratio (0–1) at or above which the verdict is <c>Confident</c>. Default 2/3.</summary>
    public double ConfidentConsensus { get; init; } = 0.66;

    /// <summary>Consensus ratio (0–1) below which the ensemble <c>Abstains</c>. Default 0.40.</summary>
    public double MinConsensus { get; init; } = 0.40;

    /// <summary>When true, votes are weighted by each sample's self-reported confidence instead of a flat 1-per-sample.</summary>
    public bool WeightByConfidence { get; init; } = false;

    /// <summary>Canonicalizes answers so equivalent forms vote together (e.g. "4", "Four", "four."). Default: trim + lowercase.</summary>
    public Func<string, string>? NormalizeAnswer { get; init; }

    /// <summary>Observability hook fired once per completed sample: <c>(index, sample)</c>.</summary>
    public Action<int, ReasoningSample>? OnSample { get; init; }
}

/// <summary>
/// Self-consistency aggregator: draws several independent samples for the same
/// question, normalizes their answers, takes a (optionally confidence-weighted)
/// majority vote, and decides whether the consensus is strong enough to trust —
/// abstaining when it is not.
/// </summary>
class EnsembleVoter
{
    private readonly EnsembleOptions _options;

    public EnsembleVoter(EnsembleOptions? options = null) => _options = options ?? new EnsembleOptions();

    /// <summary>
    /// Draw <paramref name="samples"/> independent samples via <paramref name="sampler"/>
    /// (called with the sample index), then vote and decide.
    /// </summary>
    public async Task<EnsembleResult> RunAsync(
        int samples,
        Func<int, CancellationToken, Task<ReasoningSample>> sampler,
        CancellationToken ct = default)
    {
        if (samples <= 0) throw new ArgumentOutOfRangeException(nameof(samples), "Need at least one sample.");
        ArgumentNullException.ThrowIfNull(sampler);

        var drawn = new List<ReasoningSample>(samples);
        for (var i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sample = await sampler(i, ct);
            drawn.Add(sample);
            _options.OnSample?.Invoke(i, sample);
        }

        return Aggregate(drawn);
    }

    /// <summary>Vote over an already-collected set of samples (no sampling).</summary>
    public EnsembleResult Aggregate(IReadOnlyList<ReasoningSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) throw new ArgumentException("No samples to aggregate.", nameof(samples));

        var normalize = _options.NormalizeAnswer ?? DefaultNormalize;

        // Bucket samples by their normalized answer, preserving the first original
        // spelling as the representative and the authoring order for tie-breaks.
        var order = new List<string>();
        var buckets = new Dictionary<string, (string Display, double Votes, int Count, int FirstIndex)>();

        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var key = normalize(s.Answer ?? "");
            var weight = _options.WeightByConfidence ? Math.Clamp(s.Confidence, 0.0, 1.0) : 1.0;

            if (buckets.TryGetValue(key, out var b))
                buckets[key] = (b.Display, b.Votes + weight, b.Count + 1, b.FirstIndex);
            else
            {
                buckets[key] = (s.Answer ?? "", weight, 1, i);
                order.Add(key);
            }
        }

        // Rank buckets by RAW vote mass desc, then by first appearance (stable,
        // deterministic). Ranking and consensus use the unrounded weights; rounding
        // is applied only when projecting into the display tally below, so the
        // reported Consensus stays exactly WinningVotes / TotalWeight.
        var ranked = order
            .Select(k => buckets[k])
            .OrderByDescending(v => v.Votes)
            .ThenBy(v => v.FirstIndex)
            .ToList();

        var tally = ranked
            .Select(v => new VoteTally(v.Display, Round(v.Votes), v.Count))
            .ToList();

        var totalWeight = _options.WeightByConfidence
            ? samples.Sum(s => Math.Clamp(s.Confidence, 0.0, 1.0))
            : samples.Count;

        var winner = ranked[0];
        // Consensus: winner share of total weight, computed from the RAW (unrounded)
        // winner mass and total. WinningVotes/TotalWeight are reported RAW too (only the
        // per-answer display Tally above is rounded), so the identity
        // Consensus == WinningVotes / TotalWeight holds exactly for callers that recompute
        // it. Guard against an all-zero-confidence weighted ensemble (everyone maximally
        // unsure) → treat as zero consensus.
        var consensus = totalWeight > 0 ? winner.Votes / totalWeight : 0.0;

        var verdict = consensus >= _options.ConfidentConsensus ? EnsembleVerdict.Confident
                    : consensus >= _options.MinConsensus ? EnsembleVerdict.Tentative
                    : EnsembleVerdict.Abstained;

        var answer = verdict == EnsembleVerdict.Abstained ? null : winner.Display;

        return new EnsembleResult(
            Verdict: verdict,
            Answer: answer,
            Consensus: consensus,
            WinningVotes: winner.Votes,
            TotalWeight: totalWeight,
            Tally: tally,
            Samples: samples);
    }

    private static string DefaultNormalize(string answer) => answer.Trim().ToLowerInvariant();

    private static double Round(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
}
