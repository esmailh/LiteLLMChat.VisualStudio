You are working on my existing Visual Studio extension project.

Your task is to study the actual implementation of the Chat experience in VS Code's GitHub Copilot / Copilot Chat and then reproduce the relevant architecture, UI behavior, interaction model, and implementation patterns in my existing project.

## Main Objective

I do NOT want you to build a generic AI chat UI from scratch.

I want you to:

1. Find and inspect the relevant VS Code source code responsible for the GitHub Copilot Chat experience.
2. Understand how the Chat UI, message rendering, input box, model selection, streaming responses, tool/function calls, progress states, errors, attachments/context, history, commands, and interactions are implemented.
3. Trace the implementation from the UI layer down through the chat controller/provider/service layer.
4. Identify which parts are reusable concepts and which parts are specific to VS Code.
5. Map those concepts to the architecture of my existing Visual Studio extension.
6. Implement the equivalent functionality in my project while preserving my existing architecture and requirements.

## Important: Source-Code-First Approach

Before writing code:

* Locate the actual VS Code repositories/source files.
* Find the exact files/classes/components responsible for Copilot Chat.
* Read the relevant implementation, not just documentation or screenshots.
* Follow the call flow between the important components.
* Identify the communication protocol/API used between the Chat UI and backend/model layer.
* Identify how messages and streaming responses are represented.
* Identify how model selection is implemented.
* Identify how Chat state/history is maintained.
* Identify how loading/streaming/cancellation/error states are handled.
* Identify how tool calls or agent actions are represented in the UI.
* Identify how markdown/code blocks/diffs/files/context are rendered.
* Identify keyboard shortcuts and important interaction behavior where applicable.

Create a short technical analysis before implementation containing:

### 1. VS Code Architecture

Show the relevant architecture approximately like:

UI
→ Chat UI component
→ Chat controller/provider
→ Chat service/session
→ model/provider
→ streaming response
→ message renderer

Use the actual architecture you discover. Do not assume the above is correct.

### 2. Source Mapping

Create a table:

| VS Code Component | Source File | Responsibility | Equivalent in My Project |
| ----------------- | ----------- | -------------- | ------------------------ |

Only include components that are actually relevant to the Chat experience.

### 3. Behavior Mapping

Document:

* Chat initialization
* New conversation
* Sending a message
* Streaming
* Stop/cancel
* Regenerate/retry
* Model selection
* Message rendering
* Markdown
* Code blocks
* Tool calls
* Errors
* Loading states
* Conversation history
* Context/attachments
* Keyboard behavior
* Any other important Chat behavior you discover

## Implementation Requirements

After the analysis, implement the feature in my existing project.

### Critical rule

Do NOT create a parallel or separate Chat application.

The implementation must integrate into the existing project and existing Copilot/Chat architecture.

Reuse existing components, services, models, dependency injection, configuration, and communication mechanisms wherever possible.

Do not introduce unnecessary abstractions, interfaces, frameworks, or architectural layers.

Prefer the simplest implementation that accurately reproduces the relevant VS Code behavior.

## UI Requirement

The resulting Chat UI should behave as closely as technically possible to VS Code Copilot Chat.

Pay particular attention to:

* Layout
* Message spacing
* User vs assistant messages
* Markdown rendering
* Code blocks
* Syntax highlighting
* Copy buttons
* Streaming rendering
* Loading/progress indicators
* Stop generation
* Retry/regenerate
* Model selector
* Input box
* Send button
* Keyboard shortcuts
* Error presentation
* Conversation state
* Tool/agent progress
* Context indicators
* File/code references
* Scrolling behavior
* Auto-scroll while streaming
* Handling very long responses

Do not merely copy the visual appearance.

Reproduce the underlying interaction behavior where applicable.

## Model Provider

My extension needs to work with my own LLM server/provider.

Do not hard-code the VS Code/GitHub backend.

Keep the model/provider layer compatible with my existing custom LLM configuration.

The Chat UI should communicate with my existing model/provider implementation.

If VS Code uses a protocol or abstraction that is useful for this purpose, reproduce the concept in my project without unnecessarily copying VS Code-specific infrastructure.

