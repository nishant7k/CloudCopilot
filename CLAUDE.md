# CLAUDE.md — CloudCopilot

This file provides context for AI assistants working on the CloudCopilot codebase.

---

## Project Overview

CloudCopilot is a **Blazor Server** chatbot that helps users discover and compare cloud instance pricing. It integrates the **GitHub Copilot SDK** for natural language understanding and a **Vantage MCP (Model Context Protocol) server** for cloud pricing data.

The agent is intentionally constrained to 5 MCP tools — no filesystem, web browsing, or code execution access.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10.0 |
| UI Framework | Blazor Server (SignalR/WebSocket) |
| Copilot Integration | `GitHub.Copilot.SDK` v0.1.18 (reflection-wrapped) |
| Markdown Rendering | Markdig v0.39.1 |
| Testing | xUnit v2.9.3 + coverlet |
| CI/CD | GitHub Actions (`.github/workflows/dotnet.yml`) |

---

## Repository Structure

```
CloudCopilot/
├── .github/workflows/dotnet.yml   # CI: restore → build → test
├── CloudCopilot.sln               # Solution file (main + tests)
├── README.md
├── src/
│   ├── CloudCopilot.csproj
│   ├── Program.cs                 # DI registration, endpoints, app config
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   ├── Properties/launchSettings.json
│   ├── Components/
│   │   ├── App.razor              # HTML shell
│   │   ├── Routes.razor           # Router + default layout
│   │   ├── _Imports.razor         # Global using directives
│   │   ├── Pages/
│   │   │   ├── Home.razor         # Main chat UI (interactive server-side)
│   │   │   ├── Error.razor
│   │   │   └── NotFound.razor
│   │   └── Layout/
│   │       ├── MainLayout.razor
│   │       ├── ReconnectModal.razor(.css/.js)
│   │       └── MainLayout.razor.css
│   ├── Services/
│   │   ├── CopilotAgent.cs        # Core orchestration: plan → execute → answer
│   │   ├── CopilotClientManager.cs# Reflection wrapper for GitHub.Copilot.SDK
│   │   ├── CopilotStartupService.cs
│   │   ├── McpClient.cs           # JSON-RPC 2.0 HTTP client for MCP server
│   │   ├── McpStartupService.cs
│   │   ├── CopilotDebugPromptService.cs
│   │   ├── ChatState.cs           # Thread-safe message store (ConcurrentQueue)
│   │   ├── ChatMessage.cs
│   │   ├── ConnectionStatus.cs    # MCP + Copilot health state
│   │   ├── CopilotPlan.cs         # Structured response model
│   │   ├── CopilotToolCall.cs
│   │   ├── McpOptions.cs          # MCP config: Url, ApiKey, TimeoutSeconds
│   │   ├── McpResultStore.cs      # Circular buffer: last 20 raw MCP responses
│   │   ├── McpToolDefinition.cs
│   │   ├── ToolCallLog.cs
│   │   └── ToolCallStore.cs       # Circular buffer: last 50 tool calls
│   └── wwwroot/app.css
└── tests/
    ├── CloudCopilot.Tests.csproj
    └── ChatStateTests.cs
```

---

## Build & Run

```bash
# Install dependencies and run (from repo root)
cd src
dotnet restore
dotnet run
```

**Default URLs:**
- HTTP: `http://localhost:5082`
- HTTPS: `https://localhost:7037`

**Run tests:**
```bash
dotnet test
```

---

## Required Environment Variables

| Variable | Required | Description |
|---|---|---|
| `VANTAGE_INSTANCES_MCP_URL` | **Yes** | URL of the Vantage MCP server |
| `VANTAGE_INSTANCES_MCP_KEY` | No | Bearer token for MCP authentication |
| `COPILOT_DEBUG_PROMPT` | No | Prompt executed at startup (dev/debugging only) |

Set in `appsettings.Development.json` or as environment variables. The SDK wrapper automatically sets `COPILOT_SDK_SERVER_MODE=cli` and `COPILOT_SERVER_MODE=cli`.

GitHub Copilot CLI must be authenticated on the machine running the app.

---

## Architecture & Key Patterns

### 1. Two-Phase Agent Orchestration (`CopilotAgent.cs`)

Every user message goes through two Copilot SDK calls:

1. **Plan Phase** — Copilot returns a JSON `CopilotPlan` with one of:
   - `action: "clarify"` + a clarifying question
   - `action: "direct_answer"` + the answer (no tools needed)
   - `action: "call_tools"` + a list of `CopilotToolCall` objects

2. **Answer Phase** — if tools were called, their results are injected and Copilot synthesizes a final answer.

### 2. Allowed MCP Tools (Strict Allowlist)

