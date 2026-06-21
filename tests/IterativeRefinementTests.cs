using Xunit;

namespace AgenticRecipes.Tests;

public class IterativeRefinementTests
{
    // A generator that improves by one rubric point each round (driven by feedback count).
    private static string ImprovingGenerator(string task, IReadOnlyList<string> feedback, int iteration)
        => $"draft|addressed={feedback.Count}|iter={iteration}";

    private static int AddressedCount(string draft)
    {
        var marker = "addressed=";
        var idx = draft.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return 0;
        var rest = new string(draft[(idx + marker.Length)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(rest, out var n) ? n : 0;
    }

    // A critic whose score rises 20 points per addressed issue (40 → 60 → 80 → 100),
    // always leaving one open issue until perfect so feedback keeps accumulating.
    private static Critique RisingCritic(string task, string draft)
    {
        var addressed = AddressedCount(draft);
        double score = Math.Min(100, 40 + addressed * 20);
        var issues = score >= 100
            ? new List<string>()
            : new List<string> { $"fix item #{addressed + 1}" };
        return new Critique(score, score >= 100 ? "perfect" : "keep going", issues);
    }

    [Fact]
    public async Task RefineAsync_ReachesTarget_StopsEarlyWithTargetReached()
    {
        var refiner = new IterativeRefiner(new RefinerOptions { TargetScore = 80, MaxIterations = 10 });

        var result = await refiner.RefineAsync("task", ImprovingGenerator, RisingCritic);

        Assert.Equal(StopReason.TargetReached, result.StopReason);
        Assert.True(result.TargetMet);
        Assert.True(result.BestScore >= 80);
        // 40 → 60 → 80 reaches 80 on the third round.
        Assert.Equal(3, result.Iterations.Count);
    }

    [Fact]
    public async Task RefineAsync_BudgetTooSmallForTarget_StopsWithBudgetExhausted()
    {
        var refiner = new IterativeRefiner(new RefinerOptions
        {
            TargetScore = 100,
            MaxIterations = 2,
            // Generous improvement requirement so plateau never trips before the budget.
            MinImprovement = 1.0,
            PlateauPatience = 99
        });

        var result = await refiner.RefineAsync("task", ImprovingGenerator, RisingCritic);

        Assert.Equal(StopReason.BudgetExhausted, result.StopReason);
        Assert.False(result.TargetMet);
        Assert.Equal(2, result.Iterations.Count);
        Assert.Equal(60, result.BestScore); // 40 then 60, budget done
    }

    [Fact]
    public async Task RefineAsync_ScorePlateaus_StopsWithPlateaued()
    {
        // Generator that can never improve: critic always returns the same score.
        var refiner = new IterativeRefiner(new RefinerOptions
        {
            TargetScore = 95,
            MaxIterations = 10,
            MinImprovement = 5.0,
            PlateauPatience = 2
        });

        var result = await refiner.RefineAsync(
            "task",
            generate: (t, fb, i) => $"v{i}",
            critique: (t, draft) => new Critique(50, "flat", new List<string> { "noop" }));

        Assert.Equal(StopReason.Plateaued, result.StopReason);
        Assert.False(result.TargetMet);
        // Round 1 sets the baseline; rounds 2 and 3 are stale → stop after 3.
        Assert.Equal(3, result.Iterations.Count);
        Assert.Equal(50, result.BestScore);
    }

    [Fact]
    public async Task RefineAsync_ReturnsBestDraft_NotNecessarilyLast()
    {
        // Scores go 50 → 90 → 30: the peak is round 2, which must be returned
        // even though a later round regressed.
        var scores = new Queue<double>(new double[] { 50, 90, 30 });
        var refiner = new IterativeRefiner(new RefinerOptions
        {
            TargetScore = 200,        // unreachable so all rounds run
            MaxIterations = 3,
            MinImprovement = -1000,   // never treat anything as a plateau
            PlateauPatience = 99
        });

        var result = await refiner.RefineAsync(
            "task",
            generate: (t, fb, i) => $"draft-{i}",
            critique: (t, draft) => new Critique(scores.Dequeue(), "fb", new List<string> { "x" }));

        Assert.Equal(3, result.Iterations.Count);
        Assert.Equal(90, result.BestScore);
        Assert.Equal(2, result.BestIteration);
        Assert.Equal("draft-2", result.BestDraft);
    }

    [Fact]
    public async Task RefineAsync_AccumulatesFeedback_AcrossRounds()
    {
        var feedbackSizes = new List<int>();
        var refiner = new IterativeRefiner(new RefinerOptions
        {
            TargetScore = 1000,
            MaxIterations = 3,
            MinImprovement = -1000,
            PlateauPatience = 99
        });

        await refiner.RefineAsync(
            "task",
            generate: (t, fb, i) =>
            {
                feedbackSizes.Add(fb.Count);
                return $"v{i}";
            },
            critique: (t, draft) => new Critique(10, "fb", new List<string> { "issue" }));

        // Round 1 sees 0 prior notes, round 2 sees 1, round 3 sees 2.
        Assert.Equal(new[] { 0, 1, 2 }, feedbackSizes);
    }

    [Fact]
    public async Task RefineAsync_OnIteration_FiresOncePerRound()
    {
        var observed = new List<int>();
        var refiner = new IterativeRefiner(new RefinerOptions
        {
            TargetScore = 1000,
            MaxIterations = 4,
            MinImprovement = -1000,
            PlateauPatience = 99,
            OnIteration = step => observed.Add(step.Iteration)
        });

        await refiner.RefineAsync(
            "task",
            generate: (t, fb, i) => $"v{i}",
            critique: (t, draft) => new Critique(42, "fb", new List<string>()));

        Assert.Equal(new[] { 1, 2, 3, 4 }, observed);
    }

    [Fact]
    public async Task RefineAsync_ClampsScores_IntoZeroHundredRange()
    {
        var refiner = new IterativeRefiner(new RefinerOptions { TargetScore = 1000, MaxIterations = 1 });

        var result = await refiner.RefineAsync(
            "task",
            generate: (t, fb, i) => "v",
            critique: (t, draft) => new Critique(250, "over the top", new List<string>()));

        Assert.Equal(100, result.Iterations[0].Score);
        Assert.Equal(100, result.BestScore);
    }

    [Fact]
    public async Task RefineAsync_NegativeScore_ClampsToZero()
    {
        var refiner = new IterativeRefiner(new RefinerOptions { TargetScore = 1000, MaxIterations = 1 });

        var result = await refiner.RefineAsync(
            "task",
            generate: (t, fb, i) => "v",
            critique: (t, draft) => new Critique(-50, "negative", new List<string>()));

        Assert.Equal(0, result.Iterations[0].Score);
        Assert.Equal(0, result.BestScore);
    }

    [Fact]
    public async Task RefineAsync_MaxIterationsZero_RunsAtLeastOnce()
    {
        var refiner = new IterativeRefiner(new RefinerOptions { TargetScore = 1000, MaxIterations = 0 });

        var result = await refiner.RefineAsync(
            "task",
            generate: (t, fb, i) => "only",
            critique: (t, draft) => new Critique(10, "fb", new List<string>()));

        Assert.Single(result.Iterations);
    }

    [Fact]
    public async Task RefineAsync_TargetHitOnFirstRound_StopsImmediately()
    {
        var refiner = new IterativeRefiner(new RefinerOptions { TargetScore = 50, MaxIterations = 5 });

        var result = await refiner.RefineAsync(
            "task",
            generate: (t, fb, i) => "great",
            critique: (t, draft) => new Critique(90, "nailed it", new List<string>()));

        Assert.Single(result.Iterations);
        Assert.Equal(StopReason.TargetReached, result.StopReason);
        Assert.True(result.TargetMet);
    }

    [Fact]
    public void RefinementStep_Verdict_BucketsByScore()
    {
        Assert.Equal("ship-ready", new RefinementStep(1, "d", 85, "f", new List<string>()).Verdict);
        Assert.Equal("promising", new RefinementStep(1, "d", 65, "f", new List<string>()).Verdict);
        Assert.Equal("needs work", new RefinementStep(1, "d", 64.9, "f", new List<string>()).Verdict);
    }

    [Fact]
    public async Task RefineAsync_FeedbackContent_IsTopOpenIssue()
    {
        string? secondRoundFeedback = null;
        var refiner = new IterativeRefiner(new RefinerOptions
        {
            TargetScore = 1000,
            MaxIterations = 2,
            MinImprovement = -1000,
            PlateauPatience = 99
        });

        await refiner.RefineAsync(
            "task",
            generate: (t, fb, i) =>
            {
                if (i == 2) secondRoundFeedback = fb.Count > 0 ? fb[0] : null;
                return $"v{i}";
            },
            critique: (t, draft) => new Critique(10, "fb",
                new List<string> { "most important", "secondary" }));

        Assert.Equal("most important", secondRoundFeedback);
    }

    [Fact]
    public async Task RefineAsync_AlreadyCancelled_Throws()
    {
        var refiner = new IterativeRefiner(new RefinerOptions { MaxIterations = 3 });
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await refiner.RefineAsync(
                "task",
                generate: (t, fb, i, ct) => Task.FromResult("v"),
                critique: (t, draft, ct) => Task.FromResult(new Critique(10, "fb", new List<string>())),
                cts.Token));
    }

    [Fact]
    public async Task RefineAsync_AsyncDelegates_AreAwaited()
    {
        var refiner = new IterativeRefiner(new RefinerOptions { TargetScore = 80, MaxIterations = 5 });

        var result = await refiner.RefineAsync(
            "task",
            generate: async (t, fb, i, ct) =>
            {
                await Task.Yield();
                return $"draft|addressed={fb.Count}|iter={i}";
            },
            critique: async (t, draft, ct) =>
            {
                await Task.Yield();
                return RisingCritic(t, draft);
            });

        Assert.Equal(StopReason.TargetReached, result.StopReason);
        Assert.True(result.BestScore >= 80);
    }
}

// ── Supporting types (mirrors recipes/iterative-refinement/Program.cs) ──

record Critique(double Score, string Feedback, IReadOnlyList<string> Issues);

record RefinementStep(int Iteration, string Draft, double Score, string Feedback, IReadOnlyList<string> TopIssues)
{
    public string Verdict => Score >= 85 ? "ship-ready" : Score >= 65 ? "promising" : "needs work";
}

enum StopReason
{
    TargetReached,
    BudgetExhausted,
    Plateaued
}

record RefinerOptions
{
    public double TargetScore { get; init; } = 85;
    public int MaxIterations { get; init; } = 5;
    public double MinImprovement { get; init; } = 2.0;
    public int PlateauPatience { get; init; } = 2;
    public Action<RefinementStep>? OnIteration { get; init; }
}

record RefinementResult(
    string BestDraft,
    double BestScore,
    int BestIteration,
    StopReason StopReason,
    bool TargetMet,
    IReadOnlyList<RefinementStep> Iterations);

class IterativeRefiner
{
    private readonly RefinerOptions _options;

