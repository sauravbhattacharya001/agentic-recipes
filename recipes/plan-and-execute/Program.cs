using Prompt;
using System.Text;

// ──────────────────────────────────────────────────────────────
// Plan-and-Execute Recipe
// Pattern: Decompose → Execute → Adapt (planner / executor with replanning)
//
// The Tool Agent Loop reacts one step at a time: look, decide, act,
// repeat. That is great for open-ended exploration but it has no map —
// it can wander, repeat work, or lose the thread on a long task.
//
// Plan-and-Execute flips the order. Given a GOAL, the agent first writes
// a PLAN: an ordered list of concrete steps, each with the steps it
// depends on. Then an executor runs the plan in dependency order,
// feeding each finished step's output to the steps that needed it.
//
// The agency is in what happens when a step FAILS. The executor doesn't
// just stop — it adapts, on its own, with a graduated policy:
//
//   RETRY        transient failure? try the step again (bounded budget)
//   FALLBACK     a step can carry an alternate approach; switch to it
//   SKIP         a non-critical step exhausted its options? drop it,
//                cascade-skip anything that depended on it, keep going
//   ABORT        a CRITICAL step is unrecoverable? stop — the goal is
//                no longer reachable, so don't burn effort pretending
//
// That is goal-oriented autonomy: the user states the destination, the
// agent figures out the route, and — crucially — re-routes when the road
// is closed instead of giving up at the first pothole or blindly driving
// into it.
//
// Unlike Iterative Refinement (improve ONE artifact via a critic loop)
// or the Tool Agent Loop (reactive, no upfront plan), this recipe commits
// to a structured plan and then defends that plan against failure.
// ──────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;

// 1. Configure the executor. RetryBudget is the per-step transient-retry
//    allowance; StopOnCriticalFailure decides whether an unrecoverable
//    critical step aborts the run or merely fails-and-continues.
var executor = new PlanExecutor(new PlanExecutorOptions
{
    RetryBudget = 1,                 // one retry per step before giving up on it
    StopOnCriticalFailure = true,    // a dead critical step aborts the whole run
    OnEvent = e =>
    {
        var icon = e.Kind switch
        {
            ExecEventKind.StepStarted    => "▶",
            ExecEventKind.StepSucceeded  => "✓",
            ExecEventKind.StepRetrying   => "↻",
            ExecEventKind.StepFellBack   => "⇄",
            ExecEventKind.StepSkipped    => "⤼",
            ExecEventKind.StepFailed     => "✗",
            ExecEventKind.Aborted        => "■",
            _                            => "·"
        };
        Console.WriteLine($"  {icon} {e.StepId,-14} {e.Message}");
    }
});

// 2. State the goal and lay out a plan. In a real recipe the plan comes
//    from an LLM planner ("decompose this goal into steps with
//    dependencies"); here we author it inline so the executor's control
//    flow — especially its failure handling — is easy to follow offline.
var goal = "Publish a launch-day blog post about the new release.";

var plan = new Plan(goal, new[]
{
    new PlanStep("gather_notes",  "Collect release notes from the changelog"),
    new PlanStep("draft_post",    "Write the blog post draft",        dependsOn: new[] { "gather_notes" }),
    new PlanStep("make_hero_img", "Generate a hero image",            critical: false,
        // Image generation is flaky in this demo and has no fallback → it
        // will be SKIPPED, and the social card that needs it skips too.
        run: (_, _) => throw new StepException("image service timed out")),
    new PlanStep("seo_pass",      "Optimize the draft for SEO",       dependsOn: new[] { "draft_post" },
        // First approach throws; the fallback succeeds → the step RECOVERS.
        run: (_, _) => throw new StepException("keyword API rate-limited"),
        fallback: (ctx, _) => $"seo-applied(basic) to: {ctx.Outputs["draft_post"]}"),
    new PlanStep("social_card",   "Build a social share card",        critical: false,
        dependsOn: new[] { "draft_post", "make_hero_img" }),   // depends on the skipped image
    new PlanStep("publish",       "Publish the post",                 critical: true,
        dependsOn: new[] { "draft_post", "seo_pass" }),
});

// 3. Default step behaviour for the steps that don't supply their own
//    `run` delegate: just acknowledge the work using upstream outputs.
//    In production each of these would be a real tool / model call.
string DefaultStep(PlanStep step, ExecutionContext ctx)
{
    var inputs = step.DependsOn.Count == 0
        ? "(no inputs)"
        : string.Join(", ", step.DependsOn.Select(d =>
            ctx.Outputs.ContainsKey(d) ? $"{d}=✓" : $"{d}=∅"));
    return $"{step.Id}-done[{inputs}]";
}

