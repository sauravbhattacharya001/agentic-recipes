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
}
