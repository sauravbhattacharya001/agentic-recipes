using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AgenticRecipes.Tests;

public class MultiAgentDebateTests
{
    // A judge that simply trusts each argument's self-reported confidence.
    private static double ConfidenceJudge(string question, DebateArgument argument, IReadOnlyList<DebateExchange> history)
        => argument.Confidence;

    // Build a debater that plays a fixed list of moves, repeating its last move
    // if the debate outlasts the script.
    private static Debater Scripted(string name, params DebateArgument[] moves)
        => new(name, (ctx, ct) => Task.FromResult(moves[Math.Min(ctx.Round, moves.Length - 1)]));

    [Fact]
    public async Task Converges_WhenDebatersAgree_AndStopsEarly()
    {
        var pro = Scripted("Pro",
            new DebateArgument("sorted needed", "yes", 0.8),
            new DebateArgument("still yes", "yes", 0.85));
        var con = Scripted("Con",
            new DebateArgument("no it doesn't", "no", 0.55),
            new DebateArgument("ok, conceding", "yes", 0.8)); // round 2: agrees

        var orch = new DebateOrchestrator(new DebateOptions { MaxRounds = 5 });
        var result = await orch.RunAsync("q", new[] { pro, con }, ConfidenceJudge);

        Assert.Equal(DebateVerdict.Converged, result.Verdict);
        Assert.Equal("yes", result.Answer);
        Assert.Null(result.Winner);             // consensus has no winner
        Assert.Equal(2, result.RoundsUsed);     // stopped at round 2, not the cap of 5
        Assert.Equal(5, result.RoundsBudgeted);
    }

    [Fact]
    public async Task Converges_OnRoundOne_WhenBothSidesAlreadyAgree()
    {
        var a = Scripted("A", new DebateArgument("agree", "blue", 0.7));
        var b = Scripted("B", new DebateArgument("agree too", "blue", 0.7));

        var orch = new DebateOrchestrator(new DebateOptions { MaxRounds = 4 });
        var result = await orch.RunAsync("color?", new[] { a, b }, ConfidenceJudge);

        Assert.Equal(DebateVerdict.Converged, result.Verdict);
        Assert.Equal("blue", result.Answer);
        Assert.Equal(1, result.RoundsUsed);
        Assert.True(result.Transcript[0].Converged);
    }

    [Fact]
    public async Task Decides_WhenJudgeGivesStableClearLead()
    {
        // Persistent disagreement; one side is consistently scored much higher.
        var strong = Scripted("Strong", new DebateArgument("compelling", "sqlite", 0.95));
        var weak = Scripted("Weak", new DebateArgument("meh", "file", 0.30));

        var orch = new DebateOrchestrator(new DebateOptions
        {
            MaxRounds = 6,
            DecisiveMargin = 0.34,
            StableLeadRounds = 2,
        });
        var result = await orch.RunAsync("q", new[] { strong, weak }, ConfidenceJudge);

        Assert.Equal(DebateVerdict.Decided, result.Verdict);
        Assert.Equal("Strong", result.Winner);
        Assert.Equal("sqlite", result.Answer);
        // Lead is decisive from round 1, needs 2 stable rounds -> stops at round 2.
        Assert.Equal(2, result.RoundsUsed);
        Assert.True(result.Margin >= 0.34);
    }

    [Fact]
    public async Task Hangs_WhenNeitherConvergesNorSeparates()
    {
        // Neck-and-neck the whole way: no agreement, no decisive lead.
        var f = Scripted("For", new DebateArgument("for", "for", 0.61));
        var g = Scripted("Against", new DebateArgument("against", "against", 0.60));

        var orch = new DebateOrchestrator(new DebateOptions
        {
            MaxRounds = 3,
            DecisiveMargin = 0.34,
            StableLeadRounds = 2,
        });
        var result = await orch.RunAsync("q", new[] { f, g }, ConfidenceJudge);

        Assert.Equal(DebateVerdict.Hung, result.Verdict);
        Assert.Null(result.Answer);             // refuses to fake a verdict
        Assert.Null(result.Winner);
        Assert.Equal(3, result.RoundsUsed);     // ran the full budget
    }

