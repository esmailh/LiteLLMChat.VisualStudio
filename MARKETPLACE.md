# LiteLLM Chat for Visual Studio

Bring an OpenAI-compatible AI assistant — or your Claude Code CLI — into Visual Studio without leaving the IDE. LiteLLM Chat connects to one or more LiteLLM gateways, lets you choose the model that fits the task, and provides a focused chat experience for understanding, planning, and changing code. It is also an MCP client, so the assistant can use the tools, resources and prompts of the servers you already run.

> **Bring your own model and endpoint.** This extension does not include an AI model or hosted service. You provide the LiteLLM/OpenAI-compatible endpoint and credentials, or the Claude Code CLI installed and signed in under your own account.

## Highlights

- **Two execution backends**: the built-in LiteLLM agent loop, or the Claude Code CLI for turns that should run in Claude's own harness.
- **Ask, Agent, and Plan modes** inspired by modern coding assistants.
- **Multiple LiteLLM gateways** with independent enable/disable controls.
- **Model discovery** from configured `/v1/models` endpoints.
- **MCP servers** over stdio, Streamable HTTP, and legacy HTTP+SSE: tools, resources, prompts, per-call approval, a browser, and a live log.
- **Streaming responses** with optional reasoning display.
- **Markdown rendering**, fenced code blocks, inline code, and Copy Code.
- **Clickable file and method links**: paths in a reply open in the editor, and C# types and members named in a reply jump to their declaration.
- **Solution-aware context**: active file, selected text, files, folders, and Problems from the Error List.
- **`@` file mentions** that search the solution as you type and attach the file you pick; connected MCP resources appear in the same list.
- **`#` tool mentions** that pick which MCP tools a turn may use, and `/` for the connected servers' prompts (or Claude's own commands on the Claude backend).
- **Workspace tools** for reading, searching, listing, opening, and editing solution files.
- **Safe editing workflow** with optional confirmation, visible diffs, Undo, and Visual Studio integration.
- **Automatic build verification** after edits, with errors handed back to the assistant to fix — for any project type — and an optional test run once the build passes.
- **Conversation history** with multiple sessions, rename-from-first-prompt, delete, retry, cancellation, and a per-conversation switch to send only the current turn.
- **Per-model token usage** counted from what each server reported, shown in the Dashboard.
- **Right-to-left chat rendering**, including mixed Persian/Arabic and English text.
- **Visual Studio theme integration** for light, dark, and high-contrast environments.
- **Local diagnostics** through the LiteLLM Output window and status bar.

## Requirements

- Visual Studio 2022 (17.x) or later, including Visual Studio 2026 (18.x).
- Windows 10 or 11 (x64).
- .NET Framework 4.8, which is included with Windows and Visual Studio.
- A reachable LiteLLM or OpenAI-compatible API exposing:
  - `GET /v1/models`
  - `POST /v1/chat/completions`

The extension supports Visual Studio Community, Professional, and Enterprise editions. It does not provide an API key, model, or cloud account.

The Claude Code backend is optional and needs the Claude Code CLI installed and signed in. MCP support is optional; the servers are yours, and they run only when you enable them. Running the solution's tests after an edit also needs `vstest.console.exe`, which ships with Visual Studio's test tools.

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

To use Claude Code instead, choose **Claude Code** in the chat's backend selector and set the executable under **Tools > Options > LiteLLM > Claude Code**. To use MCP servers, add them on **Dashboard > MCP** and tick the ones the LiteLLM backend should connect.

## Chat modes

### Ask

Use Ask for explanations, code questions, documentation, debugging guidance, and general assistance. Ask mode is read-only: it does not inspect or modify workspace files through tools.

### Agent

Use Agent when you want the assistant to work on the solution. The agent can inspect relevant files, search the codebase, open files in the editor, apply requested edits, and verify the result. It works iteratively and reports the actions it completed.

When file editing is enabled, edits are applied to the workspace rather than printed as snippets. Each edit can be reviewed from the chat, copied as a diff, or undone.

### Plan

Use Plan when you want analysis before implementation. The assistant investigates the workspace as needed and returns a structured implementation plan. Plan mode does not modify files and asks for confirmation before implementation.

## Execution backends

The backend selector in the chat header is independent of the Ask, Agent, and Plan modes on the LiteLLM path.

