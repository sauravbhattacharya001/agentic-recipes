using Prompt;
using System.Text.Json;
using Xunit;

namespace AgenticRecipes.Tests;

public class ToolAgentLoopTests
{
    private static AgentTool CreateWeatherTool()
    {
        return new AgentTool("get_weather", "Get weather for a city",
            (argsJson, ct) =>
            {
                var args = JsonDocument.Parse(argsJson).RootElement;
                var city = args.GetProperty("city").GetString()!;
                return Task.FromResult(city.ToLower() switch
                {
                    "seattle" => "{\"temp\":62,\"condition\":\"cloudy\"}",
                    "phoenix" => "{\"temp\":105,\"condition\":\"sunny\"}",
                    _ => "{\"temp\":70,\"condition\":\"clear\"}"
                });
            })
            .AddParameter("city", "string", "City name", required: true);
    }

    private static AgentTool CreateCalculatorTool()
    {
        return new AgentTool("calculate", "Evaluate math",
            (argsJson, ct) =>
            {
                var args = JsonDocument.Parse(argsJson).RootElement;
                var expr = args.GetProperty("expression").GetString()!;
                return Task.FromResult(expr switch
                {
                    "105-62" => "43",
                    "2+2" => "4",
                    _ => "0"
                });
            })
            .AddParameter("expression", "string", "Math expression", required: true);
    }

