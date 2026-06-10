using Prompt;
using System.Text;

// ──────────────────────────────────────────────────────────────
// Iterative Refinement Recipe
// Pattern: Critic Loop (generate → critique → revise → repeat)
//
// A generator produces a first draft. A critic scores it and
// returns actionable feedback. The generator revises using that
// feedback. The loop repeats until the critic's score clears a
// target bar, the iteration budget runs out, OR the score stops
// improving (an autonomous "this is as good as it gets" plateau
// stop that avoids burning calls on diminishing returns).
//
// This is the self-improvement pattern: the agent grades its own
// work and decides — on its own — when the work is good enough.
// ──────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;

// 1. Configure the refiner.
var refiner = new IterativeRefiner(new RefinerOptions
{
    TargetScore = 85,          // stop early once the critic is this happy (0-100)
    MaxIterations = 5,         // hard ceiling on generate→critique rounds
    MinImprovement = 3.0,      // a round must gain at least this to count as progress
    PlateauPatience = 2,       // stop after this many non-improving rounds in a row
    OnIteration = step =>
    {
        var bar = new string('█', (int)Math.Round(step.Score / 5.0)).PadRight(20, '░');
        Console.WriteLine($"  Round {step.Iteration}: [{bar}] {step.Score,5:F1}/100  ({step.Verdict})");
        if (step.TopIssues.Count > 0)
            Console.WriteLine($"     ↳ fix next: {string.Join("; ", step.TopIssues.Take(2))}");
    }
});

// 2. A simulated generator. In a real recipe this is an LLM call that
//    rewrites the draft using the critic's feedback. Here we model a
//    draft that measurably improves as feedback accumulates so the
//    loop's control flow is easy to follow offline.
var task = "Write a one-paragraph product blurb for a privacy-first note-taking app.";

string GenerateDraft(string taskText, IReadOnlyList<string> feedback, int iteration)
{
    // Each accumulated piece of feedback "addresses" one weakness.
    var addressed = feedback.Count;
    var sb = new StringBuilder();
    sb.Append("InkVault keeps your notes yours. ");
    if (addressed >= 1) sb.Append("Everything is end-to-end encrypted on your device before it ever syncs. ");
    if (addressed >= 2) sb.Append("There are no ads, no trackers, and no account required to start. ");
    if (addressed >= 3) sb.Append("Capture ideas in seconds with markdown, voice, and quick-clip from any app. ");
    if (addressed >= 4) sb.Append("Try it free — your first 1,000 notes are on us, no card needed. ");
    return sb.ToString().TrimEnd();
}

// 3. A simulated critic. In a real recipe this is an LLM acting as an
//    editor that returns a JSON verdict. Here we score the draft on a
//    few concrete rubric checks so the score is deterministic.
Critique CritiqueDraft(string taskText, string draft)
{
    var issues = new List<string>();
    double score = 40; // a bare draft starts mediocre

    if (draft.Contains("encrypted", StringComparison.OrdinalIgnoreCase)) score += 15;
    else issues.Add("State the core privacy mechanism (encryption) explicitly.");

    if (draft.Contains("no ads", StringComparison.OrdinalIgnoreCase) ||
        draft.Contains("no trackers", StringComparison.OrdinalIgnoreCase)) score += 15;
    else issues.Add("Call out the no-ads / no-trackers stance.");

    if (draft.Contains("markdown", StringComparison.OrdinalIgnoreCase) ||
        draft.Contains("voice", StringComparison.OrdinalIgnoreCase)) score += 12;
    else issues.Add("Mention at least one concrete capture feature.");

    if (draft.Contains("free", StringComparison.OrdinalIgnoreCase)) score += 13;
    else issues.Add("End with a clear call to action.");

    var verdict = score >= 85 ? "ship-ready" : score >= 65 ? "promising" : "needs work";
    var feedback = issues.Count == 0
        ? "Strong blurb: leads with the privacy promise, names a feature, and closes with a CTA."
        : "Address the listed gaps, keep it to one tight paragraph.";

    return new Critique(Math.Min(score, 100), feedback, issues);
}

// 4. Run the loop.
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Iterative Refinement Recipe (Critic Loop)");
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine($"Task: {task}");
Console.WriteLine();
Console.WriteLine("Refining (generate → critique → revise)…");
Console.WriteLine();

var result = await refiner.RefineAsync(task, GenerateDraft, CritiqueDraft);

// 5. Report.
Console.WriteLine();
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine($"  Stop reason : {result.StopReason}");
Console.WriteLine($"  Target met  : {(result.TargetMet ? "yes ✓" : "no")}");
Console.WriteLine($"  Rounds run  : {result.Iterations.Count}");
Console.WriteLine($"  Best score  : {result.BestScore:F1}/100 (round {result.BestIteration})");
Console.WriteLine($"  Score trail : {string.Join(" → ", result.Iterations.Select(i => i.Score.ToString("F0")))}");
Console.WriteLine();
Console.WriteLine("══ BEST DRAFT ══");
Console.WriteLine(result.BestDraft);
Console.WriteLine();

// 6. Demonstrate the autonomous plateau stop with a stubborn generator
//    that can't get past a ceiling — the loop bails out instead of
//    spending its whole budget chasing a score it will never reach.
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Bonus: plateau detection (generator hits a ceiling)");
Console.WriteLine("═══════════════════════════════════════════════════════");

