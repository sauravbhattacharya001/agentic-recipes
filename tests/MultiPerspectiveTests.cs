using System.Text.Json;
using Prompt;
using Xunit;

namespace AgenticRecipes.Tests;

/// <summary>
/// Tests for Recipe 2: Multi-Perspective Analysis
/// Validates PromptOrchestrator plan construction, fan-out/fan-in, and execution.
/// </summary>
public class MultiPerspectiveTests
{
    // ── Plan Construction ─────────────────────────────────────

    [Fact]
    public void BuildFanOutFanIn_CreatesCorrectNodeCount()
    {
        var plan = PromptOrchestrator.BuildFanOutFanIn(
            inputPrompt: "Analyze: {topic}",
            parallelPrompts: new[]
            {
                "Optimist: {input}",
                "Skeptic: {input}",
                "Pragmatist: {input}"
            },
            aggregatorPrompt: "Synthesize: {parallel_0} {parallel_1} {parallel_2}"
        );

        // 1 input + 3 parallel + 1 aggregator = 5 nodes
        Assert.Equal(5, plan.Nodes.Count);
    }

    [Fact]
    public void BuildFanOutFanIn_EntryNodeIsInput()
    {
        var plan = BuildPlan();
        Assert.Equal("input", plan.EntryNodeId);
    }

    [Fact]
    public void BuildFanOutFanIn_ParallelNodesDependOnInput()
    {
        var plan = BuildPlan();
        var parallels = plan.Nodes.Where(n => n.Id.StartsWith("parallel_")).ToList();

        Assert.Equal(3, parallels.Count);
        foreach (var p in parallels)
        {
            Assert.Contains("input", p.DependsOn);
        }
    }

    [Fact]
    public void BuildFanOutFanIn_AggregatorDependsOnAllParallels()
    {
        var plan = BuildPlan();
        var aggregator = plan.Nodes.First(n => n.Id == "aggregator");

        Assert.Contains("parallel_0", aggregator.DependsOn);
        Assert.Contains("parallel_1", aggregator.DependsOn);
        Assert.Contains("parallel_2", aggregator.DependsOn);
    }

    [Fact]
    public void BuildFanOutFanIn_AggregatorIsAggregatorType()
    {
        var plan = BuildPlan();
        var aggregator = plan.Nodes.First(n => n.Id == "aggregator");
        Assert.Equal(OrchestratorNodeType.Aggregator, aggregator.Type);
    }

    // ── Plan Validation ──────────────────────────────────────

    [Fact]
    public void Validate_ValidPlan_ReturnsNoErrors()
    {
        var plan = BuildPlan();
        var errors = plan.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_InvalidEntryNode_ReturnsError()
    {
        var plan = BuildPlan();
        plan.EntryNodeId = "nonexistent";
        var errors = plan.Validate();
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_DanglingDependency_ReturnsError()
    {
        var plan = new OrchestratorPlan
        {
            EntryNodeId = "a",
            Nodes = new()
            {
                new OrchestratorNode("a", "prompt a") { DependsOn = { "missing" } }
            }
        };
        var errors = plan.Validate();
        Assert.Contains(errors, e => e.Contains("missing"));
    }

    // ── Execution Layers ─────────────────────────────────────

    [Fact]
    public void GetExecutionLayers_FanOutFanIn_HasThreeLayers()
    {
        var plan = BuildPlan();
        var layers = plan.GetExecutionLayers();

        // Layer 0: input (no deps)
        // Layer 1: parallel_0, parallel_1, parallel_2 (depend on input)
        // Layer 2: aggregator (depends on all parallels)
        Assert.Equal(3, layers.Count);
    }

    [Fact]
    public void GetExecutionLayers_Layer0_IsInputOnly()
    {
        var plan = BuildPlan();
        var layers = plan.GetExecutionLayers();

        Assert.Single(layers[0]);
        Assert.Equal("input", layers[0][0].Id);
    }

    [Fact]
    public void GetExecutionLayers_Layer1_IsAllParallels()
    {
        var plan = BuildPlan();
        var layers = plan.GetExecutionLayers();

        Assert.Equal(3, layers[1].Count);
        var ids = layers[1].Select(n => n.Id).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "parallel_0", "parallel_1", "parallel_2" }, ids);
    }

    [Fact]
    public void GetExecutionLayers_Layer2_IsAggregator()
    {
        var plan = BuildPlan();
        var layers = plan.GetExecutionLayers();

        Assert.Single(layers[2]);
        Assert.Equal("aggregator", layers[2][0].Id);
    }

    // ── Critical Path ────────────────────────────────────────

    [Fact]
    public void CriticalPath_FanOutFanIn_HasThreeNodes()
    {
        var plan = BuildPlan();
        var cp = plan.GetCriticalPath();

        // input → parallel_X → aggregator = 3 nodes deep
        Assert.Equal(3, cp.Count);
        Assert.Equal("input", cp[0]);
        Assert.StartsWith("parallel_", cp[1]);
        Assert.Equal("aggregator", cp[2]);
    }

    // ── Orchestrator Execution ───────────────────────────────

    [Fact]
    public async Task Execute_FanOutFanIn_AllNodesSucceed()
    {
        var callCount = 0;
        var orchestrator = new PromptOrchestrator(async prompt =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(10); // simulate latency
            return $"Response for: {prompt[..Math.Min(30, prompt.Length)]}";
        });

        var plan = BuildPlan();
        var execution = await orchestrator.ExecuteAsync(
            plan,
            new Dictionary<string, string> { ["topic"] = "unit test topic" });