    [Fact]
    public async Task StableLead_RequiresConsecutiveDecisiveRounds_NotJustTwoTotal()
    {
        // Same leader (A) throughout, but the per-round margin DIPS below the
        // decisive bar in the middle round before recovering:
        //   r0: A=0.9 B=0.1 -> cum 0.9/0.1, margin 0.80  (decisive)
        //   r1: A=0.0 B=0.8 -> cum 0.9/0.9, margin 0.00  (NOT decisive)
        //   r2: A=0.9 B=0.0 -> cum 1.8/0.9, margin 0.33  (decisive again)
        // The decisive rounds (0 and 2) are NOT consecutive, so a StableLeadRounds=2
        // requirement must NOT be satisfied -> the debate stays Hung. A streak that
        // resets to 1 (instead of 0) on the non-decisive round would wrongly reach 2
        // on r2 and fire an early Decided.
        var a = new Debater("A", (ctx, ct) => Task.FromResult(ctx.Round switch
        {
            0 => new DebateArgument("strong", "a", 0.9),
            1 => new DebateArgument("silent", "a", 0.0),
            _ => new DebateArgument("strong", "a", 0.9),
        }));
        var b = new Debater("B", (ctx, ct) => Task.FromResult(ctx.Round switch
        {
            0 => new DebateArgument("weak", "b", 0.1),
            1 => new DebateArgument("strong", "b", 0.8),
            _ => new DebateArgument("silent", "b", 0.0),
        }));

        var orch = new DebateOrchestrator(new DebateOptions
        {
            MaxRounds = 3,
            DecisiveMargin = 0.30,
            StableLeadRounds = 2,
        });
        var result = await orch.RunAsync("q", new[] { a, b }, ConfidenceJudge);

        // A never held a decisive lead for two *consecutive* rounds, so no early stop.
        Assert.Equal(DebateVerdict.Hung, result.Verdict);
        Assert.Null(result.Winner);
        Assert.Equal(3, result.RoundsUsed); // ran the full budget instead of deciding at r2
    }

    [Fact]
    public async Task StableLead_Resets_WhenLeaderFlips()
    {
        // A leads big in round 1, then B leads big in round 2. The lead is never
        // held by the SAME debater for two rounds, so no early Decided stop fires.
        var a = new Debater("A", (ctx, ct) => Task.FromResult(
            ctx.Round == 0 ? new DebateArgument("strong", "a", 0.95)
                           : new DebateArgument("weak", "a", 0.05)));
        var b = new Debater("B", (ctx, ct) => Task.FromResult(
            ctx.Round == 0 ? new DebateArgument("weak", "b", 0.05)
                           : new DebateArgument("strong", "b", 0.95)));

        var orch = new DebateOrchestrator(new DebateOptions
        {
            MaxRounds = 2,
            DecisiveMargin = 0.34,
            StableLeadRounds = 2,
        });
        var result = await orch.RunAsync("q", new[] { a, b }, ConfidenceJudge);

        Assert.Equal(DebateVerdict.Hung, result.Verdict);
        // Totals are tied (0.95 + 0.05 each), so no winner emerges.
        Assert.Equal(2, result.RoundsUsed);
    }

    [Fact]
    public async Task DebatersSeeOpponentHistory_OnLaterRounds()
    {
        // First round: empty history. Later rounds: the previous exchange is visible.
        var seenHistoryCounts = new List<int>();
        var probe = new Debater("Probe", (ctx, ct) =>
        {
            seenHistoryCounts.Add(ctx.History.Count);
            return Task.FromResult(new DebateArgument("x", "x", 0.5));
        });
        var foil = Scripted("Foil", new DebateArgument("y", "y", 0.5));

        var orch = new DebateOrchestrator(new DebateOptions { MaxRounds = 3 });
        await orch.RunAsync("q", new[] { probe, foil }, ConfidenceJudge);

        Assert.Equal(new[] { 0, 1, 2 }, seenHistoryCounts);
    }

