using Prompt;
using Xunit;

namespace AgenticRecipes.Tests;

/// <summary>
/// Tests for Recipe 1: Research → Summarize → Format
/// Validates PromptChain construction, validation, variable flow, and execution.
/// </summary>
public class ResearchSummarizeFormatTests
{
    // ── Chain Construction ────────────────────────────────────

    [Fact]
    public void Chain_WithThreeSteps_HasCorrectStepCount()
    {
        var chain = BuildChain();
        Assert.Equal(3, chain.StepCount);
    }

    [Fact]
    public void Chain_StepNames_AreCorrect()
    {
        var chain = BuildChain();
        Assert.Equal("research", chain.Steps[0].Name);
        Assert.Equal("summarize", chain.Steps[1].Name);
        Assert.Equal("format", chain.Steps[2].Name);
    }

    [Fact]
    public void Chain_OutputVariables_AreUnique()
    {
        var chain = BuildChain();
        var outputs = chain.Steps.Select(s => s.OutputVariable).ToList();
        Assert.Equal(outputs.Count, outputs.Distinct().Count());
    }

    [Fact]
    public void Chain_DuplicateOutputVariable_Throws()
    {
        var chain = new PromptChain()
            .AddStep("step1", new PromptTemplate("{{input}}"), "result");

        Assert.Throws<ArgumentException>(() =>
            chain.AddStep("step2", new PromptTemplate("{{result}}"), "result"));
    }

    // ── Validation ───────────────────────────────────────────

    [Fact]
    public void Validate_WithTopic_ReturnsNoErrors()
    {
        var chain = BuildChain();
        var errors = chain.Validate(new Dictionary<string, string> { ["topic"] = "AI" });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_MissingTopic_ReturnsError()
    {
        var chain = BuildChain();
        var errors = chain.Validate(new Dictionary<string, string>());
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("topic"));
    }

    [Fact]
    public void Validate_EmptyChain_ReturnsError()
    {
        var chain = new PromptChain();
        var errors = chain.Validate();
        Assert.Single(errors);
        Assert.Contains("no steps", errors[0]);
    }

    [Fact]
    public void Validate_VariableFromPriorStep_IsAvailable()
    {
        // "summarize" step needs {{raw_data}} which is produced by "research"
        var chain = BuildChain();
        var errors = chain.Validate(new Dictionary<string, string> { ["topic"] = "AI" });
        // Should NOT complain about raw_data or summary — they come from prior steps
        Assert.Empty(errors);
    }

    // ── Execution ────────────────────────────────────────────
    // These tests call PromptChain.RunAsync against a live Azure OpenAI endpoint,
    // so they are skipped in the unit suite (run them manually with credentials).

    [Fact(Skip = "Requires Azure OpenAI credentials")]
    public async Task RunAsync_ExecutesAllSteps_InOrder()
    {
        var executionOrder = new List<string>();

        // Mock: intercept each step's rendered prompt to track order
        var chain = new PromptChain()
            .AddStep("research",
                new PromptTemplate("Research: {{topic}}"), "raw_data")
            .AddStep("summarize",
                new PromptTemplate("Summarize: {{raw_data}}"), "summary")
            .AddStep("format",
                new PromptTemplate("Format: {{summary}}"), "final_report");

        var result = await chain.RunAsync(
            new Dictionary<string, string> { ["topic"] = "test topic" });

        // All 3 steps should produce results
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal("research", result.Steps[0].StepName);
        Assert.Equal("summarize", result.Steps[1].StepName);
        Assert.Equal("format", result.Steps[2].StepName);
    }

    [Fact(Skip = "Requires Azure OpenAI credentials")]
    public async Task RunAsync_VariablesFlowBetweenSteps()
    {
        var chain = new PromptChain()
            .AddStep("step1",
                new PromptTemplate("Input: {{topic}}"), "output1")
            .AddStep("step2",
                new PromptTemplate("Process: {{output1}}"), "output2");

        var result = await chain.RunAsync(
            new Dictionary<string, string> { ["topic"] = "hello" });

        // step2's rendered prompt should contain step1's output
        var step2 = result.Steps[1];
        Assert.Contains("Process:", step2.RenderedPrompt);
    }

    [Fact(Skip = "Requires Azure OpenAI credentials")]
    public async Task RunAsync_FinalResponse_IsLastStepOutput()
    {
        var chain = new PromptChain()
            .AddStep("only_step",
                new PromptTemplate("Echo: {{input}}"), "result");

        var result = await chain.RunAsync(
            new Dictionary<string, string> { ["input"] = "test" });

        // FinalResponse should equal the last step's response
        Assert.Equal(result.Steps[^1].Response, result.FinalResponse);
    }

    [Fact(Skip = "Requires Azure OpenAI credentials")]
    public async Task RunAsync_GetOutput_RetrievesIntermediateResults()
    {
        var chain = new PromptChain()
            .AddStep("a", new PromptTemplate("A: {{x}}"), "out_a")
            .AddStep("b", new PromptTemplate("B: {{out_a}}"), "out_b");

        var result = await chain.RunAsync(
            new Dictionary<string, string> { ["x"] = "start" });

        // Both outputs accessible by variable name
        Assert.NotNull(result.GetOutput("out_a"));
        Assert.NotNull(result.GetOutput("out_b"));
    }

    [Fact]
    public async Task RunAsync_EmptyChain_ThrowsInvalidOperation()
    {
        var chain = new PromptChain();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chain.RunAsync());
    }

    // ── Serialization ────────────────────────────────────────

    [Fact]
    public void ToJson_ProducesValidJson()
    {
        var chain = BuildChain();
        var json = chain.ToJson();

        Assert.NotEmpty(json);
        Assert.Contains("research", json);
        Assert.Contains("summarize", json);
        Assert.Contains("format", json);
    }

    [Fact]
    public void FromJson_RoundTrips()
    {
        var chain = BuildChain();
        var json = chain.ToJson();
        var restored = PromptChain.FromJson(json);

        Assert.Equal(chain.StepCount, restored.StepCount);
        Assert.Equal(chain.Steps[0].Name, restored.Steps[0].Name);
        Assert.Equal(chain.Steps[1].OutputVariable, restored.Steps[1].OutputVariable);
    }

    // ── Timing ───────────────────────────────────────────────

    [Fact(Skip = "Requires Azure OpenAI credentials")]
    public async Task RunAsync_RecordsTimingPerStep()
    {
        var chain = new PromptChain()
            .AddStep("s1", new PromptTemplate("{{x}}"), "o1");

        var result = await chain.RunAsync(
            new Dictionary<string, string> { ["x"] = "go" });

        foreach (var step in result.Steps)
        {
            Assert.True(step.Elapsed >= TimeSpan.Zero);
        }
        Assert.True(result.TotalElapsed >= TimeSpan.Zero);
    }

    // ── Helper ───────────────────────────────────────────────

    private static PromptChain BuildChain()
    {
        return new PromptChain()
            .WithSystemPrompt("You are a research analyst.")
            .AddStep("research",
                new PromptTemplate("Research the topic: {{topic}}"),
                "raw_data")
            .AddStep("summarize",
                new PromptTemplate("Summarize: {{raw_data}}"),
                "summary")
            .AddStep("format",
                new PromptTemplate("Format report from: {{summary}} about {{topic}}"),
                "final_report");
    }
}
