using Prompt;

namespace AgenticRecipes.ResearchSummarizeFormat;

/// <summary>
/// Recipe: Research → Summarize → Format
/// 
/// Demonstrates a linear PromptChain where each step's output
/// flows into the next step as a template variable.
/// 
/// Pattern: Sequential processing pipeline
/// Use case: Turn a topic into a polished research brief
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var topic = args.Length > 0
            ? string.Join(" ", args)
            : "recent advances in large language model reasoning";

        Console.WriteLine($"═══ Research → Summarize → Format ═══");
        Console.WriteLine($"Topic: {topic}");
        Console.WriteLine();

        // ── Step 1: Research ──────────────────────────────────
        // Gathers raw information about the topic.
        var researchTemplate = new PromptTemplate(
            """
            Research the following topic thoroughly. Provide detailed findings
            including key developments, important figures/organizations involved,
            and significant data points.

            Topic: {{topic}}

            Provide your findings in a structured but raw format — focus on
            completeness over polish. Include dates and sources where possible.
            """);

        // ── Step 2: Summarize ────────────────────────────────
        // Distills raw research into key points.
        var summarizeTemplate = new PromptTemplate(
            """
            You are given raw research findings. Distill them into exactly
            5-7 key bullet points that capture the most important insights.

            Each bullet should:
            - Be one concise sentence
            - Contain a specific fact, number, or insight
            - Be independently meaningful (no "also" or "additionally")

            Raw research:
            {{raw_data}}

            Key bullet points:
            """);

        // ── Step 3: Format ───────────────────────────────────
        // Produces the final polished report.
        var formatTemplate = new PromptTemplate(
            """
            Create a professional research brief from the following summary.

            Format requirements:
            - Title (topic-derived, engaging)
            - Executive Summary (2-3 sentences)
            - Key Findings (the bullet points, refined)
            - Implications (2-3 sentences on why this matters)
            - Output in clean Markdown

            Topic: {{topic}}
            Summary points:
            {{summary}}

            Research brief:
            """);

        // ── Build & Validate the Chain ───────────────────────
        var chain = new PromptChain()
            .WithSystemPrompt("You are a senior research analyst producing accurate, concise briefs.")
            .WithMaxRetries(2)
            .AddStep("research", researchTemplate, "raw_data")
            .AddStep("summarize", summarizeTemplate, "summary")
            .AddStep("format", formatTemplate, "final_report");

        // Static validation: ensures all variables are satisfiable
        var initialVars = new Dictionary<string, string> { ["topic"] = topic };
        var validationErrors = chain.Validate(initialVars);

        if (validationErrors.Count > 0)
        {
            Console.WriteLine("❌ Chain validation failed:");
            foreach (var err in validationErrors)
                Console.WriteLine($"   • {err}");
            return;
        }

        Console.WriteLine("✓ Chain validated — all variables satisfiable");
        Console.WriteLine($"  Steps: research → summarize → format");
        Console.WriteLine();

        // ── Execute ──────────────────────────────────────────
        Console.WriteLine("Running chain...");
        Console.WriteLine();

        var result = await chain.RunAsync(initialVars);

        // ── Results ──────────────────────────────────────────
        Console.WriteLine($"✓ Chain completed in {result.TotalElapsed.TotalSeconds:F1}s");
        Console.WriteLine();

        // Show timing per step
        Console.WriteLine("── Step Timing ──");
        foreach (var step in result.Steps)
        {
            Console.WriteLine($"  {step.StepName,-12} {step.Elapsed.TotalMilliseconds,6:F0}ms");
        }
        Console.WriteLine();

        // Show intermediate output (summary)
        Console.WriteLine("── Intermediate: Summary ──");
        Console.WriteLine(result.GetOutput("summary"));
        Console.WriteLine();

        // Show final output
        Console.WriteLine("── Final Report ──");
        Console.WriteLine(result.FinalResponse);

        // ── Export chain definition (reusable) ───────────────
        var chainJson = chain.ToJson();
        await File.WriteAllTextAsync("chain-definition.json", chainJson);
        Console.WriteLine();
        Console.WriteLine("✓ Chain definition saved to chain-definition.json (reusable)");
    }
}
