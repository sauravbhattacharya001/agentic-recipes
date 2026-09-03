using Prompt;

// ──────────────────────────────────────────────────────────────
// Multi-Agent Debate Recipe
// Pattern: Argue → Rebut → Judge → Converge-or-Decide
//
// Self-Consistency samples the SAME question many times and votes,
// but the samples never talk to each other. Multi-Perspective runs
// different personas in parallel and synthesizes them, but again the
// personas never see each other's work. Multi-Agent Debate adds the
// missing ingredient: INTERACTION. Two (or more) debaters each hold a
// position, and on every round they SEE their opponents' latest
// arguments and must rebut or revise. A neutral JUDGE scores the
// standings after each round.
//
// The agency lives in the loop control: the orchestrator watches the
// debate and stops on its own terms — it ENDS EARLY when the debaters
// CONVERGE on the same answer (no point burning more rounds), declares
// a DECISION when the judge opens a clear, stable lead, and honestly
// reports a HUNG debate (escalate) when neither happens. It debates
// exactly as long as the disagreement is still productive.
// ──────────────────────────────────────────────────────────────

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Multi-Agent Debate Recipe");
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine();

// ── Scenario 1: debate that CONVERGES early ────────────────────
// A factual question. The "Pro" debater is right; the "Con" debater
// starts wrong but, confronted with the rebuttal, concedes. Once both
// sides agree, the orchestrator stops without running the full budget.
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine("Q1: \"Does binary search require the input to be sorted?\"");
Console.WriteLine();

// Each debater is just a delegate: (question, round, transcript-so-far) → its move.
// With a real model you'd render a persona prompt; here scripted moves keep the
// recipe deterministic and unit-testable.
var proScript = new[]
{
    new DebateArgument("Yes — it halves a SORTED range; order is what makes the halving valid.", "yes", 0.82),
    new DebateArgument("Re-affirming: on unsorted data the discarded half may hold the target.", "yes", 0.9),
};
var conScript = new[]
{
    new DebateArgument("No — you can just probe the middle and recurse either way.", "no", 0.55),
    new DebateArgument("Conceding: without order the eliminated half isn't safe. Agreed: yes.", "yes", 0.8),
};

var debaters1 = new[]
{
    new Debater("Pro", Scripted(proScript)),
    new Debater("Con", Scripted(conScript)),
};

var orchestrator = new DebateOrchestrator(new DebateOptions
{
    MaxRounds = 4,
    DecisiveMargin = 0.34,          // head-to-head lead (leader-runnerUp)/(leader+runnerUp) that counts as "clear"
    NormalizeAnswer = a => a.Trim().TrimEnd('.').ToLowerInvariant(),
    OnExchange = PrintExchange,
});

// The judge scores a debater's CURRENT argument. Here a scripted rubric;
// swap in a model-graded judge for real use.
var r1 = await orchestrator.RunAsync("Does binary search require sorted input?", debaters1, ScriptedJudge);
PrintVerdict(r1);

// ── Scenario 2: judge DECIDES a persistent disagreement ────────
// A genuine trade-off question. Neither side concedes, but the judge
// consistently rates one case stronger, so a clear, stable lead lets
// the orchestrator call a winner instead of looping forever.
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine("Q2: \"For a tiny always-internal CLI, is SQLite better than a hand-rolled file format?\"");
Console.WriteLine();

var sqliteScript = new[]
{
    new DebateArgument("SQLite: crash-safe, queryable, zero parser bugs, ubiquitous.", "sqlite", 0.86),
    new DebateArgument("Durability + transactions you'd otherwise reimplement badly.", "sqlite", 0.88),
    new DebateArgument("Migration story and tooling alone justify the single dependency.", "sqlite", 0.9),
};
var fileScript = new[]
{
    new DebateArgument("A flat file is simpler and dependency-free.", "file", 0.42),
    new DebateArgument("Still simpler; the CLI never needs queries.", "file", 0.40),
    new DebateArgument("Maintaining simplicity matters for a tiny tool.", "file", 0.39),
};

