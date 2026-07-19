# Tool Agent Loop

**Pattern:** `PromptToolAgent` — ReAct (Reason → Act → Observe → Repeat)

## What It Does

The agent receives a user query, decides which tools to call, executes them, observes results, and iterates until it has enough information to produce a final answer.

## When to Use

- Chat assistants that need to call APIs (weather, search, databases)
- Multi-step research tasks where the agent decides what to look up
- Any scenario where the model needs external data to answer

## How It Works

```
User Query
    ↓
┌─────────────┐
│ Model Call   │ ← "What tools do I need?"
└──────┬──────┘
       ↓
┌─────────────┐
│ Parse Tools │ ← Extract tool calls from response
└──────┬──────┘
       ↓
┌─────────────┐
│ Execute     │ ← Run tools (parallel or sequential)
└──────┬──────┘
       ↓
┌─────────────┐
│ Feed Back   │ ← Add results to conversation
└──────┬──────┘
       ↓
  Loop or Final Answer
```

## Key Features

- **Parallel tool execution** — multiple tools run concurrently
- **Max turns limit** — prevents infinite loops
- **Tool timeout** — individual tool execution timeout
- **Hooks** — `OnTurnCompleted`, `OnBeforeToolExecution` for logging/guardrails
- **Default parser** — handles OpenAI JSON, wrapped format, markdown-fenced

## Run

```bash
dotnet run --project recipes/tool-agent-loop
```

## Swap in Real LLM

Replace `SimulatedModel` with your Azure OpenAI call:

```csharp
var result = await agent.RunAsync(
    userMessage,
    modelFunc: async (messages, tools, ct) =>
    {
        // Convert messages + tools to your LLM API format
        var response = await azureClient.GetChatCompletionsAsync(...);
        return response.Value.Choices[0].Message.Content;
    });
```
