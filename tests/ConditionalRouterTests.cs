using Prompt;
using System.Text.Json;
using Xunit;

namespace AgenticRecipes.Tests;

/// <summary>
/// Tests for Recipe 5: Conditional Router.
/// Mirrors recipes/conditional-router/Program.cs — the recipe-local
/// <c>RouteHandler</c> and <c>ClassifyResult</c> records and the <c>PromptRouter</c>
/// classify/fallback logic are re-declared here and driven deterministically.
/// The mirror reference above puts those mirrored records under the
/// <see cref="MirrorContractTests"/> field-signature contract so a field
/// rename/reorder in the recipe can't drift silently past a green suite.
/// </summary>
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

    // A keyword classifier in the spirit of the conditional-router demo's ClassifierModel:
    // it reads the customer message out of the rendered prompt and buckets it by keyword.
    // Keyword matching must be case-insensitive so "ERROR"/"Refund"/"Lawyer" route the same
    // as their lowercase forms — a case-sensitive Contains silently misrouted those to the
    // fallback route before this was fixed.
    private static Task<string> KeywordClassifier(string prompt, CancellationToken ct)
    {
        var marker = prompt.IndexOf("message: ", StringComparison.OrdinalIgnoreCase);
        var msg = marker >= 0 ? prompt[(marker + "message: ".Length)..] : prompt;
        var lower = msg.ToLowerInvariant();
        var route =
            lower.Contains("error") || lower.Contains("crash") ? "technical" :
            lower.Contains("refund") || lower.Contains("charge") ? "billing" :
            lower.Contains("lawyer") || lower.Contains("legal") ? "escalation" :
            "general";
        return MakeClassifier(route, 0.9, "keyword match");
    }

    [Theory]
    [InlineData("my app keeps CRASHING with an ERROR", "technical")]
    [InlineData("please issue a Refund for the Charge", "billing")]
    [InlineData("I am contacting my LAWYER", "escalation")]
    [InlineData("just saying hello", "general")]
    public async Task ClassifyAsync_KeywordClassifier_IsCaseInsensitive(string message, string expected)
    {
        var router = CreateRouter();
        var result = await router.ClassifyAsync(message, KeywordClassifier);
        Assert.Equal(expected, result.Route);
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
        // The real (low) confidence IS the fallback signal and must be preserved.
        Assert.Equal(0.5, result.Confidence);
        // But the reasoning argued for the REJECTED 'technical' route; pairing it with
        // the 'general' fallback would be misleading, so it must be replaced with an
        // honest fallback explanation (regression: fallback used to report "uncertain").
        Assert.DoesNotContain("uncertain", result.Reasoning);
        Assert.Contains("below threshold", result.Reasoning);
        Assert.Contains("technical", result.Reasoning); // names the route it declined
    }

    [Fact]
    public async Task ClassifyAsync_LowConfidenceOnFallbackRoute_KeepsItsOwnReasoning()
    {
        // The classifier legitimately picked the fallback route ('general') itself, just
        // below threshold. No OTHER route is being rejected, so its genuine reasoning
        // must be preserved rather than overwritten with a spurious fallback note.
        var router = CreateRouter(minConfidence: 0.7);
        var result = await router.ClassifyAsync("hello",
            async (prompt, ct) => await MakeClassifier("general", 0.4, "just a greeting"));

        Assert.Equal("general", result.Route);
        Assert.Equal(0.4, result.Confidence);
        Assert.Equal("just a greeting", result.Reasoning);
    }

    [Fact]
    public async Task ClassifyAsync_UnknownRoute_FallsBackToDefault()
    {
        var router = CreateRouter();
        var result = await router.ClassifyAsync("weird input",
            async (prompt, ct) => await MakeClassifier("nonexistent_route", 0.9, "bad route"));

        Assert.Equal("general", result.Route);
        // The classifier's 0.9 belonged to the rejected route — it must NOT be
        // reattributed to the fallback (regression: fallback used to report 0.9).
        Assert.Equal(0.0, result.Confidence);
        Assert.DoesNotContain("bad route", result.Reasoning);
    }

    [Fact]
    public async Task ClassifyAsync_UnknownRoute_OnRouteSelectedGetsZeroConfidence()
    {
        string? seenRoute = null;
        double seenConfidence = -1;
        var router = CreateRouter(onRoute: (r, c, _) => { seenRoute = r; seenConfidence = c; });

        await router.ClassifyAsync("weird input",
            async (prompt, ct) => await MakeClassifier("nonexistent_route", 0.9, "bad route"));

        // The observability hook must see the honest fallback signal, not the
        // discarded route's confidence.
        Assert.Equal("general", seenRoute);
        Assert.Equal(0.0, seenConfidence);
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
            ["technical"] = new("Technical", "You are an engineer.", 1),
            ["billing"] = new("Billing", "You are billing.", 2),
            ["general"] = new("General", "You are helpful.", 3),
            ["escalation"] = new("Escalation", "You escalate.", 0)
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
            ["technical"] = new("Technical", "engineer prompt", 1),
            ["billing"] = new("Billing", "billing prompt", 2),
            ["general"] = new("General", "general prompt", 3),
            ["escalation"] = new("Escalation", "escalation prompt", 0)
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
    public async Task RouteAsync_ChosenRouteHasNoHandler_UsesFallbackHandler()
    {
        var router = CreateRouter();
        // "technical" is a valid classification route but is intentionally NOT wired
        // up as a handler - the router must degrade to the fallback handler, not crash.
        var handlers = new Dictionary<string, RouteHandler>
        {
            ["general"] = new("General", "general prompt", 3)
        };

        var (classification, response) = await router.RouteAsync(
            "my app crashed",
            classifierFunc: async (prompt, ct) => await MakeClassifier("technical", 0.9, "crash"),
            branchFunc: (sys, msg, ct) => Task.FromResult($"Used: {sys}"),
            handlers: handlers);

        Assert.Contains("general prompt", response);
        // The fallback handler actually served the response, so the returned
        // classification must report the route that handled it (general), not the
        // handler-less route it was originally classified into (technical) - reported
        // route == executed handler. The substitution is recorded in the reasoning.
        Assert.Equal("general", classification.Route);
        Assert.Contains("technical", classification.Reasoning);
        Assert.Contains("fallback", classification.Reasoning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RouteAsync_ChosenRouteHasNoHandler_PreservesMeasuredConfidence()
    {
        var router = CreateRouter();
        // Only the fallback handler is wired; the classifier picks 'technical' at a
        // high confidence. The response is served by the fallback handler, and the
        // returned classification is re-pointed at 'general' - but the MEASURED
        // confidence (0.9) must survive, since it is a real signal about the classifier.
        var handlers = new Dictionary<string, RouteHandler>
        {
            ["general"] = new("General", "general prompt", 3)
        };

        var (classification, _) = await router.RouteAsync(
            "my app crashed",
            classifierFunc: async (prompt, ct) => await MakeClassifier("technical", 0.9, "crash"),
            branchFunc: (sys, msg, ct) => Task.FromResult("ok"),
            handlers: handlers);

        Assert.Equal("general", classification.Route);
        Assert.Equal(0.9, classification.Confidence);
    }

    [Fact]
    public async Task RouteAsync_NoHandlerAndNoFallbackHandler_ThrowsClearError()
    {
        var router = CreateRouter();
        var handlers = new Dictionary<string, RouteHandler>
        {
            ["billing"] = new("Billing", "billing prompt", 2)
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await router.RouteAsync(
                "my app crashed",
                classifierFunc: async (prompt, ct) => await MakeClassifier("technical", 0.9, "crash"),
                branchFunc: (sys, msg, ct) => Task.FromResult("unreachable"),
                handlers: handlers));

        // Clear, actionable message naming both the chosen and fallback route -
        // never a bare KeyNotFoundException.
        Assert.Contains("technical", ex.Message);
        Assert.Contains("general", ex.Message);
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

    // ── Routing-summary honesty ────────────────────────────────
    // The demo prints a "Routing Summary" (messages processed, distinct routes
    // used, escalation count, average confidence). Those numbers were previously
    // hardcoded strings that silently lie the moment the sample messages or the
    // classifier change. They are now computed from the real ClassifyResults; this
    // test pins that aggregation so a regression to hardcoded values is caught.
    [Fact]
    public async Task RoutingSummary_IsComputedFromActualClassifications()
    {
        var router = CreateRouter();

        // Mirror the recipe's simulated classifier for its four sample messages.
        Task<string> Classify(string prompt, CancellationToken ct)
        {
            if (prompt.Contains("error") || prompt.Contains("crash") || prompt.Contains("stack trace"))
                return MakeClassifier("technical", 0.92, "error/crash keywords");
            if (prompt.Contains("charge") || prompt.Contains("refund") || prompt.Contains("invoice") || prompt.Contains("bill"))
                return MakeClassifier("billing", 0.88, "billing keywords");
            if (prompt.Contains("lawyer") || prompt.Contains("legal") || prompt.Contains("sue"))
                return MakeClassifier("escalation", 0.95, "legal threat");
            return MakeClassifier("general", 0.75, "no strong signal");
        }

        var messages = new[]
        {
            "I'm getting a NullReferenceException crash with this stack trace.",
            "I was charged $49.99 but I cancelled; I want a refund.",
            "I'm contacting my lawyer and want my data deleted under GDPR.",
            "Does your product support integration with Slack?",
        };

        var classifications = new List<ClassifyResult>();
        foreach (var m in messages)
            classifications.Add(await router.ClassifyAsync(m, Classify));

        var routesUsed = classifications.Select(c => c.Route).Distinct()
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        var escalations = classifications.Count(c => c.Route == "escalation");
        var avgConfidence = classifications.Average(c => c.Confidence);

        Assert.Equal(4, classifications.Count);
        Assert.Equal(new[] { "billing", "escalation", "general", "technical" }, routesUsed);
        Assert.Equal(1, escalations);
        Assert.Equal(0.875, avgConfidence, 3); // (0.92+0.88+0.95+0.75)/4 → 87.5%
    }
}

// Supporting types needed for compilation
record RouteHandler(string Name, string SystemPrompt, int Priority);

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

            // Unknown route: discard the classifier's answer and its (irrelevant)
            // confidence/reasoning — don't attribute them to the fallback.
            if (!_options.Routes.Contains(route))
            {
                route = _options.FallbackRoute;
                confidence = 0.0;
                reasoning = "Classifier chose an unknown route; using fallback";
            }
            // Low confidence on an in-vocabulary route: keep the (real) confidence,
            // but replace the rejected route's reasoning with an honest fallback note.
            if (confidence < _options.MinConfidence && route != _options.FallbackRoute)
            {
                reasoning = $"Confidence {confidence:0.##} below threshold {_options.MinConfidence:0.##} for route '{route}'; using fallback";
                route = _options.FallbackRoute;
            }

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
        // Fall back to the fallback route's handler if the chosen route has none;
        // only throw a clear error if neither is registered (never a bare
        // KeyNotFoundException, per the graceful-degradation contract). When the
        // fallback handler actually serves the response, re-point the returned
        // classification at that route so reported route == executed handler.
        if (!handlers.TryGetValue(classification.Route, out var handler))
        {
            if (!handlers.TryGetValue(_options.FallbackRoute, out handler))
                throw new InvalidOperationException(
                    $"No handler registered for route '{classification.Route}' or fallback route '{_options.FallbackRoute}'.");
            classification = classification with
            {
                Route = _options.FallbackRoute,
                Reasoning = $"No handler for route '{classification.Route}'; " +
                            $"handled by fallback route '{_options.FallbackRoute}'"
            };
        }
        var response = await branchFunc(handler.SystemPrompt, message, ct);
        return (classification, response);
    }
}
