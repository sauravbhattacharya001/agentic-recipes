using Prompt;
using System.Text;

// ──────────────────────────────────────────────────────────────
// Reflexion Recipe
// Pattern: Attempt → Evaluate → Self-Reflect → Retry
// (Shinn et al. 2023, "Reflexion: Language Agents with Verbal
//  Reinforcement Learning")
//
// An actor attempts a task. An evaluator checks the result and
// returns a reward plus what went wrong. On failure a self-
// reflection step writes a SHORT VERBAL lesson ("I assumed the
// list was sorted; verify ordering first") into a persistent
// episodic memory. Every subsequent attempt is shown the
// accumulated reflections, so the agent learns from its own
// failure traces across trials — without any weight updates.
//
// This is the verbal-reinforcement-learning pattern: the agent
// turns each failure into language, remembers it, and uses it to
// do better next time. It stops on its own when it succeeds, when
// the trial budget runs out, or when reflecting stops producing
// any NEW insight (a stuck loop it refuses to keep paying for).
//
// How this differs from Iterative Refinement (the critic loop):
//   • The signal here is a task OUTCOME / reward (did it pass?),
//     not a 0-100 quality score on a single artifact.
//   • The carried state is a growing list of VERBAL LESSONS, not
//     the previous draft — a later trial can attack the task from
//     scratch armed only with "things I now know not to do".
// ──────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Reflexion Recipe (Attempt → Evaluate → Reflect → Retry)");
Console.WriteLine("═══════════════════════════════════════════════════════");

// ── Scenario ─────────────────────────────────────────────────
// The task: write a function body that passes a small hidden
// test suite. The actor is a stand-in for an LLM coding agent;
// it "discovers" the right approach only after the reflection
// memory has told it what its previous attempts got wrong.
//
// We model the actor as a deterministic function of the lessons
// it has accumulated, so the loop's control flow is reproducible
// offline. In production, `actor` and `reflect` are LLM calls and
// `evaluate` runs the real tests / tool / grader.

var agent = new ReflexionAgent(new ReflexionOptions
{
    MaxTrials = 5,                 // hard ceiling on attempts
    RewardThreshold = 1.0,         // reward >= this counts as success (0..1)
    MaxReflections = 8,            // cap on how many lessons we keep in memory
    OnTrial = trial =>
    {
        var status = trial.Succeeded ? "✓ PASS" : "✗ FAIL";
        Console.WriteLine($"  Trial {trial.Trial}: reward {trial.Reward:F2}  {status}");
        Console.WriteLine($"     attempt : {trial.Action}");
        if (!trial.Succeeded)
            Console.WriteLine($"     feedback: {trial.Evaluation.Feedback}");
        if (trial.Reflection is not null)
            Console.WriteLine($"     lesson  : {trial.Reflection}");
    }
});

// A tiny "rubric" of facts the correct solution must satisfy. Each
// missing fact is one failure the evaluator can point at, and one
// lesson the reflector can learn.
string Actor(string task, IReadOnlyList<string> lessons, int trial)
{
    // The actor only includes a guard once a lesson has taught it to.
    var sortFirst = lessons.Any(l => l.Contains("sort", StringComparison.OrdinalIgnoreCase));
    var handleEmpty = lessons.Any(l => l.Contains("empty", StringComparison.OrdinalIgnoreCase));
    var handleDup = lessons.Any(l => l.Contains("duplicate", StringComparison.OrdinalIgnoreCase));

    var parts = new List<string> { "binary_search(xs, target)" };
    if (sortFirst) parts.Add("assert is_sorted(xs)");
    if (handleEmpty) parts.Add("if not xs: return -1");
    if (handleDup) parts.Add("return leftmost match");
    return string.Join(" + ", parts);
}

Evaluation Evaluate(string task, string action)
{
    var missing = new List<string>();
    if (!action.Contains("is_sorted")) missing.Add("input must be sorted before binary search");
    if (!action.Contains("if not xs")) missing.Add("empty input must return -1, not crash");
    if (!action.Contains("leftmost")) missing.Add("duplicates must return the leftmost match");

    // Reward = fraction of rubric satisfied. 1.0 means all checks pass.
    double reward = (3 - missing.Count) / 3.0;
    var feedback = missing.Count == 0
        ? "All hidden tests passed."
        : "Failed tests: " + string.Join("; ", missing);
    return new Evaluation(reward, missing.Count == 0, feedback, missing);
}

// The reflector turns the FIRST open failure into a one-line lesson.
// In production this is an LLM prompted to introspect on the failure
// trace; here we map each failure to a memorable verbal cue.
string? Reflect(string task, string action, Evaluation eval, IReadOnlyList<string> priorLessons)
{
    if (eval.OpenIssues.Count == 0) return null;
    var issue = eval.OpenIssues[0];

    string lesson =
        issue.Contains("sorted") ? "Lesson: verify the input is sorted before binary search."
        : issue.Contains("empty") ? "Lesson: handle the empty-input case explicitly."
        : issue.Contains("duplicate") ? "Lesson: for duplicates, return the leftmost match."
        : $"Lesson: address — {issue}";

    // Don't re-learn something already in memory (keeps reflection honest
    // and lets the agent detect a stuck loop).
    return priorLessons.Contains(lesson) ? null : lesson;
}

