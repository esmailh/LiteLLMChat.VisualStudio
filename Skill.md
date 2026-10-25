# Task: Implement Agent Skills Support in LiteLLM Chat Provider for Visual Studio

## 1. Project Context

Repository: https://github.com/esmailh/LiteLLMChat.VisualStudio

This is an existing Visual Studio extension that provides an AI chat experience using configurable LLM providers and models. Preserve its existing architecture, user experience, chat functionality, provider integrations, and supported execution modes.

The goal is to add a complete, production-quality Skills feature that makes reusable AI workflows easy to create, discover, manage, and use.

Do not treat this as a greenfield project. First understand the current implementation, then integrate Skills into the existing architecture with minimal, justified changes.

## 2. Mandatory Engineering Rules

Before implementing anything:

1. Inspect the repository structure, existing architecture, UI, chat pipeline, prompt construction, conversation state, settings persistence, model/provider abstractions, and all supported execution harnesses.
2. Identify the existing mechanisms for user confirmation, file editing permissions, tool execution, cancellation, and error reporting.
3. Identify how the extension handles slash commands and special chat input, if supported.
4. Determine which existing components can be reused and which genuinely need new abstractions.
5. Read the official Agent Skills specification:
   https://agentskills.io/specification
6. If any existing architecture differs from the assumptions in this prompt, adapt the design to the actual codebase rather than forcing an incompatible implementation.

Engineering constraints:

- Do not introduce unrelated refactoring.
- Do not change existing business behavior or existing provider behavior.
- Do not break existing LiteLLM, custom endpoint, model selection, chat, or harness functionality.
- Avoid unnecessary interfaces, abstractions, dependencies, and configuration options.
- Follow the repository's existing coding conventions and target framework.
- Keep the implementation maintainable, testable, and production-ready.
- Preserve backward compatibility with existing user settings and conversation history.
- Do not commit, push, publish, or release anything.
- Do not overwrite user files or existing Skills without explicit confirmation.
- Do not silently swallow errors.
- Do not claim that a feature works unless it has been implemented and verified.

## 3. Product Goal

Implement Skills as reusable, discoverable instructions that teach an AI agent how to perform specific tasks consistently.

Examples:

- Review C# code according to repository conventions.
- Analyze a bug before proposing a fix.
- Create an API endpoint following the existing architecture.
- Generate unit tests for changed code.
- Review database migrations.
- Explain and troubleshoot a specific subsystem.

The feature must be easy enough for a user to manage without manually understanding the Skills file format.

Use the open Agent Skills format, centered on `SKILL.md`, instead of inventing a proprietary format.

Official specification:
https://agentskills.io/specification

Do not assume every LLM provider or execution harness has native Skills support. Implement compatibility through the extension's existing architecture.

## 4. Phase 1: Repository Analysis and Design

Before coding, inspect the actual repository and produce a concise design document covering:

- Existing chat request lifecycle.
- Existing UI composition and command handling.
- Existing settings persistence.
- Existing provider and harness abstractions.
- Current tool and permission mechanisms.
- Proposed Skills storage locations.
- Proposed discovery, validation, loading, and execution lifecycle.
- Required code changes and tests.
- Compatibility risks and mitigation.

Identify the smallest coherent implementation that integrates with the current codebase.

Do not stop after producing the design document. After documenting the design, continue with implementation unless a genuine blocker requires clarification.

## 5. Phase 2: Skills Data Model and Registry

Implement a Skills subsystem that is independent of any particular LLM provider or execution harness.

It should support:

- Discovering Skills from configured directories.
- Reading and validating `SKILL.md` metadata.
- Exposing Skill name, description, source location, and availability.
- Enabling and disabling Skills.
- Detecting duplicate names and conflicting definitions.
- Refreshing the registry when Skills are added, changed, or removed.
- Handling inaccessible directories, invalid files, and malformed metadata gracefully.
- Avoiding unnecessary repeated disk reads.
- Supporting cancellation where appropriate.

Keep the design simple. Do not introduce a database unless the existing architecture clearly requires one.

### Standard Skill structure

Support the standard directory-based format:

`my-skill/SKILL.md`

Optionally support associated resources:

- `references/`
- `scripts/`
- `assets/`

Do not invent nonstandard required fields. Validate the required metadata and any additional fields according to the official specification.

Do not execute scripts merely because they exist inside a Skill.

### Storage locations

Support at least:

1. Personal Skills, available across projects.
2. Project Skills, scoped to a repository or workspace.

Use an explicit, documented default personal directory under the user's profile, such as:

`%USERPROFILE%\.llm123\skills\`

For project Skills, support a configurable project-local directory, using `.agents/skills/` as the proposed default.

Inspect the existing configuration and filesystem conventions before finalizing these defaults.

Additionally, consider discovering existing Skills in compatible locations such as `.github/skills/` and `.claude/skills/`, where practical.

Do not assume these locations are interchangeable in every tool or that the same Skill is automatically supported by every harness.

Make storage locations configurable where appropriate, and avoid scanning the entire filesystem.

### Duplicate resolution

Define deterministic behavior for Skills with the same name.

- Never silently choose a random definition.
- Show the conflicting locations to the user.
- Document the precedence rules.
- Allow the user to resolve conflicts.
- Do not silently overwrite existing Skills.

## 6. Phase 3: Skills Management UI

Integrate Skills management into the existing Visual Studio extension UI.

Inspect the current WPF/XAML layout and use its established visual conventions. Do not create a separate standalone chat application.

Provide an accessible Skills management view or panel with:

- A list of discovered Skills.
- Search by name and description.
- Clear indication of personal versus project scope.
- Enabled/disabled status.
- Skill description and source path.
- Refresh action.
- Create Skill action.
- Edit/open existing Skill action.
- Delete action with confirmation.
- Import action where feasible.
- Clear error states and useful empty states.

Use asynchronous operations where appropriate so the UI remains responsive.

Do not overload the main chat window with excessive settings.

The basic workflow should be understandable without reading documentation.

### Safe file management

- Never delete or overwrite a Skill without confirmation.
- Never silently replace an existing Skill during import.
- Validate names and prevent path traversal.
- Prevent operations from escaping the intended Skills directory.
- Handle read-only files and permission errors gracefully.

## 7. Phase 4: Creating and Editing Skills with AI

Provide an AI-assisted Skill creation workflow.

The user should be able to:

1. Select `New Skill`.
2. Enter a name and describe what the Skill should accomplish.
3. Optionally provide examples, preferred conventions, or additional constraints.
4. Ask the currently configured AI model to generate the Skill.
5. Review the generated metadata and `SKILL.md`.
6. Edit the draft.
7. Save it to personal or project scope.

The generated Skill should include clear, actionable instructions and should not contain unnecessary generic prompt text.

Support editing an existing Skill without unintentionally replacing unrelated files.

If the user asks to improve an existing Skill, preserve its intended behavior and show the proposed changes before saving.

Where feasible, add an option to create a Skill from the current conversation or a selected portion of it.

This operation must produce a draft, not automatically save or execute unreviewed generated content.

Use the existing model/provider integration and prompt-building mechanisms. Do not hardcode an API key, provider, or model.

Handle model failures, malformed output, cancellation, and validation errors gracefully.

## 8. Phase 5: Skill Activation and Invocation

Implement three invocation modes.

### A. Automatic mode

The agent should be able to select relevant Skills based on the user's request.

Expose only the necessary Skill metadata, such as name and description, to the selection mechanism initially. Load the full Skill instructions only when needed.

Use the existing agent and tool orchestration architecture.

Automatic mode must not pretend that the model has loaded or executed a Skill when it has not.

Where the current harness supports iterative tool calls, make Skill discovery and loading available through the appropriate mechanism.

Where the harness does not support tools or iterative execution, use a compatible prompt/context injection strategy and clearly document its limitations.

Do not assume that adding Skill descriptions to a system prompt is sufficient to implement all Skills functionality.

### B. Manual mode

Allow the user to select a Skill explicitly from the UI.

The selected Skill should be made available to the current request without requiring the model to rediscover it.

Provide a clear way to remove or change the selection.

### C. Slash-command mode

If the existing chat input architecture supports slash commands, integrate with it.

Provide a command such as:

`/skill code-review`

Support additional user instructions where practical, for example:

`/skill create-api Create an endpoint for orders`

Requirements:

- Integrate with existing command parsing.
- Preserve ordinary messages that do not match a supported command.
- Handle unknown Skills and ambiguous names.
- Provide useful suggestions or completion where the existing UI supports it.
- Avoid sending the command text as an ordinary user message if it was successfully handled as a local Skill-selection command.
- Do not break existing slash commands or provider-specific commands.

Use the existing naming and parsing conventions when available.

## 9. Phase 6: Harness and Provider Compatibility

The Skills registry and storage layer must remain independent of the harness.

Implement a small, explicit integration layer that adapts Skills to the capabilities of each existing execution mode.

For each supported harness, determine:

- Whether it supports filesystem access.
- Whether it supports tools and iterative execution.
- How it receives system instructions and contextual information.
- Whether it has its own native Skills implementation.
- How it handles permissions and file modifications.

Do not duplicate the same Skill implementation in every harness.

If a harness already provides native Skills support, evaluate whether it is safer and more reliable to delegate to it or to use the extension's shared Skills implementation.

Avoid injecting the same Skill instructions twice.

For harnesses without filesystem access, do not assume they can read `SKILL.md` or associated resources. Supply the relevant content through the existing request context or explain the unsupported capability.

The feature should degrade gracefully when a provider or harness lacks required functionality.

## 10. Phase 7: Permissions and Security

Skills are instructions, not trusted code.

Treat Skill content, resource files, imported archives, and generated Skill drafts as untrusted input.

Requirements:

- Prevent path traversal.
- Validate file and directory access.
- Avoid automatically executing embedded scripts.
- Do not allow a Skill to override the extension's permission policies.
- Preserve existing file-edit confirmation behavior.
- Preserve existing tool-execution permission behavior.
- Preserve any existing distinction between read-only and editing modes.
- Require explicit approval for potentially destructive operations.
- Do not allow prompt text inside a Skill to grant itself additional permissions.
- Prevent malformed or oversized Skills from causing uncontrolled resource usage.
- Avoid sending unrelated local files or secrets to an LLM.
- Do not log API keys, sensitive file contents, or complete prompts unnecessarily.

If the current extension has a permission mode such as confirmation-before-editing versus unrestricted editing, Skills must respect that mode.

A Skill may request an operation, but the extension's permission system remains authoritative.

## 11. Phase 8: Testing

Add automated tests using the repository's existing test framework and conventions.

Cover at least:

### Registry and parsing

- Valid Skill discovery.
- Required metadata validation.
- Missing `SKILL.md`.
- Malformed metadata.
- Invalid names.
- Duplicate names.
- Unreadable directories.
- Optional resource directories.
- Refresh after file changes.
- Personal versus project scope.

### Invocation

- Manual Skill selection.
- Automatic Skill resolution where implemented.
- Explicit Skill invocation.
- Unknown Skill.
- Ambiguous Skill.
- Ordinary messages unaffected by slash-command parsing.
- No duplicate injection of Skill instructions.
- No stale content after refresh.

### Security and persistence

- Path traversal attempts.
- Deletion confirmation behavior.
- Existing Skill preservation.
- Import conflicts.
- Settings migration and backward compatibility.
- Cancellation and error propagation.

### Integration

- Existing provider behavior remains unchanged.
- Existing chat behavior remains unchanged.
- Existing harness permissions remain authoritative.
- Unsupported harness capabilities are handled gracefully.

Run the relevant tests and build the extension.

Do not claim successful validation if a test, build, or integration check was not actually run.

If Visual Studio-specific integration tests cannot run in the current environment, clearly state what was tested and what remains unverified.

## 12. Implementation Quality

Follow these architectural principles:

- Keep Skills domain logic separate from WPF presentation.
- Reuse existing settings and service patterns.
- Avoid excessive interfaces and unnecessary dependency injection.
- Use asynchronous file operations where appropriate.
- Use deterministic Skill resolution.
- Provide meaningful logging without leaking sensitive information.
- Keep UI code-behind consistent with the existing project style.
- Use small, cohesive components.
- Document non-obvious design decisions.
- Avoid unnecessary external dependencies.

Do not create a framework more complicated than the feature itself.

## 13. Suggested User Experience Defaults

Prefer the following defaults unless the actual repository architecture gives a strong reason otherwise:

- Default invocation mode: Automatic.
- Manual Skill selection is always available.
- Project Skills take precedence only when the precedence is deterministic, documented, and visible to the user.
- Full Skill instructions are loaded on demand when the execution model supports it.
- AI-generated Skills require review before saving.
- Destructive operations require confirmation.
- No Skills installed: show a helpful empty state with `Create Skill` and `Import Skill` actions.
- Conflicting Skills: show a clear conflict instead of silently selecting one.
- A running request should clearly indicate which Skill or Skills were selected and loaded.

Keep the UI compact and consistent with the existing extension.

## 14. Required Deliverables

Produce:

1. A concise architecture/design document.
2. A list of changed and added files with explanations.
3. The implemented Skills registry and loader.
4. Skills management UI.
5. AI-assisted Skill creation and editing.
6. Manual and automatic invocation.
7. Slash-command integration where compatible with the existing chat architecture.
8. Harness-specific integration and compatibility handling.
9. Automated tests.
10. Updated user documentation explaining:
   - What Skills are.
   - Where Skills are stored.
   - How to create, import, edit, and delete them.
   - How automatic and manual invocation work.
   - How to invoke a Skill from chat.
   - Which capabilities depend on the selected harness.
   - Security limitations.

## 15. Execution Workflow

Work in the following order:

1. Inspect the repository and current implementation.
2. Document the design and compatibility constraints.
3. Identify the smallest complete implementation plan.
4. Implement the registry and validation.
5. Implement storage and management UI.
6. Implement invocation and harness integration.
7. Implement AI-assisted creation.
8. Add tests and documentation.
9. Build and run all feasible validation checks.
10. Review the complete diff for regressions, security issues, unnecessary abstractions, and unrelated changes.

After each major phase, summarize the changes and validation results.

Do not commit or push.

Do not stop at a design document or a list of recommendations. Continue through implementation and verification unless a genuine blocker prevents progress.

At completion, provide a concise report containing:

- Features implemented.
- Important architectural decisions.
- Files changed.
- Tests and builds actually executed.
- Results and any failures.
- Known limitations.
- Remaining work, if any.

The final implementation must be consistent with the actual repository, preserve existing functionality, and provide a straightforward user experience for managing and using Skills.