var plateauRefiner = new IterativeRefiner(new RefinerOptions
{
    TargetScore = 95,          // deliberately unreachable here
    MaxIterations = 6,
    MinImprovement = 3.0,
    PlateauPatience = 2,
    OnIteration = step =>
        Console.WriteLine($"  Round {step.Iteration}: {step.Score,5:F1}/100  ({step.Verdict})")
});

var plateauResult = await plateauRefiner.RefineAsync(
    "Summarise the quarterly report.",
    generate: (t, fb, i) => $"Draft v{i} (capped quality).",
    // Score climbs, then flattens — the critic can't be satisfied past ~70.
    critique: (t, draft) =>
    {
        var v = ParseVersion(draft);
        double score = Math.Min(70, 40 + v * 12);
        return new Critique(score, score >= 70 ? "Diminishing returns." : "Keep going.",
            score >= 70 ? new List<string>() : new List<string> { "Add more specifics." });
    });

Console.WriteLine();
Console.WriteLine($"  Stopped after {plateauResult.Iterations.Count} rounds: {plateauResult.StopReason}");
Console.WriteLine($"  Saved {6 - plateauResult.Iterations.Count} wasted round(s) vs. running to the cap.");
Console.WriteLine();
Console.WriteLine("Pattern: generate → critique → revise, with an autonomous stop");
Console.WriteLine("on target-hit, budget-exhausted, OR quality-plateau.");

static int ParseVersion(string draft)
{
    var idx = draft.IndexOf('v');
    if (idx < 0 || idx + 1 >= draft.Length) return 0;
    var rest = new string(draft[(idx + 1)..].TakeWhile(char.IsDigit).ToArray());
    return int.TryParse(rest, out var n) ? n : 0;
}

// ── Supporting types ────────────────────────────────────────

/// <summary>A critic's verdict on a single draft.</summary>
/// <param name="Score">Quality on a 0–100 scale.</param>
/// <param name="Feedback">One-line summary the generator should act on.</param>
/// <param name="Issues">Concrete, actionable problems, most important first.</param>
record Critique(double Score, string Feedback, IReadOnlyList<string> Issues);

/// <summary>One round of the loop: the draft produced and how the critic graded it.</summary>
record RefinementStep(int Iteration, string Draft, double Score, string Feedback, IReadOnlyList<string> TopIssues)
{
    /// <summary>Human-readable bucket for the score.</summary>
    public string Verdict => Score >= 85 ? "ship-ready" : Score >= 65 ? "promising" : "needs work";
}

/// <summary>Why the refinement loop stopped.</summary>
enum StopReason
{
    /// <summary>The critic's score reached or exceeded <see cref="RefinerOptions.TargetScore"/>.</summary>
    TargetReached,
    /// <summary>The iteration budget (<see cref="RefinerOptions.MaxIterations"/>) was spent.</summary>
    BudgetExhausted,
    /// <summary>The score stopped improving for <see cref="RefinerOptions.PlateauPatience"/> rounds.</summary>
    Plateaued
}

/// <summary>Configuration for <see cref="IterativeRefiner"/>.</summary>
record RefinerOptions
{
    /// <summary>Stop early once a draft scores at least this (0–100).</summary>
    public double TargetScore { get; init; } = 85;
    /// <summary>Hard ceiling on generate→critique rounds. Always at least 1.</summary>
    public int MaxIterations { get; init; } = 5;
    /// <summary>A round must improve the score by at least this much to count as progress.</summary>
    public double MinImprovement { get; init; } = 2.0;
    /// <summary>Number of consecutive non-improving rounds tolerated before giving up.</summary>
    public int PlateauPatience { get; init; } = 2;
    /// <summary>Observability hook fired once per completed round.</summary>
    public Action<RefinementStep>? OnIteration { get; init; }
}

/// <summary>Outcome of a refinement run.</summary>
record RefinementResult(
    string BestDraft,
    double BestScore,
    int BestIteration,
    StopReason StopReason,
    bool TargetMet,
    IReadOnlyList<RefinementStep> Iterations);

/// <summary>
/// Runs a self-improving critic loop: generate a draft, have a critic score it,
/// feed the critique back into the next draft, and keep going until the work is
/// good enough, the budget runs out, or the score plateaus.
///
/// Both the generator and critic are injected delegates, so the loop's control
/// flow can be exercised deterministically in tests and wired to real LLM calls
/// in production.
/// </summary>
class IterativeRefiner
{
    private readonly RefinerOptions _options;

    public IterativeRefiner(RefinerOptions options) => _options = options;

    /// <summary>
    /// Refine a task to the best draft the critic will accept (or the best
    /// reachable before the loop decides to stop).
    /// </summary>
    /// <param name="task">The work to produce.</param>
    /// <param name="generate">
    /// Produces a draft from the task, the accumulated feedback so far, and the
    /// 1-based iteration number. The feedback list grows by one actionable note
    /// each round (most recent last).
    /// </param>
    /// <param name="critique">Scores a draft and returns actionable feedback.</param>
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

    /// <summary>Async overload: both delegates may await real model calls.</summary>
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

            // Track the best draft seen — the loop returns the peak, not just
            // the last round (a later revision can regress).
            if (score > bestScore)
            {
                bestScore = score;
                bestDraft = draft;
                bestIteration = iteration;
            }

            // Target hit → done.
            if (score >= _options.TargetScore)
            {
                stopReason = StopReason.TargetReached;
                break;
            }

            // Plateau detection: did this round move the needle enough?
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

            // Accumulate the most important open issue as feedback for the next draft.
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
