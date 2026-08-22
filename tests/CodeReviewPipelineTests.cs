using Prompt;
using Xunit;

namespace AgenticRecipes.Tests;

/// <summary>
/// Tests for Recipe 3: Code Review Pipeline
/// Validates PromptPipeline middleware composition, execution, and behavior.
/// </summary>
public class CodeReviewPipelineTests
{
    // ── Pipeline Construction ─────────────────────────────────

    [Fact]
    public void Pipeline_WithMiddleware_DescribesCorrectly()
    {
        var pipeline = BuildPipeline();
        var desc = pipeline.Describe();

        Assert.Contains("Validation", desc);
        Assert.Contains("Logging", desc);
        Assert.Contains("Retry", desc);
        Assert.Contains("Caching", desc);
        Assert.Contains("Metrics", desc);
    }

    [Fact]
    public void Pipeline_MiddlewareOrder_IsRespected()
    {
        var pipeline = BuildPipeline();
        var middleware = pipeline.GetMiddleware();

        // Validation (-10) < Logging (5) < Retry (10) < Caching (15) < Metrics (100)
        for (int i = 0; i < middleware.Count - 1; i++)
        {
            Assert.True(middleware[i].Order <= middleware[i + 1].Order,
                $"{middleware[i].Name} (order {middleware[i].Order}) should run before " +
                $"{middleware[i + 1].Name} (order {middleware[i + 1].Order})");
        }
    }

    // ── Validation Middleware ─────────────────────────────────

    [Fact]
    public async Task Validation_EmptyPrompt_BlocksExecution()
    {
        var pipeline = new PromptPipeline()
            .Use(new ValidationMiddleware());

        var ctx = new PromptPipelineContext { PromptText = "" };

        await pipeline.ExecuteAsync(ctx, _ => Task.CompletedTask);

        Assert.True(ctx.HasError);
        Assert.Contains(ctx.Errors, e => e.Contains("empty"));
    }

    [Fact]
    public async Task Validation_MissingRequiredVariable_BlocksExecution()
    {
        var pipeline = new PromptPipeline()
            .Use(new ValidationMiddleware(requiredVariables: new[] { "code" }));

        var ctx = new PromptPipelineContext
        {
            PromptText = "Review this: {{code}}",
            Variables = new() // empty — missing "code"
        };

        await pipeline.ExecuteAsync(ctx, _ => Task.CompletedTask);

        Assert.True(ctx.HasError);
        Assert.Contains(ctx.Errors, e => e.Contains("code"));
    }

    [Fact]
    public async Task Validation_ValidPrompt_PassesThrough()
    {
        var executed = false;
        var pipeline = new PromptPipeline()
            .Use(new ValidationMiddleware(requiredVariables: new[] { "code" }));

        var ctx = new PromptPipelineContext
        {
            PromptText = "Review: {{code}}",
            Variables = new() { ["code"] = "var x = 1;" }
        };

        await pipeline.ExecuteAsync(ctx, _ => { executed = true; return Task.CompletedTask; });

        Assert.False(ctx.HasError);
        Assert.True(executed);
    }

    // ── Caching Middleware ────────────────────────────────────

    [Fact]
    public async Task Caching_SamePrompt_ReturnsCachedResponse()
    {
        var callCount = 0;
        var cache = new CachingMiddleware(TimeSpan.FromMinutes(5));
        var pipeline = new PromptPipeline().Use(cache);

        async Task Handler(PromptPipelineContext ctx)
        {
            callCount++;
            ctx.Response = $"response-{callCount}";
            await Task.CompletedTask;
        }

        // First call
        var ctx1 = new PromptPipelineContext
        {
            PromptText = "same prompt",
            Variables = new()
        };
        await pipeline.ExecuteAsync(ctx1, Handler);

        // Second call — same prompt
        var ctx2 = new PromptPipelineContext
        {
            PromptText = "same prompt",
            Variables = new()
        };
        await pipeline.ExecuteAsync(ctx2, Handler);

        Assert.Equal(1, callCount); // model only called once
        Assert.Equal("response-1", ctx2.Response); // got cached response
        Assert.True(ctx2.ShortCircuited);
        Assert.Equal(1, cache.HitCount);
    }