    [Fact]
    public async Task NormalizeAnswer_TreatsEquivalentStancesAsAgreement()
    {
        // "Yes." and "yes" should count as the same stance -> convergence.
        var a = Scripted("A", new DebateArgument("a", "Yes.", 0.7));
        var b = Scripted("B", new DebateArgument("b", "yes", 0.7));

        var orch = new DebateOrchestrator(new DebateOptions
        {
            MaxRounds = 3,
            NormalizeAnswer = s => s.Trim().TrimEnd('.').ToLowerInvariant(),
        });
        var result = await orch.RunAsync("q", new[] { a, b }, ConfidenceJudge);

        Assert.Equal(DebateVerdict.Converged, result.Verdict);
        Assert.Equal(1, result.RoundsUsed);
    }

    [Fact]
    public async Task OnExchange_FiresOncePerRound()
    {
        var rounds = new List<int>();
        var a = Scripted("A", new DebateArgument("a", "a", 0.6));
        var b = Scripted("B", new DebateArgument("b", "b", 0.6));

        var orch = new DebateOrchestrator(new DebateOptions
        {
            MaxRounds = 3,
            DecisiveMargin = 2.0, // unreachable -> force full budget, no early stop
            OnExchange = ex => rounds.Add(ex.Round),
        });
        await orch.RunAsync("q", new[] { a, b }, ConfidenceJudge);

        Assert.Equal(new[] { 0, 1, 2 }, rounds);
    }

    [Fact]
    public async Task Judge_ScoreIsClampedIntoUnitInterval()
    {
        var a = Scripted("A", new DebateArgument("a", "a", 0.6));
        var b = Scripted("B", new DebateArgument("b", "b", 0.6));

        // Judge returns out-of-range scores; orchestrator must clamp to [0,1].
        var orch = new DebateOrchestrator(new DebateOptions { MaxRounds = 1 });
        var result = await orch.RunAsync("q", new[] { a, b }, (_, arg, _) => arg.Answer == "a" ? 5.0 : -3.0);

        var standA = result.Standings.Single(s => s.Debater == "A");
        var standB = result.Standings.Single(s => s.Debater == "B");
        Assert.Equal(1.0, standA.Score);
        Assert.Equal(0.0, standB.Score);
    }

    [Fact]
    public async Task Throws_WhenFewerThanTwoDebaters()
    {
        var only = Scripted("Solo", new DebateArgument("a", "a", 0.5));
        var orch = new DebateOrchestrator();

        await Assert.ThrowsAsync<ArgumentException>(
            () => orch.RunAsync("q", new[] { only }, ConfidenceJudge));
    }

