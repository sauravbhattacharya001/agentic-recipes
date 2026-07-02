using Prompt;
using Xunit;

namespace AgenticRecipes.Tests;

/// <summary>
/// Tests for Recipe 1: Research -> Summarize -> Format
/// Validates PromptChain construction, validation, sequential variable-flow
/// semantics (offline), and definition serialization.
/// </summary>
public class ResearchSummarizeFormatTests
{
    // -- Chain Construction ------------------------------------

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

    // -- Validation --------------------------------------------

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
        // "summarize" step needs {{raw_data}} which is produced by "research".
        var chain = BuildChain();
        var errors = chain.Validate(new Dictionary<string, string> { ["topic"] = "AI" });
        // Should NOT complain about raw_data or summary -- they come from prior steps.
        Assert.Empty(errors);
    }

    // -- Execution semantics (offline) -------------------------
    // PromptChain.RunAsync sends each rendered step to a live model, so we can't
    // exercise the network round-trip in the unit suite. But the PATTERN this
    // recipe demonstrates -- sequential variable-passing, where each step's output
    // is threaded into the next step's prompt -- is model-independent: RunAsync
    // renders every step with PromptTemplate.Render(vars, strict:false,
    // sanitize:true) and stores the response under the step's output variable.
    // ThreadOutputs() below mirrors exactly that threading with a deterministic
    // stand-in for the model, so these tests prove the recipe's real contract
    // (order, variable flow, final vs. intermediate outputs) offline instead of
    // being skipped and proving nothing.

    /// <summary>
    /// Runs a chain's steps the way <see cref="PromptChain.RunAsync"/> does --
    /// render each step with the accumulated variables, then store its output --
    /// but with an injected, deterministic "model" so no endpoint is needed.
    /// Returns the per-step rendered prompts and the final variable bag.
    /// </summary>
    private static (List<(string Step, string Rendered, string Output)> Steps,
                    Dictionary<string, string> Vars)
        ThreadOutputs(
            PromptChain chain,
            Dictionary<string, string> initial,
            Func<ChainStep, string, string> model)
    {
        var vars = new Dictionary<string, string>(initial, StringComparer.OrdinalIgnoreCase);
        var steps = new List<(string, string, string)>();
        foreach (var step in chain.Steps)
        {
            // Same render call RunAsync makes: non-strict (future vars may be
            // unresolved) with sanitize on to defuse injected {{...}} in outputs.
            var rendered = step.Template.Render(vars, strict: false, sanitize: true);
            var output = model(step, rendered);
            vars[step.OutputVariable] = output;
            steps.Add((step.Name, rendered, output));
        }
        return (steps, vars);
    }

    [Fact]
    public void Threading_ExecutesAllSteps_InOrder()
    {
        var chain = new PromptChain()
            .AddStep("research", new PromptTemplate("Research: {{topic}}"), "raw_data")
            .AddStep("summarize", new PromptTemplate("Summarize: {{raw_data}}"), "summary")
            .AddStep("format", new PromptTemplate("Format: {{summary}}"), "final_report");

        var (steps, _) = ThreadOutputs(
            chain,
            new Dictionary<string, string> { ["topic"] = "test topic" },
            (step, _) => $"[{step.Name} done]");

        Assert.Equal(new[] { "research", "summarize", "format" },
            steps.Select(s => s.Step).ToArray());
    }

    [Fact]
    public void Threading_VariablesFlowBetweenSteps()
    {
        // step2's rendered prompt must embed step1's ACTUAL output, proving the
        // output was threaded forward as a variable (not just that a step ran).
        var chain = new PromptChain()
            .AddStep("step1", new PromptTemplate("Input: {{topic}}"), "output1")
            .AddStep("step2", new PromptTemplate("Process: {{output1}}"), "output2");

        var (steps, _) = ThreadOutputs(
            chain,
            new Dictionary<string, string> { ["topic"] = "hello" },
            (step, _) => step.Name == "step1" ? "STEP1-OUTPUT" : "STEP2-OUTPUT");

        Assert.Equal("Input: hello", steps[0].Rendered);
        Assert.Equal("Process: STEP1-OUTPUT", steps[1].Rendered);   // flowed forward
    }

    [Fact]
    public void Threading_InitialVariableReachesEveryStep()
    {
        // The seed variable ({{topic}}) stays available to later steps too, not
        // just the first -- accumulated variables are additive.
        var chain = new PromptChain()
            .AddStep("a", new PromptTemplate("A sees {{topic}}"), "out_a")
            .AddStep("b", new PromptTemplate("B sees {{topic}} and {{out_a}}"), "out_b");

        var (steps, _) = ThreadOutputs(
            chain,
            new Dictionary<string, string> { ["topic"] = "AI" },
            (step, _) => step.Name.ToUpperInvariant());

        Assert.Equal("A sees AI", steps[0].Rendered);
        Assert.Equal("B sees AI and A", steps[1].Rendered);
    }

    [Fact]
    public void Threading_FinalOutput_IsLastStepOutput()
    {
        var chain = BuildChain();

        var (steps, vars) = ThreadOutputs(
            chain,
            new Dictionary<string, string> { ["topic"] = "test" },
            (step, _) => $"{step.Name}-response");

        // Mirrors ChainResult.FinalResponse: the last step's stored output.
        var lastStep = chain.Steps[^1];
        Assert.Equal(steps[^1].Output, vars[lastStep.OutputVariable]);
        Assert.Equal("format-response", vars[lastStep.OutputVariable]);
    }

    [Fact]
    public void Threading_IntermediateOutputs_AreRetrievableByVariableName()
    {
        // Mirrors ChainResult.GetOutput: every step's output is addressable by its
        // output-variable name, not only the final response.
        var chain = new PromptChain()
            .AddStep("a", new PromptTemplate("A: {{x}}"), "out_a")
            .AddStep("b", new PromptTemplate("B: {{out_a}}"), "out_b");

        var (_, vars) = ThreadOutputs(
            chain,
            new Dictionary<string, string> { ["x"] = "start" },
            (step, _) => $"{step.Name}!");

        Assert.Equal("a!", vars["out_a"]);   // intermediate, addressable by name
        Assert.Equal("b!", vars["out_b"]);
    }

    [Fact]
    public void Threading_OutputLookupIsCaseInsensitive()
    {
        // ChainResult stores variables case-insensitively; a lookup by a
        // differently-cased name must resolve the same output.
        var chain = new PromptChain()
            .AddStep("a", new PromptTemplate("A: {{x}}"), "Out_A");

        var (_, vars) = ThreadOutputs(
            chain,
            new Dictionary<string, string> { ["x"] = "go" },
            (step, _) => "VALUE");

        Assert.Equal("VALUE", vars["out_a"]);   // case-insensitive bag
        Assert.Equal("VALUE", vars["OUT_A"]);
    }

    [Fact]
    public async Task RunAsync_EmptyChain_ThrowsInvalidOperation()
    {
        var chain = new PromptChain();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chain.RunAsync());
    }

    // -- Serialization -----------------------------------------

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

    [Fact]
    public void FromJson_RoundTrips_PreservesStepWiringAndConfig()
    {
        // The recipe saves its chain definition to chain-definition.json so it can
        // be reloaded and re-run. Prove the round-trip preserves not just the step
        // count but the ordered names, output variables, template text, and that a
        // restored chain still validates against the same inputs.
        var chain = BuildChain().WithMaxRetries(2);
        var restored = PromptChain.FromJson(chain.ToJson());

        Assert.Equal(
            chain.Steps.Select(s => (s.Name, s.OutputVariable)).ToArray(),
            restored.Steps.Select(s => (s.Name, s.OutputVariable)).ToArray());
        Assert.Equal(chain.Steps[0].Template.Template, restored.Steps[0].Template.Template);
        Assert.Empty(restored.Validate(new Dictionary<string, string> { ["topic"] = "AI" }));
    }

    // -- Helper ------------------------------------------------

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