## Existing Project Constraints

Before changing anything:

1. Inspect the complete project structure.
2. Understand the current architecture.
3. Identify the existing Chat-related code.
4. Identify the current model/provider implementation.
5. Identify the current model-selection implementation.
6. Identify existing UI components that should be reused.
7. Identify existing configuration/settings.
8. Identify any limitations imposed by Visual Studio 2022.

Do not replace working functionality just because VS Code implements it differently.

Preserve existing functionality unless the change is required for this implementation.

## Do Not Guess

If you find multiple possible implementations in VS Code:

* Find the currently relevant implementation.
* Prefer the implementation used by the current Copilot Chat experience.
* Check recent source history when necessary.
* Clearly document which implementation you chose and why.

Do not invent APIs, classes, source paths, or behavior.

## Compatibility

The target is Visual Studio 2022.

Adapt VS Code concepts to the Visual Studio extension architecture where necessary.

Do NOT attempt to literally copy VS Code-specific TypeScript/React/webview infrastructure into Visual Studio if it is incompatible.

Instead:

VS Code implementation
→ understand architecture/behavior
→ identify equivalent concept
→ implement equivalent in Visual Studio.

The goal is behavioral and architectural equivalence, not blindly copying incompatible source code.

## Implementation Process

Follow this order:

### Phase 1 - Repository Investigation

Inspect the existing project and identify:

* project structure
* UI technology
* extension architecture
* Chat implementation
* provider/model implementation
* configuration
* existing dependencies

Do not modify code yet.

### Phase 2 - VS Code Investigation

Inspect the actual VS Code/Copilot Chat source.

Find the relevant components and follow their implementation.

Do not stop at the first UI component you find.

Trace the complete path from user input to model response and rendering.

### Phase 3 - Architecture Mapping

Produce:

* VS Code architecture
* Existing project architecture
* Mapping between them
* Gaps
* Required changes

### Phase 4 - Implementation

Implement the changes incrementally.

After each major change:

* build
* fix compilation errors
* run relevant tests
* inspect the resulting diff

### Phase 5 - Verification

Verify at minimum:

1. Chat opens correctly.
2. New conversation works.
3. User can send a message.
4. Model response streams correctly.
5. Streaming UI behaves correctly.
6. Stop/cancel works.
7. Errors are displayed correctly.
8. Model selection works.
9. Markdown renders correctly.
10. Code blocks render correctly.
11. Long responses do not break the UI.
12. Conversation state is preserved correctly.
13. Existing extension functionality still works.
14. The project builds successfully.

## Important Restrictions

Do NOT:

* create a fake/demo Chat
* create mock model responses
* hard-code sample conversations
* hard-code model names
* replace the existing provider with a mock
* create a second independent Chat system
* remove existing functionality unnecessarily
* add dependencies without justification
* blindly copy VS Code source code
* copy GitHub proprietary implementation if it is not legally/reusably available
* assume a source file is relevant without tracing its usage
* stop after creating only the UI

## Legal / Licensing

Only use source code that is publicly available and compatible with its license.

If a VS Code/Copilot component is proprietary, unavailable, generated, or not legally reusable:

* do NOT copy it verbatim.
* document what you learned from its behavior/architecture.
* implement an independent equivalent in my project.

Clearly identify any such cases.

## Deliverables

At the end provide:

### A. Investigation Summary

What VS Code components were inspected and what they do.

### B. Architecture Comparison

VS Code architecture vs my project's architecture.

### C. Implemented Changes

List every important file changed and why.

### D. Behavioral Differences

List anything that could not be reproduced exactly and explain why.

### E. Verification

Provide:

* build result
* tests executed
* test results
* important manual verification results

### F. Remaining Work

List only genuinely remaining work.

Do not hide skipped work.

## Final Requirement

The most important requirement is:

Do not build "a Chat UI inspired by Copilot".

Study the actual VS Code Copilot Chat implementation and reproduce its relevant architecture and behavior in my Visual Studio extension as closely as the Visual Studio platform allows.

The final implementation should feel like the same Chat experience, adapted to Visual Studio and my custom LLM provider, rather than a separately designed Chat application.
