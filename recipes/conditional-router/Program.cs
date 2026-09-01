using Prompt;
using System.Text.Json;

// ──────────────────────────────────────────────────────────────
// Conditional Router Recipe
// Pattern: PromptRouter (classify → branch → merge)
//
// A classifier examines the user's input, assigns it to one of
// several categories, then routes it to a specialized handler
// pipeline. Each branch has its own system prompt, tools, and
// response strategy. Results are merged into a unified output.
// ──────────────────────────────────────────────────────────────

// 1. Define route handlers — each is a specialized mini-pipeline
var routes = new Dictionary<string, RouteHandler>
{
    ["technical"] = new RouteHandler(
        Name: "Technical Support",
        SystemPrompt: @"You are a senior software engineer. Diagnose the technical issue,
provide step-by-step resolution, and suggest preventive measures.
Format: ## Diagnosis\n## Steps\n## Prevention",
        Priority: 1),

    ["billing"] = new RouteHandler(
        Name: "Billing & Accounts",
        SystemPrompt: @"You are a billing specialist. Review the account concern,
explain charges or policies clearly, and offer resolution options.
Format: ## Issue Summary\n## Explanation\n## Options",
        Priority: 2),

    ["general"] = new RouteHandler(
        Name: "General Inquiry",
        SystemPrompt: @"You are a friendly customer service representative.
Answer the question helpfully and concisely. If the question
needs specialist routing, say so clearly.",
        Priority: 3),

    ["escalation"] = new RouteHandler(
        Name: "Escalation Required",
        SystemPrompt: @"You are an escalation coordinator. The issue requires
human intervention. Summarize the issue, assess urgency (low/medium/high/critical),
and specify which team should handle it.
Format: ## Summary\n## Urgency\n## Assigned Team\n## Context for Agent",
        Priority: 0)
};

// 2. Build the router
var router = new PromptRouter(new RouterOptions
{
    Routes = routes.Keys.ToList(),
    ClassifierPrompt = @"Classify the following customer message into exactly ONE category.
Categories:
- technical: software bugs, errors, crashes, configuration, integration issues
- billing: charges, invoices, refunds, subscription, payment methods, pricing
- general: product questions, feature requests, how-to, feedback
- escalation: threats, legal mentions, repeated failures, urgent outages, safety

Respond with ONLY a JSON object: {""route"": ""<category>"", ""confidence"": <0.0-1.0>, ""reasoning"": ""<brief>""}

Message: {{message}}",
    FallbackRoute = "general",
    MinConfidence = 0.6,
    OnRouteSelected = (route, confidence, reasoning) =>
    {
        Console.WriteLine($"  🔀 Routed to: {route} (confidence: {confidence:P0})");
        Console.WriteLine($"     Reasoning: {reasoning}");
    }
});

// 3. Simulate the classifier model
Task<string> ClassifierModel(string prompt, CancellationToken ct)
{
    // Extract the message from the rendered prompt. Guard the marker lookup: if the
    // template ever changes and "Message: " is absent, IndexOf returns -1 and a naive
    // `+ 9` would slice from an arbitrary offset (or throw) — fall back to the whole prompt.
    var marker = prompt.IndexOf("Message: ", StringComparison.Ordinal);
    var message = (marker >= 0 ? prompt[(marker + "Message: ".Length)..] : prompt).Trim();

    // Simulated classification logic. Match keywords case-insensitively — a real
    // classifier doesn't care whether the customer typed "ERROR" or "Refund", and a
    // case-sensitive Contains would silently misroute those to the general handler.
    var lower = message.ToLowerInvariant();
    // Check the ESCALATION (safety) lane FIRST, honoring its Priority: 0. A message can
    // legitimately mention both a technical symptom AND a legal threat ("your app keeps
    // crashing and I'm calling my lawyer"); if technical/billing were tested first, that
    // message would route to 'technical' and the legal escalation — the whole reason the
    // safety lane exists — would be silently lost. Highest-priority category wins the tie.
    if (lower.Contains("lawyer") || lower.Contains("legal") || lower.Contains("sue"))
        return Task.FromResult(JsonSerializer.Serialize(new { route = "escalation", confidence = 0.95, reasoning = "Legal threat detected — requires human review" }));
    if (lower.Contains("error") || lower.Contains("crash") || lower.Contains("stack trace"))
        return Task.FromResult(JsonSerializer.Serialize(new { route = "technical", confidence = 0.92, reasoning = "Contains error/crash keywords indicating a software issue" }));
    if (lower.Contains("charge") || lower.Contains("refund") || lower.Contains("invoice") || lower.Contains("bill"))
        return Task.FromResult(JsonSerializer.Serialize(new { route = "billing", confidence = 0.88, reasoning = "Financial/billing keywords detected" }));
    return Task.FromResult(JsonSerializer.Serialize(new { route = "general", confidence = 0.75, reasoning = "No strong category signal; routing to general" }));
}

