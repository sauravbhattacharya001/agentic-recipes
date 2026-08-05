using Xunit;

namespace AgenticRecipes.Tests;

public class PlanAndExecuteTests
{
    // A default step delegate that just echoes the step id.
    private static string EchoStep(PlanStep step, ExecutionContext ctx) => $"{step.Id}-ok";

    private static PlanExecutor Executor(
        int retryBudget = 1,
        bool stopOnCritical = true,
        Action<ExecEvent>? onEvent = null)
        => new(new PlanExecutorOptions
        {
            RetryBudget = retryBudget,
            StopOnCriticalFailure = stopOnCritical,
            OnEvent = onEvent
        });

    // ── Plan validation & ordering ───────────────────────────

    [Fact]
    public void ExecutionOrder_PutsDependenciesFirst()
    {
        var plan = new Plan("g", new[]
        {
            new PlanStep("c", "", dependsOn: new[] { "b" }),
            new PlanStep("a", ""),
            new PlanStep("b", "", dependsOn: new[] { "a" }),
        });

        var order = plan.ExecutionOrder().Select(s => s.Id).ToArray();

        Assert.Equal(new[] { "a", "b", "c" }, order);
    }

    [Fact]
    public void ExecutionOrder_BreaksTiesByAuthoringOrder()
    {
        // Two independent steps plus one that depends on both — the two roots
        // keep their declared order for a stable, deterministic schedule.
        var plan = new Plan("g", new[]
        {
            new PlanStep("first", ""),
            new PlanStep("second", ""),
            new PlanStep("join", "", dependsOn: new[] { "second", "first" }),
        });

        var order = plan.ExecutionOrder().Select(s => s.Id).ToArray();

        Assert.Equal(new[] { "first", "second", "join" }, order);
    }