    [Fact]
    public async Task Agent_CompletesWithFinalAnswer()
    {
        var agent = new PromptToolAgent();
        agent.AddTool(CreateWeatherTool());

        int turn = 0;
        var result = await agent.RunAsync("Weather in Seattle?",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1) return Task.FromResult("[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Seattle\\\"}\"}]");
                return Task.FromResult("It's 62°F and cloudy in Seattle.");
            });

        Assert.True(result.Completed);
        Assert.Contains("62", result.FinalAnswer);
        Assert.Equal(2, result.TotalTurns);
        Assert.Equal(1, result.TotalToolCalls);
    }

    [Fact]
    public async Task Agent_ExecutesMultipleToolsInOneTurn()
    {
        var agent = new PromptToolAgent(new AgentOptions { ParallelToolExecution = true });
        agent.AddTool(CreateWeatherTool());
        agent.AddTool(CreateCalculatorTool());

        int turn = 0;
        var result = await agent.RunAsync("Compare Seattle and Phoenix",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1)
                    return Task.FromResult("[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Seattle\\\"}\"},{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Phoenix\\\"}\"}]");
                return Task.FromResult("Seattle is 62°F, Phoenix is 105°F.");
            });

        Assert.Equal(2, result.Turns[0].ToolCalls.Count);
        Assert.All(result.Turns[0].ToolResults, r => Assert.True(r.Success));
    }

    [Fact]
    public async Task Agent_MultiTurnReasoning_ChainsToolCalls()
    {
        var agent = new PromptToolAgent();
        agent.AddTool(CreateWeatherTool());
        agent.AddTool(CreateCalculatorTool());

        int turn = 0;
        var result = await agent.RunAsync("How much hotter is Phoenix than Seattle?",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1) return Task.FromResult("[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Seattle\\\"}\"}]");
                if (turn == 2) return Task.FromResult("[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Phoenix\\\"}\"}]");
                if (turn == 3) return Task.FromResult("[{\"name\":\"calculate\",\"arguments\":\"{\\\"expression\\\":\\\"105-62\\\"}\"}]");
                return Task.FromResult("Phoenix is 43°F hotter than Seattle.");
            });

        Assert.True(result.Completed);
        Assert.Equal(4, result.TotalTurns);
        Assert.Equal(3, result.TotalToolCalls);
        Assert.Contains("43", result.FinalAnswer);
    }

    [Fact]
    public async Task Agent_MaxTurns_StopsGracefully()
    {
        var agent = new PromptToolAgent(new AgentOptions { MaxTurns = 2 });
        agent.AddTool(CreateWeatherTool());

        var result = await agent.RunAsync("Keep going forever",
            modelFunc: (msgs, tools, ct) =>
                Task.FromResult("[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Seattle\\\"}\"}]"));

        Assert.False(result.Completed);
        Assert.Contains("maximum turns", result.StopReason);
    }

    [Fact]
    public async Task Agent_UnknownTool_GracefulError()
    {
        var agent = new PromptToolAgent();
        agent.AddTool(CreateWeatherTool());

        int turn = 0;
        var result = await agent.RunAsync("Use a bad tool",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1) return Task.FromResult("[{\"name\":\"nonexistent\",\"arguments\":\"{}\"}]");
                return Task.FromResult("That tool doesn't exist.");
            });

        Assert.False(result.Turns[0].ToolResults[0].Success);
        Assert.Contains("Unknown tool", result.Turns[0].ToolResults[0].Error);
    }

    [Fact]
    public async Task Agent_ToolTimeout_ReportsTimeout()
    {
        var slowTool = new AgentTool("slow", "Takes forever",
            async (args, ct) =>
            {
                await Task.Delay(5000, ct);
                return "done";
            });

        var agent = new PromptToolAgent(new AgentOptions { ToolTimeout = TimeSpan.FromMilliseconds(50) });
        agent.AddTool(slowTool);

        int turn = 0;
        var result = await agent.RunAsync("Use slow tool",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1) return Task.FromResult("[{\"name\":\"slow\",\"arguments\":\"{}\"}]");
                return Task.FromResult("Tool timed out.");
            });

        Assert.False(result.Turns[0].ToolResults[0].Success);
        Assert.Contains("timed out", result.Turns[0].ToolResults[0].Error);
    }

    [Fact]
    public async Task Agent_OnBeforeToolExecution_CanBlockDangerousTools()
    {
        var deleteTool = new AgentTool("delete_file", "Delete a file",
            (args, ct) => Task.FromResult("deleted"));

        var agent = new PromptToolAgent(new AgentOptions
        {
            OnBeforeToolExecution = call => call.Name != "delete_file"
        });
        agent.AddTool(deleteTool);
        agent.AddTool(CreateWeatherTool());

        int turn = 0;
        var result = await agent.RunAsync("Delete all files",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1) return Task.FromResult("[{\"name\":\"delete_file\",\"arguments\":\"{}\"}]");
                return Task.FromResult("I'm not allowed to delete files.");
            });

        Assert.False(result.Turns[0].ToolResults[0].Success);
        Assert.Contains("blocked", result.Turns[0].ToolResults[0].Error);
    }

    [Fact]
    public async Task Agent_SystemPrompt_PassedToModel()
    {
        var agent = new PromptToolAgent(new AgentOptions
        {
            SystemPrompt = "You are a weather assistant."
        });

        List<ConversationMessage>? captured = null;
        await agent.RunAsync("Hi",
            modelFunc: (msgs, tools, ct) =>
            {
                captured = new List<ConversationMessage>(msgs);
                return Task.FromResult("Hello!");
            });

        Assert.Equal("system", captured![0].Role);
        Assert.Equal("You are a weather assistant.", captured[0].Content);
    }

    [Fact]
    public async Task Agent_ToolResultsFedBackToModel()
    {
        var agent = new PromptToolAgent();
        agent.AddTool(CreateWeatherTool());

        List<ConversationMessage>? secondCall = null;
        int turn = 0;
        await agent.RunAsync("Weather?",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1) return Task.FromResult("[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Seattle\\\"}\"}]");
                secondCall = new List<ConversationMessage>(msgs);
                return Task.FromResult("62°F, cloudy.");
            });

        var toolMsg = secondCall!.Last(m => m.Role == "tool");
        Assert.Contains("62", toolMsg.Content);
        Assert.Equal("get_weather", toolMsg.ToolName);
    }

    [Fact]
    public async Task Agent_TracksTimingPerTurn()
    {
        var agent = new PromptToolAgent();
        agent.AddTool(CreateWeatherTool());

        int turn = 0;
        var result = await agent.RunAsync("Weather?",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1) return Task.FromResult("[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Seattle\\\"}\"}]");
                return Task.FromResult("Done.");
            });

        Assert.All(result.Turns, t => Assert.True(t.Duration > TimeSpan.Zero));
        Assert.True(result.TotalDuration > TimeSpan.Zero);
    }

    // ── Immediate final answer (no tools) ──────────────────────

    [Fact]
    public async Task Agent_NoToolCalls_FinishesInOneTurn()
    {
        // The model answers directly on the first turn without invoking a tool.
        // That single turn IS the final answer: completed, one turn, zero calls.
        var agent = new PromptToolAgent();
        agent.AddTool(CreateWeatherTool());

        var result = await agent.RunAsync("Just say hi",
            modelFunc: (msgs, tools, ct) => Task.FromResult("Hello! No tools needed."));

        Assert.True(result.Completed);
        Assert.Equal(1, result.TotalTurns);
        Assert.Equal(0, result.TotalToolCalls);
        Assert.True(result.Turns[0].IsFinalAnswer);
        Assert.Equal("Hello! No tools needed.", result.FinalAnswer);
    }

    // ── OnTurnCompleted hook (documented, demoed in Program.cs) ─

    [Fact]
    public async Task Agent_OnTurnCompleted_FiresOncePerTurn_IncludingFinal()
    {
        // The README lists OnTurnCompleted as a key feature and Program.cs drives
        // its console output from it. It must fire for every turn — the tool turn
        // AND the closing final-answer turn — in order.
        var seen = new List<(int Turn, bool Final, int Calls)>();
        var agent = new PromptToolAgent(new AgentOptions
        {
            OnTurnCompleted = t => seen.Add((t.TurnNumber, t.IsFinalAnswer, t.ToolCalls.Count))
        });
        agent.AddTool(CreateWeatherTool());

        int turn = 0;
        await agent.RunAsync("Weather in Seattle?",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1) return Task.FromResult("[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Seattle\\\"}\"}]");
                return Task.FromResult("It's 62\u00B0F.");
            });

        Assert.Equal(2, seen.Count);
        Assert.Equal((1, false, 1), seen[0]); // tool turn
        Assert.Equal((2, true, 0), seen[1]);  // final-answer turn
    }

    // ── Sequential tool execution ──────────────────────────────

    [Fact]
    public async Task Agent_SequentialExecution_RunsAllToolsInOneTurn()
    {
        // With ParallelToolExecution = false the agent still executes every
        // requested tool in the turn (just one after another), and all succeed.
        var agent = new PromptToolAgent(new AgentOptions { ParallelToolExecution = false });
        agent.AddTool(CreateWeatherTool());
        agent.AddTool(CreateCalculatorTool());

        int turn = 0;
        var result = await agent.RunAsync("Seattle weather then 2+2",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1)
                    return Task.FromResult("[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Seattle\\\"}\"},{\"name\":\"calculate\",\"arguments\":\"{\\\"expression\\\":\\\"2+2\\\"}\"}]");
                return Task.FromResult("Both done.");
            });

        Assert.Equal(2, result.Turns[0].ToolCalls.Count);
        Assert.All(result.Turns[0].ToolResults, r => Assert.True(r.Success));
        Assert.Equal("4", result.Turns[0].ToolResults.Single(r => r.Call.Name == "calculate").Output);
    }

    // ── A tool that throws (distinct from timeout / unknown) ───

    [Fact]
    public async Task Agent_ToolThrows_SurfacesExceptionMessageAndKeepsGoing()
    {
        // A tool that throws a normal exception is reported as a failed result
        // carrying the exception's message (not the timeout text), and the loop
        // feeds that error back so the model can still produce a final answer.
        var boom = new AgentTool("boom", "Always fails",
            (args, ct) => throw new InvalidOperationException("kaboom"));

        var agent = new PromptToolAgent();
        agent.AddTool(boom);

        int turn = 0;
        var result = await agent.RunAsync("Use boom",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1) return Task.FromResult("[{\"name\":\"boom\",\"arguments\":\"{}\"}]");
                return Task.FromResult("Recovered after the tool error.");
            });

        var tr = result.Turns[0].ToolResults[0];
        Assert.False(tr.Success);
        Assert.Equal("kaboom", tr.Error);
        Assert.True(result.Completed);
        Assert.Equal("Recovered after the tool error.", result.FinalAnswer);
    }

    // ── Per-call permission gating (block one, allow another) ──

    [Fact]
    public async Task Agent_OnBeforeToolExecution_BlocksOnlyTheDeniedTool()
    {
        // The guard is consulted per call: a denied tool fails while a permitted
        // tool in the SAME turn still runs and succeeds.
        var agent = new PromptToolAgent(new AgentOptions
        {
            ParallelToolExecution = false,
            OnBeforeToolExecution = call => call.Name != "calculate" // block only the calculator
        });
        agent.AddTool(CreateWeatherTool());
        agent.AddTool(CreateCalculatorTool());

        int turn = 0;
        var result = await agent.RunAsync("weather + math",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1)
                    return Task.FromResult("[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Seattle\\\"}\"},{\"name\":\"calculate\",\"arguments\":\"{\\\"expression\\\":\\\"2+2\\\"}\"}]");
                return Task.FromResult("Done.");
            });

        var weather = result.Turns[0].ToolResults.Single(r => r.Call.Name == "get_weather");
        var calc = result.Turns[0].ToolResults.Single(r => r.Call.Name == "calculate");
        Assert.True(weather.Success);
        Assert.False(calc.Success);
        Assert.Contains("blocked", calc.Error);
    }

    // ── TotalToolCalls aggregates across turns ─────────────────

    [Fact]
    public async Task Agent_TotalToolCalls_SumsAcrossTurns()
    {
        // Turn 1 makes two calls, turn 2 makes one → TotalToolCalls == 3,
        // proving the aggregate spans turns rather than reporting a single turn.
        var agent = new PromptToolAgent(new AgentOptions { ParallelToolExecution = false });
        agent.AddTool(CreateWeatherTool());
        agent.AddTool(CreateCalculatorTool());

        int turn = 0;
        var result = await agent.RunAsync("multi",
            modelFunc: (msgs, tools, ct) =>
            {
                turn++;
                if (turn == 1)
                    return Task.FromResult("[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Seattle\\\"}\"},{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Phoenix\\\"}\"}]");
                if (turn == 2)
                    return Task.FromResult("[{\"name\":\"calculate\",\"arguments\":\"{\\\"expression\\\":\\\"105-62\\\"}\"}]");
                return Task.FromResult("43\u00B0F warmer.");
            });

        Assert.Equal(3, result.TotalTurns);
        Assert.Equal(3, result.TotalToolCalls);
    }

    // ── Guards ─────────────────────────────────────────────────

    [Fact]
    public async Task Agent_EmptyUserMessage_Throws()
    {
        var agent = new PromptToolAgent();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            agent.RunAsync("   ", modelFunc: (msgs, tools, ct) => Task.FromResult("hi")));
    }

    [Fact]
    public void RemoveTool_UnregistersTool()
    {
        var agent = new PromptToolAgent();
        agent.AddTool(CreateWeatherTool());
        Assert.True(agent.Tools.ContainsKey("get_weather"));

        Assert.True(agent.RemoveTool("get_weather"));
        Assert.False(agent.Tools.ContainsKey("get_weather"));
        Assert.False(agent.RemoveTool("get_weather")); // already gone
    }

    // ── DefaultToolCallParser: the documented input formats ────
    // The README advertises the parser handles "OpenAI JSON, wrapped format,
    // markdown-fenced". These assert each shape directly (verified against
    // promptlib 7.0.0) so a parser regression is caught at the recipe boundary.

    [Fact]
    public void Parser_PlainArray_ExtractsCall()
    {
        var calls = PromptToolAgent.DefaultToolCallParser(
            "[{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Seattle\\\"}\"}]");

        Assert.Single(calls);
        Assert.Equal("get_weather", calls[0].Name);
        Assert.Contains("Seattle", calls[0].Arguments);
    }

    [Fact]
    public void Parser_FunctionWrapper_ReadsNestedNameAndArguments()
    {
        // OpenAI tool_calls element shape: {"function": {"name", "arguments"}}.
        var calls = PromptToolAgent.DefaultToolCallParser(
            "[{\"function\": {\"name\": \"get_weather\", \"arguments\": \"{\\\"city\\\":\\\"Seattle\\\"}\"}}]");

        Assert.Single(calls);
        Assert.Equal("get_weather", calls[0].Name);
        Assert.Contains("Seattle", calls[0].Arguments);
    }

    [Fact]
    public void Parser_ToolCallsObjectWrapper_Unwraps()
    {
        var calls = PromptToolAgent.DefaultToolCallParser(
            "{\"tool_calls\": [{\"name\": \"calculate\", \"arguments\": \"{\\\"expression\\\":\\\"2+2\\\"}\"}]}");

        Assert.Single(calls);
        Assert.Equal("calculate", calls[0].Name);
    }

    [Fact]
    public void Parser_MarkdownFencedJson_ExtractsFromSurroundingProse()
    {
        var calls = PromptToolAgent.DefaultToolCallParser(
            "Sure, let me check.\n```json\n[{\"name\": \"search\", \"arguments\": \"{\\\"query\\\":\\\"x\\\"}\"}]\n```\nDone.");

        Assert.Single(calls);
        Assert.Equal("search", calls[0].Name);
    }

    [Fact]
    public void Parser_ArgumentsAsObject_KeepsRawJson()
    {
        // arguments given as a raw JSON object (not a JSON string) → preserved as raw text.
        var calls = PromptToolAgent.DefaultToolCallParser(
            "[{\"name\": \"calculate\", \"arguments\": {\"expression\": \"2+2\"}}]");

        Assert.Single(calls);
        Assert.Equal("calculate", calls[0].Name);
        Assert.Contains("expression", calls[0].Arguments);
        Assert.Contains("2+2", calls[0].Arguments);
    }

    [Fact]
    public void Parser_PlainProse_YieldsNoCalls()
    {
        // No JSON payload → no tool calls (the loop treats this as a final answer).
        var calls = PromptToolAgent.DefaultToolCallParser(
            "The weather in Seattle is 62\u00B0F and cloudy. No tools needed.");

        Assert.Empty(calls);
    }

    [Fact]
    public void Parser_ElementMissingName_IsSkipped()
    {
        // An entry without a usable name is dropped rather than yielding a nameless call.
        var calls = PromptToolAgent.DefaultToolCallParser("[{\"arguments\": \"{}\"}]");
        Assert.Empty(calls);
    }

    [Fact]
    public void Parser_EmptyOrWhitespace_YieldsNoCalls()
    {
        Assert.Empty(PromptToolAgent.DefaultToolCallParser(""));
        Assert.Empty(PromptToolAgent.DefaultToolCallParser("   "));
    }
}