var debaters2 = new[]
{
    new Debater("SQLite", Scripted(sqliteScript)),
    new Debater("FlatFile", Scripted(fileScript)),
};

var r2 = await orchestrator.RunAsync("SQLite vs hand-rolled file format?", debaters2, ScriptedJudge);
PrintVerdict(r2);

// ── Scenario 3: a HUNG debate → abstain & escalate ─────────────
// A values question where the judge keeps the sides neck-and-neck. No
// convergence, no decisive lead — so the orchestrator refuses to fake a
// winner and hands the call off.
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine("Q3: \"Should the team adopt a 4-day work week?\" (no clean answer)");
Console.WriteLine();

var forScript = new[]
{
    new DebateArgument("For: focus + retention gains outweigh the lost day.", "for", 0.61),
    new DebateArgument("For: studies show output holds with happier staff.", "for", 0.6),
    new DebateArgument("For: it's a hiring differentiator.", "for", 0.59),
};
var againstScript = new[]
{
    new DebateArgument("Against: client coverage and deadlines suffer.", "against", 0.6),
    new DebateArgument("Against: not every role compresses cleanly.", "against", 0.61),
    new DebateArgument("Against: risky without a measured trial first.", "against", 0.6),
};

var debaters3 = new[]
{
    new Debater("For", Scripted(forScript)),
    new Debater("Against", Scripted(againstScript)),
};

var r3 = await orchestrator.RunAsync("Adopt a 4-day work week?", debaters3, ScriptedJudge);
PrintVerdict(r3);

Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("Pattern: argue → rebut with opponents in view → judge → stop on convergence or a clear lead.");
Console.WriteLine("Debate only as long as the disagreement is productive; abstain when it isn't.");

// ── A scripted judge rubric used by all three scenarios ─────────
// Scores the latest argument of a debater in [0,1]. A real judge would
// be a model grading the argument's strength against the transcript.
static double ScriptedJudge(string question, DebateArgument argument, IReadOnlyList<DebateExchange> history)
    => argument.Confidence;

// Wrap a fixed list of moves into a debater delegate. The debater repeats
// its final move if the debate runs longer than its script.
static Func<DebateTurnContext, CancellationToken, Task<DebateArgument>> Scripted(IReadOnlyList<DebateArgument> moves)
    => (ctx, ct) => Task.FromResult(moves[Math.Min(ctx.Round, moves.Count - 1)]);

// ── Console rendering helpers ──────────────────────────────────
static void PrintExchange(DebateExchange ex)
{
    Console.WriteLine($"  Round {ex.Round + 1}");
    foreach (var move in ex.Moves)
        Console.WriteLine($"    🗣️  {move.Debater,-9} → [{move.Argument.Answer}] score={move.Score:0.00}  ⟨{move.Argument.Reasoning}⟩");
    if (ex.Converged)
        Console.WriteLine($"    🤝 all debaters now agree on \"{ex.LeadingAnswer}\" — converged");
    Console.WriteLine();
}

static void PrintVerdict(DebateResult r)
{
    var icon = r.Verdict switch
    {
        DebateVerdict.Converged => "🤝",
        DebateVerdict.Decided => "⚖️",
        _ => "🛑",
    };
    Console.WriteLine($"  {icon} Verdict : {r.Verdict.ToString().ToUpperInvariant()}");
    Console.WriteLine($"     Answer  : {(r.Answer is null ? "— (hung, needs escalation)" : r.Answer)}");
    if (r.Winner is not null)
        Console.WriteLine($"     Winner  : {r.Winner}");
    Console.WriteLine($"     Rounds  : {r.RoundsUsed}/{r.RoundsBudgeted}  (margin {r.Margin:P0})");
    Console.WriteLine("     Scores  : " + string.Join("  ", r.Standings.Select(s => $"{s.Debater}={s.Score:0.##}")));
    Console.WriteLine();
}

// ══════════════════════════════════════════════════════════════
//  Supporting types — the reusable recipe logic
// ══════════════════════════════════════════════════════════════

