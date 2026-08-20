using Xunit;

namespace AgenticRecipes.Tests;

public class SelfConsistencyTests
{
    private static ReasoningSample S(string answer, double confidence = 1.0, string reasoning = "r")
        => new(reasoning, answer, confidence);

    private static EnsembleResult Vote(IEnumerable<ReasoningSample> samples, EnsembleOptions? opts = null)
        => new EnsembleVoter(opts).Aggregate(samples.ToList());

    // ── Core verdicts ──────────────────────────────────────────

    [Fact]
    public void StrongMajority_IsConfident()
    {
        var r = Vote(new[] { S("9"), S("9"), S("9"), S("9"), S("8") });

        Assert.Equal(EnsembleVerdict.Confident, r.Verdict);
        Assert.Equal("9", r.Answer);
        Assert.Equal(0.8, r.Consensus, 3);
        Assert.Equal(5, r.Samples.Count);
    }

    [Fact]
    public void EvenSplit_Abstains_AndReturnsNullAnswer()
    {
        // A dead 50/50 tie: with a majority floor above one-half, the ensemble
        // refuses to crown either side and abstains.
        var r = Vote(
            new[] { S("yes"), S("yes"), S("no"), S("no") },
            new EnsembleOptions { MinConsensus = 0.51 });

        Assert.Equal(EnsembleVerdict.Abstained, r.Verdict);
        Assert.Null(r.Answer);
        Assert.Equal(0.5, r.Consensus, 3);

        // Even on a dead tie the display tally is deterministic: both sides have
        // equal vote mass, so the tie breaks by first appearance — "yes" (index 0)
        // heads the tally ahead of "no" (index 1). This pins the recipe's Q2
        // console output (Tally: yes=2  no=2) so the README stays honest.
        Assert.Equal("yes", r.Tally[0].Answer);
        Assert.Equal("no", r.Tally[1].Answer);
    }

    [Fact]
    public void EvenSplit_WithDefaultFloor_IsTentative()
    {
        // Same 50/50 split but with the default 0.40 floor: 0.50 clears it, so a
        // (weak) winner is reported rather than an abstention.
        var r = Vote(new[] { S("yes"), S("yes"), S("no"), S("no") });

        Assert.Equal(EnsembleVerdict.Tentative, r.Verdict);
        Assert.Equal(0.5, r.Consensus, 3);
    }

    [Fact]
    public void PluralityBetweenThresholds_IsTentative()
    {
        // 3/5 = 0.60 consensus: above MinConsensus 0.40, below ConfidentConsensus 0.66.
        var r = Vote(
            new[] { S("a"), S("a"), S("a"), S("b"), S("c") },
            new EnsembleOptions { ConfidentConsensus = 0.66, MinConsensus = 0.40 });

        Assert.Equal(EnsembleVerdict.Tentative, r.Verdict);
        Assert.Equal("a", r.Answer);
        Assert.Equal(0.6, r.Consensus, 3);
    }

    [Fact]
    public void Unanimous_IsConfident_WithFullConsensus()
    {
        var r = Vote(new[] { S("42"), S("42"), S("42") });

        Assert.Equal(EnsembleVerdict.Confident, r.Verdict);
        Assert.Equal("42", r.Answer);
        Assert.Equal(1.0, r.Consensus, 3);
    }

    // ── Threshold boundaries ───────────────────────────────────

    [Fact]
    public void ConsensusExactlyAtConfidentBar_IsConfident()
    {
        // 2/3 ≈ 0.6667 ≥ 0.66 → Confident.
        var r = Vote(
            new[] { S("x"), S("x"), S("y") },
            new EnsembleOptions { ConfidentConsensus = 0.66, MinConsensus = 0.40 });

        Assert.Equal(EnsembleVerdict.Confident, r.Verdict);
    }

    [Fact]
    public void ConsensusExactlyAtMinBar_IsTentative_NotAbstained()
    {
        // 2/5 = 0.40 == MinConsensus → Tentative (boundary is inclusive).
        var r = Vote(
            new[] { S("x"), S("x"), S("y"), S("z"), S("w") },
            new EnsembleOptions { ConfidentConsensus = 0.66, MinConsensus = 0.40 });

        Assert.Equal(EnsembleVerdict.Tentative, r.Verdict);
        Assert.Equal("x", r.Answer);
    }

    // ── Answer normalization ───────────────────────────────────

    [Fact]
    public void DefaultNormalization_FoldsCaseAndWhitespace()
    {
        var r = Vote(new[] { S(" Paris "), S("paris"), S("PARIS"), S("London") });

        Assert.Equal(EnsembleVerdict.Confident, r.Verdict);
        // Display keeps the first original spelling.
        Assert.Equal(" Paris ", r.Answer);
        Assert.Equal(0.75, r.Consensus, 3);
    }

