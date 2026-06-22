using Prompt;
using System.Text.Json;
using Xunit;

namespace AgenticRecipes.Tests;

public class ConditionalRouterTests
{
    private static PromptRouter CreateRouter(
        double minConfidence = 0.6,
        string fallback = "general",
        Action<string, double, string>? onRoute = null)
    {
        return new PromptRouter(new RouterOptions
        {
            Routes = new List<string> { "technical", "billing", "general", "escalation" },
            ClassifierPrompt = "Classify: {{message}}",
            FallbackRoute = fallback,
            MinConfidence = minConfidence,
            OnRouteSelected = onRoute
        });
    }

    private static Task<string> MakeClassifier(string route, double confidence, string reasoning)
    {
        return Task.FromResult(JsonSerializer.Serialize(new { route, confidence, reasoning }));
    }

    [Fact]
    public async Task ClassifyAsync_TechnicalRoute_ReturnsCorrectly()
    {
        var router = CreateRouter();
        var result = await router.ClassifyAsync("app crashes",
            async (prompt, ct) => await MakeClassifier("technical", 0.92, "crash keyword"));

        Assert.Equal("technical", result.Route);
        Assert.Equal(0.92, result.Confidence);
        Assert.Equal("crash keyword", result.Reasoning);
    }

    [Fact]
    public async Task ClassifyAsync_BillingRoute_ReturnsCorrectly()
    {
        var router = CreateRouter();
        var result = await router.ClassifyAsync("refund my charge",
            async (prompt, ct) => await MakeClassifier("billing", 0.88, "billing keywords"));

        Assert.Equal("billing", result.Route);
        Assert.Equal(0.88, result.Confidence);
    }

    [Fact]
    public async Task ClassifyAsync_EscalationRoute_ReturnsCorrectly()
    {
        var router = CreateRouter();
        var result = await router.ClassifyAsync("contacting my lawyer",
            async (prompt, ct) => await MakeClassifier("escalation", 0.95, "legal threat"));

        Assert.Equal("escalation", result.Route);
        Assert.Equal(0.95, result.Confidence);
    }

    [Fact]
    public async Task ClassifyAsync_LowConfidence_FallsBackToDefault()
    {
        var router = CreateRouter(minConfidence: 0.7);
        var result = await router.ClassifyAsync("something vague",
            async (prompt, ct) => await MakeClassifier("technical", 0.5, "uncertain"));

        Assert.Equal("general", result.Route); // Fell back due to low confidence
    }

    [Fact]
    public async Task ClassifyAsync_UnknownRoute_FallsBackToDefault()
    {
        var router = CreateRouter();
        var result = await router.ClassifyAsync("weird input",
            async (prompt, ct) => await MakeClassifier("nonexistent_route", 0.9, "bad route"));

        Assert.Equal("general", result.Route);
    }

    [Fact]
    public async Task ClassifyAsync_InvalidJson_FallsBackGracefully()
    {
        var router = CreateRouter();
        var result = await router.ClassifyAsync("anything",
            (prompt, ct) => Task.FromResult("this is not json at all"));

        Assert.Equal("general", result.Route);
        Assert.Equal(0.0, result.Confidence);
        Assert.Contains("Parse error", result.Reasoning);
    }

    // ── Structurally-malformed but syntactically VALID JSON ──────────────
    // A flaky classifier can return well-formed JSON that is missing a field
    // or carries the wrong type for one. These used to throw
    // KeyNotFoundException / InvalidOperationException (NOT JsonException), so
    // they escaped the parse-failure catch and crashed the router — violating
    // the README's "All failures are logged, never crash" contract.

    [Fact]
    public async Task ClassifyAsync_MissingRouteKey_FallsBackInsteadOfThrowing()
    {
        var router = CreateRouter();
        // Valid JSON, but no "route" property.
        var result = await router.ClassifyAsync("anything",
            (prompt, ct) => Task.FromResult("""{"confidence": 0.9, "reasoning": "no route field"}"""));

        Assert.Equal("general", result.Route);
    }

    [Fact]
    public async Task ClassifyAsync_ConfidenceAsString_FallsBackInsteadOfThrowing()
    {
        var router = CreateRouter();
        // "confidence" is a string, not a number — GetDouble() would have thrown.
        // An unparseable score is treated as no confidence → low-confidence fallback.
        var result = await router.ClassifyAsync("anything",
            (prompt, ct) => Task.FromResult("""{"route": "technical", "confidence": "high", "reasoning": "r"}"""));

        Assert.Equal("general", result.Route);
        Assert.Equal(0.0, result.Confidence);
    }