/// <summary>One debater's move on a single round.</summary>
/// <param name="Reasoning">The argument text (for transparency / the transcript).</param>
/// <param name="Answer">The stance/answer this argument supports.</param>
/// <param name="Confidence">The debater's self-reported conviction in [0, 1].</param>
record DebateArgument(string Reasoning, string Answer, double Confidence = 1.0);

/// <summary>Context handed to a debater when it's asked for its next move.</summary>
/// <param name="Question">The motion under debate.</param>
/// <param name="DebaterName">This debater's name.</param>
/// <param name="Round">Zero-based round index.</param>
/// <param name="History">Completed exchanges so far (opponents' arguments are here).</param>
record DebateTurnContext(
    string Question,
    string DebaterName,
    int Round,
    IReadOnlyList<DebateExchange> History);

/// <summary>A participant: a name plus a delegate that produces its next argument.</summary>
/// <param name="Name">Unique debater name.</param>
/// <param name="Argue">Produces the next <see cref="DebateArgument"/> given the running context.</param>
record Debater(
    string Name,
    Func<DebateTurnContext, CancellationToken, Task<DebateArgument>> Argue);

/// <summary>One debater's scored contribution within a round.</summary>
record DebateMove(string Debater, DebateArgument Argument, double Score);

/// <summary>All moves played in a single round, plus the round-level read-out.</summary>
/// <param name="Round">Zero-based round index.</param>
/// <param name="Moves">Each debater's scored move this round.</param>
/// <param name="Converged">True if every debater's normalized answer matched this round.</param>
/// <param name="LeadingAnswer">The answer with the most cumulative judge weight after this round.</param>
record DebateExchange(
    int Round,
    IReadOnlyList<DebateMove> Moves,
    bool Converged,
    string LeadingAnswer);

/// <summary>A debater's cumulative standing at the end of the debate.</summary>
record DebateStanding(string Debater, double Score, string Answer);

/// <summary>How the debate resolved.</summary>
enum DebateVerdict
{
    /// <summary>The debaters reached the same answer — settled by agreement.</summary>
    Converged,
    /// <summary>No agreement, but the judge gave one side a clear, stable lead.</summary>
    Decided,
    /// <summary>Neither convergence nor a decisive lead — escalate.</summary>
    Hung,
}

/// <summary>The orchestrator's final decision after the debate.</summary>
record DebateResult(
    DebateVerdict Verdict,
    string? Answer,
    string? Winner,
    double Margin,
    int RoundsUsed,
    int RoundsBudgeted,
    IReadOnlyList<DebateStanding> Standings,
    IReadOnlyList<DebateExchange> Transcript);

/// <summary>Configuration for <see cref="DebateOrchestrator"/>.</summary>
record DebateOptions
{
    /// <summary>Hard cap on debate rounds. Default 4.</summary>
    public int MaxRounds { get; init; } = 4;

    /// <summary>
    /// Judge lead — as a share of the top two debaters' head-to-head weight,
    /// (leader − runner-up) / (leader + runner-up) — at or above which the
    /// orchestrator may end early with a <c>Decided</c> verdict, provided the
    /// same debater has led for <see cref="StableLeadRounds"/> rounds. Default 0.34.
    /// </summary>
    public double DecisiveMargin { get; init; } = 0.34;

    /// <summary>How many consecutive rounds the leader must hold a decisive margin before an early <c>Decided</c> stop. Default 2.</summary>
    public int StableLeadRounds { get; init; } = 2;

    /// <summary>Canonicalizes answers so equivalent stances compare equal (e.g. "Yes" == "yes."). Default: trim + lowercase.</summary>
    public Func<string, string>? NormalizeAnswer { get; init; }

    /// <summary>Observability hook fired once per completed round.</summary>
    public Action<DebateExchange>? OnExchange { get; init; }
}