    [Fact]
    public void CustomNormalizer_FoldsEquivalentForms()
    {
        var opts = new EnsembleOptions
        {
            NormalizeAnswer = a => a.Trim().TrimEnd('.').ToLowerInvariant()
        };
        var r = Vote(new[] { S("Four."), S("four"), S("FOUR"), S("five") }, opts);

        Assert.Equal(EnsembleVerdict.Confident, r.Verdict);
        Assert.Equal("Four.", r.Answer);
    }

    [Fact]
    public void Tally_CollapsesNormalizedAnswersWithCounts()
    {
        var r = Vote(new[] { S("yes"), S("Yes"), S("no") });

        var yes = r.Tally.Single(t => t.Answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, yes.Count);
        Assert.Equal(2.0, yes.Votes, 3);
        Assert.Equal(2, r.Tally.Count); // yes + no
    }

    // ── Confidence weighting ───────────────────────────────────

    [Fact]
    public void WeightByConfidence_LetsConvictionBeatHeadcount()
    {
        // Head-count: B wins 3-2. Weighted: A (0.97+0.95=1.92) beats B (0.41+0.38+0.44=1.23).
        var samples = new[]
        {
            S("A", 0.97), S("A", 0.95),
            S("B", 0.41), S("B", 0.38), S("B", 0.44),
        };

        var weighted = Vote(samples, new EnsembleOptions { WeightByConfidence = true, ConfidentConsensus = 0.60 });
        Assert.Equal("A", weighted.Answer);

        var flat = Vote(samples); // default: 1 vote each → B wins
        Assert.Equal("B", flat.Answer);
    }

    [Fact]
    public void WeightByConfidence_ComputesConsensusOverTotalWeight()
    {
        var samples = new[] { S("A", 0.8), S("A", 0.6), S("B", 0.2) };
        var r = Vote(samples, new EnsembleOptions { WeightByConfidence = true });

        // A weight 1.4 / total 1.6 = 0.875
        Assert.Equal("A", r.Answer);
        Assert.Equal(0.875, r.Consensus, 3);
        Assert.Equal(1.6, r.TotalWeight, 3);
    }

    [Fact]
    public void WeightByConfidence_ConsensusEqualsWinningVotesOverTotalWeight_Exactly()
    {
        // Regression: consensus must be computed from the RAW winner mass and total,
        // not from the display-rounded tally. With repeating-decimal confidences the
        // winner mass has >4 significant decimals, so rounding it before the division
        // (the old bug) made Consensus disagree with both the true ratio AND the
        // reported WinningVotes / TotalWeight. Pin all three to the same value.
        var samples = new[] { S("A", 1.0 / 3.0), S("A", 1.0 / 3.0), S("B", 0.1) };
        var r = Vote(samples, new EnsembleOptions { WeightByConfidence = true });

        var expected = (2.0 / 3.0) / (2.0 / 3.0 + 0.1); // raw winner / raw total
        Assert.Equal("A", r.Answer);
        Assert.Equal(expected, r.Consensus, 12); // Consensus is exact, not display-rounded.
        // WinningVotes / TotalWeight are now reported RAW (only the per-answer Tally is
        // display-rounded), so recomputing the ratio from them equals Consensus EXACTLY,
        // not merely to display precision.
        Assert.Equal(r.Consensus, r.WinningVotes / r.TotalWeight, 12);
    }

    [Fact]
    public void WeightByConfidence_AllZeroConfidence_AbstainsInsteadOfDivideByZero()
    {
        var samples = new[] { S("A", 0.0), S("B", 0.0) };
        var r = Vote(samples, new EnsembleOptions { WeightByConfidence = true });

        Assert.Equal(EnsembleVerdict.Abstained, r.Verdict);
        Assert.Equal(0.0, r.Consensus, 3);
        Assert.Null(r.Answer);
    }

    [Fact]
    public void WeightByConfidence_ClampsOutOfRangeConfidence()
    {
        // Confidence > 1 and < 0 are clamped to [0,1] so weights stay sane.
        var samples = new[] { S("A", 5.0), S("B", -3.0) };
        var r = Vote(samples, new EnsembleOptions { WeightByConfidence = true });

        Assert.Equal("A", r.Answer);
        Assert.Equal(1.0, r.TotalWeight, 3); // 1.0 (clamped) + 0.0 (clamped)
        Assert.Equal(1.0, r.Consensus, 3);
    }

    // ── Determinism / tie-breaking ─────────────────────────────

    [Fact]
    public void Ties_BreakByFirstAppearance_Deterministically()
    {
        // a and b each get 1 vote; a appears first → a wins the tally head.
        var r = Vote(new[] { S("a"), S("b") }, new EnsembleOptions { MinConsensus = 0.0 });

        Assert.Equal("a", r.Tally[0].Answer);
        Assert.Equal("b", r.Tally[1].Answer);
        Assert.Equal("a", r.Answer);
    }