The agent is restricted to exactly these 5 tools, enforced in `CopilotAgent.cs`:

```
list_providers
list_families
search_instances
get_pricing
compare_instances
```

Do not add new tools without updating `_allowedTools` in `CopilotAgent.cs` AND verifying the MCP server exposes them.

### 3. Reflection-Based Copilot SDK (`CopilotClientManager.cs`)

The `GitHub.Copilot.SDK` assembly is loaded and invoked entirely via reflection. This is intentional — it decouples the app from SDK version-specific type changes. When the SDK is updated, test that `CopilotClient`, `CopilotClientOptions`, and `SessionConfig` still exist with compatible signatures.

### 4. MCP Client (`McpClient.cs`)

Uses JSON-RPC 2.0 over HTTP POST. Handles Server-Sent Events (SSE) response framing, including `[DONE]` terminators. Responses are deserialized from the `result` field of JSON-RPC responses.

### 5. Dependency Lifetimes (`Program.cs`)

| Lifetime | Services |
|---|---|
| Singleton | `ConnectionStatus`, `ToolCallStore`, `McpResultStore`, `CopilotClientManager` |
| Scoped | `ChatState`, `CopilotAgent` |
| HttpClient (named) | `McpClient` (30s timeout) |
| IHostedService | `McpStartupService`, `CopilotStartupService`, `CopilotDebugPromptService` |

`ChatState` is scoped (per Blazor circuit/user session). `ConnectionStatus` is a singleton shared across all users.

### 6. Health Endpoint

`GET /health` returns JSON:
```json
{
  "mcp": { "connected": true, "error": null, "tools": ["list_providers", ...] },
  "copilot": { "connected": true, "error": null }
}
```

### 7. Circular Buffer Stores

`ToolCallStore` (max 50) and `McpResultStore` (max 20) use `ConcurrentQueue` with a trim-on-add pattern. These are for in-memory debugging only — data is lost on restart.

---

## Code Conventions

- **All service classes are `sealed`** — do not remove `sealed` without reason.
- **Immutable data models use `record`** — `ChatMessage`, `ToolCallLog`, `McpToolDefinition`, etc.
- **Nullable reference types are enabled** — avoid `#nullable disable` suppressions.
- **No explicit XML doc comments** — keep this consistent; don't add doc comments unless documenting a public API.
- **No database** — all state is in-memory. Do not introduce a database without significant design discussion.
- **No EditorConfig / StyleCop** — follow existing style: PascalCase types/methods, camelCase locals, no trailing whitespace.

---

## Testing

Tests live in `/tests/`. The project uses **xUnit**.

Current coverage:
- `ChatStateTests` — message enqueue, clear, concurrent snapshot
- `ConnectionStatusTests` — default state validation

When adding new services, add corresponding unit tests. Run the full suite before pushing:

```bash
dotnet test --verbosity normal
```

---

## CI/CD

GitHub Actions workflow at `.github/workflows/dotnet.yml`:

- **Triggers**: push to `main`, PR to `main`
- **Steps**: checkout → setup .NET 10 → `dotnet restore` → `dotnet build --no-restore` → `dotnet test --no-build`

All tests must pass before merging to `main`.

---

## Common Tasks

### Add a new MCP tool
1. Add the tool name to `_allowedTools` in `CopilotAgent.cs`
2. Add a typed wrapper method in `McpClient.cs`
3. Update the system prompt in `CopilotAgent.cs` to describe the new tool
4. Add it to the UI tool panel in `Home.razor` if needed

### Add a new Blazor page
1. Create `ComponentName.razor` under `src/Components/Pages/`
2. Add `@page "/route"` directive
3. Add `@rendermode InteractiveServer` if the page needs interactivity
4. Import any new services in `_Imports.razor` or add `@inject` directly

### Change the Copilot system prompt
Edit the prompt string in `CopilotAgent.cs` (the `BuildSystemPrompt()` method or equivalent). The prompt includes the allowed tool list — keep that section accurate.

### Debug MCP responses
Enable the debug panel in `Home.razor` or check the raw responses stored in `McpResultStore`. The `COPILOT_DEBUG_PROMPT` environment variable can trigger an automated prompt at startup for testing without UI interaction.

---

## Security Notes

- The Copilot agent is restricted to cloud pricing tools only — never grant filesystem, shell, or web access tools.
- Provider and region names are normalized/validated via regex before being passed to MCP tools (see `CopilotAgent.cs`).
- The MCP API key (`VANTAGE_INSTANCES_MCP_KEY`) must never be committed to source control.
- Blazor Server renders server-side; `@((MarkupString)...)` usage in `Home.razor` is for sanitized Markdig output only — do not pass raw user input through `MarkupString`.