/// <summary>
/// Runs a multi-agent debate: each round every debater argues (seeing the
/// transcript so far), a judge scores the moves, and the orchestrator decides
/// whether to keep going. It stops early when the debaters converge on one
/// answer or when the judge opens a clear and stable lead, and reports a hung
/// debate when neither happens within the round budget.
/// </summary>
class DebateOrchestrator
{
    private readonly DebateOptions _options;

    public DebateOrchestrator(DebateOptions? options = null) => _options = options ?? new DebateOptions();

    /// <summary>
    /// Conduct the debate over <paramref name="debaters"/>, scoring each move with
    /// <paramref name="judge"/> (question, latest argument, transcript-so-far) → score in [0,1].
    /// </summary>
    public async Task<DebateResult> RunAsync(
        string question,
        IReadOnlyList<Debater> debaters,
        Func<string, DebateArgument, IReadOnlyList<DebateExchange>, double> judge,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(debaters);
        ArgumentNullException.ThrowIfNull(judge);
        if (debaters.Count < 2) throw new ArgumentException("A debate needs at least two debaters.", nameof(debaters));
        if (_options.MaxRounds <= 0) throw new ArgumentOutOfRangeException(nameof(_options), "Need at least one round.");

        var normalize = _options.NormalizeAnswer ?? DefaultNormalize;
        var transcript = new List<DebateExchange>();

        // Cumulative judge weight per debater, and per (normalized) answer.
        var scoreByDebater = debaters.ToDictionary(d => d.Name, _ => 0.0);
        var answerByDebater = debaters.ToDictionary(d => d.Name, _ => "");
        var weightByAnswer = new Dictionary<string, (string Display, double Weight)>();

        string? leader = null;
        var stableLeadStreak = 0;
        var verdict = DebateVerdict.Hung;

        for (var round = 0; round < _options.MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            // 1) Every debater argues, seeing the transcript so far.
            var moves = new List<DebateMove>(debaters.Count);
            foreach (var d in debaters)
            {
                var ctx = new DebateTurnContext(question, d.Name, round, transcript);
                var argument = await d.Argue(ctx, ct);
                var score = Math.Clamp(judge(question, argument, transcript), 0.0, 1.0);
                moves.Add(new DebateMove(d.Name, argument, score));

                // 2) Accumulate judge weight toward the debater and its current answer.
                scoreByDebater[d.Name] += score;
                answerByDebater[d.Name] = argument.Answer ?? "";
                var key = normalize(argument.Answer ?? "");
                var display = argument.Answer ?? "";
                var prev = weightByAnswer.TryGetValue(key, out var w) ? w : (Display: display, Weight: 0.0);
                // Keep a DETERMINISTIC display form for the bucket: the ordinal-smallest
                // spelling seen for this normalized answer. Two debaters can converge on the
                // same normalized stance with different spellings ("Yes" vs "yes"); taking
                // whichever arrived first would make the round read-out's LeadingAnswer depend
                // on debater registration order — the exact non-determinism the final
                // converged Answer is careful to avoid. Pinning the min display keeps both in
                // agreement regardless of order.
                var canonicalDisplay = string.CompareOrdinal(display, prev.Display) < 0 ? display : prev.Display;
                weightByAnswer[key] = (canonicalDisplay, prev.Weight + score);
            }

            // 3) Round read-out: did everyone land on the same answer? Who leads overall?
            var distinctAnswers = answerByDebater.Values.Select(normalize).Distinct().Count();
            var converged = distinctAnswers == 1;
            var leadingAnswer = weightByAnswer
                .OrderByDescending(kv => kv.Value.Weight)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .First().Value.Display;

            var exchange = new DebateExchange(round, moves, converged, leadingAnswer);
            transcript.Add(exchange);
            _options.OnExchange?.Invoke(exchange);

            // 4) Stop conditions.
            //    (a) Convergence: the debaters now agree — settle immediately.
            if (converged)
            {
                verdict = DebateVerdict.Converged;
                break;
            }

            //    (b) Decisive, stable judge lead: one debater is clearly ahead for
            //        several rounds running — call it without looping to the cap.
            var (curLeader, margin) = CurrentLead(scoreByDebater);
            var decisiveThisRound = curLeader == leader && margin >= _options.DecisiveMargin;
            // Count only CONSECUTIVE decisive rounds for the same leader: a non-decisive
            // round (or a leader flip) breaks the streak back to zero, and a fresh decisive
            // round restarts it at one. Reviving the streak to one after a gap would let a
            // single decisive round masquerade as a multi-round stable lead.
            stableLeadStreak = decisiveThisRound ? stableLeadStreak + 1
                             : margin >= _options.DecisiveMargin ? 1
                             : 0;
            leader = curLeader;

            if (margin >= _options.DecisiveMargin && stableLeadStreak >= _options.StableLeadRounds)
            {
                verdict = DebateVerdict.Decided;
                break;
            }
        }

        return Resolve(verdict, transcript, scoreByDebater, answerByDebater);
    }

