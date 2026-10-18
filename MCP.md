# Task: Implement Complete MCP Tool Invocation Support in VS Code Copilot Chat

## Objective

Inspect the existing project and implement complete, reliable MCP tool invocation support for the following three usage patterns:

1. **Automatic tool selection:** The agent identifies when an MCP tool is relevant and invokes it automatically.
2. **Explicit tool selection using `#`:** The user can discover and select available MCP tools from the chat input using the existing VS Code Copilot Chat tool-selection mechanism.
3. **Explicit tool invocation through natural-language instructions:** The user can request a specific MCP tool by name, and the agent should invoke that tool when appropriate.

The implementation must integrate with the project's existing architecture and preserve existing functionality.

## Phase 1: Inspect the Existing Implementation

Before modifying any files:

1. Inspect the entire relevant project structure, including:
   - MCP server configuration and lifecycle management.
   - MCP client implementation and protocol handling.
   - Tool discovery and registration.
   - Copilot Chat integration.
   - Chat input and tool-selection UI.
   - Agent orchestration and tool invocation.
   - Tool execution, approval, error handling, and result processing.
   - Existing tests and documentation.

2. Determine whether the project:
   - Implements its own chat UI and agent orchestration.
   - Integrates with VS Code's native Copilot Chat APIs.
   - Uses a custom tool registry or VS Code's native MCP/tool discovery mechanisms.
   - Already supports any of the three requested behaviors.

3. Identify the exact missing functionality and the smallest architectural changes needed to implement it.

4. Document the findings and proposed implementation plan before making changes.

Do not assume that VS Code's native `#` tool picker can be reproduced or controlled through a public API. Verify the available APIs and the project's actual integration model first.

## Phase 2: Implement Automatic MCP Tool Selection

Implement or complete automatic tool selection.

Requirements:

- Discover tools exposed by connected and enabled MCP servers.
- Make tool names, descriptions, and input schemas available to the agent through the supported tool-calling mechanism.
- Allow the model to select appropriate tools based on the user's request.
- Support multiple tool calls when a task requires multiple steps.
- Pass validated arguments to the correct MCP server and tool.
- Return tool results to the model so it can continue reasoning and produce a final response.
- Handle tool errors, timeouts, server disconnections, invalid arguments, and unavailable tools.
- Respect the existing permission and approval settings.
- Prevent unauthorized tools from executing.
- Avoid unnecessary tool calls when the request does not require external capabilities.
- Ensure that the agent does not claim a tool was executed unless an actual invocation occurred.

Do not implement keyword matching as the only tool-selection strategy. Natural-language understanding and the model's supported tool-calling mechanism should drive tool selection.

## Phase 3: Implement Explicit Tool Selection Using `#`

Make MCP tools discoverable and selectable through the chat interface.

First, determine whether the project can use VS Code's native Copilot Chat tool picker.

### If the project uses native Copilot Chat tool integration

- Register MCP tools through the supported extension APIs.
- Ensure available tools appear in the native tool picker when supported.
- Preserve native enablement, selection, and approval behavior.
- Verify that selecting a tool makes it available to the relevant conversation or agent.

### If the project uses a custom chat UI

- Inspect whether `#`-based completion or tool selection already exists.
- If native integration is unavailable, implement an equivalent inline tool picker.
- Trigger suggestions when the user types `#`.
- Display available MCP tools with their server and tool names, descriptions, and enabled status.
- Support filtering tools as the user types.
- Support keyboard navigation, selection, and insertion into the chat input.
- Handle tool names containing dots, underscores, hyphens, or other valid characters.
- Keep the picker synchronized with changes to connected MCP servers and their available tools.
- Distinguish between selecting a tool and actually invoking it.
- Ensure that selecting a tool makes the selected tool available to the agent for the current request.

Do not insert decorative `#tool-name` text without implementing the corresponding selection and invocation behavior.

Do not claim to have integrated with the native VS Code tool picker if the implementation actually uses a custom UI.

## Phase 4: Implement Explicit Invocation by Tool Name

Allow users to request a specific MCP tool in natural language.

Examples:

- "Use the `search_customers` MCP tool to find customer 123."
- "حتماً از ابزار `query_database` استفاده کن و نتیجه را برگردان."
- "Use the `get_order` tool from the `orders` MCP server."
- "First call `search_customers`, then call `get_order` using the returned customer or order identifier."

Requirements:

- Resolve requested tool names against the actual registry of available tools.
- Support server-qualified names when multiple MCP servers expose tools with identical names.
- Prefer exact tool-name matches over ambiguous fuzzy matches.
- If multiple tools match the request, ask for clarification rather than invoking an arbitrary tool.
- If the requested tool does not exist or is unavailable, explain that clearly.
- Ensure that explicit instructions influence tool selection without bypassing authorization, validation, or approval requirements.
- Allow tool results to be incorporated into subsequent tool calls when the task requires a sequence of operations.
- Preserve the user's original task and constraints throughout the interaction.