// 4. Run the plan.
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Plan-and-Execute Recipe");
Console.WriteLine("  (decompose → execute in dependency order → adapt)");
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine($"Goal: {goal}");
Console.WriteLine();
Console.WriteLine($"Plan ({plan.Steps.Count} steps, execution order shown):");
foreach (var (step, i) in plan.ExecutionOrder().Select((s, i) => (s, i + 1)))
{
    var deps = step.DependsOn.Count == 0 ? "—" : string.Join("+", step.DependsOn);
    var tag = step.Critical ? " [critical]" : "";
    Console.WriteLine($"  {i}. {step.Id,-14} after: {deps,-26} {step.Description}{tag}");
}
Console.WriteLine();
Console.WriteLine("Executing…");
Console.WriteLine();

var result = await executor.ExecuteAsync(plan, DefaultStep);

// 5. Report.
Console.WriteLine();
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine($"  Goal reached : {(result.GoalReached ? "yes ✓" : "no ✗")}");
Console.WriteLine($"  Outcome      : {result.Outcome}");
Console.WriteLine($"  Succeeded    : {result.Succeeded.Count}/{plan.Steps.Count}  ({string.Join(", ", result.Succeeded)})");
if (result.Recovered.Count > 0)
    Console.WriteLine($"  Recovered    : {string.Join(", ", result.Recovered)}  (failed first, then fell back)");
if (result.Skipped.Count > 0)
    Console.WriteLine($"  Skipped      : {string.Join(", ", result.Skipped)}");
if (result.Failed.Count > 0)
    Console.WriteLine($"  Failed       : {string.Join(", ", result.Failed)}");
Console.WriteLine();
Console.WriteLine("══ FINAL OUTPUTS ══");
foreach (var step in plan.ExecutionOrder())
{
    if (result.Outputs.TryGetValue(step.Id, out var output))
        Console.WriteLine($"  {step.Id,-14} → {output}");
}
Console.WriteLine();

// 6. Demonstrate the hard stop: when a CRITICAL step can't recover, the
//    executor aborts instead of marching on to steps that can never
//    succeed without it. Here `build` is critical and has no fallback.
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Bonus: critical-failure abort (goal becomes unreachable)");
Console.WriteLine("═══════════════════════════════════════════════════════");

var fragilePlan = new Plan("Ship the binary.", new[]
{
    new PlanStep("checkout", "Check out the source"),
    new PlanStep("build",    "Compile the project", critical: true, dependsOn: new[] { "checkout" },
        run: (_, _) => throw new StepException("compiler error CS1002")),
    new PlanStep("ship",     "Upload the artifact", critical: true, dependsOn: new[] { "build" }),
});

var abortExecutor = new PlanExecutor(new PlanExecutorOptions
{
    RetryBudget = 1,
    StopOnCriticalFailure = true,
    OnEvent = e => Console.WriteLine($"  {e.StepId,-10} {e.Kind}: {e.Message}")
});

var abortResult = await abortExecutor.ExecuteAsync(
    fragilePlan,
    (step, ctx) => $"{step.Id}-ok");

Console.WriteLine();
Console.WriteLine($"  Outcome     : {abortResult.Outcome}");
Console.WriteLine($"  Goal reached: {(abortResult.GoalReached ? "yes" : "no")}");
Console.WriteLine($"  'ship' never ran because its critical dependency 'build' died —");
Console.WriteLine($"  the executor stopped instead of attempting an impossible upload.");
Console.WriteLine();
Console.WriteLine("Pattern: plan the route up front, execute in dependency order, and");
Console.WriteLine("adapt to failure with retry → fallback → skip → abort — autonomy that");
Console.WriteLine("re-routes around closed roads but won't drive off a cliff.");

// ── Supporting types ────────────────────────────────────────

/// <summary>
/// Raised by a step's work delegate to signal a recoverable failure
/// (transient error, bad approach, …). The executor catches it and applies
/// its retry / fallback / skip / abort policy. Any other exception type is
/// handled the same way but its message is surfaced in the step's record.
/// </summary>
class StepException : Exception
{
    public StepException(string message) : base(message) { }
}

/// <summary>
/// One unit of work in a plan. A step optionally declares the steps it
/// <paramref name="dependsOn"/> (its inputs), whether it is
/// <paramref name="critical"/> to the goal, a primary <paramref name="run"/>
/// delegate, and a <paramref name="fallback"/> approach to try if the
/// primary keeps failing.
/// </summary>
class PlanStep
{
    public string Id { get; }
    public string Description { get; }
    public IReadOnlyList<string> DependsOn { get; }
    public bool Critical { get; }