// 4. Simulate the branch handler model
Task<string> BranchModel(string systemPrompt, string message, CancellationToken ct)
{
    // Simulated responses per route
    if (systemPrompt.Contains("software engineer"))
    {
        return Task.FromResult("""
            ## Diagnosis
            The NullReferenceException on line 42 indicates the `UserService` is not being 
            injected into the controller. The DI container registration is likely missing.

            ## Steps
            1. Open `Program.cs` (or `Startup.cs` for older .NET)
            2. Add: `builder.Services.AddScoped<IUserService, UserService>();`
            3. Rebuild and verify the endpoint returns 200

            ## Prevention
            - Add a startup integration test that resolves all registered services
            - Enable nullable reference types (`<Nullable>enable</Nullable>`) to catch null issues at compile time
            """);
    }
    if (systemPrompt.Contains("billing specialist"))
    {
        return Task.FromResult("""
            ## Issue Summary
            Customer was charged $49.99 on June 1 for the Pro plan despite requesting 
            cancellation on May 28.

            ## Explanation
            Cancellation requests submitted after the billing cycle lock (3 days before renewal)
            are processed for the *next* cycle. The May 28 request was 3 days before the 
            June 1 renewal — right at the cutoff boundary.

            ## Options
            1. **Full refund** — process immediately ($49.99 credit, 3-5 business days)
            2. **Extend access** — keep Pro through June 30, cancel after
            3. **Downgrade** — switch to Free plan now, pro-rate refund of $33.33
            """);
    }
    if (systemPrompt.Contains("escalation coordinator"))
    {
        return Task.FromResult("""
            ## Summary
            Customer has mentioned legal action regarding data handling practices.
            They reference GDPR Article 17 (right to erasure) and claim repeated 
            failed attempts to delete their account data.

            ## Urgency
            **HIGH** — Legal threat + data privacy regulation cited

            ## Assigned Team
            Legal & Compliance (cc: Data Protection Officer)

            ## Context for Agent
            - Customer has made 3 prior support contacts about this issue
            - Account flagged for GDPR data subject request on May 15
            - Request appears legitimate; previous contacts show no resolution
            - Recommend immediate acknowledgment + 72-hour resolution commitment
            """);
    }
    return Task.FromResult("""
        Thank you for reaching out! Here's what I can help with:

        Our product supports integration with most major platforms. You can find 
        setup guides at docs.example.com/integrations.

        If you need a feature that isn't available yet, I'd recommend submitting 
        a feature request at feedback.example.com — our team reviews these weekly.

        Is there anything else I can help you with?
        """);
}

// 5. Run the demo with multiple inputs
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Conditional Router Recipe (PromptRouter)");
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine();

var testMessages = new[]
{
    "I'm getting a NullReferenceException crash with this stack trace when I try to load the dashboard. It started after the latest update.",
    "I was charged $49.99 but I cancelled my subscription last week. I want a refund.",
    "Your service has lost my data THREE times. I'm contacting my lawyer about this. I want every piece of my data deleted under GDPR Article 17.",
    "Hey, does your product support integration with Slack? I'd like to get notifications there."
};

var classifications = new List<ClassifyResult>();

foreach (var message in testMessages)
{
    Console.WriteLine("───────────────────────────────────────────────────────");
    Console.WriteLine($"📨 Customer: {message[..Math.Min(80, message.Length)]}...");
    Console.WriteLine();

    // Phase 1: Classify
    var classifyResult = await router.ClassifyAsync(message, ClassifierModel);
    classifications.Add(classifyResult);

    // Phase 2: Route to handler
    var handler = routes[classifyResult.Route];
    Console.WriteLine($"  📌 Handler: {handler.Name} (priority: {handler.Priority})");
    Console.WriteLine();

    // Phase 3: Execute the branch
    var response = await BranchModel(handler.SystemPrompt, message, CancellationToken.None);

    Console.WriteLine("  💬 Response:");
    foreach (var line in response.Split('\n'))
        Console.WriteLine($"     {line.TrimStart()}");
    Console.WriteLine();
}

// 6. Show routing summary
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Routing Summary");
Console.WriteLine("═══════════════════════════════════════════════════════");
// Compute the summary from the actual classifications rather than hardcoding it,
// so it stays honest if the sample messages (or the classifier) change.
var routesUsed = classifications
    .Select(c => c.Route)
    .Distinct()
    .OrderBy(r => r, StringComparer.Ordinal)
    .ToList();
var escalations = classifications.Count(c => c.Route == "escalation");
var avgConfidence = classifications.Count > 0
    ? classifications.Average(c => c.Confidence)
    : 0.0;
Console.WriteLine($"  Messages processed: {classifications.Count}");
Console.WriteLine($"  Routes used: {string.Join(", ", routesUsed)}");
Console.WriteLine($"  Escalations: {escalations}");
Console.WriteLine($"  Avg confidence: {avgConfidence:P1}");
Console.WriteLine();
Console.WriteLine("Pattern: classify → branch → handle");
Console.WriteLine("Each branch has specialized prompts, context, and response format.");

// ── Supporting types ────────────────────────────────────────

/// <summary>Route handler configuration.</summary>
record RouteHandler(string Name, string SystemPrompt, int Priority);