    [Fact]
    public void Tally_IsSortedByVoteMassDescending()
    {
        var r = Vote(new[] { S("low"), S("high"), S("high"), S("high"), S("mid"), S("mid") });

        Assert.Equal("high", r.Tally[0].Answer);
        Assert.Equal("mid", r.Tally[1].Answer);
        Assert.Equal("low", r.Tally[2].Answer);
    }

    [Fact]
    public void SingleSample_IsConfident()
    {
        var r = Vote(new[] { S("only") });

        Assert.Equal(EnsembleVerdict.Confident, r.Verdict);
        Assert.Equal("only", r.Answer);
        Assert.Equal(1.0, r.Consensus, 3);
    }

    // ── RunAsync sampling ──────────────────────────────────────

    [Fact]
    public async Task RunAsync_CallsSamplerOncePerIndex()
    {
        var seen = new List<int>();
        var voter = new EnsembleVoter();

        var r = await voter.RunAsync(4, (i, ct) =>
        {
            seen.Add(i);
            return Task.FromResult(S("same"));
        });

        Assert.Equal(new[] { 0, 1, 2, 3 }, seen);
        Assert.Equal(4, r.Samples.Count);
        Assert.Equal("same", r.Answer);
    }

    [Fact]
    public async Task RunAsync_OnSampleHook_FiresPerSample()
    {
        var hits = new List<(int Index, string Answer)>();
        var voter = new EnsembleVoter(new EnsembleOptions
        {
            OnSample = (i, s) => hits.Add((i, s.Answer))
        });

        await voter.RunAsync(3, (i, ct) => Task.FromResult(S($"ans{i}")));

        Assert.Equal(3, hits.Count);
        Assert.Equal((0, "ans0"), hits[0]);
        Assert.Equal((2, "ans2"), hits[2]);
    }

    [Fact]
    public async Task RunAsync_PropagatesAnswersIntoVote()
    {
        var answers = new[] { "yes", "yes", "no" };
        var voter = new EnsembleVoter(new EnsembleOptions { ConfidentConsensus = 0.6 });

        var r = await voter.RunAsync(answers.Length, (i, ct) => Task.FromResult(S(answers[i])));

        Assert.Equal("yes", r.Answer);
        Assert.Equal(EnsembleVerdict.Confident, r.Verdict);
    }

    [Fact]
    public async Task RunAsync_ZeroSamples_Throws()
    {
        var voter = new EnsembleVoter();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            voter.RunAsync(0, (i, ct) => Task.FromResult(S("x"))));
    }

    [Fact]
    public async Task RunAsync_Cancellation_Propagates()
    {
        var voter = new EnsembleVoter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            voter.RunAsync(3, (i, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(S("x"));
            }, cts.Token));
    }

    // ── Aggregate guards ───────────────────────────────────────

    [Fact]
    public void Aggregate_EmptyList_Throws()
    {
        var voter = new EnsembleVoter();
        Assert.Throws<ArgumentException>(() => voter.Aggregate(new List<ReasoningSample>()));
    }

    [Fact]
    public void DefaultOptions_UseTwoThirdsAndFortyPercent()
    {
        var o = new EnsembleOptions();
        Assert.Equal(0.66, o.ConfidentConsensus, 3);
        Assert.Equal(0.40, o.MinConsensus, 3);
        Assert.False(o.WeightByConfidence);
    }
}

// ══════════════════════════════════════════════════════════════
//  Supporting types — mirrored from recipes/self-consistency/Program.cs
//  (recipe programs are standalone executables, so the logic under test
//   is copied here per the repo's testing convention).
// ══════════════════════════════════════════════════════════════

record ReasoningSample(string Reasoning, string Answer, double Confidence = 1.0);

record VoteTally(string Answer, double Votes, int Count);

record EnsembleResult(
    EnsembleVerdict Verdict,
    string? Answer,
    double Consensus,
    double WinningVotes,
    double TotalWeight,
    IReadOnlyList<VoteTally> Tally,
    IReadOnlyList<ReasoningSample> Samples);

enum EnsembleVerdict
{
    Confident,
    Tentative,
    Abstained,
}

record EnsembleOptions
{
    public double ConfidentConsensus { get; init; } = 0.66;
    public double MinConsensus { get; init; } = 0.40;
    public bool WeightByConfidence { get; init; } = false;
    public Func<string, string>? NormalizeAnswer { get; init; }
    public Action<int, ReasoningSample>? OnSample { get; init; }
}

class EnsembleVoter
{
    private readonly EnsembleOptions _options;

    public EnsembleVoter(EnsembleOptions? options = null) => _options = options ?? new EnsembleOptions();

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

    public EnsembleResult Aggregate(IReadOnlyList<ReasoningSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) throw new ArgumentException("No samples to aggregate.", nameof(samples));

        var normalize = _options.NormalizeAnswer ?? DefaultNormalize;

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
        // Consensus is computed from the RAW (unrounded) winner mass and total. The
        // reported WinningVotes/TotalWeight are RAW too (only the display Tally is
        // rounded), so Consensus == WinningVotes / TotalWeight holds exactly.
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
