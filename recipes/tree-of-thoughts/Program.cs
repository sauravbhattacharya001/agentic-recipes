using Prompt;
using System.Text;

// ──────────────────────────────────────────────────────────────
// Tree-of-Thoughts Recipe
// Pattern: Expand → Evaluate → Search (explore a tree of partial
//          solutions, keep the most promising, backtrack on dead ends)
// (Yao et al. 2023, "Tree of Thoughts: Deliberate Problem Solving
//  with Large Language Models")
//
// A single chain of thought commits to one line of reasoning and
// lives or dies by it. Tree-of-Thoughts instead treats reasoning
// as a SEARCH: from a partial solution ("a thought") it expands
// several candidate next steps, an evaluator scores how promising
// each new state looks, and the agent keeps only the best handful
// (the "beam") to expand next. Bad branches are pruned; when a
// frontier dead-ends the search naturally falls back to the next
// best unexpanded node — i.e. it BACKTRACKS — instead of pushing a
// hopeless path to the end.
//
// The agency here is deliberate look-ahead with pruning: the agent
// spends its limited expansion budget where the state evaluation
// says the payoff is highest, abandons branches that score below a
// floor, and stops on its own the moment a state is judged solved,
// the frontier empties, or the budget/depth runs out.
//
// How this differs from the neighbouring recipes:
//   • Self-Consistency samples the SAME question N times and votes
//     over flat, independent answers — no branching, no search,
//     no partial states. ToT builds a TREE and searches it.
//   • Reflexion retries a whole task linearly, carrying verbal
//     lessons forward. ToT explores many partial states in parallel
//     and keeps the structurally-best ones.
//   • Iterative Refinement polishes ONE artifact via a critic score.
//     ToT compares MANY rival partial solutions and expands winners.
//   • Plan-and-Execute commits to one dependency-ordered plan up
//     front. ToT discovers the path by search and prunes/backtracks.
// ──────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Tree-of-Thoughts Recipe (Expand → Evaluate → Search)");
Console.WriteLine("═══════════════════════════════════════════════════════");

// ── Scenario ─────────────────────────────────────────────────
// A toy "reach the target number" puzzle that exercises the search
// without needing a model: starting from 0 we may append one of a
// few arithmetic moves (+9, +5, *2, -1). A "thought" is the running
// expression; a state is solved when it evaluates to the target.
//
// The expander stands in for an LLM proposing candidate next
// reasoning steps; the evaluator stands in for an LLM (or tool)
// scoring how promising a partial solution looks. Both are
// deterministic here so the search is reproducible offline.

const int target = 23;
var moves = new (string Label, Func<int, int> Apply)[]
{
    ("+9", n => n + 9),
    ("+5", n => n + 5),
    ("*2", n => n * 2),
    ("-1", n => n - 1),
};

// Helper: pull the integer value out of a state/thought string
// (states look like "0 +9 *2 = 18"; thoughts like "0 +9 *2").
int StateValue(string s)
{
    if (string.IsNullOrWhiteSpace(s)) return 0;
    var afterEquals = s.Contains('=') ? s[(s.IndexOf('=') + 1)..] : s;
    if (int.TryParse(afterEquals.Trim(), out var v)) return v;
    // Fall back: replay the move labels from "0" if there's no "= value".
    var value = 0;
    foreach (var tok in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        var move = moves.FirstOrDefault(m => m.Label == tok);
        if (move.Apply is not null) value = move.Apply(value);
    }
    return value;
}

// Expand: from a state's thought, propose candidate next thoughts.
// Each candidate is a short reasoning step + the new state it yields.
IReadOnlyList<ThoughtExpansion> Expand(string thought, int depth)
{
    var current = StateValue(thought);
    var options = new List<ThoughtExpansion>();
    foreach (var (label, apply) in moves)
    {
        var next = apply(current);
        // Prune obviously useless moves early (the expander has taste):
        // never overshoot far past the target, never go negative.
        if (next < 0 || next > target + 12) continue;
        var nextThought = thought.Length == 0 ? $"0 {label}" : $"{thought} {label}";
        options.Add(new ThoughtExpansion(
            Step: $"apply {label} -> {next}",
            State: $"{nextThought} = {next}"));
    }
    return options;
}

// Evaluate: score how promising a state is in [0, 1] and flag solved.
// Closeness to the target is the heuristic; an exact hit is solved.
ThoughtEvaluation Evaluate(string state, int depth)
{
    var value = StateValue(state);
    if (value == target)
        return new ThoughtEvaluation(1.0, true, $"reached target {target}");
    var distance = Math.Abs(target - value);
    // Map distance -> score: closer is better, capped into [0, 1).
    var score = Math.Max(0.0, 0.95 - distance / 30.0);
    return new ThoughtEvaluation(score, false, $"value {value}, {distance} away");
}