        Assert.Equal(OrchestratorStatus.Completed, execution.Status);
        Assert.Equal(5, execution.Results.Count);
        Assert.All(execution.Results.Values, r => Assert.True(r.Success));
    }

    [Fact]
    public async Task Execute_FanOutFanIn_ParallelNodesRunConcurrently()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        var orchestrator = new PromptOrchestrator(async prompt =>
        {
            lock (lockObj)
            {
                concurrentCount++;
                maxConcurrent = Math.Max(maxConcurrent, concurrentCount);
            }
            await Task.Delay(50); // overlap window
            lock (lockObj) { concurrentCount--; }
            return "done";
        });

        var plan = BuildPlan();
        await orchestrator.ExecuteAsync(plan,
            new Dictionary<string, string> { ["topic"] = "concurrency test" });

        // At least 2 parallel nodes should overlap (3 ideally)
        Assert.True(maxConcurrent >= 2,
            $"Expected parallel execution but max concurrent was {maxConcurrent}");
    }

    [Fact]
    public async Task Execute_NodeFailure_ReturnsPartialSuccess()
    {
        var callNum = 0;
        var orchestrator = new PromptOrchestrator(async prompt =>
        {
            var n = Interlocked.Increment(ref callNum);
            await Task.Delay(5);
            if (n == 3) throw new Exception("Simulated failure");
            return "ok";
        });

        var plan = BuildPlan();
        var execution = await orchestrator.ExecuteAsync(plan,
            new Dictionary<string, string> { ["topic"] = "failure test" });

        // Should be PartialSuccess or Failed, not Completed
        Assert.NotEqual(OrchestratorStatus.Completed, execution.Status);
    }

    [Fact]
    public async Task Execute_EventLog_CapturesAllEvents()
    {
        var orchestrator = new PromptOrchestrator(async prompt =>
        {
            await Task.Delay(1);
            return "ok";
        });

        var plan = BuildPlan();
        var execution = await orchestrator.ExecuteAsync(plan,
            new Dictionary<string, string> { ["topic"] = "events test" });

        Assert.NotEmpty(execution.EventLog);
        Assert.Contains(execution.EventLog,
            e => e.Type == OrchestratorEventType.ExecutionStarted);
        Assert.Contains(execution.EventLog,
            e => e.Type == OrchestratorEventType.ExecutionCompleted);
        Assert.Contains(execution.EventLog,
            e => e.Type == OrchestratorEventType.NodeCompleted);
    }

    // ── Report Generation ────────────────────────────────────

    [Fact]
    public async Task GenerateMarkdown_ProducesReport()
    {
        var orchestrator = new PromptOrchestrator(async prompt =>
        {
            await Task.Delay(1);
            return "response";
        });

        var plan = BuildPlan();
        var execution = await orchestrator.ExecuteAsync(plan,
            new Dictionary<string, string> { ["topic"] = "report test" });

        var markdown = OrchestratorReport.GenerateMarkdown(execution);
        Assert.Contains("Orchestration Report", markdown);
        Assert.Contains("Node Results", markdown);
        Assert.Contains("✅", markdown);
    }

    [Fact]
    public async Task GenerateMermaid_ProducesFlowchart()
    {
        var orchestrator = new PromptOrchestrator(async prompt =>
        {
            await Task.Delay(1);
            return "response";
        });

        var plan = BuildPlan();
        var execution = await orchestrator.ExecuteAsync(plan,
            new Dictionary<string, string> { ["topic"] = "mermaid test" });

        var mermaid = OrchestratorReport.GenerateMermaid(execution);
        Assert.Contains("mermaid", mermaid);
        Assert.Contains("flowchart", mermaid);
        Assert.Contains("input", mermaid);
        Assert.Contains("aggregator", mermaid);
    }

    [Fact]
    public async Task GenerateText_ProducesPlainSummary()
    {
        var orchestrator = new PromptOrchestrator(async prompt =>
        {
            await Task.Delay(1);
            return "response";
        });

        var plan = BuildPlan();
        var execution = await orchestrator.ExecuteAsync(plan,
            new Dictionary<string, string> { ["topic"] = "text test" });

        var text = OrchestratorReport.GenerateText(execution);
        // A plain-text summary that names every node and reports the final status.
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("input", text);
        Assert.Contains("aggregator", text);
        Assert.Contains(execution.Status.ToString(), text);
    }

    [Fact]
    public async Task GenerateJson_ProducesParseableExport()
    {
        var orchestrator = new PromptOrchestrator(async prompt =>
        {
            await Task.Delay(1);
            return "response";
        });

        var plan = BuildPlan();
        var execution = await orchestrator.ExecuteAsync(plan,
            new Dictionary<string, string> { ["topic"] = "json test" });

        var json = OrchestratorReport.GenerateJson(execution);
        // Must be a real, well-formed JSON export of the execution (not just a string).
        Assert.False(string.IsNullOrWhiteSpace(json));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        // The raw JSON should carry the node ids and final status it exports.
        Assert.Contains("aggregator", json);
        Assert.Contains(execution.Status.ToString(), json);
    }

    // ── Helper ───────────────────────────────────────────────

    private static OrchestratorPlan BuildPlan()
    {
        return PromptOrchestrator.BuildFanOutFanIn(
            inputPrompt: "Analyze the topic: {topic}",
            parallelPrompts: new[]
            {
                "As an optimist, evaluate: {input}",
                "As a skeptic, evaluate: {input}",
                "As a pragmatist, evaluate: {input}"
            },
            aggregatorPrompt: "Synthesize these views: {parallel_0} | {parallel_1} | {parallel_2}"
        );
    }
}