    [Fact]
    public async Task ClassifyAsync_ConfidenceNull_FallsBackInsteadOfThrowing()
    {
        var router = CreateRouter();
        var result = await router.ClassifyAsync("anything",
            (prompt, ct) => Task.FromResult("""{"route": "technical", "confidence": null, "reasoning": "r"}"""));

        Assert.Equal("general", result.Route);
        Assert.Equal(0.0, result.Confidence);
    }

    [Fact]
    public async Task ClassifyAsync_MissingReasoning_DefaultsToEmptyNotThrow()
    {
        var router = CreateRouter();
        // Valid route + confidence, but no "reasoning" key: should keep the route
        // and report an empty reasoning rather than throwing.
        var result = await router.ClassifyAsync("anything",
            (prompt, ct) => Task.FromResult("""{"route": "technical", "confidence": 0.92}"""));

        Assert.Equal("technical", result.Route);
        Assert.Equal(0.92, result.Confidence);
        Assert.Equal("", result.Reasoning);
    }

    [Fact]
    public async Task ClassifyAsync_RendersMessageInPrompt()
    {
        var router = CreateRouter();
        string? capturedPrompt = null;

        await router.ClassifyAsync("my specific message",
            async (prompt, ct) =>
            {
                capturedPrompt = prompt;
                return await MakeClassifier("general", 0.8, "ok");
            });

        Assert.Contains("my specific message", capturedPrompt!);
        Assert.Contains("Classify:", capturedPrompt!);
    }

    [Fact]
    public async Task ClassifyAsync_OnRouteSelected_IsCalled()
    {
        string? selectedRoute = null;
        double selectedConfidence = 0;
        string? selectedReasoning = null;

        var router = CreateRouter(onRoute: (route, conf, reason) =>
        {
            selectedRoute = route;
            selectedConfidence = conf;
            selectedReasoning = reason;
        });

        await router.ClassifyAsync("billing issue",
            async (prompt, ct) => await MakeClassifier("billing", 0.85, "billing detected"));

        Assert.Equal("billing", selectedRoute);
        Assert.Equal(0.85, selectedConfidence);
        Assert.Equal("billing detected", selectedReasoning);
    }

    [Fact]
    public async Task ClassifyAsync_ExactMinConfidence_DoesNotFallback()
    {
        var router = CreateRouter(minConfidence: 0.6);
        var result = await router.ClassifyAsync("edge case",
            async (prompt, ct) => await MakeClassifier("technical", 0.6, "at threshold"));

        Assert.Equal("technical", result.Route);
    }

    [Fact]
    public async Task ClassifyAsync_JustBelowMinConfidence_Fallbacks()
    {
        var router = CreateRouter(minConfidence: 0.6);
        var result = await router.ClassifyAsync("edge case",
            async (prompt, ct) => await MakeClassifier("technical", 0.59, "just below"));

        Assert.Equal("general", result.Route);
    }

    [Fact]
    public async Task RouteAsync_FullPipeline_ClassifiesAndHandles()
    {
        var router = CreateRouter();
        var handlers = new Dictionary<string, RouteHandler>
        {
            ["technical"] = new("Technical", "You are an engineer.", 1, 0),
            ["billing"] = new("Billing", "You are billing.", 2, 0),
            ["general"] = new("General", "You are helpful.", 3, 0),
            ["escalation"] = new("Escalation", "You escalate.", 0, 0)
        };

        var (classification, response) = await router.RouteAsync(
            "my app crashed",
            classifierFunc: async (prompt, ct) => await MakeClassifier("technical", 0.9, "crash"),
            branchFunc: (sys, msg, ct) => Task.FromResult($"Handled by: {sys[..15]}"),
            handlers: handlers);

        Assert.Equal("technical", classification.Route);
        Assert.Contains("You are an engi", response);
    }

    [Fact]
    public async Task RouteAsync_FallbackRoute_UsesCorrectHandler()
    {
        var router = CreateRouter(minConfidence: 0.8);
        var handlers = new Dictionary<string, RouteHandler>
        {
            ["technical"] = new("Technical", "engineer prompt", 1, 0),
            ["billing"] = new("Billing", "billing prompt", 2, 0),
            ["general"] = new("General", "general prompt", 3, 0),
            ["escalation"] = new("Escalation", "escalation prompt", 0, 0)
        };

        var (classification, response) = await router.RouteAsync(
            "unclear message",
            classifierFunc: async (prompt, ct) => await MakeClassifier("technical", 0.3, "low conf"),
            branchFunc: (sys, msg, ct) => Task.FromResult($"Used: {sys}"),
            handlers: handlers);

        Assert.Equal("general", classification.Route);
        Assert.Contains("general prompt", response);
    }

