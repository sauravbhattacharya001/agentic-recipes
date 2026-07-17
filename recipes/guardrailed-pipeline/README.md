# Guardrailed Pipeline

**Pattern:** Inspect → Decide → Act (defend-in-depth)

## Overview

Every production agent needs a **safety membrane** between untrusted input and the
model. The Guardrailed Pipeline is that membrane. Before a single token reaches the
LLM, the input passes through a stack of guardrails, and the pipeline makes an
**autonomous decision** about what to do next.

1. **Inspect** — run a stack of detectors over the raw input, each reporting findings
   with a severity:
   - **Prompt injection / jailbreak** — "ignore all previous instructions", "act as DAN",
     "reveal your system prompt", developer-mode tricks, …
   - **PII / secrets** — emails, phone numbers, credit-card numbers, API keys
   - **Disallowed content** — topics the agent must refuse outright
2. **Decide** — aggregate the findings and pick the action implied by the *worst* one
3. **Act** — one of three outcomes, chosen by the pipeline itself:
   - **Allow** — nothing tripped; forward the input untouched
   - **Sanitize** — recoverable issues; redact secrets / strip the injection, then forward
   - **Block** — a critical risk; refuse and **never call the model**

This is the agentic part: the guardrail *acts on its own* to protect the model, the
user, and the surrounding system — no human in the loop, no model call wasted on input
that should never have reached it.

## Architecture

```
              ┌─────────────────────┐
              │   Untrusted input   │
              └──────────┬──────────┘
                         │
   ┌─────────────────────▼─────────────────────┐
   │                 Inspect                     │
   │  ① injection   ② content-policy   ③ PII     │
   │     findings: [{guardrail, severity, msg}]  │
   └─────────────────────┬─────────────────────┘
                         │  worst severity
            ┌────────────▼────────────┐
            │         Decide          │
            │  critical → Block       │
            │  injection → Block/Strip │
            │  pii       → Sanitize    │
            │  none      → Allow       │
            └────────────┬────────────┘
                         │
        ┌────────────────┼────────────────┐
        │                │                │
   ┌────▼────┐     ┌─────▼─────┐    ┌─────▼─────┐
   │  Allow  │     │ Sanitize  │    │   Block   │
   │ forward │     │ redact &  │    │  refuse,  │
   │ as-is   │     │ forward   │    │ no model  │
   └────┬────┘     └─────┬─────┘    └───────────┘
        │                │
        └───────┬────────┘
          ┌─────▼─────┐
          │   Model    │   (only reached on Allow / Sanitize)
          └───────────┘
```

## Key Concepts

### Findings carry a severity

Each detector emits zero or more `Finding`s. The decision is driven by the **single
worst** severity seen across all detectors, so one critical hit overrides a pile of
minor ones.

```csharp
record Finding(string Guardrail, Severity Severity, string Message);
enum Severity { None, Low, Medium, High, Critical }
```

### Three autonomous actions

```csharp
enum GuardAction { Allow, Sanitize, Block }
```

- **Block** when content policy is violated (Critical) or an injection is detected and
  `BlockOnInjection` is on — the model is **never called**.
- **Sanitize** when the issue is recoverable: redact PII/secrets in place, or strip the
  sentences carrying an injection, then forward the cleaned text.
- **Allow** when nothing trips.

> When `RedactPii = false` there is no safe forward for secrets, so a **High-severity**
> secret (API key / card number) is **blocked** rather than passed to the model verbatim
> — turning redaction off must never turn the guardrail into a leak. Softer Low/Medium
> PII (e.g. a bare email) may still be allowed through in that mode.

### Sanitize keeps the conversation alive

Blocking is blunt. For PII the pipeline **redacts** (`john@x.com` → `[REDACTED_EMAIL]`)
and forwards the rest, so a user who pasted a secret by accident still gets an answer
without leaking it downstream. With `BlockOnInjection = false`, injected sentences are
**stripped** and the benign remainder is forwarded.

### Configurable posture

```csharp
var guard = new GuardrailPipeline(new GuardrailOptions
{
    BlockOnInjection = true,        // injection → hard block (vs. strip & continue)
    RedactPii        = true,        // mask secrets instead of blocking on them
    BlockAtOrAbove   = Severity.Critical,
    OnFinding        = f => Log(f)  // telemetry hook per finding
});
```

### Sync core, async wrapper → testable and model-agnostic

`Evaluate` is a pure, deterministic function (no I/O), so tests pin exact behaviour;
`EvaluateAsync` wraps it so callers can `await` it next to their real model calls.

## Running

```bash
dotnet run --project recipes/guardrailed-pipeline
```

The demo runs five inputs — clean, prompt injection, leaked secrets/PII, disallowed
content, and a mixed injection+PII case — and prints the finding trail plus the
Allow / Sanitize / Block decision for each.

## When to Use This Pattern

| Use Case | Why |
|----------|-----|
| Public-facing chatbots | Untrusted input must be screened before the model sees it |
| Tool-using agents | Block injections that try to hijack tool calls |
| Anything handling user data | Redact PII/secrets before they hit logs or the LLM |
| Compliance-sensitive flows | Refuse disallowed topics deterministically |
| RAG over user uploads | Sanitize retrieved chunks the same way as direct input |

## Extending

- Replace the keyword detectors with an **LLM-as-judge** classifier returning
  `{severity, category}` JSON
- Add an **output** guardrail (same engine, run on the model's response) for
  defence on both sides
- Maintain a **per-user risk score** that tightens thresholds after repeated trips
- Emit findings to your **SIEM / audit log** via the `OnFinding` hook
- Add an **allowlist** so known-safe phrases never trip the injection detector

## Related Patterns

- [Conditional Router](../conditional-router/) — classify then branch; here we classify *risk* then act
- [Code Review Pipeline](../code-review-pipeline/) — staged validate → review → fix
- [Tool Agent Loop](../tool-agent-loop/) — the loop a hijacked injection would try to abuse
- [Iterative Refinement](../iterative-refinement/) — autonomous stop/continue decisions