var agent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
{
    BeamWidth = 2,            // keep the 2 most promising frontier nodes
    MaxDepth = 5,             // never reason deeper than 5 steps
    MaxExpansions = 25,       // hard ceiling on how many nodes we expand
    SolvedThreshold = 1.0,    // a state counts as solved at score >= this
    PruneThreshold = 0.10,    // drop candidate states scoring below this
    Strategy = SearchStrategy.BestFirst,
    OnNode = node =>
    {
        var indent = new string(' ', node.Depth * 2);
        var flag = node.Solved ? "  * SOLVED" : "";
        Console.WriteLine($"  {indent}d{node.Depth} [{node.Score:F2}] {node.State}{flag}");
    }
});

Console.WriteLine($"Goal: build an expression that reaches {target} (moves: +9 +5 *2 -1)");
Console.WriteLine();
Console.WriteLine("Searching the thought tree (best-first, beam width 2)...");
Console.WriteLine();

var result = await agent.SearchAsync(rootThought: "", Expand, Evaluate);

Console.WriteLine();
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine($"  Outcome        : {result.Outcome}");
Console.WriteLine($"  Solved         : {(result.Solved ? "yes *" : "no")}");
Console.WriteLine($"  Nodes expanded : {result.NodesExpanded}");
Console.WriteLine($"  Nodes scored   : {result.NodesEvaluated}");
Console.WriteLine($"  Best score     : {result.BestScore:F2}");
Console.WriteLine();
Console.WriteLine("== WINNING PATH (root -> solution) ==");
if (result.SolutionPath.Count == 0)
    Console.WriteLine("  (no path found)");
else
    foreach (var (step, i) in result.SolutionPath.Select((s, i) => (s, i + 1)))
        Console.WriteLine($"  {i}. {step}");
Console.WriteLine();
Console.WriteLine($"  Final state : {result.BestState}");
Console.WriteLine();

// ── Bonus: pruning + a budget-bounded search ─────────────────
// Best-first search is not greedy hill-climbing: when the most
// promising branch stalls below its siblings, the frontier ranks
// a previously-shelved node back to the top and the search resumes
// there. Here the target sits far beyond what the small budget can
// reach, so the agent stops ITSELF at the budget instead of burning
// unbounded compute chasing it.
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Bonus: budget-bounded search (a faraway target)");
Console.WriteLine("═══════════════════════════════════════════════════════");

const int farTarget = 1000;   // far past what a 10-node budget can build
var pruneAgent = new TreeOfThoughtsAgent(new TreeOfThoughtsOptions
{
    BeamWidth = 3,             // a few nodes stay open each round
    MaxDepth = 30,             // depth is generous, so the BUDGET is what bites
    MaxExpansions = 10,        // deliberately small budget
    Strategy = SearchStrategy.BestFirst,
});

var pruneResult = await pruneAgent.SearchAsync(
    rootThought: "",
    expand: (thought, depth) =>
    {
        var current = StateValue(thought);
        var outs = new List<ThoughtExpansion>();
        foreach (var (label, apply) in moves)
        {
            var next = apply(current);
            if (next < 0) continue;
            var nextThought = thought.Length == 0 ? $"0 {label}" : $"{thought} {label}";
            outs.Add(new ThoughtExpansion($"apply {label}", $"{nextThought} = {next}"));
        }
        return outs;
    },
    evaluate: (state, depth) =>
    {
        var value = StateValue(state);
        if (value == farTarget) return new ThoughtEvaluation(1.0, true, "hit");
        var score = Math.Max(0.0, 0.95 - Math.Abs(farTarget - value) / 1500.0);
        return new ThoughtEvaluation(score, false, $"value {value}");
    });

Console.WriteLine($"  Outcome        : {pruneResult.Outcome}");
Console.WriteLine($"  Nodes expanded : {pruneResult.NodesExpanded} (budget 10)");
Console.WriteLine($"  Best score     : {pruneResult.BestScore:F2}  -> {pruneResult.BestState}");
Console.WriteLine("  Stopped at the budget instead of chasing a faraway target forever.");
Console.WriteLine();
Console.WriteLine("Pattern: expand candidate thoughts -> score states -> keep the");
Console.WriteLine("beam -> backtrack on dead ends, with an autonomous stop on");
Console.WriteLine("solved, frontier-exhausted, depth-limited, OR budget-exhausted.");

// ── Supporting types ────────────────────────────────────────

/// <summary>A candidate next thought proposed by the expander.</summary>
/// <param name="Step">A short, human-readable description of the reasoning step.</param>
/// <param name="State">
/// The full partial-solution state produced by taking this step. This is what the
/// evaluator scores and what the expander is handed to grow the next level.
/// </param>
record ThoughtExpansion(string Step, string State);

