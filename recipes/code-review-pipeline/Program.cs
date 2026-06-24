using Prompt;

namespace AgenticRecipes.CodeReviewPipeline;

/// <summary>
/// Recipe: Code Review Pipeline
/// 
/// Demonstrates the PromptPipeline middleware pattern for building
/// production-grade prompt execution with cross-cutting concerns:
/// validation, retry, caching, logging, and metrics.
/// 
/// Pattern: Middleware Pipeline (analyze → review → fix)
/// Use case: Automated code review with structured output
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        // Resolve the code to review: file argument → piped stdin → bundled
        // sample, in that order. (The README documents the stdin path, so it
        // must actually be honored rather than silently falling through to the
        // sample.)
        var stdinText = Console.IsInputRedirected
            ? await Console.In.ReadToEndAsync()
            : null;
        var code = ResolveInput(args, stdinText, SampleCode);

        Console.WriteLine("═══ Code Review Pipeline ═══");
        Console.WriteLine($"Input: {code.Split('\n').Length} lines of code");
        Console.WriteLine();

        // ── Build the middleware pipeline ─────────────────────
        // These middleware components wrap every model call with
        // cross-cutting concerns — you define them once, they
        // protect every stage automatically.

        var logs = new List<string>();
        var metrics = new MetricsMiddleware();

        var pipeline = new PromptPipeline()
            .Use(new ValidationMiddleware(
                maxTokens: 16000,
                requiredVariables: new[] { "code" }))
            .Use(new LoggingMiddleware(msg =>
            {
                logs.Add(msg);
                Console.WriteLine($"  📋 {msg}");
            }, order: 5))
            .Use(new RetryMiddleware(maxRetries: 2, order: 10))
            .Use(new CachingMiddleware(
                ttl: TimeSpan.FromMinutes(10),
                maxEntries: 100,
                order: 15))
            .Use(metrics);

        Console.WriteLine("Pipeline configured:");
        Console.WriteLine(pipeline.Describe());
        Console.WriteLine();

        // ── Stage 1: Analyze ─────────────────────────────────
        Console.WriteLine("── Stage 1: Analyze ──");

        var analyzeContext = new PromptPipelineContext
        {
            PromptText =
                """
                Analyze the following code. Identify:
                1. Language and framework
                2. Cyclomatic complexity (estimate: low/medium/high)
                3. Key patterns used (design patterns, idioms)
                4. Potential issue categories (performance, security, maintainability)

                Do NOT suggest fixes yet — just identify and categorize.

                Code:
                ```
                {{code}}
                ```
                """,
            Variables = new() { ["code"] = code }
        };

        await pipeline.ExecuteAsync(analyzeContext, ModelCall);

        var analysis = analyzeContext.Response ?? "Analysis failed";
        Console.WriteLine($"  ✓ Analysis complete ({analyzeContext.ExecutionTime.TotalMilliseconds:F0}ms)");
        Console.WriteLine();

        // ── Stage 2: Review ──────────────────────────────────
        Console.WriteLine("── Stage 2: Review ──");

        var reviewContext = new PromptPipelineContext
        {
            PromptText =
                """
                You are a senior code reviewer. Given the code and its analysis,
                produce a structured review.

                For each issue found:
                - **Severity**: 🔴 Critical | 🟡 Warning | 🔵 Info
                - **Line(s)**: approximate location
                - **Issue**: one-sentence description
                - **Why**: why this matters

                Analysis:
                {{analysis}}

                Code:
                ```
                {{code}}
                ```

                Produce at most 5 issues, ordered by severity (critical first).
                """,
            Variables = new()
            {
                ["code"] = code,
                ["analysis"] = analysis
            }
        };

        await pipeline.ExecuteAsync(reviewContext, ModelCall);

        var review = reviewContext.Response ?? "Review failed";
        Console.WriteLine($"  ✓ Review complete ({reviewContext.ExecutionTime.TotalMilliseconds:F0}ms)");
        Console.WriteLine();

        // ── Stage 3: Fix ─────────────────────────────────────
        Console.WriteLine("── Stage 3: Fix ──");

        var fixContext = new PromptPipelineContext
        {
            PromptText =
                """
                You are a code improvement assistant. Given the original code and
                a review with identified issues, produce a corrected version.

                Rules:
                - Fix ALL issues marked 🔴 Critical
                - Fix 🟡 Warning issues where the fix is straightforward
                - Add a brief comment above each fix explaining the change
                - Preserve the overall structure and intent of the code
                - Output ONLY the corrected code (no explanation outside code block)

                Review:
                {{review}}

                Original code:
                ```
                {{code}}
                ```

                Corrected code:
                """,
            Variables = new()
            {
                ["code"] = code,
                ["review"] = review
            }
        };

        await pipeline.ExecuteAsync(fixContext, ModelCall);

        Console.WriteLine($"  ✓ Fix complete ({fixContext.ExecutionTime.TotalMilliseconds:F0}ms)");
        Console.WriteLine();

        // ── Output Results ───────────────────────────────────

        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("══ REVIEW ══");
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine(review);
        Console.WriteLine();

        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("══ FIXED CODE ══");
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine(fixContext.Response);
        Console.WriteLine();

        // ── Pipeline Metrics ─────────────────────────────────

        Console.WriteLine("── Pipeline Metrics ──");
        Console.WriteLine($"  Total executions: {metrics.GetMetrics().Count}");
        Console.WriteLine($"  Avg execution time: {metrics.AverageExecutionTime().TotalMilliseconds:F0}ms");
        Console.WriteLine($"  Total tokens (est): {metrics.TotalTokens()}");
        Console.WriteLine($"  Error rate: {metrics.ErrorRate():P0}");
        Console.WriteLine($"  Log entries: {logs.Count}");
    }

    /// <summary>
    /// Decide which code to review, applying a clear precedence so the
    /// documented inputs behave predictably:
    /// <list type="number">
    ///   <item>a readable file path passed as the first argument;</item>
    ///   <item>otherwise non-empty text piped in on stdin;</item>
    ///   <item>otherwise the bundled sample.</item>
    /// </list>
    /// Pure and side-effect free (I/O is read by the caller) so the precedence
    /// can be unit-tested without touching the console or the file system.
    /// </summary>
    /// <param name="args">Process arguments; <c>args[0]</c> may be a file path.</param>
    /// <param name="stdinText">Text already read from stdin, or <c>null</c> when stdin was not redirected.</param>
    /// <param name="sample">Fallback used when neither a file argument nor piped input is available.</param>
    internal static string ResolveInput(string[] args, string? stdinText, string sample)
    {
        if (args is { Length: > 0 } && !string.IsNullOrWhiteSpace(args[0]) && File.Exists(args[0]))
            return File.ReadAllText(args[0]);

        if (!string.IsNullOrWhiteSpace(stdinText))
            return stdinText;

        return sample;
    }

    /// <summary>
    /// Terminal handler — the actual model call that middleware wraps.
    /// </summary>
    private static async Task ModelCall(PromptPipelineContext ctx)
    {
        var response = await Prompt.Main.GetResponseAsync(
            ctx.RenderedPrompt,
            systemPrompt: "You are an expert code reviewer. Be precise and actionable.",
            maxRetries: 1);

        ctx.Response = response;
    }

    /// <summary>
    /// Sample code with intentional issues for the pipeline to find.
    /// </summary>
    private const string SampleCode =
        """
        public class UserService
        {
            private string connectionString = "Server=prod;Password=admin123;";

            public async Task<User> GetUser(int id)
            {
                // TODO: add caching
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();

                var cmd = new SqlCommand($"SELECT * FROM Users WHERE Id = {id}", conn);
                var reader = await cmd.ExecuteReaderAsync();

                if (reader.Read())
                {
                    return new User
                    {
                        Id = (int)reader["Id"],
                        Name = (string)reader["Name"],
                        Email = (string)reader["Email"]
                    };
                }

                return null;
            }

            public void DeleteUser(int id)
            {
                var conn = new SqlConnection(connectionString);
                conn.Open();
                var cmd = new SqlCommand($"DELETE FROM Users WHERE Id = {id}", conn);
                cmd.ExecuteNonQuery();
                // forgot to close connection
            }
        }
        """;
}
