using Xunit;

namespace AgenticRecipes.Tests;

public class TreeOfThoughtsTests
{
    // ── Helpers ──────────────────────────────────────────────
    // A deterministic "reach the target number" search, mirroring the recipe's
    // demo: from 0, each move appends +9 / +5 / *2 / -1; a state is solved when it
    // evaluates exactly to the target. The expander proposes the next moves and the
    // evaluator scores closeness — both pure, so the search is fully reproducible.

    private static readonly (string Label, Func<int, int> Apply)[] Moves =
    {
        ("+9", n => n + 9),
        ("+5", n => n + 5),
        ("*2", n => n * 2),
        ("-1", n => n - 1),
    };

    private static int StateValue(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        // States look like "0 +9 = 9 +5 = 14"; the final value follows the LAST '='.
        var afterEquals = s.Contains('=') ? s[(s.LastIndexOf('=') + 1)..] : s;
        return int.TryParse(afterEquals.Trim(), out var v) ? v : 0;
    }

    private static IReadOnlyList<ThoughtExpansion> ArithmeticExpander(string thought, int depth)
    {
        var current = StateValue(thought);
        var outs = new List<ThoughtExpansion>();
        foreach (var (label, apply) in Moves)
        {
            var next = apply(current);
            if (next < 0) continue;
            var nextThought = thought.Length == 0 ? $"0 {label}" : $"{thought} {label}";
            outs.Add(new ThoughtExpansion($"apply {label}", $"{nextThought} = {next}"));
        }
        return outs;
    }

    private static Func<string, int, ThoughtEvaluation> CloserToTarget(int target, double spread = 30.0) =>
        (state, depth) =>
        {
            var value = StateValue(state);
            if (value == target) return new ThoughtEvaluation(1.0, true, "hit");
            var score = Math.Max(0.0, 0.95 - Math.Abs(target - value) / spread);
            return new ThoughtEvaluation(score, false, $"value {value}");
        };

    // ── Tests ────────────────────────────────────────────────

    [Fact]
    public void StateValue_MultiStepState_ReadsValueAfterLastEquals()
    {
        // Regression: a state accumulates as "0 +9 = 9 +5 = 14", so the running
        // value is what follows the LAST '='. Reading from the FIRST '=' would leave
        // " 9 +5 = 14" (unparseable) and mis-read the state. Multi-step states must
        // resolve to their final value.
        Assert.Equal(14, StateValue("0 +9 = 9 +5 = 14"));
        Assert.Equal(23, StateValue("0 +9 = 9 +9 = 18 +5 = 23"));
        Assert.Equal(9, StateValue("0 +9 = 9"));
        Assert.Equal(0, StateValue(""));
    }