    [Fact]
    public async Task ClassifyAsync_CustomFallbackRoute_UsesConfigured()
    {
        var router = new PromptRouter(new RouterOptions
        {
            Routes = new List<string> { "a", "b", "fallback_custom" },
            ClassifierPrompt = "{{message}}",
            FallbackRoute = "fallback_custom",
            MinConfidence = 0.5
        });

        var result = await router.ClassifyAsync("test",
            (prompt, ct) => Task.FromResult("broken json}}}"));

        Assert.Equal("fallback_custom", result.Route);
    }

    [Fact]
    public async Task ClassifyAsync_EmptyMessage_StillRoutes()
    {
        var router = CreateRouter();
        var result = await router.ClassifyAsync("",
            async (prompt, ct) => await MakeClassifier("general", 0.7, "empty input"));

        Assert.Equal("general", result.Route);
        Assert.Equal(0.7, result.Confidence);
    }

    [Fact]
    public async Task ClassifyAsync_Cancellation_Propagates()
    {
        var router = CreateRouter();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await router.ClassifyAsync("test",
                async (prompt, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return await MakeClassifier("general", 0.8, "ok");
                }, cts.Token));
    }
}

// Supporting types needed for compilation
record RouteHandler(string Name, string SystemPrompt, int Priority, double Confidence);

record RouterOptions
{
    public List<string> Routes { get; init; } = new();
    public string ClassifierPrompt { get; init; } = "";
    public string FallbackRoute { get; init; } = "general";
    public double MinConfidence { get; init; } = 0.5;
    public Action<string, double, string>? OnRouteSelected { get; init; }
}

record ClassifyResult(string Route, double Confidence, string Reasoning);

class PromptRouter
{
    private readonly RouterOptions _options;

    public PromptRouter(RouterOptions options) => _options = options;

    public async Task<ClassifyResult> ClassifyAsync(
        string message,
        Func<string, CancellationToken, Task<string>> classifierFunc,
        CancellationToken ct = default)
    {
        var rendered = _options.ClassifierPrompt.Replace("{{message}}", message);
        var raw = await classifierFunc(rendered, ct);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // Defensive reads: syntactically valid JSON can still be missing a field or
            // have the wrong type (confidence as a string/null). GetProperty/GetDouble
            // would throw KeyNotFoundException/InvalidOperationException (not JsonException),
            // escaping the catch and crashing the router. TryGet* keeps it graceful.
            var route = root.TryGetProperty("route", out var routeEl) && routeEl.ValueKind == JsonValueKind.String
                ? routeEl.GetString()!
                : _options.FallbackRoute;
            var confidence = root.TryGetProperty("confidence", out var confEl) && confEl.ValueKind == JsonValueKind.Number
                ? confEl.GetDouble()
                : 0.0;
            var reasoning = root.TryGetProperty("reasoning", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                ? reasonEl.GetString()!
                : "";

            if (!_options.Routes.Contains(route))
                route = _options.FallbackRoute;
            if (confidence < _options.MinConfidence)
                route = _options.FallbackRoute;

            _options.OnRouteSelected?.Invoke(route, confidence, reasoning);
            return new ClassifyResult(route, confidence, reasoning);
        }
        catch (JsonException)
        {
            _options.OnRouteSelected?.Invoke(_options.FallbackRoute, 0.0, "Classification parse failed; using fallback");
            return new ClassifyResult(_options.FallbackRoute, 0.0, "Parse error");
        }
    }

    public async Task<(ClassifyResult Classification, string Response)> RouteAsync(
        string message,
        Func<string, CancellationToken, Task<string>> classifierFunc,
        Func<string, string, CancellationToken, Task<string>> branchFunc,
        Dictionary<string, RouteHandler> handlers,
        CancellationToken ct = default)
    {
        var classification = await ClassifyAsync(message, classifierFunc, ct);
        var handler = handlers[classification.Route];
        var response = await branchFunc(handler.SystemPrompt, message, ct);
        return (classification, response);
    }
}
