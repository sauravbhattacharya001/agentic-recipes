using Prompt;

namespace AgenticRecipes.MultiPerspective;

/// <summary>
/// Recipe: Multi-Perspective Analysis
/// 
/// Demonstrates a fan-out/fan-in orchestration pattern where one input
/// is analyzed by multiple "persona" prompts in parallel, then an
/// aggregator synthesizes their diverse viewpoints.
/// 
/// Pattern: Fan-Out / Fan-In (parallel execution + aggregation)
/// Use case: Get balanced, multi-angle analysis of any proposal/decision
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var topic = args.Length > 0
            ? string.Join(" ", args)
            : "Should we adopt AI code generation tools across our engineering team?";

        Console.WriteLine($"═══ Multi-Perspective Analysis ═══");
        Console.WriteLine($"Topic: {topic}");
        Console.WriteLine();

        // ── Define the prompts ───────────────────────────────

        // Entry: frame the topic for analysis
        var inputPrompt =
            """
            You are preparing a topic for multi-perspective analysis.
            Frame the following proposal/question clearly, identifying the key
            decision points and stakeholders involved.

            Topic: {topic}

            Provide a 2-3 sentence framing that the analysis team can evaluate.
            """;

        // Fan-out: three parallel perspective prompts
        var optimistPrompt =
            """
            You are an optimistic technology strategist. You see opportunities
            where others see risk. Analyze the following from a best-case perspective:

            Proposal: {input}

            Provide:
            1. Top 3 opportunities/upsides
            2. Best-case timeline and outcome
            3. Competitive advantage if we move now

            Be specific and cite concrete benefits.
            """;

        var skepticPrompt =
            """
            You are a skeptical risk analyst. Your job is to find failure modes
            that others miss. Analyze the following critically:

            Proposal: {input}

            Provide:
            1. Top 3 risks or failure modes
            2. Hidden costs that aren't immediately obvious
            3. What could go wrong in the worst case

            Be specific — vague concerns are not useful.
            """;

        var pragmatistPrompt =
            """
            You are a pragmatic engineering manager. You focus on what's actually
            achievable given real constraints. Analyze the following realistically:

            Proposal: {input}

            Provide:
            1. Realistic resource requirements (people, time, money)
            2. Suggested phased approach (what to try first)
            3. Key metrics to track for go/no-go decisions

            Ground everything in practical reality.
            """;

        // Aggregator: synthesize all perspectives
        var aggregatorPrompt =
            """
            You are a senior advisor synthesizing three different analyses of the
            same proposal. Create a balanced recommendation.

            OPTIMIST VIEW:
            {parallel_0}

            SKEPTIC VIEW:
            {parallel_1}

            PRAGMATIST VIEW:
            {parallel_2}

            Synthesize into:
            1. **Recommendation** (Go / No-Go / Conditional Go) with one-sentence rationale
            2. **Key conditions** — what must be true for success
            3. **Immediate next step** — single concrete action to take this week
            4. **Watch-outs** — top 2 things to monitor

            Be decisive, not wishy-washy.
            """;

        // ── Build the Orchestration Plan ─────────────────────

        var plan = PromptOrchestrator.BuildFanOutFanIn(
            inputPrompt: inputPrompt,
            parallelPrompts: new[] { optimistPrompt, skepticPrompt, pragmatistPrompt },
            aggregatorPrompt: aggregatorPrompt
        );

        // ── Create executor backed by prompt-lib ─────────────

        var orchestrator = new PromptOrchestrator(async prompt =>
        {
            // Uses prompt-lib's Main.GetResponseAsync under the hood
            var response = await Prompt.Main.GetResponseAsync(
                prompt,
                systemPrompt: "You are a senior analyst. Be specific and concise.",
                maxRetries: 2);
            return response ?? throw new InvalidOperationException("No response from model");
        });

        // ── Execute ──────────────────────────────────────────

        Console.WriteLine("Executing orchestration plan...");
        Console.WriteLine($"  Nodes: {plan.Nodes.Count} (1 input → 3 parallel → 1 aggregator)");
        Console.WriteLine();

        var variables = new Dictionary<string, string> { ["topic"] = topic };
        var execution = await orchestrator.ExecuteAsync(plan, variables);

        // ── Results ──────────────────────────────────────────

        Console.WriteLine($"Status: {execution.Status}");
        Console.WriteLine($"Duration: {execution.TotalDuration.TotalSeconds:F1}s");
        Console.WriteLine();

        // Show each perspective
        var perspectiveNames = new[] { "Optimist", "Skeptic", "Pragmatist" };
        for (int i = 0; i < 3; i++)
        {
            var nodeId = $"parallel_{i}";
            if (execution.Results.TryGetValue(nodeId, out var result) && result.Success)
            {
                Console.WriteLine($"── {perspectiveNames[i]} ({result.Duration.TotalMilliseconds:F0}ms) ──");
                Console.WriteLine(result.Output);
                Console.WriteLine();
            }
        }

        // Show synthesized recommendation
        if (execution.Results.TryGetValue("aggregator", out var aggResult) && aggResult.Success)
        {
            Console.WriteLine("══════════════════════════════════════");
            Console.WriteLine("══ SYNTHESIZED RECOMMENDATION ══");
            Console.WriteLine("══════════════════════════════════════");
            Console.WriteLine(aggResult.Output);
        }

        // ── Generate execution report ────────────────────────
        Console.WriteLine();
        Console.WriteLine("── Execution Report (Markdown) ──");
        Console.WriteLine(OrchestratorReport.GenerateMarkdown(execution));

        // Save Mermaid diagram
        var mermaid = OrchestratorReport.GenerateMermaid(execution);
        await File.WriteAllTextAsync("execution-flow.md", mermaid);
        Console.WriteLine("✓ Mermaid diagram saved to execution-flow.md");
    }
}