Do not assume that mentioning a tool name in the prompt guarantees that the underlying model will call it. Implement the appropriate tool-selection, orchestration, or routing behavior supported by the existing architecture.

## Phase 5: Tool Invocation and Security

Verify the complete execution pipeline:

1. Discover the tool.
2. Check whether its server and tool are enabled.
3. Resolve the requested tool.
4. Validate the input arguments against the tool schema.
5. Apply existing authorization and approval rules.
6. Execute the MCP call.
7. Process the response, including structured content and errors.
8. Return the result to the agent.
9. Display the execution and final result appropriately in the chat.

Security requirements:

- Never bypass existing approval or permission controls.
- Do not expose API keys, access tokens, or other secrets in chat logs.
- Do not execute a tool based solely on untrusted content returned by another tool.
- Handle malformed responses and server failures safely.
- Prevent accidental duplicate execution when processing retries or repeated model tool calls.
- Do not weaken existing security controls to make demonstrations pass.

## Phase 6: User Experience

Provide clear feedback about MCP tool usage.

The chat interface should make it possible to distinguish between:

- A tool that is available.
- A tool that is selected or enabled.
- A tool invocation that is awaiting approval.
- A tool that is currently executing.
- A successful tool invocation.
- A failed invocation.

Where practical, display the MCP server name, tool name, execution status, and a readable summary of the result.

Do not expose sensitive tool arguments or secrets in the UI.

Follow the project's existing design conventions and avoid introducing unnecessary abstractions, dependencies, or architectural layers.

## Phase 7: Automated Tests

Add or update tests for all three usage patterns.

### Automatic selection

- A relevant request results in the appropriate tool being invoked.
- An unrelated request does not invoke an unnecessary tool.
- Multiple tool calls execute in the correct sequence.
- Tool results are passed back to the agent.
- Tool failures are handled without falsely reporting success.

### `#` tool selection

- Available tools appear in the picker.
- Filtering and selection work correctly.
- Selecting a tool makes it available to the current request.
- Disabled or disconnected tools cannot be invoked.
- The picker updates when the tool registry changes.

### Explicit tool invocation

- Exact tool names resolve correctly.
- Server-qualified names resolve correctly when names collide.
- Unknown or ambiguous tool names are handled safely.
- Explicit requests do not bypass permissions or validation.

### Regression and integration

- Existing chat functionality continues to work.
- Existing model providers and agent modes remain functional.
- Existing MCP configuration and lifecycle behavior remain intact.
- Tool results, errors, cancellation, and approval flows work correctly.

Use the project's existing test framework and conventions. Prefer deterministic tests with mocked MCP servers and model responses where possible.

## Phase 8: Validation

After implementation:

1. Build the project.
2. Run relevant unit and integration tests.
3. Run formatting, linting, or static analysis if already configured.
4. Fix regressions introduced by the changes.
5. Verify each usage pattern with concrete test scenarios.
6. Inspect the final diff for unnecessary changes, security regressions, and unrelated refactoring.

If a feature depends on a VS Code API that is unavailable to the project, document the limitation and implement the closest supported alternative. Do not introduce unsupported APIs or claim that a feature works when it has not been verified.

## Constraints

- Do not change existing business or functional behavior unrelated to this task.
- Do not remove or replace existing features without a demonstrated technical necessity.
- Do not introduce unnecessary interfaces, service layers, or abstractions.
- Follow the project's existing coding conventions and target framework.
- Do not hardcode MCP server names, tool names, or schemas.
- Do not commit or push changes.
- Do not silently skip failed tests or report unverified behavior as complete.
- Keep implementation changes focused and maintainable.

## Required Deliverables

At completion, provide:

1. A concise summary of the existing architecture and identified gaps.
2. The files changed and the purpose of each change.
3. An explanation of how automatic selection works.
4. An explanation of how `#` tool selection works, including whether it uses native VS Code integration or a custom picker.
5. An explanation of explicit tool invocation and tool-name resolution.
6. Build and test results, including any failures.
7. Any limitations imposed by VS Code APIs or the existing architecture.
8. A manual verification guide with example prompts for all three usage patterns.

## Execution Workflow

Work in clearly defined phases:

- Phase 1: Inspect and document.
- Phase 2: Implement the missing functionality.
- Phase 3: Test and validate.
- Phase 4: Review and report.

Start with Phase 1. Do not begin implementation until the architecture and supported integration points have been established. After inspection, present the findings and implementation plan for review before proceeding.

The objective is working, verifiable MCP tool invocation, not merely adding UI elements or documenting expected behavior.