    [Fact]
    public void Plan_DuplicateStepId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Plan("g", new[]
        {
            new PlanStep("dup", ""),
            new PlanStep("dup", ""),
        }));
        Assert.Contains("Duplicate", ex.Message);
    }

    [Fact]
    public void Plan_DependencyOnUnknownStep_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Plan("g", new[]
        {
            new PlanStep("a", "", dependsOn: new[] { "ghost" }),
        }));
        Assert.Contains("unknown step", ex.Message);
    }

    [Fact]
    public void ExecutionOrder_Cycle_Throws()
    {
        var plan = new Plan("g", new[]
        {
            new PlanStep("a", "", dependsOn: new[] { "b" }),
            new PlanStep("b", "", dependsOn: new[] { "a" }),
        });

        var ex = Assert.Throws<InvalidOperationException>(() => plan.ExecutionOrder());
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public void PlanStep_EmptyId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PlanStep("  ", "desc"));
    }

    // ── Happy path & data flow ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AllSucceed_GoalReached()
    {
        var plan = new Plan("g", new[]
        {
            new PlanStep("a", ""),
            new PlanStep("b", "", dependsOn: new[] { "a" }),
        });

        var result = await Executor().ExecuteAsync(plan, EchoStep);

        Assert.Equal(PlanOutcome.Completed, result.Outcome);
        Assert.True(result.GoalReached);
        Assert.Equal(new[] { "a", "b" }, result.Succeeded.OrderBy(x => x).ToArray());
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_FeedsUpstreamOutputs_ToDependents()
    {
        var plan = new Plan("the-goal", new[]
        {
            new PlanStep("up", "", run: (_, _) => "UP-VALUE"),
            new PlanStep("down", "", dependsOn: new[] { "up" },
                run: (ctx, _) => $"saw:{ctx.Outputs["up"]};goal:{ctx.Goal}"),
        });

        var result = await Executor().ExecuteAsync(plan, EchoStep);

        Assert.Equal("saw:UP-VALUE;goal:the-goal", result.Outputs["down"]);
    }

    [Fact]
    public async Task ExecuteAsync_RecordsAttemptsAndStatus_PerStep()
    {
        var plan = new Plan("g", new[] { new PlanStep("solo", "", run: (_, _) => "done") });

        var result = await Executor().ExecuteAsync(plan, EchoStep);

        var step = Assert.Single(result.StepResults);
        Assert.Equal("solo", step.StepId);
        Assert.Equal(StepStatus.Succeeded, step.Status);
        Assert.Equal(1, step.Attempts);
        Assert.Equal("done", step.Output);
        Assert.Null(step.Error);
    }

    // ── Retry ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TransientFailure_RetriesThenSucceeds()
    {
        var calls = 0;
        var plan = new Plan("g", new[]
        {
            new PlanStep("flaky", "", run: (_, _) =>
            {
                calls++;
                if (calls == 1) throw new StepException("transient");
                return "recovered-on-retry";
            }),
        });

        var result = await Executor(retryBudget: 1).ExecuteAsync(plan, EchoStep);

        Assert.Equal(2, calls);
        var step = Assert.Single(result.StepResults);
        Assert.Equal(StepStatus.Succeeded, step.Status);
        Assert.Equal(2, step.Attempts);
        Assert.True(result.GoalReached);
    }

    [Fact]
    public async Task ExecuteAsync_RetryBudgetZero_DoesNotRetry()
    {
        var calls = 0;
        var plan = new Plan("g", new[]
        {
            new PlanStep("x", "", critical: false, run: (_, _) => { calls++; throw new StepException("nope"); }),
        });

        var result = await Executor(retryBudget: 0).ExecuteAsync(plan, EchoStep);

        Assert.Equal(1, calls); // primary attempted exactly once, no retry
        Assert.Equal(StepStatus.Skipped, result.StepResults.Single().Status);
    }

    [Fact]
    public async Task ExecuteAsync_NegativeRetryBudget_ClampsToZero_StillAttemptsPrimaryOnce()
    {
        // The option documents "Clamped to ≥ 0". A negative budget must NOT drop the
        // primary entirely (which is what a raw `tryNo <= budget` loop would do for a
        // negative bound): the step must still be attempted exactly once, then fail
        // over to skip like a zero budget.
        var calls = 0;
        var plan = new Plan("g", new[]
        {
            new PlanStep("x", "", critical: false, run: (_, _) => { calls++; throw new StepException("nope"); }),
        });

        var result = await Executor(retryBudget: -5).ExecuteAsync(plan, EchoStep);

        Assert.Equal(1, calls); // clamped to 0 → one attempt, never zero
        var step = result.StepResults.Single();
        Assert.Equal(StepStatus.Skipped, step.Status);
        Assert.Equal(1, step.Attempts);
    }

    [Fact]
    public async Task ExecuteAsync_NegativeRetryBudget_SucceedingPrimary_ProducesOutput()
    {
        // A negative (clamped) budget must not sabotage a primary that would succeed:
        // the one allowed attempt runs and its output is recorded.
        var plan = new Plan("g", new[]
        {
            new PlanStep("x", ""),
        });

        var result = await Executor(retryBudget: -1).ExecuteAsync(plan, EchoStep);

        var step = result.StepResults.Single();
        Assert.Equal(StepStatus.Succeeded, step.Status);
        Assert.Equal("x-ok", step.Output);
        Assert.Equal(1, step.Attempts);
        Assert.True(result.GoalReached);
    }

    // ── Fallback ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_PrimaryExhausted_FallsBackAndRecovers()
    {
        var primaryCalls = 0;
        var plan = new Plan("g", new[]
        {
            new PlanStep("seo", "",
                run: (_, _) => { primaryCalls++; throw new StepException("rate-limited"); },
                fallback: (_, _) => "fallback-output"),
        });

        var result = await Executor(retryBudget: 2).ExecuteAsync(plan, EchoStep);

        Assert.Equal(3, primaryCalls); // 1 + 2 retries
        var step = Assert.Single(result.StepResults);
        Assert.Equal(StepStatus.Recovered, step.Status);
        Assert.Equal("fallback-output", step.Output);
        Assert.Contains("seo", result.Recovered);
        Assert.True(result.GoalReached);
    }

    [Fact]
    public async Task ExecuteAsync_RecoveredStepOutput_FlowsToDependents()
    {
        var plan = new Plan("g", new[]
        {
            new PlanStep("a", "",
                run: (_, _) => throw new StepException("fail"),
                fallback: (_, _) => "from-fallback"),
            new PlanStep("b", "", dependsOn: new[] { "a" },
                run: (ctx, _) => $"got:{ctx.Outputs["a"]}"),
        });

        var result = await Executor(retryBudget: 0).ExecuteAsync(plan, EchoStep);

        Assert.Equal("got:from-fallback", result.Outputs["b"]);
        Assert.True(result.GoalReached);
    }

    [Fact]
    public async Task ExecuteAsync_NonStepException_StillRetriesAndFallsBack()
    {
        // The README's "Wiring to a real model" section promises the retry →
        // fallback → skip → abort policy wraps whatever the step delegate does,
        // so a flaky *tool* call is retried without that logic leaking into the
        // step. A real tool throws ordinary framework exceptions
        // (TimeoutException, HttpRequestException, …) — NOT the demo's
        // StepException. This pins that the executor's catch is exception-type
        // agnostic: a plain TimeoutException from the primary is retried within
        // budget and then recovered via the fallback, exactly like StepException.
        var primaryCalls = 0;
        var plan = new Plan("g", new[]
        {
            new PlanStep("call_api", "",
                run: (_, _) =>
                {
                    primaryCalls++;
                    throw new TimeoutException("gateway timed out"); // not a StepException
                },
                fallback: (_, _) => "served-from-cache"),
        });

        var result = await Executor(retryBudget: 2).ExecuteAsync(plan, EchoStep);

        Assert.Equal(3, primaryCalls); // 1 primary + 2 retries, all on a non-StepException
        var step = Assert.Single(result.StepResults);
        Assert.Equal(StepStatus.Recovered, step.Status);
        Assert.Equal("served-from-cache", step.Output);
        Assert.Equal("gateway timed out", step.Error); // surfaced the real exception message
        Assert.True(result.GoalReached);
    }

    [Fact]
    public async Task ExecuteAsync_FallbackAlsoFails_NonCritical_Skips()
    {
        var plan = new Plan("g", new[]
        {
            new PlanStep("a", "", critical: false,
                run: (_, _) => throw new StepException("primary"),
                fallback: (_, _) => throw new StepException("fallback too")),
        });

        var result = await Executor(retryBudget: 0).ExecuteAsync(plan, EchoStep);

        var step = Assert.Single(result.StepResults);
        Assert.Equal(StepStatus.Skipped, step.Status);
        Assert.Equal("fallback too", step.Error);
    }

    // ── Skip & cascade-skip ──────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CriticalStepCascadeSkipped_GoalNotReached()
    {
        // A CRITICAL step whose (non-critical) dependency was skipped ends up
        // Skipped, not Failed. The run must NOT report goal-reached: a critical
        // step never ran, so the goal is unmet even though nothing is Failed.
        var plan = new Plan("g", new[]
        {
            new PlanStep("optional", "", critical: false, run: (_, _) => throw new StepException("timeout")),
            new PlanStep("must_ship", "", critical: true, dependsOn: new[] { "optional" },
                run: (_, _) => "shipped"),
        });

        var result = await Executor(retryBudget: 0, stopOnCritical: true).ExecuteAsync(plan, EchoStep);

        Assert.Contains("optional", result.Skipped);
        Assert.Contains("must_ship", result.Skipped);   // critical, cascade-skipped
        Assert.Empty(result.Failed);                     // nothing is Failed...
        Assert.False(result.GoalReached);                // ...but the goal is still unmet
        Assert.Equal(PlanOutcome.CompletedWithFailures, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_NonCriticalFailure_SkipsAndCascades()
    {
        var downstreamRan = false;
        var plan = new Plan("g", new[]
        {
            new PlanStep("img", "", critical: false, run: (_, _) => throw new StepException("timeout")),
            new PlanStep("card", "", dependsOn: new[] { "img" },
                run: (_, _) => { downstreamRan = true; return "card"; }),
            new PlanStep("unrelated", "", run: (_, _) => "still-runs"),
        });

        var result = await Executor(retryBudget: 0).ExecuteAsync(plan, EchoStep);

        Assert.False(downstreamRan); // cascade-skipped, never invoked
        Assert.Contains("img", result.Skipped);
        Assert.Contains("card", result.Skipped);
        Assert.Contains("unrelated", result.Succeeded); // independent branch keeps going
        Assert.Equal(PlanOutcome.Completed, result.Outcome); // no critical failure
        Assert.True(result.GoalReached);

        var card = result.StepResults.Single(r => r.StepId == "card");
        Assert.Equal(0, card.Attempts); // skipped without ever attempting
        Assert.Contains("img", card.Error);
    }

    // ── Critical abort ───────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CriticalFailure_AbortsRun()
    {
        var shipRan = false;
        var plan = new Plan("g", new[]
        {
            new PlanStep("build", "", critical: true, run: (_, _) => throw new StepException("CS1002")),
            new PlanStep("ship", "", dependsOn: new[] { "build" },
                run: (_, _) => { shipRan = true; return "ship"; }),
        });

        var result = await Executor(retryBudget: 1, stopOnCritical: true).ExecuteAsync(plan, EchoStep);

        Assert.False(shipRan);
        Assert.Equal(PlanOutcome.Aborted, result.Outcome);
        Assert.False(result.GoalReached);
        Assert.Contains("build", result.Failed);
        // 'ship' never appears at all because the loop broke before reaching it.
        Assert.DoesNotContain("ship", result.StepResults.Select(r => r.StepId));
    }

    [Fact]
    public async Task ExecuteAsync_CriticalFailure_BestEffortMode_KeepsGoing()
    {
        var plan = new Plan("g", new[]
        {
            new PlanStep("build", "", critical: true, run: (_, _) => throw new StepException("boom")),
            new PlanStep("ship", "", dependsOn: new[] { "build" }, run: (_, _) => "ship"),
            new PlanStep("notify", "", run: (_, _) => "sent"), // independent, should run
        });

        var result = await Executor(retryBudget: 0, stopOnCritical: false).ExecuteAsync(plan, EchoStep);

        Assert.Equal(PlanOutcome.CompletedWithFailures, result.Outcome);
        Assert.False(result.GoalReached);
        Assert.Contains("build", result.Failed);
        Assert.Contains("ship", result.Skipped);   // dependent cascade-skips
        Assert.Contains("notify", result.Succeeded); // independent still runs
    }

    // ── Observability ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmitsEvents_ForEachTransition()
    {
        var events = new List<ExecEventKind>();
        var plan = new Plan("g", new[]
        {
            new PlanStep("ok", "", run: (_, _) => "fine"),
            new PlanStep("recover", "",
                run: (_, _) => throw new StepException("x"),
                fallback: (_, _) => "fb"),
        });

        await Executor(retryBudget: 1, onEvent: e => events.Add(e.Kind)).ExecuteAsync(plan, EchoStep);

        Assert.Contains(ExecEventKind.StepStarted, events);
        Assert.Contains(ExecEventKind.StepSucceeded, events);
        Assert.Contains(ExecEventKind.StepRetrying, events);
        Assert.Contains(ExecEventKind.StepFellBack, events);
    }

    // ── Async + cancellation ─────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AsyncDefaultStep_IsAwaited()
    {
        var plan = new Plan("g", new[] { new PlanStep("a", "") });

        var result = await Executor().ExecuteAsync(plan, async (step, ctx, ct) =>
        {
            await Task.Yield();
            return $"async-{step.Id}";
        });

        Assert.Equal("async-a", result.Outputs["a"]);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyCancelled_Throws()
    {
        var plan = new Plan("g", new[] { new PlanStep("a", "") });
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Executor().ExecuteAsync(plan, EchoStep, cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_NullPlan_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Executor().ExecuteAsync(null!, EchoStep));
    }
}

// ── Mirrored types under test ────────────────────────────────
// The recipe's Program.cs is a standalone executable and is NOT referenced by
// this test assembly, so the logic types it exercises are re-declared here
// (house convention). Keep these in sync with recipes/plan-and-execute/Program.cs.

class StepException : Exception
{
    public StepException(string message) : base(message) { }
}

class PlanStep
{
    public string Id { get; }
    public string Description { get; }
    public IReadOnlyList<string> DependsOn { get; }
    public bool Critical { get; }
    public Func<ExecutionContext, CancellationToken, string>? Run { get; }
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

        foreach (var s in Steps)
            foreach (var dep in s.DependsOn)
                if (!_byId.ContainsKey(dep))
                    throw new ArgumentException($"Step '{s.Id}' depends on unknown step '{dep}'.", nameof(steps));
    }

    public PlanStep this[string id] => _byId[id];

    public IReadOnlyList<PlanStep> ExecutionOrder()
    {
        var ordered = new List<PlanStep>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
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

enum StepStatus { Succeeded, Recovered, Skipped, Failed }

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

record ExecEvent(ExecEventKind Kind, string StepId, string Message);

record StepResult(string StepId, StepStatus Status, string? Output, int Attempts, string? Error);

enum PlanOutcome { Completed, CompletedWithFailures, Aborted }

record PlanResult(
    PlanOutcome Outcome,
    bool GoalReached,
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyList<StepResult> StepResults)
{
    public IReadOnlyList<string> Succeeded =>
        StepResults.Where(r => r.Status == StepStatus.Succeeded).Select(r => r.StepId).ToList();

    public IReadOnlyList<string> Recovered =>
        StepResults.Where(r => r.Status == StepStatus.Recovered).Select(r => r.StepId).ToList();

    public IReadOnlyList<string> Skipped =>
        StepResults.Where(r => r.Status == StepStatus.Skipped).Select(r => r.StepId).ToList();

    public IReadOnlyList<string> Failed =>
        StepResults.Where(r => r.Status == StepStatus.Failed).Select(r => r.StepId).ToList();
}

record PlanExecutorOptions
{
    public int RetryBudget { get; init; } = 1;
    public bool StopOnCriticalFailure { get; init; } = true;
    public Action<ExecEvent>? OnEvent { get; init; }
}

class PlanExecutor
{
    private readonly PlanExecutorOptions _options;

    public PlanExecutor(PlanExecutorOptions options) => _options = options;

    public Task<PlanResult> ExecuteAsync(
        Plan plan,
        Func<PlanStep, ExecutionContext, string> defaultStep,
        CancellationToken ct = default)
        => ExecuteAsync(plan, (step, ctx, _) => Task.FromResult(defaultStep(step, ctx)), ct);

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

            if (!step.Critical)
            {
                statusById[step.Id] = StepStatus.Skipped;
                results.Add(new StepResult(step.Id, StepStatus.Skipped, null, attempts, lastError));
                Emit(ExecEventKind.StepSkipped, step.Id,
                    $"non-critical and out of options ({lastError}); skipped");
                continue;
            }

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
        }

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
