using Xunit;

namespace AgenticRecipes.Tests;

public class ReflexionTests
{
    // ── Helpers ──────────────────────────────────────────────

    // An actor that includes a guard only once the matching lesson is in memory.
    // Mirrors the recipe's binary_search scenario: the agent learns to sort-check,
    // handle empty input, and return the leftmost duplicate one lesson at a time.
    private static string LearningActor(string task, IReadOnlyList<string> lessons, int trial)
    {
        var parts = new List<string> { "core" };
        if (lessons.Any(l => l.Contains("sort", StringComparison.OrdinalIgnoreCase))) parts.Add("is_sorted");
        if (lessons.Any(l => l.Contains("empty", StringComparison.OrdinalIgnoreCase))) parts.Add("if not xs");
        if (lessons.Any(l => l.Contains("duplicate", StringComparison.OrdinalIgnoreCase))) parts.Add("leftmost");
        return string.Join("+", parts);
    }

    private static ReflexionEvaluation RubricEvaluator(string task, string action)
    {
        var missing = new List<string>();
        if (!action.Contains("is_sorted")) missing.Add("input must be sorted");
        if (!action.Contains("if not xs")) missing.Add("empty input handling");
        if (!action.Contains("leftmost")) missing.Add("duplicate leftmost match");
        double reward = (3 - missing.Count) / 3.0;
        return new ReflexionEvaluation(reward, missing.Count == 0, "fb", missing);
    }

    private static string? RubricReflector(
        string task, string action, ReflexionEvaluation eval, IReadOnlyList<string> prior)
    {
        if (eval.OpenIssues.Count == 0) return null;
        var issue = eval.OpenIssues[0];
        string lesson =
            issue.Contains("sorted") ? "Lesson: sort first"
            : issue.Contains("empty") ? "Lesson: handle empty"
            : issue.Contains("duplicate") ? "Lesson: duplicate leftmost"
            : $"Lesson: {issue}";
        return prior.Contains(lesson) ? null : lesson;
    }

    // ── Tests ────────────────────────────────────────────────