    [Fact]
    public async Task Throws_WhenMaxRoundsNotPositive()
    {
        var a = Scripted("A", new DebateArgument("a", "a", 0.5));
        var b = Scripted("B", new DebateArgument("b", "b", 0.5));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new DebateOrchestrator(new DebateOptions { MaxRounds = 0 })
                .RunAsync("q", new[] { a, b }, ConfidenceJudge));
    }

    [Fact]
    public async Task Cancellation_IsHonored()
    {
        var a = Scripted("A", new DebateArgument("a", "a", 0.5));
        var b = Scripted("B", new DebateArgument("b", "b", 0.5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var orch = new DebateOrchestrator(new DebateOptions { MaxRounds = 3 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orch.RunAsync("q", new[] { a, b }, ConfidenceJudge, cts.Token));
    }

    [Fact]
    public async Task Transcript_RecordsEveryRoundWithAllMoves()
    {
        var a = Scripted("A", new DebateArgument("a1", "a", 0.6), new DebateArgument("a2", "a", 0.6));
        var b = Scripted("B", new DebateArgument("b1", "b", 0.5), new DebateArgument("b2", "b", 0.5));

        var orch = new DebateOrchestrator(new DebateOptions
        {
            MaxRounds = 2,
            DecisiveMargin = 2.0, // never decisive -> run both rounds
        });
        var result = await orch.RunAsync("q", new[] { a, b }, ConfidenceJudge);

        Assert.Equal(2, result.Transcript.Count);
        Assert.All(result.Transcript, ex => Assert.Equal(2, ex.Moves.Count));
        Assert.Equal(0, result.Transcript[0].Round);
        Assert.Equal(1, result.Transcript[1].Round);
    }

    // ── Three+ debaters ───────────────────────────────────────
    // The README advertises "two or more agents", and the orchestrator is
    // written for N debaters (the runner-up in CurrentLead is the 2nd of N,
    // convergence is "one distinct answer across all of them", and several
    // debaters can pile weight onto the same answer). The cases below lock in
    // that N-debater contract, which the two-debater tests above can't express.

    [Fact]
    public async Task ThreeDebaters_AllAgree_Converges()
    {
        // Every move lands on the same answer in round 1 -> convergence needs
        // ALL three to match, not just a pair.
        var a = Scripted("A", new DebateArgument("a", "green", 0.7));
        var b = Scripted("B", new DebateArgument("b", "green", 0.6));
        var c = Scripted("C", new DebateArgument("c", "green", 0.8));

        var orch = new DebateOrchestrator(new DebateOptions { MaxRounds = 4 });
        var result = await orch.RunAsync("color?", new[] { a, b, c }, ConfidenceJudge);

        Assert.Equal(DebateVerdict.Converged, result.Verdict);
        Assert.Equal("green", result.Answer);
        Assert.Null(result.Winner);
        Assert.Equal(1, result.RoundsUsed);
        Assert.Equal(3, result.Transcript[0].Moves.Count);
        Assert.Equal(3, result.Standings.Count);
    }

    [Fact]
    public async Task ThreeDebaters_TwoAgreeOneDissents_DoesNotConverge()
    {
        // A majority (two share "merge") is NOT consensus while a third holds out:
        // distinctAnswers stays > 1, so the debate must not settle on convergence.
        // Neither does the A-vs-B margin separate (they're neck-and-neck), so with
        // a short budget it ends Hung rather than faking agreement.
        var a = Scripted("A", new DebateArgument("merge", "merge", 0.6));
        var b = Scripted("B", new DebateArgument("merge too", "merge", 0.6));
        var c = Scripted("C", new DebateArgument("split", "split", 0.55));

        var orch = new DebateOrchestrator(new DebateOptions
        {
            MaxRounds = 3,
            DecisiveMargin = 0.34,
            StableLeadRounds = 2,
        });
        var result = await orch.RunAsync("q", new[] { a, b, c }, ConfidenceJudge);

        Assert.Equal(DebateVerdict.Hung, result.Verdict);
        Assert.Null(result.Answer);
        Assert.All(result.Transcript, ex => Assert.False(ex.Converged));
        // "merge" outweighs "split" each round, so it's the leading answer even
        // though the debate never formally converges on it.
        Assert.Equal("merge", result.Transcript[^1].LeadingAnswer);
        Assert.Equal(3, result.RoundsUsed);
    }

    [Fact]
    public async Task ThreeDebaters_OneDominatesBothOthers_Decides()
    {
        // A clear, stable lead over the FIELD: A must out-score the runner-up
        // (2nd of three) by the decisive margin for two consecutive rounds.
        var a = Scripted("A", new DebateArgument("strong", "x", 0.95));
        var b = Scripted("B", new DebateArgument("weak", "y", 0.20));
        var c = Scripted("C", new DebateArgument("weaker", "z", 0.15));

        var orch = new DebateOrchestrator(new DebateOptions
        {
            MaxRounds = 5,
            DecisiveMargin = 0.34,
            StableLeadRounds = 2,
        });
        var result = await orch.RunAsync("q", new[] { a, b, c }, ConfidenceJudge);

        Assert.Equal(DebateVerdict.Decided, result.Verdict);
        Assert.Equal("A", result.Winner);
        Assert.Equal("x", result.Answer);
        Assert.Equal(2, result.RoundsUsed); // decisive from r0, stops once the streak hits 2
        // Standings are ranked best-first across all three.
        Assert.Equal(new[] { "A", "B", "C" }, result.Standings.Select(s => s.Debater).ToArray());
    }
}

// ══════════════════════════════════════════════════════════════
//  Reusable recipe logic under test (mirrors recipes/multi-agent-debate/Program.cs)
// ══════════════════════════════════════════════════════════════

record DebateArgument(string Reasoning, string Answer, double Confidence = 1.0);

record DebateTurnContext(
    string Question,
    string DebaterName,
    int Round,
    IReadOnlyList<DebateExchange> History);

record Debater(
    string Name,
    Func<DebateTurnContext, CancellationToken, Task<DebateArgument>> Argue);

record DebateMove(string Debater, DebateArgument Argument, double Score);

record DebateExchange(
    int Round,
    IReadOnlyList<DebateMove> Moves,
    bool Converged,
    string LeadingAnswer);

record DebateStanding(string Debater, double Score, string Answer);

enum DebateVerdict
{
    Converged,
    Decided,
    Hung,
}

record DebateResult(
    DebateVerdict Verdict,
    string? Answer,
    string? Winner,
    double Margin,
    int RoundsUsed,
    int RoundsBudgeted,
    IReadOnlyList<DebateStanding> Standings,
    IReadOnlyList<DebateExchange> Transcript);

record DebateOptions
{
    public int MaxRounds { get; init; } = 4;
    public double DecisiveMargin { get; init; } = 0.34;
    public int StableLeadRounds { get; init; } = 2;
    public Func<string, string>? NormalizeAnswer { get; init; }
    public Action<DebateExchange>? OnExchange { get; init; }
}

class DebateOrchestrator
{
    private readonly DebateOptions _options;

    public DebateOrchestrator(DebateOptions? options = null) => _options = options ?? new DebateOptions();

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

        var scoreByDebater = debaters.ToDictionary(d => d.Name, _ => 0.0);
        var answerByDebater = debaters.ToDictionary(d => d.Name, _ => "");
        var weightByAnswer = new Dictionary<string, (string Display, double Weight)>();

        string? leader = null;
        var stableLeadStreak = 0;
        var verdict = DebateVerdict.Hung;

        for (var round = 0; round < _options.MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var moves = new List<DebateMove>(debaters.Count);
            foreach (var d in debaters)
            {
                var ctx = new DebateTurnContext(question, d.Name, round, transcript);
                var argument = await d.Argue(ctx, ct);
                var score = Math.Clamp(judge(question, argument, transcript), 0.0, 1.0);
                moves.Add(new DebateMove(d.Name, argument, score));

                scoreByDebater[d.Name] += score;
                answerByDebater[d.Name] = argument.Answer ?? "";
                var key = normalize(argument.Answer ?? "");
                var prev = weightByAnswer.TryGetValue(key, out var w) ? w : (Display: argument.Answer ?? "", Weight: 0.0);
                weightByAnswer[key] = (prev.Display, prev.Weight + score);
            }

            var distinctAnswers = answerByDebater.Values.Select(normalize).Distinct().Count();
            var converged = distinctAnswers == 1;
            var leadingAnswer = weightByAnswer
                .OrderByDescending(kv => kv.Value.Weight)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .First().Value.Display;

            var exchange = new DebateExchange(round, moves, converged, leadingAnswer);
            transcript.Add(exchange);
            _options.OnExchange?.Invoke(exchange);

            if (converged)
            {
                verdict = DebateVerdict.Converged;
                break;
            }

            var (curLeader, margin) = CurrentLead(scoreByDebater);
            var decisiveThisRound = curLeader == leader && margin >= _options.DecisiveMargin;
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
                answer = answerByDebater.Values.First();
                winner = null;
                break;
            case DebateVerdict.Decided:
                winner = leaderName;
                answer = answerByDebater[leaderName];
                break;
            default:
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

    private static string DefaultNormalize(string answer) => (answer ?? "").Trim().ToLowerInvariant();

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