    [Fact]
    public async Task SearchAsync_FindsSolution_ReturnsSolvedWithPath()
    {
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 2,
            MaxDepth = 5,
            MaxExpansions = 25,
            PruneThreshold = 0.10,
        });

        var result = await agent.SearchAsync("", ArithmeticExpander, CloserToTarget(23));

        Assert.Equal(ToTOutcome.Solved, result.Outcome);
        Assert.True(result.Solved);
        Assert.Equal(1.0, result.BestScore);
        // The solution state evaluates exactly to the target.
        Assert.Equal(23, StateValue(result.BestState));
        // The reconstructed path is the chain of steps from the root to the goal.
        Assert.NotEmpty(result.SolutionPath);
        Assert.All(result.SolutionPath, step => Assert.StartsWith("apply", step));
    }

    [Fact]
    public async Task SearchAsync_SolutionPath_ReplaysToTheSolvedState()
    {
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 3,
            MaxDepth = 6,
            MaxExpansions = 40,
        });

        var result = await agent.SearchAsync("", ArithmeticExpander, CloserToTarget(23));

        // Replaying the move labels in the path from 0 must reproduce the target.
        var value = 0;
        foreach (var step in result.SolutionPath)
        {
            var label = step.Replace("apply", "").Trim();
            var move = Moves.First(m => m.Label == label);
            value = move.Apply(value);
        }
        Assert.Equal(23, value);
        Assert.Equal(value, StateValue(result.BestState));
    }

    [Fact]
    public async Task SearchAsync_RootAlreadySolved_StopsImmediately()
    {
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions { MaxExpansions = 10 });

        var expandCalls = 0;
        var result = await agent.SearchAsync(
            rootThought: "seed",
            expand: (thought, depth) => { expandCalls++; return ArithmeticExpander(thought, depth); },
            evaluate: (state, depth) => new ThoughtEvaluation(1.0, true, "root is the answer"));

        Assert.Equal(ToTOutcome.Solved, result.Outcome);
        Assert.True(result.Solved);
        Assert.Equal(0, result.NodesExpanded);          // never expanded anything
        Assert.Equal(0, expandCalls);
        Assert.Equal(1, result.NodesEvaluated);         // only the root was scored
        Assert.Empty(result.SolutionPath);              // root has no steps above it
    }

    [Fact]
    public async Task SearchAsync_BudgetTooSmall_StopsWithBudgetExhausted()
    {
        // A faraway target with a tiny budget and generous depth: the expansion
        // budget is the binding constraint.
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 3,
            MaxDepth = 50,
            MaxExpansions = 5,
        });

        var result = await agent.SearchAsync("", ArithmeticExpander, CloserToTarget(100000, spread: 5000.0));

        Assert.Equal(ToTOutcome.BudgetExhausted, result.Outcome);
        Assert.False(result.Solved);
        Assert.Equal(5, result.NodesExpanded);          // spent exactly the budget
    }

    [Fact]
    public async Task SearchAsync_DepthLimitBlocksEveryBranch_StopsWithDepthLimited()
    {
        // Depth 1 with an unreachable-at-depth-1 target: the root expands once, all
        // children sit at the depth cap and cannot grow -> DepthLimited.
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 4,
            MaxDepth = 1,
            MaxExpansions = 50,
        });

        var result = await agent.SearchAsync("", ArithmeticExpander, CloserToTarget(500, spread: 600.0));

        Assert.Equal(ToTOutcome.DepthLimited, result.Outcome);
        Assert.False(result.Solved);
        Assert.Equal(1, result.NodesExpanded);          // only the root expanded
        // Every scored child is at depth 1 (the cap).
        Assert.All(result.Explored.Where(n => n.Depth > 0), n => Assert.Equal(1, n.Depth));
    }

    [Fact]
    public async Task SearchAsync_AllChildrenPruned_StopsWithFrontierExhausted()
    {
        // Prune threshold above every reachable score AND depth high enough that the
        // depth cap never trips: the frontier empties with nothing kept -> exhausted.
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 3,
            MaxDepth = 10,
            MaxExpansions = 50,
            PruneThreshold = 2.0,   // nothing can ever clear this
        });

        var result = await agent.SearchAsync("", ArithmeticExpander, CloserToTarget(23));

        Assert.Equal(ToTOutcome.FrontierExhausted, result.Outcome);
        Assert.False(result.Solved);
        Assert.Equal(1, result.NodesExpanded);          // root expanded once, then frontier empty
    }

    [Fact]
    public async Task SearchAsync_FrontierEmptiedByPruning_ReportsFrontierExhausted_EvenWhenAnUnrelatedNodeHitMaxDepth()
    {
        // Regression: DepthLimited must be reserved for when the depth ceiling is the
        // SOLE reason the search stalled. Here a high-scoring branch reaches MaxDepth
        // (so a node IS depth-capped), but the frontier ultimately empties because a
        // DIFFERENT, lower-scoring branch has ALL its children pruned below the floor.
        // Since pruning - not depth - drained the frontier, the honest outcome is the
        // general FrontierExhausted, not DepthLimited. The old code reported
        // DepthLimited whenever any node had ever hit MaxDepth, conflating the two.
        //
        // Graph (best-first, MaxDepth 2, PruneThreshold 0.30):
        //   root(0.10) -> HI(0.90, d1), LO(0.50, d1)          both clear the floor
        //   expand HI  -> HI-DEEP(0.80, d2)                    survives, enqueued
        //   pop HI-DEEP: d2 >= MaxDepth 2 -> depth-capped (sets anyDepthCapped)
        //   expand LO  -> LO-A(0.10), LO-B(0.20)               BOTH below 0.30 -> pruned
        //   frontier now empty: the terminal cause was LO's pruning, not depth.
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 4,
            MaxDepth = 2,
            MaxExpansions = 50,
            PruneThreshold = 0.30,
            Strategy = SearchStrategy.BestFirst,
        });

        var result = await agent.SearchAsync(
            rootThought: "root",
            expand: (thought, depth) => thought switch
            {
                "root" => new[]
                {
                    new ThoughtExpansion("to-hi", "HI"),
                    new ThoughtExpansion("to-lo", "LO"),
                },
                "HI" => new[] { new ThoughtExpansion("hi-deeper", "HI-DEEP") },
                "LO" => new[]
                {
                    new ThoughtExpansion("lo-a", "LO-A"),   // pruned (0.10 < 0.30)
                    new ThoughtExpansion("lo-b", "LO-B"),   // pruned (0.20 < 0.30)
                },
                _ => Array.Empty<ThoughtExpansion>(),
            },
            evaluate: (state, depth) => state switch
            {
                "HI" => new ThoughtEvaluation(0.90, false, "promising"),
                "HI-DEEP" => new ThoughtEvaluation(0.80, false, "still promising but at the cap"),
                "LO" => new ThoughtEvaluation(0.50, false, "middling"),
                "LO-A" => new ThoughtEvaluation(0.10, false, "weak"),
                "LO-B" => new ThoughtEvaluation(0.20, false, "weak"),
                _ => new ThoughtEvaluation(0.10, false, "root"),
            });

        // Pruning drained the frontier while an unrelated node was merely depth-capped:
        // the outcome must be FrontierExhausted, NOT DepthLimited.
        Assert.Equal(ToTOutcome.FrontierExhausted, result.Outcome);
        Assert.False(result.Solved);
        // Sanity: a node really did reach the depth cap (so the old code WOULD have
        // mislabeled this as DepthLimited).
        Assert.Contains(result.Explored, n => n.State == "HI-DEEP" && n.Depth == 2);
    }

    [Fact]
    public async Task SearchAsync_DepthLimited_RequiresNoPruningOrDeadEnds()
    {
        // Complements the regression above: when the ONLY thing stopping the search is
        // the depth ceiling (nothing is pruned, no branch dead-ends), the outcome is
        // DepthLimited. PruneThreshold 0.0 means no reachable score is ever pruned, and
        // the expander always returns children, so depth is the sole binding constraint.
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 4,
            MaxDepth = 2,
            MaxExpansions = 50,
            PruneThreshold = 0.0,
            SolvedThreshold = 2.0,   // unreachable, so it never "solves"
        });

        var result = await agent.SearchAsync(
            "",
            ArithmeticExpander,
            evaluate: (state, depth) => new ThoughtEvaluation(0.5, false, "flat, never solves"));

        Assert.Equal(ToTOutcome.DepthLimited, result.Outcome);
        Assert.False(result.Solved);
        // Every explored non-root node sits within the depth cap.
        Assert.All(result.Explored.Where(n => n.Depth > 0), n => Assert.True(n.Depth <= 2));
    }

    [Fact]
    public async Task SearchAsync_BeamWidth_BoundsTheOpenFrontier()
    {
        // With beam width 1 and a never-solving evaluator, each round keeps exactly
        // one open node, so expansions march one-per-depth until the depth cap.
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 1,
            MaxDepth = 4,
            MaxExpansions = 100,
            Strategy = SearchStrategy.BestFirst,
        });

        var result = await agent.SearchAsync(
            "",
            ArithmeticExpander,
            evaluate: (state, depth) => new ThoughtEvaluation(0.5, false, "flat"));

        // root(d0) + one survivor per depth 1..3 expand = 4 expansions, then the
        // depth-4 survivor cannot grow.
        Assert.Equal(ToTOutcome.DepthLimited, result.Outcome);
        Assert.Equal(4, result.NodesExpanded);
    }

    [Fact]
    public async Task SearchAsync_DeadEndExpander_BacktracksToSibling()
    {
        // The expander returns NO children for one specific high-scoring state,
        // forcing the search to fall back to a lower-scoring sibling and still solve.
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 3,
            MaxDepth = 3,
            MaxExpansions = 30,
        });

        var result = await agent.SearchAsync(
            rootThought: "0",
            expand: (thought, depth) =>
            {
                if (depth == 0)
                    return new[]
                    {
                        new ThoughtExpansion("go-trap", "TRAP"),    // looks best, dead-ends
                        new ThoughtExpansion("go-good", "GOOD-1"),  // the real route
                    };
                if (thought == "TRAP")
                    return Array.Empty<ThoughtExpansion>();          // dead end -> backtrack
                if (thought == "GOOD-1")
                    return new[] { new ThoughtExpansion("finish", "WIN") };
                return Array.Empty<ThoughtExpansion>();
            },
            evaluate: (state, depth) => state switch
            {
                "TRAP" => new ThoughtEvaluation(0.9, false, "tempting but dead"),
                "GOOD-1" => new ThoughtEvaluation(0.6, false, "the right way"),
                "WIN" => new ThoughtEvaluation(1.0, true, "solved"),
                _ => new ThoughtEvaluation(0.1, false, "root"),
            });

        Assert.Equal(ToTOutcome.Solved, result.Outcome);
        Assert.True(result.Solved);
        Assert.Equal("WIN", result.BestState);
        // The winning path went through the lower-scoring sibling, not the trap.
        Assert.Equal(new[] { "go-good", "finish" }, result.SolutionPath);
    }

    [Fact]
    public async Task SearchAsync_PruneThreshold_DropsWeakChildrenButKeepsStrong()
    {
        // One child clears the floor, the rest do not: only the strong branch stays
        // open, so the search still drives toward the target through it.
        var kept = new List<string>();
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 4,
            MaxDepth = 5,
            MaxExpansions = 30,
            PruneThreshold = 0.40,
            OnNode = n => { if (n.Depth == 1) kept.Add(n.State); },
        });

        var result = await agent.SearchAsync("", ArithmeticExpander, CloserToTarget(23));

        // The recipe solves 23; pruning weak depth-1 states never blocks the route.
        Assert.True(result.Solved);
        Assert.Contains(kept, s => StateValue(s) == 9);   // "+9" (score 0.48) was explored
    }

    [Fact]
    public async Task SearchAsync_OnNode_FiresForRootAndEveryScoredChild()
    {
        var seen = new List<int>();
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 2,
            MaxDepth = 3,
            MaxExpansions = 20,
            OnNode = node => seen.Add(node.Id),
        });

        var result = await agent.SearchAsync("", ArithmeticExpander, CloserToTarget(23));

        // The hook fired exactly once per scored node, ids are unique and 1-based.
        Assert.Equal(result.NodesEvaluated, seen.Count);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
        Assert.Contains(1, seen);   // the root (id 1) was reported
    }

    [Fact]
    public async Task SearchAsync_BestFirst_ExpandsHighestScoringNodeFirst()
    {
        // Record expansion order via OnNode's parent relationship: best-first must
        // expand the depth-1 child with the highest score before its weaker siblings.
        var expandedStates = new List<string>();
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 4,
            MaxDepth = 2,
            MaxExpansions = 3,        // root + the two best depth-1 nodes
            Strategy = SearchStrategy.BestFirst,
        });

        var result = await agent.SearchAsync(
            rootThought: "",
            expand: (thought, depth) =>
            {
                expandedStates.Add(thought);
                return ArithmeticExpander(thought, depth);
            },
            evaluate: CloserToTarget(23));

        // First expansion is the root (""), then the best depth-1 state.
        // "+9" -> 9 scores highest at depth 1, so it is expanded before "+5" -> 5.
        Assert.Equal("", expandedStates[0]);
        Assert.Equal(9, StateValue(expandedStates[1]));
    }

    [Fact]
    public async Task SearchAsync_ClampsScores_IntoZeroOneRange()
    {
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 2,
            MaxDepth = 1,
            MaxExpansions = 10,
        });

        var result = await agent.SearchAsync(
            "",
            ArithmeticExpander,
            evaluate: (state, depth) =>
                depth == 0
                    ? new ThoughtEvaluation(-5.0, false, "very negative")   // clamps to 0
                    : new ThoughtEvaluation(9.0, false, "way over"));       // clamps to <=1

        Assert.All(result.Explored, n => Assert.InRange(n.Score, 0.0, 1.0));
    }

    [Fact]
    public async Task SearchAsync_ScoreAtOrAboveSolvedThreshold_CountsAsSolved()
    {
        // SolvedThreshold below 1.0: a merely "good enough" state ends the search.
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 3,
            MaxDepth = 5,
            MaxExpansions = 30,
            SolvedThreshold = 0.75,
        });

        var result = await agent.SearchAsync("", ArithmeticExpander, CloserToTarget(23));

        Assert.Equal(ToTOutcome.Solved, result.Outcome);
        Assert.True(result.Solved);
        Assert.True(result.BestScore >= 0.75);
    }

    [Fact]
    public async Task SearchAsync_TracksBestNode_EvenWhenNotSolved()
    {
        // Never solves (threshold unreachable), but the best partial state seen must
        // be reported with its score and a path back to the root.
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 2,
            MaxDepth = 3,
            MaxExpansions = 20,
            SolvedThreshold = 2.0,   // unreachable
        });

        // A pure closeness score that never raises the solved flag, so nothing can
        // satisfy the (unreachable) threshold -> the search never "solves".
        var result = await agent.SearchAsync(
            "",
            ArithmeticExpander,
            evaluate: (state, depth) =>
            {
                var value = StateValue(state);
                var score = Math.Max(0.0, 0.95 - Math.Abs(23 - value) / 30.0);
                return new ThoughtEvaluation(score, false, $"value {value}");
            });

        Assert.False(result.Solved);
        Assert.True(result.BestScore > 0);
        // The best state is the closest-to-23 state discovered; its path replays to it.
        Assert.NotEmpty(result.SolutionPath);
    }

    [Fact]
    public async Task SearchAsync_BreadthFirst_DrainsLevelByLevel()
    {
        // Breadth-first keeps the beam but processes the frontier FIFO. With a wide
        // beam and depth 2 it should still find the 23 solution.
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 8,
            MaxDepth = 3,
            MaxExpansions = 50,
            Strategy = SearchStrategy.BreadthFirst,
        });

        var result = await agent.SearchAsync("", ArithmeticExpander, CloserToTarget(23));

        Assert.Equal(ToTOutcome.Solved, result.Outcome);
        Assert.Equal(23, StateValue(result.BestState));
    }

    [Fact]
    public async Task SearchAsync_ExploredList_HasUniqueIncreasingIds()
    {
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 2,
            MaxDepth = 3,
            MaxExpansions = 20,
        });

        var result = await agent.SearchAsync("", ArithmeticExpander, CloserToTarget(23));

        var ids = result.Explored.Select(n => n.Id).ToList();
        Assert.Equal(ids.OrderBy(x => x).ToList(), ids);          // discovery order
        Assert.Equal(ids.Distinct().Count(), ids.Count);          // all unique
        Assert.Equal(1, ids.First());                             // 1-based
        // Every non-root node references a real parent that was discovered earlier.
        foreach (var node in result.Explored.Where(n => n.ParentId != 0))
            Assert.Contains(result.Explored, p => p.Id == node.ParentId && p.Id < node.Id);
    }

    [Fact]
    public async Task SearchAsync_AlreadyCancelled_Throws()
    {
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions { MaxExpansions = 5 });
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await agent.SearchAsync(
                "",
                expand: (thought, depth, ct) => Task.FromResult<IReadOnlyList<ThoughtExpansion>>(Array.Empty<ThoughtExpansion>()),
                evaluate: (state, depth, ct) => Task.FromResult(new ThoughtEvaluation(0.0, false, "x")),
                cts.Token));
    }

    [Fact]
    public async Task SearchAsync_AsyncDelegates_AreAwaited()
    {
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 2,
            MaxDepth = 5,
            MaxExpansions = 25,
        });

        var result = await agent.SearchAsync(
            rootThought: "",
            expand: async (thought, depth, ct) =>
            {
                await Task.Yield();
                return ArithmeticExpander(thought, depth);
            },
            evaluate: async (state, depth, ct) =>
            {
                await Task.Yield();
                return CloserToTarget(23)(state, depth);
            });

        Assert.Equal(ToTOutcome.Solved, result.Outcome);
        Assert.Equal(23, StateValue(result.BestState));
    }

    [Fact]
    public async Task SearchAsync_DoesNotExpandBeyondBudget_EvenWithWideBeam()
    {
        var expandCount = 0;
        var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
        {
            BeamWidth = 10,
            MaxDepth = 50,
            MaxExpansions = 7,
        });

        await agent.SearchAsync(
            rootThought: "",
            expand: (thought, depth) => { expandCount++; return ArithmeticExpander(thought, depth); },
            evaluate: CloserToTarget(100000, spread: 5000.0));

        Assert.Equal(7, expandCount);   // expander invoked exactly budget times
    }
}

