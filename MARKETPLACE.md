# LiteLLM Chat for Visual Studio 2022

Bring an OpenAI-compatible AI assistant into Visual Studio without leaving your IDE. LiteLLM Chat connects to one or more LiteLLM gateways, lets you choose the model that fits the task, and provides a focused chat experience for understanding, planning, and changing code.

> **Bring your own model and endpoint.** This extension does not include an AI model or hosted service. You provide the LiteLLM/OpenAI-compatible endpoint and credentials.

## Highlights

- **Ask, Agent, and Plan modes** inspired by modern coding assistants.
- **Multiple LiteLLM gateways** with independent enable/disable controls.
- **Model discovery** from configured `/v1/models` endpoints.
- **Streaming responses** with optional reasoning display.
- **Markdown rendering**, fenced code blocks, inline code, and Copy Code.
- **Solution-aware context**: active file, selected text, files, folders, and Problems from the Error List.
- **Workspace tools** for reading, searching, listing, opening, and editing solution files.
- **Safe editing workflow** with optional confirmation, visible diffs, Undo, and Visual Studio integration.
- **Conversation history** with multiple sessions, rename-from-first-prompt, delete, retry, and cancellation.
- **Right-to-left chat rendering**, including mixed Persian/Arabic and English text.
- **Visual Studio theme integration** for light, dark, and high-contrast environments.
- **Local diagnostics** through the LiteLLM Output window and status bar.

## Requirements

- Visual Studio 2022 17.x or later supported version.
- Windows with the Visual Studio extension development components installed by Visual Studio.
- .NET Framework 4.8 targeting support.
- A reachable LiteLLM or OpenAI-compatible API exposing:
  - `GET /v1/models`
  - `POST /v1/chat/completions`

The extension supports Visual Studio Community, Professional, and Enterprise editions. It does not provide an API key, model, or cloud account.

## Getting started

1. Install **LiteLLM Chat for Visual Studio** from the Visual Studio Marketplace.
2. Open **Tools > LiteLLM: Open Dashboard**.
3. Add a server:
   - Give it a descriptive name.
   - Enter the base URL, for example `http://localhost:4000`.
   - Enter an optional API key.
4. Select **Test**, then refresh and enable the models you want to use.
5. Open **Tools > LiteLLM: Open Chat**.
6. Select a conversation, model, and chat mode, then send a prompt.

For a standard LiteLLM proxy, use the proxy base URL without appending `/v1` if the extension is already adding the API path.

## Chat modes

### Ask

Use Ask for explanations, code questions, documentation, debugging guidance, and general assistance. Ask mode is read-only: it does not inspect or modify workspace files through tools.

### Agent

Use Agent when you want the assistant to work on the solution. The agent can inspect relevant files, search the codebase, open files in the editor, apply requested edits, and verify the result. It works iteratively and reports the actions it completed.

When file editing is enabled, edits are applied to the workspace rather than printed as snippets. Each edit can be reviewed from the chat, copied as a diff, or undone.

### Plan

Use Plan when you want analysis before implementation. The assistant investigates the workspace as needed and returns a structured implementation plan. Plan mode does not modify files and asks for confirmation before implementation.

## Context and attachments

The composer supports VS Code-style context attachment:

- **Active file**: optionally follows the file currently open in the editor.
- **Files and folders**: searchable multi-select from the current solution.
- **Selection**: sends the current editor selection.
- **Problems**: sends available errors and warnings from Visual Studio's Error List.

Use the paperclip/context button or type `#` at the beginning of a word. Attached files are represented by compact chips above the composer. Click a chip to open its file or use `×` to remove it.

When tool calling is enabled, file contents are read on demand. When tool calling is disabled, bounded file excerpts are included only for the current request. Conversation history stores bounded context data rather than an unbounded copy of the workspace.

## Workspace tools

In Agent mode, the assistant can use tools for:

- Finding the active file and selected text.
- Listing solution files.
- Searching text across supported source files.
- Reading bounded line ranges.
- Opening a file at a specified line.
- Creating files and directories.
- Applying patches and replacing exact text.
- Reviewing and undoing applied edits.

Workspace paths are confined to the open solution root. Paths outside the solution and directory traversal attempts are rejected. The extension does not execute arbitrary shell commands, build commands, or network requests through workspace tools.

## Editing and permissions

Open **Tools > Options > LiteLLM** to configure tool calling and file editing.

- Disable **Enable file editing** to make the assistant read-only.
- Enable **Confirm before each edit** to review a proposed diff before it is applied.
- Use **Allow** or **Skip** for each proposed edit.
- Use Visual Studio **Undo** or the edit row's **Undo this edit** command to revert an applied change.
- Set a maximum tool-round budget to prevent an agent turn from continuing indefinitely. When the limit is reached, the chat offers a Continue or Stop checkpoint.

## Conversations and controls

- Press **Enter** to send.
- Press **Shift+Enter** for a new line.
- **Stop** cancels an active response.
- **New chat** starts an independent conversation.
- **Delete** removes the selected conversation after confirmation.
- **Retry response** regenerates the latest completed or failed turn, including its prompt and attachments.
- Assistant responses can be selected and copied. Right-click a response for Copy message, Retry response, request/response diagnostics, and related actions.

The composer and assistant responses use right-to-left layout when appropriate, so Persian, Arabic, Hebrew, English, and mixed-script text remain readable. Code blocks remain left-to-right for accurate source-code reading.

## Configuration

The Dashboard manages servers and models. **Tools > Options > LiteLLM** manages behavior such as:

- Request timeout.
- Streaming responses.
- Temperature and maximum tokens.
- Reasoning visibility.
- Tool calling.
- File editing and edit confirmation.
- Maximum tool rounds.
- Logging and context behavior.

API keys are protected with Windows DPAPI for the current Windows user. Settings and conversation data are stored locally under `%LOCALAPPDATA%\LiteLLM.VisualStudio`. The extension does not send data to a service operated by the extension author; prompts and attached context are sent to the endpoint you configure.

## Privacy and security

- Requests are sent to your configured server only.
- API keys are encrypted for the current Windows user and are not intentionally written to logs.
- Workspace tools are restricted to the open solution root.
- File editing can be disabled globally or confirmed per edit.
- Conversation history is stored locally and should be treated as sensitive because it may contain prompts, responses, file paths, and selected context.
- Your configured LiteLLM gateway and model provider may retain or process requests according to their own policies. Review those policies before sending confidential source code.

## Troubleshooting

### No models appear

Verify the server base URL, confirm that `/v1/models` is reachable, enable the server, and select Refresh in the Dashboard.

### Authentication fails

Check the API key and the gateway's authentication configuration. A blank key preserves an existing saved key; remove and recreate the server if you need to remove it.

### A request returns 404

Check whether the configured URL already includes `/v1`. The extension expects a base URL and builds the OpenAI-compatible routes itself.

### Requests time out

Increase the request timeout in **Tools > Options > LiteLLM** and inspect **View > Output > LiteLLM** for diagnostics.

### The agent does not edit files

Confirm that Agent mode is selected, file editing is enabled, and the configured model supports tool calling. Ask mode and Plan mode are intentionally non-editing.

## Support and feedback

Please report bugs and feature requests in the project's GitHub repository:

<https://github.com/esmailh/LiteLLMChat.VisualStudio/issues>

When reporting a problem, include the Visual Studio version, extension version, model/gateway type, and relevant LiteLLM Output diagnostics. Never include API keys or confidential source code.

## License

See the repository license and notices:

<https://github.com/esmailh/LiteLLMChat.VisualStudio>