/// <summary>Router configuration.</summary>
record RouterOptions
{
    public List<string> Routes { get; init; } = new();
    public string ClassifierPrompt { get; init; } = "";
    public string FallbackRoute { get; init; } = "general";
    public double MinConfidence { get; init; } = 0.5;
    public Action<string, double, string>? OnRouteSelected { get; init; }
}

/// <summary>Result of classification.</summary>
record ClassifyResult(string Route, double Confidence, string Reasoning);

/// <summary>
/// Conditional router: classifies input into a category, then delegates
/// to the appropriate handler pipeline. Falls back gracefully on low confidence.
/// </summary>
class PromptRouter
{
    private readonly RouterOptions _options;

    public PromptRouter(RouterOptions options) => _options = options;

    public async Task<ClassifyResult> ClassifyAsync(
        string message,
        Func<string, CancellationToken, Task<string>> classifierFunc,
        CancellationToken ct = default)
    {
        // Render the classifier prompt with the message
        var rendered = _options.ClassifierPrompt.Replace("{{message}}", message);

        // Call the classifier
        var raw = await classifierFunc(rendered, ct);

        // Parse the JSON response
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // Read fields defensively: a real classifier can return syntactically valid
            // JSON that's missing a field or has the wrong type for one (e.g. confidence
            // as the string "high" or null). GetProperty(...)/GetDouble() would throw
            // KeyNotFoundException/InvalidOperationException for those — which is NOT a
            // JsonException, so it would escape the catch below and crash the router,
            // breaking the "never crash; fall back" contract. TryGet* keeps it graceful.
            var route = root.TryGetProperty("route", out var routeEl) && routeEl.ValueKind == JsonValueKind.String
                ? routeEl.GetString()!
                : _options.FallbackRoute;
            // Missing/non-numeric confidence → 0.0, which trips the low-confidence
            // fallback below (treat an unparseable score as "no confidence").
            var confidence = root.TryGetProperty("confidence", out var confEl) && confEl.ValueKind == JsonValueKind.Number
                ? confEl.GetDouble()
                : 0.0;
            var reasoning = root.TryGetProperty("reasoning", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                ? reasonEl.GetString()!
                : "";

            // Validate route exists. When the classifier names a route outside the
            // configured vocabulary we discard its answer entirely — so its self-
            // reported confidence and reasoning describe a route we are NOT taking and
            // must not be attributed to the fallback (that would pair "general" with a
            // spurious 0.9). Zero the confidence and record why, mirroring the other
            // fallback paths (parse-failure / low-confidence) so callers and the
            // OnRouteSelected hook get an honest signal.
            if (!_options.Routes.Contains(route))
            {
                route = _options.FallbackRoute;
                confidence = 0.0;
                reasoning = "Classifier chose an unknown route; using fallback";
            }

            // Check confidence threshold. Here the measured confidence IS meaningful
            // (it is a real, in-vocabulary route that simply wasn't trusted enough), so
            // preserve it — the low value is exactly the signal that triggered fallback.
            // But the reasoning argued for the REJECTED route, so pairing it with the
            // fallback would be misleading (the same reattribution bug the unknown-route
            // path avoids); replace it with an honest explanation of the fallback.
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
            // Fallback on parse failure
            _options.OnRouteSelected?.Invoke(_options.FallbackRoute, 0.0, "Classification parse failed; using fallback");
            return new ClassifyResult(_options.FallbackRoute, 0.0, "Parse error");
        }
    }

    /// <summary>
    /// Full route execution: classify then call the branch handler.
    /// </summary>
    public async Task<(ClassifyResult Classification, string Response)> RouteAsync(
        string message,
        Func<string, CancellationToken, Task<string>> classifierFunc,
        Func<string, string, CancellationToken, Task<string>> branchFunc,
        Dictionary<string, RouteHandler> handlers,
        CancellationToken ct = default)
    {
        var classification = await ClassifyAsync(message, classifierFunc, ct);

        // The classification vocabulary (RouterOptions.Routes) and the executable
        // branch handlers are separate config and can legitimately diverge - a route
        // may be classifiable yet have no handler wired up. Indexing handlers[...]
        // directly would throw KeyNotFoundException and crash the router, breaking the
        // documented "never crash; fall back" contract. Prefer the chosen route's
        // handler, then the fallback route's; only if neither exists do we surface a
        // clear configuration error instead of a bare KeyNotFoundException.
        if (!handlers.TryGetValue(classification.Route, out var handler))
        {
            if (!handlers.TryGetValue(_options.FallbackRoute, out handler))
                throw new InvalidOperationException(
                    $"No handler registered for route '{classification.Route}' or fallback route '{_options.FallbackRoute}'.");

            // The chosen route had no handler, so the FALLBACK handler actually served
            // the response. Returning the original classification unchanged would report
            // Route='technical' while the general handler ran - the reported route and
            // the executed handler would silently disagree, misleading any caller that
            // logs or branches on classification.Route. Re-point the returned
            // classification at the route that actually handled the message (preserving
            // the measured confidence) and record the substitution in the reasoning, so
            // reported route == executed handler.
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