// ── Supporting types (mirrors recipes/tree-of-thoughts/Program.cs) ──
// Prefixed so they don't collide with other test files that share the global
// namespace. Logic is duplicated here because each recipe is a standalone
// top-level program, not a project reference.

record ThoughtExpansion(string Step, string State);

record ThoughtEvaluation(double Score, bool Solved, string Rationale);

record SearchNode(
    int Id,
    int ParentId,
    int Depth,
    string Step,
    string State,
    double Score,
    bool Solved);

enum SearchStrategy
{
    BestFirst,
    BreadthFirst
}

enum ToTOutcome
{
    Solved,
    FrontierExhausted,
    BudgetExhausted,
    DepthLimited
}

record TreeOfThoughtsOptions
{
    public int BeamWidth { get; init; } = 3;
    public int MaxDepth { get; init; } = 4;
    public int MaxExpansions { get; init; } = 30;
    public double SolvedThreshold { get; init; } = 1.0;
    public double PruneThreshold { get; init; } = 0.0;
    public SearchStrategy Strategy { get; init; } = SearchStrategy.BestFirst;
    public Action<SearchNode>? OnNode { get; init; }
}

record TreeOfThoughtsResult(
    string BestState,
    double BestScore,
    bool Solved,
    ToTOutcome Outcome,
    IReadOnlyList<string> SolutionPath,
    int NodesExpanded,
    int NodesEvaluated,
    IReadOnlyList<SearchNode> Explored);