    public IterativeRefiner(RefinerOptions options) => _options = options;

    public async Task<RefinementResult> RefineAsync(
        string task,
        Func<string, IReadOnlyList<string>, int, string> generate,
        Func<string, string, Critique> critique,
        CancellationToken ct = default)
    {
        return await RefineAsync(
            task,
            (t, fb, i, _) => Task.FromResult(generate(t, fb, i)),
            (t, draft, _) => Task.FromResult(critique(t, draft)),
            ct);
    }

    public async Task<RefinementResult> RefineAsync(
        string task,
        Func<string, IReadOnlyList<string>, int, CancellationToken, Task<string>> generate,
        Func<string, string, CancellationToken, Task<Critique>> critique,
        CancellationToken ct = default)
    {
        var maxIterations = Math.Max(1, _options.MaxIterations);
        var steps = new List<RefinementStep>();
        var feedback = new List<string>();

        string bestDraft = "";
        double bestScore = double.NegativeInfinity;
        int bestIteration = 0;
        double previousScore = double.NegativeInfinity;
        int staleRounds = 0;
        var stopReason = StopReason.BudgetExhausted;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var draft = await generate(task, feedback.AsReadOnly(), iteration, ct);
            var verdict = await critique(task, draft, ct);
            var score = Clamp(verdict.Score, 0, 100);

            var step = new RefinementStep(iteration, draft, score, verdict.Feedback, verdict.Issues);
            steps.Add(step);
            _options.OnIteration?.Invoke(step);

            if (score > bestScore)
            {
                bestScore = score;
                bestDraft = draft;
                bestIteration = iteration;
            }

            if (score >= _options.TargetScore)
            {
                stopReason = StopReason.TargetReached;
                break;
            }

            if (iteration > 1 && score - previousScore < _options.MinImprovement)
            {
                staleRounds++;
                if (staleRounds >= Math.Max(1, _options.PlateauPatience))
                {
                    stopReason = StopReason.Plateaued;
                    break;
                }
            }
            else
            {
                staleRounds = 0;
            }

            previousScore = score;

            if (verdict.Issues.Count > 0)
                feedback.Add(verdict.Issues[0]);
        }

        return new RefinementResult(
            bestDraft,
            bestScore < 0 ? 0 : bestScore,
            bestIteration,
            stopReason,
            bestScore >= _options.TargetScore,
            steps);
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}
