# Task: Implement Intelligent, Low-Latency MCP Tool Selection in LiteLLMChat.VisualStudio

## 1. Objective

Inspect the existing `LiteLLMChat.VisualStudio` codebase and implement a production-quality MCP tool selection experience that is intelligent, predictable, responsive, and efficient.

The goal is to make MCP usage feel natural to users:

- The agent automatically selects and invokes relevant MCP tools when appropriate.
- Users can explicitly reference an MCP server or tool in their prompts.
- Users can manually restrict the available MCP tools when they need direct control.
- The system respects configured permissions and existing confirmation policies.
- MCP selection must not introduce unnecessary latency, redundant model calls, or expensive processing.

This is an implementation task, not merely a design proposal. Inspect the repository, understand the existing architecture, implement the changes, run relevant tests, and report the results.

## 2. Mandatory Repository Investigation

Before making changes:

1. Inspect the repository structure, current MCP implementation, chat request lifecycle, agent/tool-calling pipeline, UI architecture, settings persistence, and permission/confirmation mechanisms.
2. Identify the actual LLM providers and models supported by the existing implementation, including how tool definitions and tool-call responses are handled.
3. Determine whether MCP tools are currently exposed directly to the model, invoked by an internal dispatcher, or handled through another mechanism.
4. Identify the existing abstractions and reuse them wherever practical.
5. Read relevant documentation and existing tests before deciding on the implementation.
6. Write a concise implementation plan based on the actual codebase.

Do not assume that the repository uses any particular framework, API, UI control, or MCP SDK version without verifying it.

Do not replace existing working infrastructure simply to introduce a new abstraction.

## 3. Required User Experience

Implement the following capabilities.

### A. Automatic Mode — Default

Automatic mode must be the default experience.

The user enters a normal prompt, for example:

"Investigate why the integration tests are failing and inspect the related GitHub issues."

The agent should be able to select relevant tools from the available MCP tools and use them through the existing agent/tool-calling pipeline.

Requirements:

- Do not require the user to name an MCP server for ordinary requests.
- Expose relevant available tools to the model using the existing provider's supported tool-calling mechanism.
- Let the model decide whether a tool call is necessary.
- Do not invoke MCP tools merely because they are available.
- Do not make an additional LLM call solely to classify a request or select an MCP server.
- Preserve the existing conversation context and tool-call results.
- Support multiple tool calls and multi-step workflows when supported by the existing agent architecture.
- Follow existing iteration limits, cancellation behavior, error handling, and execution policies.
- Avoid repeated calls with equivalent arguments when unnecessary.

Automatic mode must work naturally for both requests that require MCP tools and requests that can be answered without tools.

### B. Explicit Reference Mode

Allow users to explicitly reference MCP servers or tools in their prompts.

Support the existing UI's conventions and, if compatible with the current chat editor, provide a `#`-based reference experience similar to VS Code.

Examples:

- `#github Find issue 123 and summarize its root cause.`
- `Use #postgres to inspect the schema of the Orders table.`
- `Use the GitHub search tool to find issues related to authentication.`

Requirements:

- Provide discoverable autocomplete or suggestions for configured MCP servers and tools when technically feasible.
- Display recognizable server and tool names.
- Resolve references to actual configured server/tool identities, not just matching text.
- Handle missing, ambiguous, unavailable, and disabled references gracefully.
- Do not silently pretend that a requested tool was invoked when it was not.
- Explicit references should influence tool selection, but must never bypass authorization, permission checks, or execution confirmations.
- Do not force users to use a special syntax when ordinary natural-language requests work.
- Avoid parsing conflicts with existing editor features, Markdown, or provider-specific syntax.
- If the existing architecture cannot support reliable explicit references for every provider, implement the strongest compatible behavior and document its limitations.

The UI must clearly distinguish a server reference from a specific tool reference.

### C. Manual Tool Selection

Add an optional UI mechanism that allows users to restrict which MCP tools are available for the current request or conversation.

Prefer reusing the existing chat toolbar, tool picker, or settings components.

Requirements:

- Display configured MCP servers and their available tools in a discoverable interface.
- Allow selecting individual tools when the existing architecture supports it.
- Support selecting or deselecting a server and handling its tools consistently.
- Clearly distinguish enabled, disabled, unavailable, and failed tools.
- Define and document the selection scope: current request, conversation, or persistent preference.
- Do not silently change global MCP configuration when the user only intended to change the current request.
- Provide a clear way to return to Automatic mode.
- Persist settings only where consistent with the existing settings architecture.

Manual selection is an optional control, not a mandatory step before every prompt.