/// <summary>The result of scoring a partial-solution state.</summary>
/// <param name="Score">How promising this state looks, in [0, 1]; higher expands sooner.</param>
/// <param name="Solved">True when this state fully solves the task.</param>
/// <param name="Rationale">One-line note on why it scored this way (for transparency).</param>
record ThoughtEvaluation(double Score, bool Solved, string Rationale);

/// <summary>One node in the search tree: a scored partial solution.</summary>
/// <param name="Id">Stable 1-based id in discovery order.</param>
/// <param name="ParentId">Id of the parent node, or 0 for the root.</param>
/// <param name="Depth">Distance from the root (root = 0).</param>
/// <param name="Step">The reasoning step that produced this node.</param>
/// <param name="State">The partial-solution state at this node.</param>
/// <param name="Score">The evaluator's score for <see cref="State"/>.</param>
/// <param name="Solved">Whether the evaluator marked this state solved.</param>
record SearchNode(
    int Id,
    int ParentId,
    int Depth,
    string Step,
    string State,
    double Score,
    bool Solved);

/// <summary>How the frontier is ordered when choosing the next node to expand.</summary>
enum SearchStrategy
{
    /// <summary>Always expand the highest-scoring frontier node (look-ahead with backtracking).</summary>
    BestFirst,
    /// <summary>Expand level by level (FIFO), keeping the beam at each depth.</summary>
    BreadthFirst
}

/// <summary>Why the Tree-of-Thoughts search stopped.</summary>
enum ToTOutcome
{
    /// <summary>A state reached the solved threshold (or was flagged solved).</summary>
    Solved,
    /// <summary>
    /// The frontier emptied with no node left to expand because branches were pruned
    /// below the floor or dead-ended (the expander returned no children). This is the
    /// general "ran out of survivors" outcome, and also covers a mixed exhaustion where
    /// some branches were pruned/dead-ended while others hit the depth ceiling.
    /// </summary>
    FrontierExhausted,
    /// <summary>The expansion budget (MaxExpansions) was spent.</summary>
    BudgetExhausted,
    /// <summary>
    /// The depth ceiling was the SOLE reason the search stopped short: at least one open
    /// branch hit <c>MaxDepth</c> and nothing was ever pruned or dead-ended. If any branch
    /// was pruned/dead-ended, the outcome is the more general <see cref="FrontierExhausted"/>.
    /// </summary>
    DepthLimited
}

/// <summary>Configuration for <see cref="TreeOfThoughtsAgent"/>.</summary>
record TreeOfThoughtsOptions
{
    /// <summary>How many of the best frontier nodes to retain after each expansion (>= 1).</summary>
    public int BeamWidth { get; init; } = 3;
    /// <summary>Maximum tree depth to explore (root is depth 0; >= 1).</summary>
    public int MaxDepth { get; init; } = 4;
    /// <summary>Hard ceiling on the number of nodes expanded (>= 1).</summary>
    public int MaxExpansions { get; init; } = 30;
    /// <summary>Score (0-1) at/above which a state counts as solved.</summary>
    public double SolvedThreshold { get; init; } = 1.0;
    /// <summary>Candidate states scoring strictly below this are pruned and never enqueued.</summary>
    public double PruneThreshold { get; init; } = 0.0;
    /// <summary>Frontier ordering strategy.</summary>
    public SearchStrategy Strategy { get; init; } = SearchStrategy.BestFirst;
    /// <summary>Observability hook fired once per scored node (including the root).</summary>
    public Action<SearchNode>? OnNode { get; init; }
}

/// <summary>Outcome of a Tree-of-Thoughts run.</summary>
/// <param name="BestState">The highest-scoring state discovered (the solution if solved).</param>
/// <param name="BestScore">Its score in [0, 1].</param>
/// <param name="Solved">True when the search reached a solved state.</param>
/// <param name="Outcome">Why the search stopped.</param>
/// <param name="SolutionPath">The reasoning steps from the root to the best node.</param>
/// <param name="NodesExpanded">How many nodes were expanded.</param>
/// <param name="NodesEvaluated">How many candidate states were scored.</param>
/// <param name="Explored">Every node that was scored, in discovery order.</param>
record TreeOfThoughtsResult(
    string BestState,
    double BestScore,
    bool Solved,
    ToTOutcome Outcome,
    IReadOnlyList<string> SolutionPath,
    int NodesExpanded,
    int NodesEvaluated,
    IReadOnlyList<SearchNode> Explored);