var task = "Implement binary_search(xs, target) so it passes the hidden test suite.";
Console.WriteLine($"Task: {task}");
Console.WriteLine();
Console.WriteLine("Running trials (attempt → evaluate → reflect → retry)…");
Console.WriteLine();

var result = await agent.SolveAsync(task, Actor, Evaluate, Reflect);

Console.WriteLine();
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine($"  Outcome      : {result.Outcome}");
Console.WriteLine($"  Solved       : {(result.Solved ? "yes ✓" : "no")}");
Console.WriteLine($"  Trials used  : {result.Trials.Count}");
Console.WriteLine($"  Best reward  : {result.BestReward:F2} (trial {result.BestTrial})");
Console.WriteLine($"  Reward trail : {string.Join(" → ", result.Trials.Select(t => t.Reward.ToString("F2")))}");
Console.WriteLine();
Console.WriteLine("══ EPISODIC MEMORY (lessons learned) ══");
if (result.Reflections.Count == 0)
    Console.WriteLine("  (none — solved on the first try)");
else
    foreach (var (lesson, i) in result.Reflections.Select((l, i) => (l, i + 1)))
        Console.WriteLine($"  {i}. {lesson}");
Console.WriteLine();
Console.WriteLine("══ BEST SOLUTION ══");
Console.WriteLine($"  {result.BestAction}");
Console.WriteLine();

// ── Bonus: the "stuck loop" stop ─────────────────────────────
// If reflecting stops producing any NEW lesson AND the agent still
// isn't succeeding, Reflexion bails out (Stuck) instead of burning
// its whole trial budget repeating a mistake it can't articulate
// its way out of.
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Bonus: stuck-loop detection (no new lesson to learn)");
Console.WriteLine("═══════════════════════════════════════════════════════");

var stuckAgent = new ReflexionAgent(new ReflexionOptions
{
    MaxTrials = 6,
    RewardThreshold = 1.0,
    StuckPatience = 1,             // give up after 1 trial with no new lesson
    OnTrial = t =>
        Console.WriteLine($"  Trial {t.Trial}: reward {t.Reward:F2}  {(t.Succeeded ? "PASS" : "FAIL")}" +
                          (t.Reflection is null ? "  (no new lesson)" : $"  lesson: {t.Reflection}"))
});

var stuckResult = await stuckAgent.SolveAsync(
    "Solve an unsolvable rubric.",
    actor: (t, lessons, i) => $"attempt-{i}",
    evaluate: (t, a) => new Evaluation(0.5, false, "Half-right, but the rest is unknowable.",
        new List<string> { "the unknowable part" }),
    // Reflector keeps producing the SAME lesson → no new insight → stuck.
    reflect: (t, a, e, prior) =>
    {
        const string lesson = "Lesson: the unknowable part is unknowable.";
        return prior.Contains(lesson) ? null : lesson;
    });

Console.WriteLine();
Console.WriteLine($"  Stopped after {stuckResult.Trials.Count} trials: {stuckResult.Outcome}");
Console.WriteLine($"  Saved {6 - stuckResult.Trials.Count} wasted trial(s) vs. running to the cap.");
Console.WriteLine();
Console.WriteLine("Pattern: attempt → evaluate → reflect → retry, with an");
Console.WriteLine("autonomous stop on success, budget-exhausted, OR stuck-loop.");

// ── Supporting types ────────────────────────────────────────

/// <summary>The result of grading a single attempt.</summary>
/// <param name="Reward">Scalar reward in [0, 1]; 1.0 means a perfect attempt.</param>
/// <param name="Succeeded">True when the attempt fully solved the task.</param>
/// <param name="Feedback">One-line summary of what happened (shown to the reflector).</param>
/// <param name="OpenIssues">Concrete remaining failures, most important first.</param>
record Evaluation(double Reward, bool Succeeded, string Feedback, IReadOnlyList<string> OpenIssues);

/// <summary>One trial of the loop: the attempt, its grade, and any lesson drawn from it.</summary>
record ReflexionTrial(
    int Trial,
    string Action,
    double Reward,
    bool Succeeded,
    Evaluation Evaluation,
    string? Reflection);

/// <summary>Why the Reflexion loop stopped.</summary>
enum ReflexionOutcome
{
    /// <summary>An attempt reached <see cref="ReflexionOptions.RewardThreshold"/>.</summary>
    Solved,
    /// <summary>The trial budget (<see cref="ReflexionOptions.MaxTrials"/>) was spent.</summary>
    BudgetExhausted,
    /// <summary>Reflection stopped producing new lessons while still failing — a stuck loop.</summary>
    Stuck
}