### D. Restricted Automatic Mode

Support a mode in which the agent automatically chooses among a user-defined subset of allowed MCP tools.

This mode must:

- Reuse the same intelligent tool-calling mechanism as Automatic mode.
- Limit tool definitions and executable tool identities to the configured allowed set.
- Reject calls to tools outside the allowed set, even if requested by the model.
- Avoid adding another model call to filter tools.
- Make the active restrictions visible to the user.
- Provide a straightforward way to change the allowed set.

Do not confuse tool selection with permission to execute potentially dangerous operations.

## 4. Selection Precedence and Conflict Resolution

Implement deterministic rules for resolving conflicting selection signals.

Use the following policy unless the existing architecture requires a documented adjustment:

1. Security policies, tool authorization, and execution permissions always take precedence.
2. An explicitly selected manual tool allowlist restricts the tools available to the agent.
3. Explicit tool references should be honored when the referenced tools are available and permitted.
4. If an explicit reference conflicts with the manual allowlist, do not silently bypass either constraint. Explain the conflict and let the user resolve it when necessary.
5. Without explicit references or a manual restriction, use Automatic mode.
6. When a tool is unavailable or fails, report the actual problem and do not fabricate successful execution.
7. If a task does not require tools, allow the model to respond without invoking MCP.

Do not make the system unpredictable by silently switching modes or overriding the user's explicit selections.

## 5. Performance Requirements

Performance is a primary acceptance criterion.

Implement the feature without introducing unnecessary work into the normal chat path.

### Tool discovery and metadata

- Reuse existing MCP connections, discovery results, and cached tool metadata.
- Avoid reconnecting to MCP servers or rediscovering their tools on every user message.
- Refresh metadata when connections change, tools are added or removed, or an appropriate invalidation event occurs.
- Use asynchronous operations for I/O-bound work.
- Do not block the UI thread while discovering tools or waiting for server responses.
- Avoid redundant serialization, repeated schema conversion, and unnecessary object allocation.
- Do not perform expensive semantic matching or embeddings-based routing unless the repository already requires and supports it.
- Do not add a separate LLM-based router.

### Tool definitions and request construction

- Reuse provider-compatible tool schemas where possible.
- Filter the available tool definitions before request submission according to the active mode and allowlist.
- Avoid exposing tools that the current request is not permitted to use.
- Preserve schema correctness for providers with different tool-calling requirements.
- Handle large tool catalogs efficiently without introducing arbitrary, silent truncation that makes required tools inaccessible.
- If provider limitations require a smaller tool set, implement a deterministic, transparent strategy and document the trade-offs.

### Chat responsiveness

- Preserve streaming responses where currently supported.
- Ensure cancellation propagates through LLM requests and MCP calls wherever possible.
- Avoid holding locks across network calls.
- Avoid synchronous waits and UI-thread blocking.
- Do not add a preliminary model call just to decide whether MCP is needed.
- Keep ordinary non-MCP conversations as close as possible to their existing latency and behavior.

### Observability

Use existing logging and diagnostics conventions.

Record useful diagnostic information such as:

- Active selection mode.
- Selected or available tool identities where appropriate.
- Tool discovery failures.
- Tool invocation duration and outcome.
- Provider/tool-calling failures.

Never log API keys, authentication tokens, sensitive tool arguments, or confidential tool results.

Do not introduce a telemetry dependency solely for this feature.

## 6. Security and Permission Boundaries

MCP tools can read sensitive data and perform side effects. Treat their execution as a separate security boundary.

Requirements:

- Reuse the existing permission and confirmation system.
- Enforce the effective tool allowlist in the execution dispatcher, not only in the UI or prompt.
- Validate that a requested server/tool identity is actually configured and available.
- Do not trust model-generated tool names, arguments, or claims of successful execution.
- Validate arguments against the actual tool schema and applicable local policies.
- Preserve existing confirmation requirements for sensitive or destructive operations.
- Do not treat an explicit `#tool` reference as permission to execute a dangerous action.
- Do not allow tool output to modify security policy or override system-level restrictions.
- Handle server disconnections, timeouts, malformed responses, and tool errors safely.
- Never silently fall back to a broader set of tools when restricted execution fails.

Tool availability, tool selection, and permission to execute must remain separate concepts.

## 7. UI and Configuration

Implement the smallest UI change that provides a clear and usable experience.

The UI should make these concepts understandable:

- Automatic
- Explicit references
- Manual selection
- Restricted automatic selection

Avoid introducing four separate complicated workflows if a simpler mode selector plus a tool picker can provide the same functionality.