/// <summary>
/// Runs the Tree-of-Thoughts search: from a root thought it repeatedly expands
/// the most promising frontier node into candidate next thoughts, scores each
/// candidate state, prunes the weak ones, and keeps the best <c>BeamWidth</c>
/// nodes on the frontier. Because the frontier is re-ranked after every
/// expansion, a stalled branch yields to a better-scoring sibling - the search
/// BACKTRACKS rather than marching down one limb. It stops on its own when a
/// state is solved, the frontier empties (branches pruned or dead-ended), the
/// depth ceiling alone boxes in the remaining branches, or the expansion budget
/// runs out.
///
/// The expander and evaluator are injected delegates, so the control flow can be
/// exercised deterministically in tests and wired to real LLM / tool calls in
/// production.
/// </summary>
class TreeOfThoughtsAgent
{
    private readonly TreeOfThoughtsOptions _options;

    public TreeOfThoughtsAgent(TreeOfThoughtsOptions options) => _options = options;

    /// <summary>Sync convenience overload.</summary>
    /// <param name="rootThought">The starting (possibly empty) partial solution.</param>
    /// <param name="expand">
    /// Given a thought and its depth, proposes candidate next thoughts. Return an
    /// empty list to signal a dead end (that branch is abandoned).
    /// </param>
    /// <param name="evaluate">Scores a candidate state and flags whether it is solved.</param>
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

    /// <summary>Async overload: the expander and evaluator may await real calls.</summary>
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

        // Score the root so the search has a seed and a baseline best.
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

        // The frontier holds open nodes eligible for expansion. Best-first keeps
        // it ranked by score; breadth-first treats it as a FIFO queue.
        var frontier = new List<SearchNode> { root };
        var outcome = ToTOutcome.FrontierExhausted;
        var anyDepthCapped = false;      // some open branch could not grow past MaxDepth
        var anyPrunedOrDeadEnd = false;  // some below-max branch produced zero survivors

        while (frontier.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            if (nodesExpanded >= maxExpansions)
            {
                outcome = ToTOutcome.BudgetExhausted;
                break;
            }

            // Choose the next node to expand per the strategy.
            var node = TakeNext(frontier);

            // Respect the depth limit: a node already at max depth cannot grow.
            if (node.Depth >= maxDepth)
            {
                anyDepthCapped = true;
                continue;
            }

            var candidates = await expand(node.State, node.Depth, ct);
            nodesExpanded++;

            // Score each candidate, prune the weak, and keep the survivors.
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

                // Prune below the floor; never enqueue a hopeless branch.
                if (score < _options.PruneThreshold) continue;
                children.Add(child);
            }

            // A below-max node that yielded no surviving children (all pruned, or the
            // expander dead-ended) is a frontier-exhaustion cause, distinct from a
            // branch that simply ran into the depth ceiling.
            if (children.Count == 0)
                anyPrunedOrDeadEnd = true;

            // Add the survivors and re-apply the beam: keep only the best
            // `beamWidth` OPEN nodes overall so the tree stays bounded.
            frontier.AddRange(children);
            ApplyBeam(frontier, beamWidth);
        }

        // Report DepthLimited only when the depth ceiling was the SOLE thing that
        // stopped the search: a branch hit MaxDepth and nothing was ever pruned or
        // dead-ended. If any branch was pruned/dead-ended (even while an unrelated
        // node also hit MaxDepth), the honest label is the general FrontierExhausted
        // rather than blaming depth.
        if (outcome == ToTOutcome.FrontierExhausted && anyDepthCapped && !anyPrunedOrDeadEnd && !bestNode.Solved)
            outcome = ToTOutcome.DepthLimited;

        return Build(bestNode, outcome, explored, nodesExpanded, nodesEvaluated);
    }

    /// <summary>Pop the next node to expand according to the configured strategy.</summary>
    private SearchNode TakeNext(List<SearchNode> frontier)
    {
        if (_options.Strategy == SearchStrategy.BreadthFirst)
        {
            var first = frontier[0];
            frontier.RemoveAt(0);
            return first;
        }

        // Best-first: highest score wins; ties broken by earliest discovery
        // (lowest id) for fully deterministic ordering.
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

    /// <summary>
    /// Trim the OPEN frontier down to the best <paramref name="beamWidth"/> nodes.
    /// For breadth-first we keep insertion order (FIFO) among the top scorers so a
    /// level is still drained in order; best-first re-ranks purely by score.
    /// </summary>
    private void ApplyBeam(List<SearchNode> frontier, int beamWidth)
    {
        if (frontier.Count <= beamWidth) return;

        // Rank by score desc, then by id asc for determinism, take the top beam,
        // then restore id order so breadth-first keeps its FIFO discipline.
        var kept = frontier
            .OrderByDescending(n => n.Score)
            .ThenBy(n => n.Id)
            .Take(beamWidth)
            .OrderBy(n => n.Id)
            .ToList();
        frontier.Clear();
        frontier.AddRange(kept);
    }

    /// <summary>Assemble the final result, reconstructing the root->best step path.</summary>
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