/// <summary>Configuration for <see cref="ReflexionAgent"/>.</summary>
record ReflexionOptions
{
    /// <summary>Reward (0–1) an attempt must reach to count as solved.</summary>
    public double RewardThreshold { get; init; } = 1.0;
    /// <summary>Hard ceiling on attempts. Always at least 1.</summary>
    public int MaxTrials { get; init; } = 4;
    /// <summary>Maximum number of verbal lessons kept in episodic memory (oldest dropped first).</summary>
    public int MaxReflections { get; init; } = 8;
    /// <summary>Consecutive failing trials with no NEW lesson tolerated before giving up.</summary>
    public int StuckPatience { get; init; } = 2;
    /// <summary>Observability hook fired once per completed trial.</summary>
    public Action<ReflexionTrial>? OnTrial { get; init; }
}

/// <summary>Outcome of a Reflexion run.</summary>
record ReflexionResult(
    string BestAction,
    double BestReward,
    int BestTrial,
    ReflexionOutcome Outcome,
    bool Solved,
    IReadOnlyList<string> Reflections,
    IReadOnlyList<ReflexionTrial> Trials);

/// <summary>
/// Runs the Reflexion loop: attempt a task, evaluate the result, and on
/// failure write a short verbal self-reflection into a persistent episodic
/// memory that is fed into every subsequent attempt. Keeps going until the
/// task is solved, the trial budget runs out, or reflection stops producing
/// new lessons (a stuck loop).
///
/// The actor, evaluator, and reflector are all injected delegates, so the
/// loop's control flow can be exercised deterministically in tests and wired
/// to real LLM / tool calls in production.
/// </summary>
class ReflexionAgent
{
    private readonly ReflexionOptions _options;

    public ReflexionAgent(ReflexionOptions options) => _options = options;

    /// <summary>Sync convenience overload.</summary>
    /// <param name="task">The task to solve.</param>
    /// <param name="actor">
    /// Produces an attempt from the task, the accumulated lessons so far, and the
    /// 1-based trial number.
    /// </param>
    /// <param name="evaluate">Grades an attempt and returns a reward + open issues.</param>
    /// <param name="reflect">
    /// Given a failed attempt and its evaluation, returns a SHORT verbal lesson to
    /// remember (or <c>null</c> when there is nothing new to learn).
    /// </param>
    public async Task<ReflexionResult> SolveAsync(
        string task,
        Func<string, IReadOnlyList<string>, int, string> actor,
        Func<string, string, Evaluation> evaluate,
        Func<string, string, Evaluation, IReadOnlyList<string>, string?> reflect,
        CancellationToken ct = default)
    {
        return await SolveAsync(
            task,
            (t, lessons, i, _) => Task.FromResult(actor(t, lessons, i)),
            (t, a, _) => Task.FromResult(evaluate(t, a)),
            (t, a, e, prior, _) => Task.FromResult(reflect(t, a, e, prior)),
            ct);
    }

    /// <summary>Async overload: actor, evaluator, and reflector may await real calls.</summary>
    public async Task<ReflexionResult> SolveAsync(
        string task,
        Func<string, IReadOnlyList<string>, int, CancellationToken, Task<string>> actor,
        Func<string, string, CancellationToken, Task<Evaluation>> evaluate,
        Func<string, string, Evaluation, IReadOnlyList<string>, CancellationToken, Task<string?>> reflect,
        CancellationToken ct = default)
    {
        var maxTrials = Math.Max(1, _options.MaxTrials);
        var maxReflections = Math.Max(0, _options.MaxReflections);
        var stuckPatience = Math.Max(1, _options.StuckPatience);

        var trials = new List<ReflexionTrial>();
        var reflections = new List<string>();   // episodic memory of verbal lessons

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

            // Reflect on failure (success needs no lesson).
            string? lesson = null;
            if (!solved)
            {
                lesson = await reflect(task, action, evaluation, reflections.AsReadOnly(), ct);
                if (!string.IsNullOrWhiteSpace(lesson) && !reflections.Contains(lesson))
                {
                    reflections.Add(lesson);
                    // Bound episodic memory: drop the oldest lesson when over budget.
                    if (reflections.Count > maxReflections && maxReflections > 0)
                        reflections.RemoveAt(0);
                    noNewLessonStreak = 0;
                }
                else
                {
                    // Nothing new to remember this round.
                    lesson = null;
                    noNewLessonStreak++;
                }
            }

            var trial = new ReflexionTrial(trialNum, action, reward, solved, evaluation, lesson);
            trials.Add(trial);
            _options.OnTrial?.Invoke(trial);

            // Track the best attempt seen — a later trial can regress.
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

            // Stuck-loop stop: still failing AND reflection isn't adding anything new.
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