    /// <summary>Current leader and their lead expressed as a share of the head-to-head
    /// weight against the runner-up — i.e. (top - runnerUp) / (top + runnerUp). This is
    /// independent of how many rounds have elapsed, so a steady per-round gap yields a
    /// steady margin instead of being diluted by the growing total.</summary>
    private static (string Leader, double Margin) CurrentLead(IReadOnlyDictionary<string, double> scoreByDebater)
    {
        var ranked = scoreByDebater
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        var top = ranked[0];
        var runnerUp = ranked.Count > 1 ? ranked[1].Value : 0.0;
        var pair = top.Value + runnerUp;
        var margin = pair > 0 ? (top.Value - runnerUp) / pair : 0.0;
        return (top.Key, margin);
    }

    private DebateResult Resolve(
        DebateVerdict verdict,
        IReadOnlyList<DebateExchange> transcript,
        IReadOnlyDictionary<string, double> scoreByDebater,
        IReadOnlyDictionary<string, string> answerByDebater)
    {
        var standings = scoreByDebater
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new DebateStanding(kv.Key, Round(kv.Value), answerByDebater[kv.Key]))
            .ToList();

        var (leaderName, margin) = CurrentLead(scoreByDebater);

        string? answer;
        string? winner;
        switch (verdict)
        {
            case DebateVerdict.Converged:
                // Everyone agrees on one normalized stance. Report the highest-weighted
                // display form of it rather than answerByDebater.Values.First(): dictionary
                // enumeration order is not guaranteed, and two debaters can converge on the
                // same normalized answer while spelling it differently ("yes" vs "Yes.").
                // Picking .First() would make the reported answer depend on registration
                // order; the top-weighted display is deterministic (weight desc, then key).
                answer = LeadingDisplayAnswer(scoreByDebater, answerByDebater);
                winner = null; // a converged debate has no "winner" — it's a consensus
                break;
            case DebateVerdict.Decided:
                winner = leaderName;
                answer = answerByDebater[leaderName];
                break;
            default: // Hung
                answer = null;
                winner = null;
                break;
        }

        return new DebateResult(
            Verdict: verdict,
            Answer: answer,
            Winner: winner,
            Margin: Round(margin),
            RoundsUsed: transcript.Count,
            RoundsBudgeted: _options.MaxRounds,
            Standings: standings,
            Transcript: transcript);
    }

    /// <summary>The display form of the agreed answer with the most cumulative judge weight,
    /// tie-broken deterministically by display string. Used for a converged verdict so the
    /// reported answer never depends on debater registration order.</summary>
    private static string LeadingDisplayAnswer(
        IReadOnlyDictionary<string, double> scoreByDebater,
        IReadOnlyDictionary<string, string> answerByDebater)
        => answerByDebater
            .OrderByDescending(kv => scoreByDebater.TryGetValue(kv.Key, out var s) ? s : 0.0)
            .ThenBy(kv => kv.Value, StringComparer.Ordinal)
            .First().Value;

    private static string DefaultNormalize(string answer) => (answer ?? "").Trim().ToLowerInvariant();

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