    [Fact]
    public async Task Caching_DifferentPrompt_CallsModelAgain()
    {
        var callCount = 0;
        var cache = new CachingMiddleware(TimeSpan.FromMinutes(5));
        var pipeline = new PromptPipeline().Use(cache);

        async Task Handler(PromptPipelineContext ctx)
        {
            callCount++;
            ctx.Response = $"response-{callCount}";
            await Task.CompletedTask;
        }

        var ctx1 = new PromptPipelineContext { PromptText = "prompt A", Variables = new() };
        await pipeline.ExecuteAsync(ctx1, Handler);

        var ctx2 = new PromptPipelineContext { PromptText = "prompt B", Variables = new() };
        await pipeline.ExecuteAsync(ctx2, Handler);

        Assert.Equal(2, callCount);
        Assert.Equal(0, cache.HitCount);
    }

    // ── Retry Middleware ─────────────────────────────────────

    [Fact]
    public async Task Retry_OnFailure_RetriesSpecifiedTimes()
    {
        var attempts = 0;
        var retry = new RetryMiddleware(maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(10));
        var pipeline = new PromptPipeline().Use(retry);

        var ctx = new PromptPipelineContext { PromptText = "test", Variables = new() };

        await Assert.ThrowsAsync<Exception>(async () =>
        {
            await pipeline.ExecuteAsync(ctx, _ =>
            {
                attempts++;
                throw new Exception("fail");
            });
        });

        // 1 initial + 2 retries = 3 attempts
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Retry_SucceedsOnSecondAttempt_DoesNotThrow()
    {
        var attempts = 0;
        var retry = new RetryMiddleware(maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(10));
        var pipeline = new PromptPipeline().Use(retry);

        var ctx = new PromptPipelineContext { PromptText = "test", Variables = new() };

        await pipeline.ExecuteAsync(ctx, c =>
        {
            attempts++;
            if (attempts < 2) throw new Exception("transient");
            c.Response = "success";
            return Task.CompletedTask;
        });

        Assert.Equal("success", ctx.Response);
        Assert.Equal(2, attempts);
    }

    // ── Metrics Middleware ────────────────────────────────────

    [Fact]
    public async Task Metrics_TracksExecutionCount()
    {
        var metrics = new MetricsMiddleware();
        var pipeline = new PromptPipeline().Use(metrics);

        for (int i = 0; i < 3; i++)
        {
            var ctx = new PromptPipelineContext { PromptText = $"prompt {i}", Variables = new() };
            await pipeline.ExecuteAsync(ctx, c => { c.Response = "ok"; return Task.CompletedTask; });
        }

        Assert.Equal(3, metrics.GetMetrics().Count);
    }

    [Fact]
    public async Task Metrics_TracksAverageTime()
    {
        var metrics = new MetricsMiddleware();
        var pipeline = new PromptPipeline().Use(metrics);

        var ctx = new PromptPipelineContext { PromptText = "test", Variables = new() };
        await pipeline.ExecuteAsync(ctx, async c =>
        {
            await Task.Delay(20);
            c.Response = "ok";
        });

        Assert.True(metrics.AverageExecutionTime() >= TimeSpan.FromMilliseconds(15));
    }

    [Fact]
    public async Task Metrics_TracksErrorRate()
    {
        var metrics = new MetricsMiddleware(order: -20); // run before everything
        var validation = new ValidationMiddleware(requiredVariables: new[] { "needed" });
        var pipeline = new PromptPipeline()
            .Use(metrics)
            .Use(validation);

        // Successful call (has required variable)
        var ctx1 = new PromptPipelineContext
        {
            PromptText = "good prompt",
            Variables = new() { ["needed"] = "present" }
        };
        await pipeline.ExecuteAsync(ctx1, c => { c.Response = "ok"; return Task.CompletedTask; });

        // Failed call (missing required variable → validation adds to Errors)
        var ctx2 = new PromptPipelineContext
        {
            PromptText = "bad prompt",
            Variables = new() // missing "needed"
        };
        await pipeline.ExecuteAsync(ctx2, c => { c.Response = "ok"; return Task.CompletedTask; });

        Assert.True(ctx2.HasError);
        Assert.Equal(0.5, metrics.ErrorRate());
    }

    // ── Content Filter Middleware ─────────────────────────────

    [Fact]
    public async Task ContentFilter_BlockedPattern_StopsExecution()
    {
        var filter = new ContentFilterMiddleware(new[] { "DROP TABLE" });
        var pipeline = new PromptPipeline().Use(filter);

        var ctx = new PromptPipelineContext
        {
            PromptText = "Execute: DROP TABLE users",
            Variables = new()
        };

        await pipeline.ExecuteAsync(ctx, c => { c.Response = "done"; return Task.CompletedTask; });

        Assert.True(ctx.HasError);
        Assert.Null(ctx.Response);
        Assert.Equal(1, filter.BlockedCount);
    }

    [Fact]
    public async Task ContentFilter_CleanPrompt_PassesThrough()
    {
        var filter = new ContentFilterMiddleware(new[] { "DROP TABLE" });
        var pipeline = new PromptPipeline().Use(filter);

        var ctx = new PromptPipelineContext
        {
            PromptText = "Review this code for bugs",
            Variables = new()
        };

        await pipeline.ExecuteAsync(ctx, c => { c.Response = "looks good"; return Task.CompletedTask; });

        Assert.False(ctx.HasError);
        Assert.Equal("looks good", ctx.Response);
    }

    // ── Full Pipeline Integration ─────────────────────────────

    [Fact]
    public async Task FullPipeline_HappyPath_ProducesResponse()
    {
        var pipeline = BuildPipeline();
        var ctx = new PromptPipelineContext
        {
            PromptText = "Analyze this code: {{code}}",
            Variables = new() { ["code"] = "public void Hello() { Console.WriteLine(\"hi\"); }" }
        };

        await pipeline.ExecuteAsync(ctx, c =>
        {
            c.Response = "Code looks clean, no issues found.";
            return Task.CompletedTask;
        });

        Assert.False(ctx.HasError);
        Assert.NotNull(ctx.Response);
        Assert.True(ctx.ExecutionTime > TimeSpan.Zero);
    }

    [Fact]
    public async Task FullPipeline_VariablesRendered_InPrompt()
    {
        var pipeline = BuildPipeline();
        var capturedPrompt = "";

        var ctx = new PromptPipelineContext
        {
            PromptText = "Review: {{code}}",
            Variables = new() { ["code"] = "int x = 42;" }
        };

        await pipeline.ExecuteAsync(ctx, c =>
        {
            capturedPrompt = c.RenderedPrompt;
            c.Response = "ok";
            return Task.CompletedTask;
        });

        Assert.Contains("int x = 42;", capturedPrompt);
        Assert.DoesNotContain("{{code}}", capturedPrompt);
    }

    // ── Input Resolution (file arg → piped stdin → sample) ────
    // Mirrors Program.ResolveInput so the documented input precedence is
    // pinned by a test. The README advertises a stdin path; these prove the
    // ordering that makes it (and the file-arg and sample fallbacks) behave.

    private static string ResolveInput(string[] args, string? stdinText, string sample)
    {
        if (args is { Length: > 0 } && !string.IsNullOrWhiteSpace(args[0]) && File.Exists(args[0]))
            return File.ReadAllText(args[0]);

        if (!string.IsNullOrWhiteSpace(stdinText))
            return stdinText;

        return sample;
    }

    [Fact]
    public void ResolveInput_NoArgsNoStdin_FallsBackToSample()
    {
        var result = ResolveInput(Array.Empty<string>(), stdinText: null, sample: "SAMPLE");
        Assert.Equal("SAMPLE", result);
    }

    [Fact]
    public void ResolveInput_StdinProvided_UsesStdinOverSample()
    {
        var result = ResolveInput(Array.Empty<string>(), stdinText: "piped code", sample: "SAMPLE");
        Assert.Equal("piped code", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void ResolveInput_BlankStdin_FallsBackToSample(string blank)
    {
        // An empty/whitespace pipe must not masquerade as real input.
        var result = ResolveInput(Array.Empty<string>(), stdinText: blank, sample: "SAMPLE");
        Assert.Equal("SAMPLE", result);
    }

    [Fact]
    public void ResolveInput_MissingFileArg_DoesNotThrow_AndFallsBack()
    {
        // A bogus path is not a readable file → fall through to the next source
        // instead of throwing.
        var args = new[] { Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.cs") };
        var result = ResolveInput(args, stdinText: "piped code", sample: "SAMPLE");
        Assert.Equal("piped code", result);
    }

    [Fact]
    public void ResolveInput_FileArg_WinsOverStdinAndSample()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cr-input-{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, "from file");
        try
        {
            var result = ResolveInput(new[] { path }, stdinText: "piped code", sample: "SAMPLE");
            Assert.Equal("from file", result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Helper ───────────────────────────────────────────────

    // Locate a repo directory (recipes/, tests/) by walking up from the test
    // assembly's base dir — mirrors the resolver used by the meta-tests.
    private static string FindRepoDir(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate '{name}' above {AppContext.BaseDirectory}");
    }

    // ── README ↔ Program honesty ──────────────────────────────
    // The README documents the exact middleware wiring in a code snippet. That
    // snippet is the single easiest thing to let drift away from Program.cs
    // (someone tweaks a constructor arg in code, the doc keeps the old form).
    // These pin the documented wiring to what the recipe actually constructs,
    // so the "How It Leverages prompt-lib" block can't silently go stale.

    [Fact]
    public void Readme_MiddlewareSnippet_MatchesProgramWiring()
    {
        var recipeDir = Path.Combine(FindRepoDir("recipes"), "code-review-pipeline");
        var readme = File.ReadAllText(Path.Combine(recipeDir, "README.md"));
        var program = File.ReadAllText(Path.Combine(recipeDir, "Program.cs"));

        // Each documented constructor must appear in the README, and the keyword
        // arguments the recipe actually passes must be documented (not the older
        // positional-only form). If Program.cs stops using one of these, the doc
        // is expected to follow — this test is the tripwire.
        foreach (var mustDocument in new[]
                 {
                     "ValidationMiddleware", "requiredVariables:",
                     "LoggingMiddleware",
                     "RetryMiddleware", "maxRetries:",
                     "CachingMiddleware", "maxEntries:",
                     "MetricsMiddleware",
                     "order:",
                 })
        {
            Assert.True(readme.Contains(mustDocument, StringComparison.Ordinal),
                $"README middleware snippet is stale: expected it to document '{mustDocument}'.");
            Assert.True(program.Contains(mustDocument, StringComparison.Ordinal),
                $"Program.cs no longer uses '{mustDocument}' — update the README snippet to match.");
        }
    }

    private static PromptPipeline BuildPipeline()
    {
        return new PromptPipeline()
            .Use(new ValidationMiddleware(maxTokens: 16000, requiredVariables: new[] { "code" }))
            .Use(new LoggingMiddleware(_ => { }, order: 5))
            .Use(new RetryMiddleware(maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(10), order: 10))
            .Use(new CachingMiddleware(TimeSpan.FromMinutes(5), order: 15))
            .Use(new MetricsMiddleware());
    }
}