    [Fact]
    public async Task SolveAsync_LearnsAcrossTrials_EventuallySolves()
    {
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 6, RewardThreshold = 1.0 });

        var result = await agent.SolveAsync("task", LearningActor, RubricEvaluator, RubricReflector);

        Assert.Equal(ReflexionOutcome.Solved, result.Outcome);
        Assert.True(result.Solved);
        Assert.Equal(1.0, result.BestReward);
        // Trial 1 fails (3 issues), then one lesson learned per trial -> all three guards
        // present by trial 4, which passes. Reward climbs 0.00 -> 0.33 -> 0.67 -> 1.00.
        Assert.Equal(4, result.Trials.Count);
        Assert.Equal(new[] { 0.0, 1.0 / 3, 2.0 / 3, 1.0 }, result.Trials.Select(t => t.Reward));
    }

    [Fact]
    public async Task SolveAsync_AccumulatesReflections_InEpisodicMemory()
    {
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 6, RewardThreshold = 1.0 });

        var result = await agent.SolveAsync("task", LearningActor, RubricEvaluator, RubricReflector);

        // Three distinct lessons were learned before the agent solved the task.
        Assert.Equal(
            new[] { "Lesson: sort first", "Lesson: handle empty", "Lesson: duplicate leftmost" },
            result.Reflections);
    }

    [Fact]
    public async Task SolveAsync_MemoryGrowsByOnePerFailingTrial()
    {
        var memorySizes = new List<int>();
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 6, RewardThreshold = 1.0 });

        await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) =>
            {
                memorySizes.Add(lessons.Count);
                return LearningActor(t, lessons, i);
            },
            evaluate: RubricEvaluator,
            reflect: RubricReflector);

        // Trial 1 sees 0 lessons, trial 2 sees 1, trial 3 sees 2, trial 4 sees 3.
        Assert.Equal(new[] { 0, 1, 2, 3 }, memorySizes);
    }

    [Fact]
    public async Task SolveAsync_SolvedOnFirstTry_NoReflections()
    {
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 5, RewardThreshold = 1.0 });

        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => "perfect",
            evaluate: (t, a) => new ReflexionEvaluation(1.0, true, "ok", new List<string>()),
            reflect: RubricReflector);

        Assert.Single(result.Trials);
        Assert.Equal(ReflexionOutcome.Solved, result.Outcome);
        Assert.Empty(result.Reflections);
        Assert.Null(result.Trials[0].Reflection);
    }

    [Fact]
    public async Task SolveAsync_BudgetTooSmall_StopsWithBudgetExhausted()
    {
        // Learns one lesson per trial but the budget runs out before all three guards land.
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 2, RewardThreshold = 1.0 });

        var result = await agent.SolveAsync("task", LearningActor, RubricEvaluator, RubricReflector);

        Assert.Equal(ReflexionOutcome.BudgetExhausted, result.Outcome);
        Assert.False(result.Solved);
        Assert.Equal(2, result.Trials.Count);
    }

    [Fact]
    public async Task SolveAsync_NoNewLesson_StopsWithStuck()
    {
        // Reflector always proposes the SAME lesson -> no new insight after the first.
        var agent = new ReflexionAgent(new ReflexionOptions
        {
            MaxTrials = 6,
            RewardThreshold = 1.0,
            StuckPatience = 1
        });

        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => $"v{i}",
            evaluate: (t, a) => new ReflexionEvaluation(0.5, false, "half", new List<string> { "x" }),
            reflect: (t, a, e, prior) =>
            {
                const string lesson = "Lesson: same every time";
                return prior.Contains(lesson) ? null : lesson;
            });

        Assert.Equal(ReflexionOutcome.Stuck, result.Outcome);
        Assert.False(result.Solved);
        // Trial 1 learns the lesson; trial 2 produces no new lesson -> stuck (patience 1).
        Assert.Equal(2, result.Trials.Count);
        Assert.Single(result.Reflections);
    }

    [Fact]
    public async Task SolveAsync_StuckPatienceToleratesRepeats_BeforeGivingUp()
    {
        var agent = new ReflexionAgent(new ReflexionOptions
        {
            MaxTrials = 10,
            RewardThreshold = 1.0,
            StuckPatience = 3   // tolerate 3 no-new-lesson trials in a row
        });

        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => $"v{i}",
            evaluate: (t, a) => new ReflexionEvaluation(0.4, false, "flat", new List<string> { "x" }),
            reflect: (t, a, e, prior) =>
            {
                const string lesson = "Lesson: only one";
                return prior.Contains(lesson) ? null : lesson;
            });

        Assert.Equal(ReflexionOutcome.Stuck, result.Outcome);
        // Trial 1 learns the lesson (streak resets to 0); trials 2,3,4 add nothing -> streak 3 -> stop.
        Assert.Equal(4, result.Trials.Count);
    }

    [Fact]
    public async Task SolveAsync_RewardThresholdBelowOne_SolvesOnPartialReward()
    {
        // Accept a reward of 2/3 as "solved" -- the third trial clears the bar.
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 6, RewardThreshold = 0.6 });

        var result = await agent.SolveAsync("task", LearningActor, RubricEvaluator, RubricReflector);

        Assert.Equal(ReflexionOutcome.Solved, result.Outcome);
        Assert.True(result.Solved);
        // 0.00 (short of 0.6) -> 0.33 -> 0.67 >= 0.6 on trial 3.
        Assert.Equal(3, result.Trials.Count);
        Assert.True(result.BestReward >= 0.6);
    }

    [Fact]
    public async Task SolveAsync_ReturnsBestAttempt_NotNecessarilyLast()
    {
        // Rewards go 0.5 -> 0.9 -> 0.3: the peak is trial 2 and must be returned even
        // though trial 3 regressed. RewardThreshold above 1 keeps the loop running.
        var rewards = new Queue<double>(new[] { 0.5, 0.9, 0.3 });
        var agent = new ReflexionAgent(new ReflexionOptions
        {
            MaxTrials = 3,
            RewardThreshold = 2.0,   // unreachable so all trials run
            StuckPatience = 99       // never trip the stuck stop
        });

        var n = 0;
        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => $"attempt-{i}",
            evaluate: (t, a) => new ReflexionEvaluation(rewards.Dequeue(), false, "fb",
                new List<string> { $"issue-{++n}" }),   // unique issue -> unique lesson each trial
            reflect: RubricReflector);

        Assert.Equal(3, result.Trials.Count);
        Assert.Equal(0.9, result.BestReward);
        Assert.Equal(2, result.BestTrial);
        Assert.Equal("attempt-2", result.BestAction);
    }

    [Fact]
    public async Task SolveAsync_ClampsReward_IntoZeroOneRange()
    {
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 1, RewardThreshold = 5.0 });

        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => "v",
            evaluate: (t, a) => new ReflexionEvaluation(2.5, false, "over", new List<string> { "x" }),
            reflect: RubricReflector);

        Assert.Equal(1.0, result.Trials[0].Reward);
        Assert.Equal(1.0, result.BestReward);
    }

    [Fact]
    public async Task SolveAsync_NegativeReward_ClampsToZero()
    {
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 1, RewardThreshold = 5.0 });

        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => "v",
            evaluate: (t, a) => new ReflexionEvaluation(-0.5, false, "neg", new List<string> { "x" }),
            reflect: RubricReflector);

        Assert.Equal(0.0, result.Trials[0].Reward);
        Assert.Equal(0.0, result.BestReward);
    }

    [Fact]
    public async Task SolveAsync_MaxTrialsZero_RunsAtLeastOnce()
    {
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 0, RewardThreshold = 5.0 });

        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => "only",
            evaluate: (t, a) => new ReflexionEvaluation(0.1, false, "fb", new List<string>()),
            reflect: RubricReflector);

        Assert.Single(result.Trials);
    }

    [Fact]
    public async Task SolveAsync_BoundsEpisodicMemory_DroppingOldestLesson()
    {
        // Each failing trial learns a brand-new lesson; memory is capped at 2.
        var agent = new ReflexionAgent(new ReflexionOptions
        {
            MaxTrials = 4,
            RewardThreshold = 2.0,   // never solves
            MaxReflections = 2,
            StuckPatience = 99
        });

        var n = 0;
        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => $"v{i}",
            evaluate: (t, a) => new ReflexionEvaluation(0.0, false, "fb",
                new List<string> { $"unique-{++n}" }),
            reflect: (t, a, e, prior) =>
            {
                var lesson = $"Lesson: {e.OpenIssues[0]}";
                return prior.Contains(lesson) ? null : lesson;
            });

        // 4 distinct lessons learned, but memory keeps only the most recent 2.
        Assert.Equal(2, result.Reflections.Count);
        Assert.Equal(new[] { "Lesson: unique-3", "Lesson: unique-4" }, result.Reflections);
    }

    [Fact]
    public async Task SolveAsync_MaxReflectionsZero_KeepsNoLessons()
    {
        // A zero cap means "accumulate nothing" — the tightest possible bound, not
        // "unbounded". Each trial still reflects (and the stuck-streak still resets on a
        // genuinely new lesson), but episodic memory never retains anything.
        var memorySizes = new List<int>();
        var agent = new ReflexionAgent(new ReflexionOptions
        {
            MaxTrials = 4,
            RewardThreshold = 2.0,   // never solves
            MaxReflections = 0,      // keep nothing
            StuckPatience = 99       // don't end early
        });

        var n = 0;
        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) =>
            {
                memorySizes.Add(lessons.Count);
                return $"v{i}";
            },
            evaluate: (t, a) => new ReflexionEvaluation(0.0, false, "fb",
                new List<string> { $"unique-{++n}" }),
            reflect: (t, a, e, prior) => $"Lesson: {e.OpenIssues[0]}");

        Assert.Equal(4, result.Trials.Count);
        // Memory is empty entering every trial and empty at the end — never grows.
        Assert.Equal(new[] { 0, 0, 0, 0 }, memorySizes);
        Assert.Empty(result.Reflections);
    }

    [Fact]
    public async Task SolveAsync_MaxReflectionsOne_KeepsOnlyMostRecentLesson()
    {
        // A cap of 1 keeps exactly the newest lesson, evicting the prior one each trial.
        var agent = new ReflexionAgent(new ReflexionOptions
        {
            MaxTrials = 3,
            RewardThreshold = 2.0,   // never solves
            MaxReflections = 1,
            StuckPatience = 99
        });

        var n = 0;
        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => $"v{i}",
            evaluate: (t, a) => new ReflexionEvaluation(0.0, false, "fb",
                new List<string> { $"unique-{++n}" }),
            reflect: (t, a, e, prior) => $"Lesson: {e.OpenIssues[0]}");

        Assert.Equal(3, result.Trials.Count);
        Assert.Single(result.Reflections);
        Assert.Equal(new[] { "Lesson: unique-3" }, result.Reflections);
    }

    [Fact]
    public async Task SolveAsync_TightMemoryReproposingEvictedLesson_StillStops()
    {
        // Regression: the stuck-loop stop must be judged against every lesson ever
        // learned, not the eviction-trimmed memory window. With a cap of 1, learning
        // lesson B evicts lesson A; a reflector that then re-proposes the *evicted* A
        // used to look like fresh insight (A was no longer in the window), reset the
        // stuck streak, and let the agent burn its whole MaxTrials budget oscillating
        // A → B → A → B forever. It must instead recognise both lessons as already-seen
        // and bail out as Stuck.
        const string lessonA = "Lesson: A";
        const string lessonB = "Lesson: B";

        var agent = new ReflexionAgent(new ReflexionOptions
        {
            MaxTrials = 8,           // generous budget the bug would have exhausted
            RewardThreshold = 1.0,   // never solves
            MaxReflections = 1,      // window holds a single lesson -> forces eviction
            StuckPatience = 2
        });

        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => $"v{i}",
            evaluate: (t, a) => new ReflexionEvaluation(0.5, false, "half", new List<string> { "x" }),
            // Propose whichever lesson is NOT currently in the (size-1) window, so the
            // bounded-window membership check can never block it. Only a full-history
            // novelty check can recognise the repeat.
            reflect: (t, a, e, prior) => prior.Contains(lessonA) ? lessonB : lessonA);

        Assert.Equal(ReflexionOutcome.Stuck, result.Outcome);
        Assert.False(result.Solved);
        // Trial 1 learns A (streak 0), trial 2 learns B (streak 0), trials 3 & 4 only
        // re-propose already-seen lessons (streak 1, then 2) -> stuck. Far short of 8.
        Assert.Equal(4, result.Trials.Count);
    }

    [Fact]
    public async Task SolveAsync_OnTrial_FiresOncePerTrial()
    {
        var observed = new List<int>();
        var agent = new ReflexionAgent(new ReflexionOptions
        {
            MaxTrials = 4,
            RewardThreshold = 2.0,
            StuckPatience = 99,
            OnTrial = trial => observed.Add(trial.Trial)
        });

        var n = 0;
        await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => $"v{i}",
            evaluate: (t, a) => new ReflexionEvaluation(0.2, false, "fb",
                new List<string> { $"issue-{++n}" }),
            reflect: (t, a, e, prior) => $"Lesson: {e.OpenIssues[0]}");

        Assert.Equal(new[] { 1, 2, 3, 4 }, observed);
    }

    [Fact]
    public async Task SolveAsync_NullReflection_IsNotStored()
    {
        var agent = new ReflexionAgent(new ReflexionOptions
        {
            MaxTrials = 3,
            RewardThreshold = 2.0,
            StuckPatience = 99   // don't let the stuck-stop end the run early
        });

        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => $"v{i}",
            evaluate: (t, a) => new ReflexionEvaluation(0.0, false, "fb", new List<string> { "x" }),
            reflect: (t, a, e, prior) => null);   // reflector never has anything to say

        Assert.Empty(result.Reflections);
        Assert.All(result.Trials, trial => Assert.Null(trial.Reflection));
    }

    [Fact]
    public async Task SolveAsync_TrialRecord_CapturesActionRewardAndReflection()
    {
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 1, RewardThreshold = 2.0 });

        var result = await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => "the-attempt",
            evaluate: (t, a) => new ReflexionEvaluation(0.5, false, "halfway", new List<string> { "gap" }),
            reflect: (t, a, e, prior) => "Lesson: mind the gap");

        var trial = Assert.Single(result.Trials);
        Assert.Equal(1, trial.Trial);
        Assert.Equal("the-attempt", trial.Action);
        Assert.Equal(0.5, trial.Reward);
        Assert.False(trial.Succeeded);
        Assert.Equal("halfway", trial.Evaluation.Feedback);
        Assert.Equal("Lesson: mind the gap", trial.Reflection);
    }

    [Fact]
    public async Task SolveAsync_AlreadyCancelled_Throws()
    {
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 3 });
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await agent.SolveAsync(
                "task",
                actor: (t, lessons, i, ct) => Task.FromResult("v"),
                evaluate: (t, a, ct) => Task.FromResult(new ReflexionEvaluation(0.0, false, "fb", new List<string>())),
                reflect: (t, a, e, prior, ct) => Task.FromResult<string?>(null),
                cts.Token));
    }

    [Fact]
    public async Task SolveAsync_AsyncDelegates_AreAwaited()
    {
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 6, RewardThreshold = 1.0 });

        var result = await agent.SolveAsync(
            "task",
            actor: async (t, lessons, i, ct) =>
            {
                await Task.Yield();
                return LearningActor(t, lessons, i);
            },
            evaluate: async (t, a, ct) =>
            {
                await Task.Yield();
                return RubricEvaluator(t, a);
            },
            reflect: async (t, a, e, prior, ct) =>
            {
                await Task.Yield();
                return RubricReflector(t, a, e, prior);
            });

        Assert.Equal(ReflexionOutcome.Solved, result.Outcome);
        Assert.True(result.Solved);
    }

    [Fact]
    public async Task SolveAsync_DoesNotReflectAfterSuccess()
    {
        var reflectCalls = 0;
        var agent = new ReflexionAgent(new ReflexionOptions { MaxTrials = 5, RewardThreshold = 1.0 });

        await agent.SolveAsync(
            "task",
            actor: (t, lessons, i) => "great",
            evaluate: (t, a) => new ReflexionEvaluation(1.0, true, "ok", new List<string>()),
            reflect: (t, a, e, prior) => { reflectCalls++; return "should not happen"; });

        Assert.Equal(0, reflectCalls);
    }
}