class TreeOfThoughtsAgent
{
    private readonly TreeOfThoughtsOptions _options;

    public TreeOfThoughtsAgent(TreeOfThoughtsOptions options) => _options = options;

    public Task<TreeOfThoughtsResult> SearchAsync(
        string rootThought,
        Func<string, int, IReadOnlyList<ThoughtExpansion>> expand,
        Func<string, int, ThoughtEvaluation> evaluate,
        CancellationToken ct = default)
    {
        return SearchAsync(
            rootThought,
            (thought, depth, _) => Task.FromResult(expand(thought, depth)),
            (state, depth, _) => Task.FromResult(evaluate(state, depth)),
            ct);
    }

    public async Task<TreeOfThoughtsResult> SearchAsync(
        string rootThought,
        Func<string, int, CancellationToken, Task<IReadOnlyList<ThoughtExpansion>>> expand,
        Func<string, int, CancellationToken, Task<ThoughtEvaluation>> evaluate,
        CancellationToken ct = default)
    {
        var beamWidth = Math.Max(1, _options.BeamWidth);
        var maxDepth = Math.Max(1, _options.MaxDepth);
        var maxExpansions = Math.Max(1, _options.MaxExpansions);

        var explored = new List<SearchNode>();
        var nextId = 1;

        var rootEval = await evaluate(rootThought, 0, ct);
        var rootSolved = rootEval.Solved || rootEval.Score >= _options.SolvedThreshold;
        var root = new SearchNode(nextId++, 0, 0, "(root)", rootThought,
            Clamp(rootEval.Score), rootSolved);
        explored.Add(root);
        _options.OnNode?.Invoke(root);

        var bestNode = root;
        var nodesEvaluated = 1;
        var nodesExpanded = 0;

        if (root.Solved)
            return Build(bestNode, ToTOutcome.Solved, explored, nodesExpanded, nodesEvaluated);

        var frontier = new List<SearchNode> { root };
        var outcome = ToTOutcome.FrontierExhausted;
        var anyDepthCapped = false;
        var anyPrunedOrDeadEnd = false;

        while (frontier.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            if (nodesExpanded >= maxExpansions)
            {
                outcome = ToTOutcome.BudgetExhausted;
                break;
            }

            var node = TakeNext(frontier);

            if (node.Depth >= maxDepth)
            {
                anyDepthCapped = true;
                continue;
            }

            var candidates = await expand(node.State, node.Depth, ct);
            nodesExpanded++;

            var children = new List<SearchNode>();
            foreach (var cand in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var eval = await evaluate(cand.State, node.Depth + 1, ct);
                var score = Clamp(eval.Score);
                var solved = eval.Solved || score >= _options.SolvedThreshold;

                var child = new SearchNode(
                    nextId++, node.Id, node.Depth + 1, cand.Step, cand.State, score, solved);
                explored.Add(child);
                nodesEvaluated++;
                _options.OnNode?.Invoke(child);

                if (score > bestNode.Score || (solved && !bestNode.Solved))
                    bestNode = child;

                if (solved)
                    return Build(child, ToTOutcome.Solved, explored, nodesExpanded, nodesEvaluated);

                if (score < _options.PruneThreshold) continue;
                children.Add(child);
            }

            if (children.Count == 0)
                anyPrunedOrDeadEnd = true;

            frontier.AddRange(children);
            ApplyBeam(frontier, beamWidth);
        }

        if (outcome == ToTOutcome.FrontierExhausted && anyDepthCapped && !anyPrunedOrDeadEnd && !bestNode.Solved)
            outcome = ToTOutcome.DepthLimited;

        return Build(bestNode, outcome, explored, nodesExpanded, nodesEvaluated);
    }