- **LiteLLM** (the default) runs the extension's own agent loop against your OpenAI-compatible endpoint. Workspace tools, edit and command approval cards, MCP approval cards, build and test verification all belong to this path.
- **Claude Code** starts one `claude -p` process per submitted turn with streamed JSON output. It uses Claude's own tools and permissions rather than the extension's: configure the executable and the allowed/disallowed tool lists under **Tools > Options > LiteLLM > Claude Code**. The CLI runs with `--permission-mode dontAsk`, so an allow-list entry is automatic approval and the extension cannot show its own per-call card inside that process. File-writing tools can be allowlisted only when editing is enabled and per-edit confirmation is off; `Bash` only when terminal tools are enabled and per-command confirmation is off; unsafe combinations are rejected before launch. **Stop** ends the process tree. On this backend `/` at the start of a line lists Claude's prompt-invoked commands, and a conversation resumes only after Claude returns a session ID.

Claude Code is a separate program: it needs its own installation and sign-in, and what it is sent is governed by Anthropic's terms rather than by your LiteLLM gateway.

## Context and attachments

The composer supports VS Code-style context attachment:

- **Active file**: optionally follows the file currently open in the editor.
- **Files and folders**: searchable multi-select from the current solution.
- **Selection**: sends the current editor selection.
- **Problems**: sends available errors and warnings from Visual Studio's Error List.
- **MCP resources**: text resources exposed by connected servers appear above the solution's files in the `@` list.

Type `@` at the start of a word to search the solution's files from the keyboard. The list narrows as you type, Up/Down moves through it, and Enter or Tab chooses a file. The file is attached as a chip and the `@` text is taken out of your prompt, so the message reads as you wrote it.

Use the paperclip/context button or type `#` at the beginning of a word to open the full multi-select picker. Attached files are represented by compact chips above the composer. Click a chip to open its file or use `×` to remove it.

On the LiteLLM backend with MCP tools connected, `#` first offers the servers' tools, and its last row, **Files & Folders…**, opens the file picker. On the Claude backend, or with no MCP tools connected, `#` opens that picker directly. See [MCP servers](#mcp-servers).

When tool calling is enabled, file contents are read on demand. When tool calling is disabled, bounded file excerpts are included only for the current request. Conversation history stores bounded context data rather than an unbounded copy of the workspace.

## MCP servers

The extension is an MCP client. Any stdio, Streamable HTTP, or legacy HTTP+SSE server can be added on **Dashboard > MCP**. Servers are only started when you enable them; a `.mcp.json` file in the repository is never loaded on its own.

- **Add, edit, test.** The server dialog changes with the transport: a command, arguments and environment for stdio; a URL, headers, bearer token or OAuth sign-in for HTTP. **Test connection** starts the server and lists its tools so the allow lists can be filled from real names.
- **Enable, disable, refresh.** Each row has a checkbox and a ↻. Ticking a server connects it at once, and a ticked server is connected again when the chat window opens, so a restart does not mean reconnecting each one by hand. Unticking disconnects it immediately. **↻** is a full reconnect: the session — and any stdio process — is dropped, started again, and its tools, resources and prompts are re-read. **Refresh** in the page header only re-reads the page. These controls drive the LiteLLM backend; the Claude CLI starts its own connections.
- **Import and export.** **Import JSON…** reads a Claude/Cursor `mcpServers` file; nothing is connected or launched until you review and enable each entry. **Export JSON…** writes the same shape without secrets.
- **Approval.** Every MCP tool call asks first, showing the server, the tool, its risk hints and its arguments with secret-looking values redacted. **Always allow this tool** writes to that server's allow list. A tool the server marks read-only runs without a card only when both the global switch and the server's own switch are on; destructive and open-world tools always ask. A denied call is still answered in the conversation, so the transcript stays valid.
- **Browse.** The detail pane lists tools, resources, prompts and the server's log. **Browse…** opens a larger window with a form generated from each tool's input schema, a resource reader, a prompt form and the live log.
- **Resources in the composer.** Type `@` and choose one of a connected server's resources to attach its text as a context chip.
- **Prompts in the composer.** On the LiteLLM backend, `/` at the start of a line lists the connected servers' prompts. Choosing one opens its argument form and inserts the text the server composed into the composer, to edit before sending.
- **Tools in the composer.** On the LiteLLM backend, `#` lists the connected servers' tools, filtered as you type; Enter or Tab inserts a server-qualified token such as `#Server.tool`. Those tokens are the turn's selection: the MCP tools offered to the model are narrowed to exactly the ones named, while the built-in tools stay. A name typed without the picker also resolves. A name that matches nothing draws a row saying so but does not take the turn's other tools away.
- **Logging.** `logging` messages and stderr go to the server's **Log** tab and, for warnings and errors, to **View > Output > LiteLLM**. Values that look like credentials are masked before they are stored in either place.
- **OAuth.** HTTP servers that advertise protected-resource metadata can be signed in to from the MCP page with PKCE in the system browser; tokens are kept under `%LOCALAPPDATA%\LiteLLM.VisualStudio\mcp-tokens.json` protected with Windows Data Protection.

