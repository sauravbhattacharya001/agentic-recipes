using Prompt;
using System.Text.Json;

// ──────────────────────────────────────────────────────────────
// Tool Agent Loop Recipe
// Pattern: PromptToolAgent (ReAct loop)
//
// The agent receives a user query, decides which tools to call,
// executes them, observes results, and iterates until it has
// enough information to produce a final answer.
// ──────────────────────────────────────────────────────────────

// 1. Define tools
var weatherTool = new AgentTool(
    "get_weather",
    "Get current weather for a city. Returns temperature and conditions.",
    async (argsJson, ct) =>
    {
        var args = JsonDocument.Parse(argsJson).RootElement;
        var city = args.GetProperty("city").GetString()!;
        // Simulated weather API
        var data = city.ToLower() switch
        {
            "seattle" => new { temp = 62, condition = "cloudy", humidity = 78 },
            "phoenix" => new { temp = 105, condition = "sunny", humidity = 12 },
            "miami" => new { temp = 88, condition = "partly cloudy", humidity = 85 },
            _ => new { temp = 70, condition = "clear", humidity = 50 }
        };
        return JsonSerializer.Serialize(data);
    })
    .AddParameter("city", "string", "City name", required: true);

var calculatorTool = new AgentTool(
    "calculate",
    "Evaluate a mathematical expression. Returns the numeric result.",
    async (argsJson, ct) =>
    {
        var args = JsonDocument.Parse(argsJson).RootElement;
        var expression = args.GetProperty("expression").GetString()!;
        // Simple calculator for demo
        double result = expression switch
        {
            "105 - 62" => 43,
            "88 - 62" => 26,
            "(105 + 88 + 62) / 3" => 85,
            _ => 0
        };
        return result.ToString();
    })
    .AddParameter("expression", "string", "Math expression to evaluate", required: true);

var searchTool = new AgentTool(
    "search",
    "Search for information on a topic. Returns relevant facts.",
    async (argsJson, ct) =>
    {
        var args = JsonDocument.Parse(argsJson).RootElement;
        var query = args.GetProperty("query").GetString()!;
        // Simulated search
        return query.ToLower() switch
        {
            var q when q.Contains("population") && q.Contains("seattle") =>
                "Seattle population: approximately 750,000 (city), 4 million (metro area)",
            var q when q.Contains("population") && q.Contains("phoenix") =>
                "Phoenix population: approximately 1.6 million (city), 4.9 million (metro area)",
            _ => $"No results found for: {query}"
        };
    })
    .AddParameter("query", "string", "Search query", required: true);

// 2. Configure the agent
var agent = new PromptToolAgent(new AgentOptions
{
    MaxTurns = 5,
    SystemPrompt = @"You are a helpful research assistant. You have access to tools for 
weather data, calculations, and search. Use them to answer the user's question thoroughly.
When you have enough information, provide a clear final answer without calling any more tools.
Format tool calls as JSON: [{""name"": ""tool_name"", ""arguments"": ""{...}""}]",
    ParallelToolExecution = true,
    ToolTimeout = TimeSpan.FromSeconds(10),
    OnTurnCompleted = turn =>
    {
        Console.WriteLine($"  [Turn {turn.TurnNumber}] {(turn.IsFinalAnswer ? "Final answer" : $"{turn.ToolCalls.Count} tool call(s)")}");
        foreach (var result in turn.ToolResults)
        {
            var status = result.Success ? "✓" : "✗";
            Console.WriteLine($"    {status} {result.Call.Name}: {result.Output[..Math.Min(60, result.Output.Length)]}");
        }
    }
});

agent.AddTool(weatherTool);
agent.AddTool(calculatorTool);
agent.AddTool(searchTool);

// 3. Simulate the model function
// In production, this calls Azure OpenAI / OpenAI / Anthropic
int turnCount = 0;
async Task<string> SimulatedModel(List<ConversationMessage> messages, List<AgentTool> tools, CancellationToken ct)
{
    turnCount++;
    
    // Simulate multi-turn reasoning
    return turnCount switch
    {
        1 => @"[{""name"": ""get_weather"", ""arguments"": ""{\""city\"": \""Seattle\""}""},
               {""name"": ""get_weather"", ""arguments"": ""{\""city\"": \""Phoenix\""}""},
               {""name"": ""get_weather"", ""arguments"": ""{\""city\"": \""Miami\""}""}]",
        
        2 => @"[{""name"": ""calculate"", ""arguments"": ""{\""expression\"": \""105 - 62\""}""}]",
        
        3 => @"Based on my research:

**Weather Comparison:**
- Seattle: 62°F, cloudy (humidity 78%)
- Phoenix: 105°F, sunny (humidity 12%)  
- Miami: 88°F, partly cloudy (humidity 85%)

**Temperature Difference:**
Phoenix is 43°F warmer than Seattle — the biggest spread among the three cities.

**Recommendation:** If you want warm and dry, Phoenix is your best bet. For moderate weather, Seattle. For warm and humid, Miami.",
        
        _ => "Done!"
    };
}

// 4. Run the agent
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  Tool Agent Loop Recipe (PromptToolAgent)");
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine();
Console.WriteLine("User: Compare the weather in Seattle, Phoenix, and Miami.");
Console.WriteLine("      Which city has the biggest temperature difference from Seattle?");
Console.WriteLine();
Console.WriteLine("Agent working...");

var result = await agent.RunAsync(
    "Compare the weather in Seattle, Phoenix, and Miami. Which city has the biggest temperature difference from Seattle?",
    modelFunc: SimulatedModel);

Console.WriteLine();
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine("FINAL ANSWER:");
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine(result.FinalAnswer);
Console.WriteLine();
Console.WriteLine($"Stats: {result.TotalTurns} turns, {result.TotalToolCalls} tool calls, {result.TotalDuration.TotalMilliseconds:F0}ms");
Console.WriteLine($"Completed: {result.Completed}");