    private SearchNode TakeNext(List<SearchNode> frontier)
    {
        if (_options.Strategy == SearchStrategy.BreadthFirst)
        {
            var first = frontier[0];
            frontier.RemoveAt(0);
            return first;
        }

        var bestIdx = 0;
        for (var i = 1; i < frontier.Count; i++)
        {
            if (frontier[i].Score > frontier[bestIdx].Score ||
                (frontier[i].Score == frontier[bestIdx].Score && frontier[i].Id < frontier[bestIdx].Id))
                bestIdx = i;
        }
        var best = frontier[bestIdx];
        frontier.RemoveAt(bestIdx);
        return best;
    }

    private void ApplyBeam(List<SearchNode> frontier, int beamWidth)
    {
        if (frontier.Count <= beamWidth) return;

        var kept = frontier
            .OrderByDescending(n => n.Score)
            .ThenBy(n => n.Id)
            .Take(beamWidth)
            .OrderBy(n => n.Id)
            .ToList();
        frontier.Clear();
        frontier.AddRange(kept);
    }

    private static TreeOfThoughtsResult Build(
        SearchNode best,
        ToTOutcome outcome,
        List<SearchNode> explored,
        int nodesExpanded,
        int nodesEvaluated)
    {
        var byId = explored.ToDictionary(n => n.Id);
        var path = new List<string>();
        var cursor = best;
        while (cursor.ParentId != 0 && byId.TryGetValue(cursor.ParentId, out var parent))
        {
            path.Add(cursor.Step);
            cursor = parent;
        }
        path.Reverse();

        return new TreeOfThoughtsResult(
            BestState: best.State,
            BestScore: best.Score,
            Solved: best.Solved,
            Outcome: outcome,
            SolutionPath: path,
            NodesExpanded: nodesExpanded,
            NodesEvaluated: nodesEvaluated,
            Explored: explored);
    }

    private static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