**The Claude backend** is given the servers marked **Expose to Claude** through a per-run `--mcp-config` file under `%LOCALAPPDATA%`, deleted when the turn ends. Because the CLI runs with `--permission-mode dontAsk`, the extension cannot show its own per-call card inside it; only tools the policy already resolves to *allow* are put in `--allowedTools`, and everything else is reported by the CLI as denied. Use the LiteLLM backend when the per-call card matters.

Global switches — enable MCP, the read-only exception, sampling, elicitation, roots, timeouts, the per-turn tool cap and the strict Claude configuration — are under **Tools > Options > LiteLLM > MCP**.

> MCP servers are programs or services you choose to trust. A stdio server runs on your machine with your permissions, and an HTTP server receives the arguments the assistant sends it. Enable only servers you trust, and read the approval card before allowing a call.

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

Workspace paths are confined to the open solution root. Paths outside the solution and directory traversal attempts are rejected.

With terminal tools enabled, LiteLLM Agent mode adds a dedicated, shell-free `run_git` tool. It accepts exact argument arrays (and legacy strings), confirms every command, follows Git's configured push/upstream remote precedence, and selects push versus fetch URLs correctly. Recognized GitHub/GitLab credential failures can offer a separately confirmed browser login through `gh` or `glab`; self-hosted providers require a conservative host-scoped CLI probe. The remote protocol is preserved where supported, sensitive diagnostics are redacted, and a successful login permits exactly one retry of the unchanged Git command. Claude Code remains an independent subprocess and does not receive this Visual Studio tool; general Bash is not auto-enabled as a workaround.

## Editing and permissions

Open **Tools > Options > LiteLLM** to configure tool calling and file editing.

- Disable **Enable file editing** to make the assistant read-only.
- Enable **Confirm before each edit** to review a proposed diff before it is applied.
- Use **Allow** or **Skip** for each proposed edit.
- Use Visual Studio **Undo** or the edit row's **Undo this edit** command to revert an applied change.
- After the assistant changes a file, the solution is built automatically and the result appears as its own row in the chat. Any errors are handed straight back to the assistant, which fixes them and is built again — up to three times in a turn. This is on by default in Agent mode as **Verify the build after edits**, and it runs Visual Studio's own **Build Solution**, so a solution of any project type is built the way its project system expects rather than by a compiler-specific command.
- **Verify the tests after edits** (off by default) runs the solution's test projects once a build has passed in the same round and hands any failures back to the assistant. It never runs against assemblies from an earlier round, and it needs **Verify the build after edits**. A solution with no test project is left alone; a test run that could not start or reported nothing gets its own row saying so rather than counting as a pass.
- Set a maximum tool-round budget to prevent an agent turn from continuing indefinitely. When the limit is reached, the chat offers a Continue or Stop checkpoint.

## Clickable references

A reply is not a dead document. File paths and `path:line` references open at that line in the editor. When the open solution has been indexed, C# types and members named in a reply become links too: a dotted name such as `ChatStateLimits.Sanitize`, or a whole inline-code identifier such as `ResponseParser`, jumps to its declaration, and the tooltip names the file and line first. A name the solution does not declare — or a word that merely looks like one — stays ordinary text, so a link always goes somewhere. The index is built in the background from the solution's `.cs` files, rebuilt when a different solution is opened or a turn has edited files, and replies already on screen gain their links when it is ready.

Clicking a link never scrolls the transcript: the chat stays where you were reading.

## Conversations and controls

- Press **Enter** to send.
- Press **Shift+Enter** for a new line.
- **Stop** cancels an active response.
- **New chat** starts an independent conversation.
- **Delete** removes the selected conversation after confirmation.
- **Retry response** regenerates the latest completed or failed turn, including its prompt and attachments.
- Assistant responses can be selected and copied. Right-click a response for Copy message, Retry response, request/response diagnostics, and related actions.
- The composer's menu (☰) has **History — ignore earlier turns in this chat** (LiteLLM backend). With it on, the window and the saved conversation keep every message, but the next request contains only the current turn and its own tool calls and results. A **History off** chip above the composer shows the state and offers `×` to turn it back on. The switch is read at the start of a turn, so changing it mid-turn affects the next one.