    /// <summary>Primary work. Null → the executor's default step delegate is used.</summary>
    public Func<ExecutionContext, CancellationToken, string>? Run { get; }

    /// <summary>Alternate approach tried once the primary exhausts its retries. Null → no fallback.</summary>
    public Func<ExecutionContext, CancellationToken, string>? Fallback { get; }

    public PlanStep(
        string id,
        string description,
        IReadOnlyList<string>? dependsOn = null,
        bool critical = false,
        Func<ExecutionContext, CancellationToken, string>? run = null,
        Func<ExecutionContext, CancellationToken, string>? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Step id must be non-empty.", nameof(id));
        Id = id;
        Description = description ?? "";
        DependsOn = dependsOn?.ToList() ?? new List<string>();
        Critical = critical;
        Run = run;
        Fallback = fallback;
    }
}

/// <summary>An ordered plan of steps that, executed, should achieve a goal.</summary>
class Plan
{
    public string Goal { get; }
    public IReadOnlyList<PlanStep> Steps { get; }
    private readonly Dictionary<string, PlanStep> _byId;

    public Plan(string goal, IEnumerable<PlanStep> steps)
    {
        Goal = goal ?? "";
        Steps = (steps ?? Array.Empty<PlanStep>()).ToList();

        _byId = new Dictionary<string, PlanStep>(StringComparer.Ordinal);
        foreach (var s in Steps)
        {
            if (_byId.ContainsKey(s.Id))
                throw new ArgumentException($"Duplicate step id '{s.Id}'.", nameof(steps));
            _byId[s.Id] = s;
        }

        // Every declared dependency must reference a real step.
        foreach (var s in Steps)
            foreach (var dep in s.DependsOn)
                if (!_byId.ContainsKey(dep))
                    throw new ArgumentException($"Step '{s.Id}' depends on unknown step '{dep}'.", nameof(steps));
    }

    /// <summary>Look up a step by id.</summary>
    public PlanStep this[string id] => _byId[id];

    /// <summary>
    /// Topologically sort the steps so every step comes after the steps it
    /// depends on. Ties are broken by the original authoring order, giving a
    /// stable, deterministic schedule. Throws if dependencies contain a cycle.
    /// </summary>
    public IReadOnlyList<PlanStep> ExecutionOrder()
    {
        var ordered = new List<PlanStep>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=unseen,1=visiting,2=done
        var index = Steps.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);

        void Visit(PlanStep step)
        {
            switch (state.GetValueOrDefault(step.Id))
            {
                case 2: return;
                case 1: throw new InvalidOperationException($"Dependency cycle detected at step '{step.Id}'.");
            }
            state[step.Id] = 1;
            foreach (var dep in step.DependsOn.OrderBy(d => index[d]))
                Visit(_byId[dep]);
            state[step.Id] = 2;
            ordered.Add(step);
        }

        foreach (var step in Steps)
            Visit(step);

        return ordered;
    }
}

/// <summary>
/// Read-only view of execution state handed to each step: the goal and the
/// outputs of every step that has finished successfully so far. A step reads
/// its dependencies' outputs from <see cref="Outputs"/>.
/// </summary>
class ExecutionContext
{
    public string Goal { get; }
    public IReadOnlyDictionary<string, string> Outputs { get; }

    public ExecutionContext(string goal, IReadOnlyDictionary<string, string> outputs)
    {
        Goal = goal;
        Outputs = outputs;
    }
}

/// <summary>How a single step ended.</summary>
enum StepStatus
{
    /// <summary>Completed on the primary approach.</summary>
    Succeeded,
    /// <summary>Primary kept failing but the fallback approach completed.</summary>
    Recovered,
    /// <summary>Non-critical and out of options (or an upstream dependency was skipped); dropped.</summary>
    Skipped,
    /// <summary>Critical and unrecoverable; the run aborts here.</summary>
    Failed
}

/// <summary>The kinds of event the executor emits as it works (observability hook).</summary>
enum ExecEventKind
{
    StepStarted,
    StepSucceeded,
    StepRetrying,
    StepFellBack,
    StepSkipped,
    StepFailed,
    Aborted
}

/// <summary>A single observability event from the executor.</summary>
record ExecEvent(ExecEventKind Kind, string StepId, string Message);

/// <summary>The record of one finished (or abandoned) step.</summary>
record StepResult(string StepId, StepStatus Status, string? Output, int Attempts, string? Error);