// ── Supporting types (mirrors recipes/reflexion/Program.cs) ──
// Prefixed with "Reflexion" so they don't collide with other test files
// that share the global namespace. Logic is duplicated here because each
// recipe is a standalone top-level program, not a project reference.

record ReflexionEvaluation(double Reward, bool Succeeded, string Feedback, IReadOnlyList<string> OpenIssues);

record ReflexionTrial(
    int Trial,
    string Action,
    double Reward,
    bool Succeeded,
    ReflexionEvaluation Evaluation,
    string? Reflection);

enum ReflexionOutcome
{
    Solved,
    BudgetExhausted,
    Stuck
}

record ReflexionOptions
{
    public double RewardThreshold { get; init; } = 1.0;
    public int MaxTrials { get; init; } = 4;
    public int MaxReflections { get; init; } = 8;
    public int StuckPatience { get; init; } = 2;
    public Action<ReflexionTrial>? OnTrial { get; init; }
}

record ReflexionResult(
    string BestAction,
    double BestReward,
    int BestTrial,
    ReflexionOutcome Outcome,
    bool Solved,
    IReadOnlyList<string> Reflections,
    IReadOnlyList<ReflexionTrial> Trials);

class ReflexionAgent
{
    private readonly ReflexionOptions _options;

    public ReflexionAgent(ReflexionOptions options) => _options = options;