Requirements:

- Follow existing UI styling and accessibility conventions.
- Provide understandable tool names and server names.
- Display loading, unavailable, and error states when necessary.
- Do not freeze the chat window during discovery or invocation.
- Preserve existing user settings and configuration compatibility.
- Avoid breaking existing chat sessions or MCP server configurations.
- Ensure settings changes take effect at the correct scope and time.

If the repository already has a suitable mode selector or tool management UI, extend it instead of creating a competing control.

## 8. Provider Compatibility

This extension may support multiple LLM endpoints and model families.

Do not assume every model supports the same tool-calling capabilities.

Inspect existing provider adapters and implement capability-aware behavior:

- Use native tool calling when supported.
- Preserve existing compatibility logic for providers with different tool schemas or response formats.
- Detect unsupported tool calling and provide a clear, actionable explanation.
- Do not claim that an MCP tool was used if the provider did not invoke it.
- Avoid silently degrading into unreliable prompt-only tool execution.
- Do not change unrelated provider behavior.

If fallback support is necessary, make it explicit, safe, and consistent with the existing architecture.

## 9. Testing Requirements

Add or update automated tests for the actual implementation.

At minimum, cover:

1. Automatic mode selects and invokes an appropriate available tool.
2. A normal prompt can be answered without an unnecessary tool call.
3. Explicit references resolve to the correct server/tool identity.
4. Missing or ambiguous references are handled correctly.
5. Manual selection restricts the available tool definitions.
6. Restricted mode rejects unauthorized tool calls at the execution boundary.
7. Conflicting explicit references and manual restrictions are handled deterministically.
8. Existing confirmation policies remain effective.
9. Tool discovery results are reused rather than repeatedly fetched for every message.
10. MCP connection failures, timeouts, malformed responses, and tool errors are handled correctly.
11. Cancellation and streaming continue to work.
12. Existing non-MCP conversations remain functional.
13. Provider-specific tool-calling behavior remains compatible.
14. Settings persist and reload correctly where persistence is intended.
15. UI selection changes do not accidentally modify global configuration.

Use the repository's existing testing framework and conventions.

Mock external MCP servers and LLM endpoints where appropriate. Do not require live credentials or production services for routine tests.

Run the relevant test suite and build the project. Fix regressions introduced by the implementation.

Do not claim tests passed unless they were actually executed.

## 10. Implementation Constraints

- No unrelated refactoring.
- No unnecessary interfaces, abstraction layers, or dependencies.
- No changes to existing business behavior outside the scope of MCP selection.
- No hardcoded server names or tool names in the core selection logic.
- No arbitrary tool selection based only on keyword matching when the existing agent can make the decision through native tool calling.
- No extra LLM call solely for routing.
- No changes that weaken existing permissions or confirmation requirements.
- Preserve existing configuration compatibility wherever possible.
- Keep the implementation maintainable and consistent with the repository.
- Do not commit, push, or publish changes.

## 11. Required Workflow

Work in these phases:

### Phase 1: Inspect and plan

Understand the current implementation and identify the files and components that need changes.

Present a concise plan and the relevant architectural findings before making substantial changes.

### Phase 2: Implement

Implement automatic selection, explicit references, manual selection, restricted automatic mode, configuration, and required UI integration using the existing architecture.

Prioritize the smallest correct implementation over a broad redesign.

### Phase 3: Validate

Build the project, run relevant automated tests, and verify provider compatibility, security boundaries, cancellation, and performance-sensitive paths.

Fix issues introduced by the changes.

### Phase 4: Report

Provide a concise final report containing:

- What was implemented.
- How each selection mode works.
- Which files and components changed.
- How explicit references are resolved.
- How restrictions and confirmations are enforced.
- What was done to avoid additional latency.
- Build and test results, including failures or unverified areas.
- Any remaining limitations or follow-up work.

Do not commit or push.

## 12. Definition of Done

The task is complete only when:

- Users can submit ordinary prompts without manually selecting MCP tools.
- The agent can use available MCP tools through the existing tool-calling pipeline.
- Users can explicitly reference tools where supported.
- Users can optionally restrict available tools through the UI.
- Restricted tools cannot be executed by bypassing the UI-level selection.
- Existing permission and confirmation policies remain effective.
- Tool discovery does not unnecessarily repeat for every message.
- No additional LLM call is introduced solely for MCP routing.
- Existing non-MCP conversations continue to work.
- The project builds successfully and relevant tests are executed.
- All unverified assumptions and limitations are clearly reported.

Start with repository investigation. Base every implementation decision on the actual codebase rather than assumptions.