/// <summary>Why the run ended.</summary>
enum PlanOutcome
{
    /// <summary>Every critical step succeeded (some non-critical steps may have been skipped).</summary>
    Completed,
    /// <summary>The run finished but at least one critical step did not (best-effort mode only).</summary>
    CompletedWithFailures,
    /// <summary>A critical step was unrecoverable and the executor stopped early.</summary>
    Aborted
}

/// <summary>The outcome of executing a whole plan.</summary>
record PlanResult(
    PlanOutcome Outcome,
    bool GoalReached,
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyList<StepResult> StepResults)
{
    /// <summary>Ids of steps that completed on their primary approach.</summary>
    public IReadOnlyList<string> Succeeded =>
        StepResults.Where(r => r.Status == StepStatus.Succeeded).Select(r => r.StepId).ToList();

    /// <summary>Ids of steps that failed first but recovered via a fallback.</summary>
    public IReadOnlyList<string> Recovered =>
        StepResults.Where(r => r.Status == StepStatus.Recovered).Select(r => r.StepId).ToList();

    /// <summary>Ids of steps that were skipped.</summary>
    public IReadOnlyList<string> Skipped =>
        StepResults.Where(r => r.Status == StepStatus.Skipped).Select(r => r.StepId).ToList();

    /// <summary>Ids of steps that failed (critical, unrecoverable).</summary>
    public IReadOnlyList<string> Failed =>
        StepResults.Where(r => r.Status == StepStatus.Failed).Select(r => r.StepId).ToList();
}

/// <summary>Configuration for <see cref="PlanExecutor"/>.</summary>
record PlanExecutorOptions
{
    /// <summary>Extra attempts at a step's primary approach after the first failure, before falling back. Clamped to ≥ 0.</summary>
    public int RetryBudget { get; init; } = 1;

    /// <summary>
    /// When true (default), an unrecoverable <b>critical</b> step aborts the run.
    /// When false, the executor records the failure and keeps going (best-effort mode).
    /// </summary>
    public bool StopOnCriticalFailure { get; init; } = true;

    /// <summary>Observability hook fired as steps start, retry, fall back, succeed, skip, or fail.</summary>
    public Action<ExecEvent>? OnEvent { get; init; }
}

/// <summary>
/// Executes a <see cref="Plan"/> in dependency order, adapting to step
/// failures with a graduated policy: retry the primary approach within a
/// budget, fall back to an alternate approach, skip a non-critical step
/// that is out of options (cascading the skip to its dependents), or abort
/// when a critical step is unrecoverable.
///
/// Step work is supplied either per-step (<see cref="PlanStep.Run"/> /
/// <see cref="PlanStep.Fallback"/>) or via the default delegate passed to
/// <c>ExecuteAsync</c>, so the whole control flow runs deterministically in
/// tests and wires to real tool / model calls in production.
/// </summary>
class PlanExecutor
{
    private readonly PlanExecutorOptions _options;

    public PlanExecutor(PlanExecutorOptions options) => _options = options;

    /// <summary>Execute a plan with a synchronous default step delegate.</summary>
    public Task<PlanResult> ExecuteAsync(
        Plan plan,
        Func<PlanStep, ExecutionContext, string> defaultStep,
        CancellationToken ct = default)
        => ExecuteAsync(plan, (step, ctx, _) => Task.FromResult(defaultStep(step, ctx)), ct);

    /// <summary>Execute a plan with an async default step delegate (e.g. a real model/tool call).</summary>
    public async Task<PlanResult> ExecuteAsync(
        Plan plan,
        Func<PlanStep, ExecutionContext, CancellationToken, Task<string>> defaultStep,
        CancellationToken ct = default)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (defaultStep is null) throw new ArgumentNullException(nameof(defaultStep));

        var retryBudget = Math.Max(0, _options.RetryBudget);
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var results = new List<StepResult>();
        var statusById = new Dictionary<string, StepStatus>(StringComparer.Ordinal);
        var aborted = false;