    public async Task<ReflexionResult> SolveAsync(
        string task,
        Func<string, IReadOnlyList<string>, int, string> actor,
        Func<string, string, ReflexionEvaluation> evaluate,
        Func<string, string, ReflexionEvaluation, IReadOnlyList<string>, string?> reflect,
        CancellationToken ct = default)
    {
        return await SolveAsync(
            task,
            (t, lessons, i, _) => Task.FromResult(actor(t, lessons, i)),
            (t, a, _) => Task.FromResult(evaluate(t, a)),
            (t, a, e, prior, _) => Task.FromResult(reflect(t, a, e, prior)),
            ct);
    }

    public async Task<ReflexionResult> SolveAsync(
        string task,
        Func<string, IReadOnlyList<string>, int, CancellationToken, Task<string>> actor,
        Func<string, string, CancellationToken, Task<ReflexionEvaluation>> evaluate,
        Func<string, string, ReflexionEvaluation, IReadOnlyList<string>, CancellationToken, Task<string?>> reflect,
        CancellationToken ct = default)
    {
        var maxTrials = Math.Max(1, _options.MaxTrials);
        var maxReflections = Math.Max(0, _options.MaxReflections);
        var stuckPatience = Math.Max(1, _options.StuckPatience);

        var trials = new List<ReflexionTrial>();
        var reflections = new List<string>();
        // Novelty (and the stuck-loop stop) is judged against every lesson ever learned,
        // not the eviction-trimmed `reflections` window — so a re-proposed evicted lesson
        // does not masquerade as "new" and reset the stuck counter.
        var seenLessons = new HashSet<string>(StringComparer.Ordinal);

        string bestAction = "";
        double bestReward = double.NegativeInfinity;
        int bestTrial = 0;
        int noNewLessonStreak = 0;
        var outcome = ReflexionOutcome.BudgetExhausted;

        for (var trialNum = 1; trialNum <= maxTrials; trialNum++)
        {
            ct.ThrowIfCancellationRequested();

            var action = await actor(task, reflections.AsReadOnly(), trialNum, ct);
            var evaluation = await evaluate(task, action, ct);
            var reward = Clamp(evaluation.Reward, 0, 1);
            var solved = evaluation.Succeeded || reward >= _options.RewardThreshold;

            string? lesson = null;
            if (!solved)
            {
                lesson = await reflect(task, action, evaluation, reflections.AsReadOnly(), ct);
                if (!string.IsNullOrWhiteSpace(lesson) && seenLessons.Add(lesson))
                {
                    reflections.Add(lesson);
                    while (reflections.Count > maxReflections)
                        reflections.RemoveAt(0);
                    noNewLessonStreak = 0;
                }
                else
                {
                    lesson = null;
                    noNewLessonStreak++;
                }
            }

            var trial = new ReflexionTrial(trialNum, action, reward, solved, evaluation, lesson);
            trials.Add(trial);
            _options.OnTrial?.Invoke(trial);

            if (reward > bestReward)
            {
                bestReward = reward;
                bestAction = action;
                bestTrial = trialNum;
            }

            if (solved)
            {
                outcome = ReflexionOutcome.Solved;
                break;
            }

            if (noNewLessonStreak >= stuckPatience)
            {
                outcome = ReflexionOutcome.Stuck;
                break;
            }
        }

        return new ReflexionResult(
            bestAction,
            bestReward < 0 ? 0 : bestReward,
            bestTrial,
            outcome,
            outcome == ReflexionOutcome.Solved,
            reflections.AsReadOnly(),
            trials);
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}