The composer and assistant responses use right-to-left layout when appropriate, so Persian, Arabic, Hebrew, English, and mixed-script text remain readable. Code blocks remain left-to-right for accurate source-code reading.

## Token usage

The Dashboard's model list shows what each model has used, counted from the usage each server reported for its replies: total tokens, the input/output split, the number of turns, and when it was last used. The caption above the list sums every model.

The counts are a floor, not an estimate: a turn whose server reported no usage is not recorded as zero, and the Dashboard says so. The totals live in `%LOCALAPPDATA%\LiteLLM.VisualStudio\token-usage.json`, so deleting a conversation does not shrink them. Tokens are not converted to money — the extension does not know your prices. The Claude backend does not write to this ledger.

## Configuration

The Dashboard manages servers, models, MCP servers and the model list. **Tools > Options > LiteLLM** manages behavior such as:

- Request timeout.
- Streaming responses.
- Temperature and maximum tokens.
- Reasoning visibility.
- Tool calling.
- File editing, edit confirmation, build verification, and test verification.
- Maximum tool rounds.
- MCP switches and limits.
- Claude Code executable and permission lists.
- Logging and context behavior.

API keys and MCP secrets are protected with Windows DPAPI for the current Windows user. Settings, conversations and the usage ledger are stored locally under `%LOCALAPPDATA%\LiteLLM.VisualStudio`. The extension does not send data to a service operated by the extension author; prompts and attached context are sent to the endpoint you configure — or, on the Claude backend, to the Claude Code CLI you launch.

## Privacy and security

- Requests are sent to your configured server only. On the Claude Code backend, the turn is handled by your Claude Code installation under your own account.
- API keys and MCP secrets are encrypted for the current Windows user, are not intentionally written to logs, and are omitted from exported MCP configuration.
- Workspace tools are restricted to the open solution root.
- File editing can be disabled globally or confirmed per edit. MCP tool calls are confirmed per call unless the server's read-only tool and both switches allow otherwise.
- A stdio MCP server runs as a process on your machine; an HTTP MCP server receives the arguments the assistant sends. No server is started or connected unless you enabled it.
- Conversation history and the usage ledger are stored locally and should be treated as sensitive because they may contain prompts, responses, file paths, and selected context.
- Your configured LiteLLM gateway, model provider, MCP servers, and the Claude Code service may retain or process requests according to their own policies. Review those policies before sending confidential source code.

## Troubleshooting

### No models appear

Verify the server base URL, confirm that `/v1/models` is reachable, enable the server, and select Refresh in the Dashboard.

### Authentication fails

Check the API key and the gateway's authentication configuration. A blank key preserves an existing saved key; remove and recreate the server if you need to remove it.

### A request returns 404

Check whether the configured URL already includes `/v1`. The extension expects a base URL and builds the OpenAI-compatible routes itself.

### Requests time out

Increase the request timeout in **Tools > Options > LiteLLM** and inspect **View > Output > LiteLLM** for diagnostics.

### An MCP server does not connect

Press ↻ on its row after fixing the command or URL. The row keeps naming the last failure, and the detail pane explains a disabled server or globally disabled MCP. A stdio server's stderr appears on its **Log** tab.

### Claude Code does not start

Confirm the executable under **Tools > Options > LiteLLM > Claude Code** resolves on `PATH` (or give its full path) and that the CLI is signed in. The turn's failure row and **View > Output > LiteLLM** report what the process said.

### Class and method links do not appear

Links need an open solution with at least one `.cs` file and a finished background index; the first build can take a moment on a large tree. A name the solution does not declare stays plain text by design.

### The agent does not edit files

Confirm that Agent mode is selected, file editing is enabled, and the configured model supports tool calling. Ask mode and Plan mode are intentionally non-editing, and the Claude backend follows Claude's own permissions.

## Support and feedback

Please report bugs and feature requests in the project's GitHub repository:

<https://github.com/esmailh/LiteLLMChat.VisualStudio/issues>

When reporting a problem, include the Visual Studio version, extension version, backend (LiteLLM or Claude), model/gateway type, and relevant LiteLLM Output diagnostics. Never include API keys or confidential source code.

## License

MIT. The full text ships with the extension as `LICENSE.txt` and is available at:

<https://github.com/esmailh/LiteLLMChat.VisualStudio/blob/main/LICENSE.txt>