        foreach (var step in plan.ExecutionOrder())
        {
            ct.ThrowIfCancellationRequested();

            // A step whose dependency was skipped or failed cannot run — its
            // inputs do not exist. Cascade the skip rather than feed it nulls.
            var deadDep = step.DependsOn.FirstOrDefault(d =>
                statusById.TryGetValue(d, out var st) && st is StepStatus.Skipped or StepStatus.Failed);
            if (deadDep is not null)
            {
                statusById[step.Id] = StepStatus.Skipped;
                results.Add(new StepResult(step.Id, StepStatus.Skipped, null, 0,
                    $"upstream '{deadDep}' did not complete"));
                Emit(ExecEventKind.StepSkipped, step.Id,
                    $"skipped — depends on '{deadDep}' which did not complete");
                continue;
            }

            Emit(ExecEventKind.StepStarted, step.Id, step.Description);

            var context = new ExecutionContext(plan.Goal, new ReadOnlyView(outputs));
            var primary = step.Run is not null
                ? new Func<ExecutionContext, CancellationToken, Task<string>>(
                    (c, t) => Task.FromResult(step.Run!(c, t)))
                : ((c, t) => defaultStep(step, c, t));

            var attempts = 0;
            string? lastError = null;
            var done = false;

            // ── RETRY: primary approach, within budget ──
            for (var tryNo = 0; tryNo <= retryBudget && !done; tryNo++)
            {
                ct.ThrowIfCancellationRequested();
                attempts++;
                if (tryNo > 0)
                    Emit(ExecEventKind.StepRetrying, step.Id,
                        $"retry {tryNo}/{retryBudget} after: {lastError}");
                try
                {
                    var output = await primary(context, ct);
                    outputs[step.Id] = output;
                    statusById[step.Id] = StepStatus.Succeeded;
                    results.Add(new StepResult(step.Id, StepStatus.Succeeded, output, attempts, null));
                    Emit(ExecEventKind.StepSucceeded, step.Id, output);
                    done = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }
            if (done) continue;

            // ── FALLBACK: a step can carry an alternate approach. ──
            if (step.Fallback is not null)
            {
                ct.ThrowIfCancellationRequested();
                attempts++;
                try
                {
                    var output = step.Fallback(context, ct);
                    outputs[step.Id] = output;
                    statusById[step.Id] = StepStatus.Recovered;
                    results.Add(new StepResult(step.Id, StepStatus.Recovered, output, attempts, lastError));
                    Emit(ExecEventKind.StepFellBack, step.Id,
                        $"primary failed ({lastError}); recovered via fallback");
                    continue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            // ── SKIP vs ABORT: out of options. ──
            if (!step.Critical)
            {
                // Non-critical and unrecoverable → drop it and keep going. Its
                // dependents will cascade-skip when their turn comes.
                statusById[step.Id] = StepStatus.Skipped;
                results.Add(new StepResult(step.Id, StepStatus.Skipped, null, attempts, lastError));
                Emit(ExecEventKind.StepSkipped, step.Id,
                    $"non-critical and out of options ({lastError}); skipped");
                continue;
            }

            // Critical and unrecoverable.
            statusById[step.Id] = StepStatus.Failed;
            results.Add(new StepResult(step.Id, StepStatus.Failed, null, attempts, lastError));
            Emit(ExecEventKind.StepFailed, step.Id, $"CRITICAL step failed ({lastError})");

            if (_options.StopOnCriticalFailure)
            {
                aborted = true;
                Emit(ExecEventKind.Aborted, step.Id,
                    "goal is no longer reachable — stopping the run");
                break;
            }
            // Best-effort mode: record the failure and keep going. Dependents
            // cascade-skip because this step's status is Failed.
        }

        // The goal is reached only when EVERY critical step actually produced a
        // result (succeeded or recovered). A critical step can also end up
        // Skipped — not just Failed — when a dependency of its did not complete
        // (cascade-skip), e.g. a critical step that depends on a skipped
        // non-critical step. Counting only Failed would mislabel that run
        // "Completed / goal reached" even though a critical step never ran.
        var criticalUnmet = results.Any(r =>
            plan[r.StepId].Critical &&
            r.Status is not (StepStatus.Succeeded or StepStatus.Recovered));
        var outcome = aborted
            ? PlanOutcome.Aborted
            : criticalUnmet
                ? PlanOutcome.CompletedWithFailures
                : PlanOutcome.Completed;
        var goalReached = !aborted && !criticalUnmet;

        return new PlanResult(
            outcome,
            goalReached,
            new Dictionary<string, string>(outputs, StringComparer.Ordinal),
            results);
    }

    private void Emit(ExecEventKind kind, string stepId, string message) =>
        _options.OnEvent?.Invoke(new ExecEvent(kind, stepId, message));

    /// <summary>A live, read-only window onto the running output map (no copy per step).</summary>
    private sealed class ReadOnlyView : IReadOnlyDictionary<string, string>
    {
        private readonly Dictionary<string, string> _inner;
        public ReadOnlyView(Dictionary<string, string> inner) => _inner = inner;

        public string this[string key] => _inner[key];
        public IEnumerable<string> Keys => _inner.Keys;
        public IEnumerable<string> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool ContainsKey(string key) => _inner.ContainsKey(key);
        public bool TryGetValue(string key, out string value) => _inner.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _inner.GetEnumerator();
    }
}
