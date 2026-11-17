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
- **`@` file mentions** that search the solution — and any project folder added to the conversation — as you type and attach the file you pick; connected MCP resources appear in the same list.
- **`#` tool mentions** that pick which MCP tools a turn may use, and `/` for `/compact`, the connected servers' prompts (or Claude's own commands on the Claude backend).
- **Workspace tools** for reading, searching, listing, opening, and editing solution files.
- **Project folders per conversation**: work in another checkout, or a shared library, beside the solution — its files are addressed as `Folder/src/Foo.cs` and the choice is saved with the chat.
- **Safe editing workflow** with optional confirmation, visible diffs, Undo, and Visual Studio integration.
- **Automatic build verification** after edits, with errors handed back to the assistant to fix — for any project type — and an optional test run once the build passes.
- **Conversation history** with multiple sessions, rename-from-first-prompt, delete, retry, cancellation, and a per-conversation switch to send only the current turn.
- **Per-model token usage** counted from what each server reported, shown in the Dashboard.
- **Right-to-left chat rendering**, including mixed Persian/Arabic and English text.
- **Visual Studio theme integration** for light, dark, and high-contrast environments.
- **Local diagnostics** through the LiteLLM Output window and status bar.
- **Full GitLab CLI access** with command confirmation, host detection and separately approved browser sign-in when needed, including Claude's packaged Git/GitLab wrapper.
- **Optional RTK output compression** for supported Git commands on LiteLLM, retaining real command status and bounded raw details.
- **Model-aware compaction** that accounts for instructions, attachments and selected MCP tool schemas before allocating history space.
- **Readable compaction**: a **Compacted** row opens to show what the checkpoint replaced, the structured checkpoint the model was given instead, and that the chat keeps every message; `/compact` writes one on demand.
- **Agent Skills** with Dashboard management, manual selection and optional model-directed loading on LiteLLM.

This guide describes **1.8.12**. The installer is **LiteLLM.VisualStudio.vsix**, with versioning inside the project/manifest. Features are not a claim of live validation against every provider, OAuth host or IDE project system.

## Current release — 1.8.12 (2026-11-17)

Version **1.8.12** is what lets a turn actually run the project and test its services, designed the way opencode does it rather than as a set of extra tools. `run_powershell` and `run_cmd` gained `background`: with it the call returns at once with a shell id, a process id and the path of a log file, and the program keeps running - a web app, an API, a worker - instead of being killed by that call's own timeout. `read_file` opens that log from its end, which is where a program says it is ready, so readiness is read instead of guessed with a sleep; testing the service is `curl.exe` through `run_cmd`, with the status code, the headers and the body as the result, and a 400 or a 500 read as a result with its body rather than as a failed call. A shell belongs to the conversation, not to the turn, so the next message can still send the service another request. When one ends by itself the model is told in its next round and the transcript gets a row; a shell a turn leaves running is named at the end with the pid that stops it (`taskkill /PID <pid> /T /F`) instead of being stopped silently. Unlike opencode, a shell ending while the chat is idle does not start a turn of its own - the notice arrives with the next message - because a background program must not spend a model call the user did not make. `start_process`, `read_process`, `stop_process`, `list_processes` and `run_http_request` are gone: one command tool, one approval card that says when a command will stay running, and `EnableTerminalTools` still gates all of it. Install `LiteLLM.VisualStudio.vsix` and verify **1.8.12** in Manage Extensions. Live visual IDE verification remains outstanding.

Version **1.8.12** also compacts a long conversation the way opencode does. When the prompt size the last response reported, plus an estimate of what has changed since, reaches the model's context window minus a buffer (the larger of 15% of the window and 16 000 tokens, or **Compaction buffer (tokens)** when that leaves room), the older part of the conversation is no longer replaced by a prose summary: it is folded into a structured checkpoint written to opencode's template — objective, requirements, decisions, work state (completed, active, blocked), next move, relevant files, important context — and the newest **Keep beside the checkpoint (tokens)** stay word for word beside it; 15 000 is the opencode default. An existing checkpoint is updated rather than replaced, so a requirement recorded three compactions ago is still in the request, and a reply that ignores the template gets one reminder before the earlier checkpoint is kept. The checkpoint travels as one `user` message wrapping `<conversation-checkpoint>`, in the request copy only — the chat and the saved conversation keep every message — and the message the turn is answering is pinned in front of the kept rounds, so a fold can cost the thread but never the question. A provider that rejects a round as too long no longer ends the turn: the conversation is folded further below the size that was refused and the same round is sent again, up to four times. **`/compact`** on the LiteLLM backend folds the conversation now, sends nothing and leaves the chat idle until the next message; it is also a row in the `/` menu. **Compact automatically** under **Tools > Options > LiteLLM > Context** can be turned off, in which case only `/compact` compacts.

Version **1.8.11** fixes what a long turn does when the older part of the conversation no longer fits: it is folded into a briefing, and a bad briefing could take the request with it. In a turn that kept running into the budget, each new briefing was written from the previous one plus the messages after it, and one of them answered pages of context with a single line — *We need continue investigating* — that no longer said what the user had asked for. Every later briefing inherited that loss, and the last rounds of that turn were told, truthfully, that the original request was absent from the retained context; the file attached to the message then landed on the briefing, which is why the model could say it had reviewed `StoreGroupService.cs` but had no request. Now the message the turn is answering is pinned verbatim in front of the kept rounds, whatever the briefing says, so a summary can cost the thread but never the request — and 1.8.12 keeps that pinning while replacing the briefing itself with a structured checkpoint. Install `LiteLLM.VisualStudio.vsix` and verify **1.8.11** in Manage Extensions. Live visual IDE verification remains outstanding.

Version **1.8.10** lets a conversation remember what it was told about MCP once. «توی این چت کلا از mcp استفاده کن» — or "use MCP in this chat from now on" — is read from the message itself, with no switch to find: from the next turn on, that chat is offered the enabled servers' tools every time, as if *Offer tools only when the message asks* were off for that chat alone. The state is kept with the conversation rather than with the settings, so a new chat does not inherit it, and the opposite sentence — «دیگه از mcp استفاده نکن» or "don't use MCP in this chat anymore" — clears it, so a chat cannot be talked into a state it cannot be talked out of. A passing mention, a question about MCP and a pasted configuration sample change nothing; only a sentence that names MCP, says it is about this chat (or every turn, or from now on) and asks to use, offer, enable or switch it off counts. One row appears when the answer changes, not on every turn, and the MCP chip's tooltip names the state and the sentence that clears it. It is read only on the LiteLLM backend; the Claude CLI runs its own harness.

Version **1.8.9** is about a turn that stopped before it was sent: *Request context exceeds the 300000-character safety budget*, with the work already done and nothing sent. That budget was spent before the request existed — a share of the model's budget was reserved for the instructions and the tool schemas, and the transcript was shortened to what was left — so the size it was decided on was an estimate, and the estimate is a few thousand characters short of the request it describes. A finished request could therefore come out just over the line and be refused whole. The request is now measured after it is built, and what it is over by is taken out of the same place the estimate took it from: old tool results lose their bodies, the newest rounds are never touched, and no message is dropped, because every announced call must keep the result that answers it. Shortening also no longer depends on which message happens to push the total over — the largest message in a request is its system prompt and it sits at the head, so a transcript can go over on a message that may not be shortened, and it now gives way by the next old result instead of staying over with every result in full. When the schemas are the part that does not fit, the round continues without them rather than the turn ending: the built-in tools stay, the prompt is rebuilt without the MCP block, and the transcript says so once. Only if the request still does not fit — attachments larger than the budget, a session that has outgrown it — is the turn refused, and the message then names the size it reached and how much of it is messages and attachments versus tool schemas.

Version **1.8.8** is the half of the file-reference fix that was still missing. A reply names a file, not a path — `StoreGroupService.cs` for the one three folders down — and the click resolved that name against the solution root alone, so every file inside a folder was drawn as a link and answered *Referenced file was not found*. References are now matched against a list of the conversation's files, read once per workspace off the UI thread: the exact path wins, then a path written from part way down (`Services/StoreGroupService.cs`), then a bare name, and the shortest path wins between two files of the same name. An absolute path the reply wrote in full is never replaced by a file elsewhere, a file a turn creates is clickable in the same reply because the list is keyed on the edit count, and an added project's files are found by the name the chat shows. Measured against the conversation that reported this: 13 of its 18 references could not be opened under the old rule, and all 18 open now.

Version **1.8.7** makes a turn start answering sooner. Every turn used to re-read `tools/list`, `resources/list`, `resources/templates/list` and `prompts/list` from every connected MCP server — four round trips per server before the model was even asked — and each list is now read once per session and re-read only when the server reports a change or the connection restarts (the row ↻ in the Dashboard, or a changed connection). A message that says nothing about MCP is also offered none of the servers' tools, so an ordinary turn no longer carries up to sixteen schemas it will not use; a `#` token, a name, or the word MCP offers them for that turn, the composer's MCP chip says so in its tooltip, and **Offer tools only when the message asks** under **Tools > Options > LiteLLM > MCP** can be turned off to restore the older behaviour. That release also shortened the text of a reference whose name contains spaces, or that points into a drive-rooted folder with spaces; 1.8.8 is what makes the click open the file.

Version **1.8.6** makes a context summary readable: the **Context summarized** row opens to show what the briefing replaced (counts of your messages, replies and tool results, with the tool names), the briefing text itself, and a sentence confirming that the chat and the saved conversation keep every message. It also lets a conversation reach past the solution: the **Project** button in the chat header (a folder-with-plus glyph, beside Delete) adds another folder the agent may read and change, its files are addressed as `Folder/src/Foo.cs`, and `@`, `#`, the file picker and every file tool see them. The list belongs to the conversation, is capped at eight folders, and is refused with a reason when a folder is already reachable or contains an existing project. Commands, Git, and the automatic build and test check stay on the solution, as the prompt and the transcript rows say; on the Claude backend each added project is passed as `--add-dir`. Install `LiteLLM.VisualStudio.vsix` and verify **1.8.6** in Manage Extensions. Live visual IDE verification remains outstanding.

Version **1.8.5** fixes Plan layout width and separates step markers from bidirectional text, placing Persian markers on the right and wrapping text right-aligned. English remains LTR. Install `LiteLLM.VisualStudio.vsix` and verify **1.8.5** in Manage Extensions. Live visual IDE verification remains outstanding.

Version **1.8.4** automatically displays Persian/Arabic plan text RTL, including steps, explanations, manual input and step evidence. English and source diffs remain LTR. Install `LiteLLM.VisualStudio.vsix` and verify **1.8.4** in Manage Extensions. Live visual IDE verification remains outstanding.

Version **1.8.3** adds agent-owned plans through LiteLLM `update_plan` and observation of Claude `TodoWrite` inputs. Install `LiteLLM.VisualStudio.vsix` and verify **1.8.3** in Manage Extensions. Live IDE/provider verification remains outstanding.

Version **1.8.2** includes inline Changes/Plan sections and the green running indicator, removes the extra workspace buttons/features described below, and retains the 1.8.1 fixes. Install `LiteLLM.VisualStudio.vsix` and verify **1.8.2** in Manage Extensions.

Patch **1.8.1** fixes cross-thread WPF access in streamed progress/Activity updates and removes **Refresh Models**, **Test Connection** and **Clear Chat** from Tools. Their Dashboard/in-chat alternatives remain. Install `LiteLLM.VisualStudio.vsix` and verify **1.8.1** in Manage Extensions. The workspace features below were introduced in 1.8.0.

### Agent workspace

**Agent plans (1.8.3):** LiteLLM can publish and revise a structured plan with `update_plan`. Pending, in-progress and completed steps appear in the existing collapsible Plan panel and are saved per turn. Progress is model-reported, not user approval or verification. Manual plans are separate. Claude `TodoWrite` inputs are displayed when the CLI emits them; unsupported formats and prose are not guessed. Planning grants no tool permissions and is not guaranteed when the model/CLI does not emit a supported plan.

Version 1.8.2 uses inline UI rather than extra chat-header buttons:

- **Activity:** green dot inside chat; a green-circle glyph beside **LiteLLM Chat** in the caption while running without keyboard focus. Approval waiting is amber. The shell controls tab backgrounds, so the extension does not override their colours. No separate Activity window or sound.
- **Changes:** collapsed per-turn sections in the transcript, with expandable file diffs, native VS comparison and conflict-checked restore actions. In-memory LiteLLM file tools only—not Claude, terminal or MCP changes. Up to 20 turn entries; capped history is reported. Temporary diff files contain source under the user temp directory.
- **Plan:** collapsible panel above the composer, saved editable steps and explicit execution/review. User-authored/pasted, not automatically extracted from model output. A response ending does not prove a step passed. Plan constraints are guidance, not a sandbox or permission grant.

Queue, search/export/bookmarks, the context-share dialog and related-test selection have been removed. Existing conversation selection, model-aware request budgeting and full discovered post-build tests remain. Test reports disclose missing assemblies and the four-assembly cap.

**Auto-hide:** hiding an unpinned chat keeps its turn, busy state and pending approval cards alive. These UI/DTE flows still need manual Visual Studio verification; unit tests validate the extracted Core logic, not IDE interaction.

Version **1.8.12** is what lets a turn actually run the project and test its services, designed the way opencode does it rather than as a set of extra tools. `run_powershell` and `run_cmd` gained `background`: with it the call returns at once with a shell id, a process id and the path of a log file, and the program keeps running - a web app, an API, a worker - instead of being killed by that call's own timeout. `read_file` opens that log from its end, which is where a program says it is ready, so readiness is read instead of guessed with a sleep; testing the service is `curl.exe` through `run_cmd`, with the status code, the headers and the body as the result, and a 400 or a 500 read as a result with its body rather than as a failed call. A shell belongs to the conversation, not to the turn, so the next message can still send the service another request. When one ends by itself the model is told in its next round and the transcript gets a row; a shell a turn leaves running is named at the end with the pid that stops it (`taskkill /PID <pid> /T /F`) instead of being stopped silently. Unlike opencode, a shell ending while the chat is idle does not start a turn of its own - the notice arrives with the next message - because a background program must not spend a model call the user did not make. `start_process`, `read_process`, `stop_process`, `list_processes` and `run_http_request` are gone: one command tool, one approval card that says when a command will stay running, and `EnableTerminalTools` still gates all of it. Install `LiteLLM.VisualStudio.vsix` and verify **1.8.12** in Manage Extensions. Live visual IDE verification remains outstanding.

Version **1.8.12** also compacts a long conversation the way opencode does. When the prompt size the last response reported, plus an estimate of what has changed since, reaches the model's context window minus a buffer (the larger of 15% of the window and 16 000 tokens, or **Compaction buffer (tokens)** when that leaves room), the older part of the conversation is no longer replaced by a prose summary: it is folded into a structured checkpoint written to opencode's template — objective, requirements, decisions, work state (completed, active, blocked), next move, relevant files, important context — and the newest **Keep beside the checkpoint (tokens)** stay word for word beside it; 15 000 is the opencode default. An existing checkpoint is updated rather than replaced, so a requirement recorded three compactions ago is still in the request, and a reply that ignores the template gets one reminder before the earlier checkpoint is kept. The checkpoint travels as one `user` message wrapping `<conversation-checkpoint>`, in the request copy only — the chat and the saved conversation keep every message — and the message the turn is answering is pinned in front of the kept rounds, so a fold can cost the thread but never the question. A provider that rejects a round as too long no longer ends the turn: the conversation is folded further below the size that was refused and the same round is sent again, up to four times. **`/compact`** on the LiteLLM backend folds the conversation now, sends nothing and leaves the chat idle until the next message; it is also a row in the `/` menu. **Compact automatically** under **Tools > Options > LiteLLM > Context** can be turned off, in which case only `/compact` compacts.

For installation use **`LiteLLM.VisualStudio.vsix`**. Close Visual Studio, run the installer, reopen and confirm **1.8.12** under **Extensions > Manage Extensions > Installed**. Older filename-suffixed installers are not current output; replacing a VSIX does not update a running IDE.

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

To use Claude Code instead, choose **Claude Code** in the chat's backend selector and set the executable under **Tools > Options > LiteLLM > General**, category **Claude Code**. To use MCP servers, add them on **Dashboard > MCP** and tick the ones the LiteLLM backend should connect.

## Chat modes

### Ask

Use Ask for explanations, code questions, documentation, debugging guidance, and general assistance. Ask mode is read-only: it does not inspect or modify workspace files through tools.

### Agent

Use Agent when you want the assistant to work on the solution. The agent can inspect relevant files, search the codebase, open files in the editor, apply requested edits, and verify the result. It works iteratively and reports the actions it completed.

When file editing is enabled, edits are applied to the workspace rather than printed as snippets. Each edit can be reviewed from the chat, copied as a diff, or undone.

### Plan

Use Plan when you want analysis before implementation. The assistant investigates the workspace as needed and returns a structured implementation plan. Plan mode does not modify files and asks for confirmation before implementation.

Plan text follows its own direction: Persian and Arabic steps read right-to-left with their marker on the right, English stays left-to-right, and source diffs remain left-to-right.

## Execution backends

The backend selector in the chat header is independent of the Ask, Agent, and Plan modes on the LiteLLM path.

- **LiteLLM** (the default) runs the extension's own agent loop against your OpenAI-compatible endpoint. Workspace tools, edit and command approval cards, MCP approval cards, build and test verification all belong to this path.
- **Claude Code** starts one `claude -p` process per submitted turn with streamed JSON output. It uses Claude's own tools and permissions; configure the executable and permission lists under **Tools > Options > LiteLLM > General**, category **Claude Code**. The CLI uses `--permission-mode dontAsk`: allowlisting is automatic approval, not the extension's per-call card. Configured write tools require editing on and per-edit confirmation off, unless explicit Agent turn-level editing consent is given as described below. Broad Bash requires terminal tools on and per-command confirmation off; the packaged Git wrapper uses a narrow rule instead. **Stop** ends the process tree. `/` lists Claude's prompt-invoked commands, and a conversation resumes only after Claude returns a session ID.

Claude Code is a separate program: it needs its own installation and sign-in, and what it is sent is governed by Anthropic's terms rather than by your LiteLLM gateway.

In **Agent** mode, the extension offers explicit **turn-level editing consent** when settings would otherwise keep Claude read-only. Accepting enables `Edit`, `Write` and `NotebookEdit` for that run only, without changing saved settings or granting general shell access. This is not per-edit confirmation; explicit disallowed tools still win. The packaged `LiteLLM.GitWrapper.exe` provides a narrow confirmed Git/GitLab path without broadly allowlisting Bash.

Progress includes startup hooks, connection, API retries and tool activity. An `authentication_failed` retry is surfaced as an actionable failure rather than a silent Thinking state. Claude retains its own model/provider credentials and context management: LiteLLM keys, RTK filtering and the extension's compaction do not configure its harness.

## Context and attachments

The composer supports VS Code-style context attachment:

- **Active file**: optionally follows the file currently open in the editor.
- **Files and folders**: searchable multi-select over the solution and the project folders added to this conversation.
- **Selection**: sends the current editor selection.
- **Problems**: sends available errors and warnings from Visual Studio's Error List.
- **MCP resources**: text resources exposed by connected servers appear above the solution's files in the `@` list.

Type `@` at the start of a word to search the solution's files and the added projects' files from the keyboard. The list narrows as you type, Up/Down moves through it, and Enter or Tab chooses a file. The file is attached as a chip and the `@` text is taken out of your prompt, so the message reads as you wrote it.

Use the paperclip/context button or type `#` at the beginning of a word to open the full multi-select picker. Attached files are represented by compact chips above the composer. Click a chip to open its file or use `×` to remove it.

On the LiteLLM backend with MCP tools connected, `#` first offers the servers' tools, and its last row, **Files & Folders…**, opens the file picker. On the Claude backend, or with no MCP tools connected, `#` opens that picker directly. See [MCP servers](#mcp-servers).

When tool calling is enabled, file contents are read on demand. When tool calling is disabled, bounded file excerpts are included only for the current request. Conversation history stores bounded context data rather than an unbounded copy of the workspace.

### Model-aware budgets and compaction (LiteLLM)

Before compacting history, the extension counts serialized system instructions, attachments and the offered tool schemas, including the MCP selection for this turn. The remaining allowance is used for compacting old rounds into a checkpoint and shortening old tool results. Call/result pairing and protected recent rounds remain intact; only the request copy changes, not the saved or displayed transcript. The final serialized request is checked again before sending.

Model capacity comes from optional `/v1/models` fields (`context_window`, `context_length`, `max_model_len`, or `model_info.max_input_tokens`). **Tools > Options > LiteLLM > General**, category **Context**, provides:

- **Fallback context tokens:** `128000` by default when metadata is absent. This is an assumption to check against your endpoint, not an automatically discovered capacity.
- **Model context overrides:** semicolon-separated `model-id=tokens` entries, such as `model-a=32768;model-b=200000`; server-specific model keys also work. Overrides take precedence over reported metadata.

The estimate reserves configured output tokens plus 10% headroom (at least 1024 tokens), allows two serialized characters per remaining token, and retains the **300000-character safety ceiling**. This is not exact tokenization: non-Latin/token-dense inputs can still exceed the provider's actual limit. The Context row reports estimated request/history allowances; usage accounting remains based on actual server-reported tokens. If fixed inputs exhaust the budget, the turn explains why and stops; tools are never silently removed. Reduce attachments or select fewer MCP tools with `#` when needed.

For example, a 32768-token model with 8192 response tokens reserved has an estimated **42600-character** request allowance after headroom; fixed instructions and tool schemas consume part of that before history is budgeted. The 300000-character ceiling is therefore a maximum, not the allowance for every model. Refresh models to retrieve updated capacity metadata, or set an accurate override when metadata is absent or incorrect. Raising an assumed capacity cannot extend the provider's actual context window.

When older rounds are folded into a checkpoint, the transcript gains a **Compacted** row that opens to show what it covered — how many of your messages, replies and tool results, and which tools — the checkpoint the model was given instead, and a sentence confirming that the chat and the saved conversation keep every message. Only the request copy carries the checkpoint; the window and the stored session still hold every message, and the row comes back with them when the conversation is reopened. The three **Context** options decide when it happens and how much stays word for word: **Compact automatically**, **Keep beside the checkpoint (tokens)** (15 000 by default) and **Compaction buffer (tokens)** (0 means the larger of 15% of the window and 16 000). The decision is measured: the prompt size the last response reported plus two characters per token for what has changed since, while the final 300000-character bound is measured on the request itself.

## MCP servers

The extension is an MCP client. Any stdio, Streamable HTTP, or legacy HTTP+SSE server can be added on **Dashboard > MCP**. Servers are only started when you enable them; a `.mcp.json` file in the repository is never loaded on its own.

- **Add, edit, test.** The server dialog changes with the transport: a command, arguments and environment for stdio; a URL, headers, bearer token or OAuth sign-in for HTTP. **Test connection** starts the server and lists its tools so the allow lists can be filled from real names.
- **Enable, disable, refresh.** Each row has a checkbox and a ↻. Ticking a server connects it at once, and a ticked server is connected again when the chat window opens, so a restart does not mean reconnecting each one by hand. Unticking disconnects it immediately. **↻** is a full reconnect: the session — and any stdio process — is dropped, started again, and its tools, resources and prompts are re-read. **Refresh** in the page header only re-reads the page. These controls drive the LiteLLM backend; the Claude CLI starts its own connections.
- **Import and export.** **Import JSON…** accepts Claude/Cursor `mcpServers`, OpenCode-style `mcp.servers` or `mcp` maps, standalone named maps and single-server objects. A local `command` may be a string plus `args` or an executable-first argv array; `environment` and `env` are supported. Nothing launches until you review and enable each entry. **Export JSON…** writes Claude-compatible configuration without secrets. This does not emulate OpenCode's Code Mode or every field in its configuration.
- **Approval.** Every MCP tool call asks first, showing the server, the tool, its risk hints and its arguments with secret-looking values redacted. **Always allow this tool** writes to that server's allow list. A tool the server marks read-only runs without a card only when both the global switch and the server's own switch are on; destructive and open-world tools always ask. A denied call is still answered in the conversation, so the transcript stays valid.
- **Browse.** The detail pane lists tools, resources, prompts and the server's log. **Browse…** opens a larger window with a form generated from each tool's input schema, a resource reader, a prompt form and the live log.
- **Resources in the composer.** Type `@` and choose one of a connected server's resources to attach its text as a context chip.
- **Prompts in the composer.** On the LiteLLM backend, `/` at the start of a line lists `/compact` and the connected servers' prompts. Choosing a prompt opens its argument form and inserts the text the server composed into the composer, to edit before sending; choosing `/compact` writes the command, which folds the conversation and sends nothing.
- **Tools in the composer.** On the LiteLLM backend, `#` lists the connected servers' tools, filtered as you type; Enter or Tab inserts a server-qualified token such as `#Server.tool`. Those tokens are the turn's selection: the MCP tools offered to the model are narrowed to exactly the ones named, while the built-in tools stay. A name typed without the picker also resolves. A name that matches nothing draws a row saying so but does not take the turn's other tools away.
- **Logging.** `logging` messages and stderr go to the server's **Log** tab and, for warnings and errors, to **View > Output > LiteLLM**. Values that look like credentials are masked before they are stored in either place.
- **OAuth.** HTTP servers that advertise protected-resource metadata can be signed in to from the MCP page with PKCE in the system browser; tokens are kept under `%LOCALAPPDATA%\LiteLLM.VisualStudio\mcp-tokens.json` protected with Windows Data Protection.

**The Claude backend** is given the servers marked **Expose to Claude** through a per-run `--mcp-config` file under `%LOCALAPPDATA%`, deleted when the turn ends. Because the CLI runs with `--permission-mode dontAsk`, the extension cannot show its own per-call card inside it; only tools the policy already resolves to *allow* are put in `--allowedTools`, and everything else is reported by the CLI as denied. Use the LiteLLM backend when the per-call card matters.

Global switches — enable MCP, the read-only exception, sampling, elicitation, roots, timeouts, the per-turn tool cap and the strict Claude configuration — are under **Tools > Options > LiteLLM > General**, category **MCP**.

> MCP servers are programs or services you choose to trust. A stdio server runs on your machine with your permissions, and an HTTP server receives the arguments the assistant sends it. Enable only servers you trust, and read the approval card before allowing a call.

### Local EXE example — no HTTP required

Save a JSON file like this, replacing the path with your actual installation, then use **Dashboard > MCP > Import JSON…**:

```json
{
  "codebase-memory-mcp": {
    "type": "local",
    "command": ["C:/Tools/codebase-memory-mcp/codebase-memory-mcp.exe"]
  }
}
```

The EXE communicates over **stdio**. The first array item is the executable; remaining items are exact arguments, not shell text. Review the imported entry, enable MCP globally if needed, then tick the server to launch/connect it. No URL is required. The solution directory is the default process working directory; importing alone starts nothing.

## Agent Skills

Skills are folders containing `SKILL.md` with a name, description and instructions. **Dashboard > Skills** supports search, enable/disable, refresh, creation, import, editing, deletion and optional AI-generated drafts. Personal skills live in `%USERPROFILE%\.llm123\skills`; project discovery includes `.agents/skills` and optional `.github/skills` / `.claude/skills`. Duplicate definitions display their precedence rather than silently disappearing.

Select skills from `/`, use `/skill name your request`, or let the LiteLLM model load relevant skills from their descriptions when automatic selection is enabled. Claude uses native discovery where possible instead of receiving duplicate bodies. Skills provide guidance, not permissions: importing a skill never runs its scripts or bypasses confirmation. See [SKILLS.md](SKILLS.md) for details.

## Workspace tools

In Agent mode, the assistant can use tools for:

- Finding the active file and selected text.
- Listing files across the solution and any added project folder.
- Searching text across supported source files in all of them.
- Reading bounded line ranges.
- Opening a file at a specified line.
- Creating files and directories.
- Applying patches and replacing exact text.
- Reviewing and undoing applied edits.

Workspace paths are confined to the open solution and the project folders added to this conversation. Paths outside them and directory traversal attempts are rejected.

Workspace file tools also reject reparse-point/junction paths, per folder. This boundary is not a sandbox for approved terminal commands, the CLI wrapper or MCP programs, which run with your permissions.

With terminal tools enabled, LiteLLM Agent mode adds a dedicated, shell-free `run_git` tool. It accepts exact argument arrays (and legacy strings), confirms every command, follows Git's configured push/upstream remote precedence, and selects push versus fetch URLs correctly. Recognized GitHub/GitLab credential failures can offer a separately confirmed browser login through `gh` or `glab`; self-hosted providers require a conservative host-scoped CLI probe. The remote protocol is preserved where supported, sensitive diagnostics are redacted, and a successful login permits exactly one retry of the unchanged Git command. Claude Code remains an independent subprocess and does not receive this Visual Studio tool; general Bash is not auto-enabled as a workaround.

### Projects beyond the solution

**Project** in the chat header — a folder-with-plus button beside **Delete**, deliberately not the plain `＋` of **New chat** — adds another folder this conversation may read and change. Files there are addressed as `Folder/src/Foo.cs`, in tool calls and in the transcript alike. A path with no such prefix is the solution's, and the solution wins a path it already has, so a solution folder that shares the added folder's name keeps its meaning; two added folders with the same name are numbered (`App (2)`).

The list is per conversation and saved with it, capped at eight folders, and refuses — with the reason — a folder that is already reachable or that contains one already in the list. File tools, the `@` list, the file picker and `open_file` all accept the added folders; `list_files` and `search_files` with no path cover every folder and name the ones they searched. Commands, Git and GitLab still run from the solution, and the automatic build and test check covers the solution only — the turn's instructions and the row drawn when a folder is added both say so; ask the agent to run an added folder's own build command to verify it. On the Claude backend each added folder is passed as `--add-dir`. Removing a folder stops further access to it; edits already applied remain applied.

### GitLab CLI and automatic browser sign-in

The shell-free **`run_glab`** tool exposes the installed CLI's command set, including merge requests, issues, CI/pipelines, releases, API and authentication. Pass an exact array, for example:

```json
{"arguments":["mr","create","--source-branch","feature/example","--target-branch","main","--title","Example","--description","Summary"]}
```

Install `glab` separately. Commands require confirmation; account/project permissions still apply. For remote operations the extension detects the host from explicit `--hostname`/`--repo`, `GITLAB_HOST` or `origin`, checks credentials and, when necessary, offers a separately approved **visible browser-OAuth login window**. Complete any CLI/browser prompts there; no login command or token needs to be pasted into chat. Credentials are checked again before the original command executes once. A declined/failed login does not run the pending operation, and ambiguous API failures do not trigger mutation retries. A GitHub origin requires an explicit GitLab target rather than guessing.

The executable can be resolved from trusted absolute installation/PATH locations, including `%USERPROFILE%\.local\bin\glab.exe`. Claude uses the same confirmation/sign-in flow through `LiteLLM.GitWrapper.exe --glab <arguments>`. Installing or building the extension does not create an MR, grant GitLab permissions or install the CLI.

### Optional RTK compression (LiteLLM only)

Under **Tools > Options > LiteLLM > General**, category **Terminal**, **RTK output** offers **Auto** (default) or **Off**. There is no RTK Dashboard tab. Install RTK separately under `%ProgramFiles%\rtk\rtk.exe`; the automatic helper does not search PATH or the repository.

Auto filters captured stdout with `rtk pipe` for successful simple `git status`, `git log` and `git diff` commands, including `run_git`. Outputs below 1000 characters, complex/quoted shell expressions, machine-readable format flags, failed/cancelled/timed-out results and already truncated output stay raw. Claude, GitLab mutations and automatic build/test parsers are excluded.

The approved command executes once: original stderr, exit status, timeout and cancellation remain authoritative. RTK runs as a bounded five-second output filter outside the repository. Missing/broken RTK or ineffective filtering retains raw output without rerunning the command. Tool rows show **RTK** or **RTK kept raw** when attempted, and **Copy full detail** exposes bounded raw stdout. RTK reduces individual outputs; history compaction is separate, and no exact token savings are promised.

## Editing and permissions

Open **Tools > Options > LiteLLM** to configure tool calling and file editing.

- Disable **Enable file editing** to make the assistant read-only.
- Enable **Confirm before each edit** to review a proposed diff before it is applied.
- Use **Allow** or **Skip** for each proposed edit.
- Use Visual Studio **Undo** or the edit row's **Undo this edit** command to revert an applied change.
- After the assistant changes a file, the solution is built automatically and the result appears as its own row in the chat. Any errors are handed straight back to the assistant, which fixes them and is built again — up to three times in a turn. This is on by default in Agent mode as **Verify the build after edits**, and it runs Visual Studio's own **Build Solution**, so a solution of any project type is built the way its project system expects rather than by a compiler-specific command.
- **Verify the tests after edits** (off by default) runs the solution's test projects once a build has passed in the same round and hands any failures back to the assistant. It never runs against assemblies from an earlier round, and it needs **Verify the build after edits**. A solution with no test project is left alone; a test run that could not start or reported nothing gets its own row saying so rather than counting as a pass.
- Set a maximum tool-round budget to prevent an agent turn from continuing indefinitely. When the limit is reached, the chat offers a Continue or Stop checkpoint.

Build verification uses Visual Studio's success/cancellation events, not an empty Error List. **Failed, cancelled, unavailable and timed-out** builds never authorize tests; contradictory error diagnostics also block them. The current Error List is read as a standing system-prompt note each round, not saved as history or treated as evidence of a new build.

Permission switches are checked at dispatch as well as when offering tools. Missing edit/command approval handlers deny execution, and directory creation requires approval. Denied calls still receive tool responses so the transcript remains valid. Edit revision tracking is monotonic despite a capped undo history.

## Clickable references

A reply is not a dead document. File paths and `path:line` references open at that line in the editor, and a file named on its own is looked up among the conversation's files — `StoreGroupService.cs` opens the one in `src/StoreGrouper/Services/`, a path written from part way down opens the file it ends with, and the shortest path wins when two files share a name; a full path the reply wrote is never swapped for a file elsewhere. When the open solution has been indexed, C# types and members named in a reply become links too: a dotted name such as `ChatStateLimits.Sanitize`, or a whole inline-code identifier such as `ResponseParser`, jumps to its declaration, and the tooltip names the file and line first. A name the solution does not declare — or a word that merely looks like one — stays ordinary text, so a link always goes somewhere. The index is built in the background from the solution's `.cs` files, rebuilt when a different solution is opened or a turn has edited files, and replies already on screen gain their links when it is ready.

Clicking a link never scrolls the transcript: the chat stays where you were reading.

## Conversations and controls

- Press **Enter** to send.
- Press **Shift+Enter** for a new line.
- **Stop** cancels an active response.
- **New chat** starts an independent conversation.
- **Delete** removes the selected conversation after confirmation.
- **Project** (folder-with-plus, beside Delete) adds or removes a folder this conversation may work in; see [Projects beyond the solution](#projects-beyond-the-solution).
- **Retry response** regenerates the latest completed or failed turn, including its prompt and attachments.
- **Compacted** rows open on click: they name what the checkpoint replaced, show the structured checkpoint the model received instead, and state that the chat and the saved conversation keep every message.
- Assistant responses can be selected and copied. Right-click a response for Copy message, Retry response, request/response diagnostics, and related actions.
- The composer's menu (☰) has **History — ignore earlier turns in this chat** (LiteLLM backend). With it on, the window and the saved conversation keep every message, but the next request contains only the current turn and its own tool calls and results. A **History off** chip above the composer shows the state and offers `×` to turn it back on. The switch is read at the start of a turn, so changing it mid-turn affects the next one.

The composer, assistant responses and plan steps use right-to-left layout when appropriate, so Persian, Arabic, Hebrew, English, and mixed-script text remain readable. Code blocks and source diffs remain left-to-right for accurate source-code reading.

Saving an active pending/streaming turn preserves its state; interrupted-turn recovery runs when stored sessions are loaded, not during every save. This prevents an in-progress response from being marked cancelled merely because the conversation was saved.

**Auto-hide lifecycle fix:** an unpinned chat can hide while its turn continues. Reopening keeps live messages, busy state and approval cards. Hiding never grants permission. Actual tool-window disposal cancels and releases handlers; WPF `Unloaded` does not. Manual IDE verification is still required.

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
- Model context fallback and per-model capacity overrides.
- RTK Auto/Off output filtering.

API keys and MCP secrets are protected with Windows DPAPI for the current Windows user. Settings, conversations and the usage ledger are stored locally under `%LOCALAPPDATA%\LiteLLM.VisualStudio`. The extension does not send data to a service operated by the extension author; prompts and attached context are sent to the endpoint you configure — or, on the Claude backend, to the Claude Code CLI you launch.

## Privacy and security

- Requests are sent to your configured server only. On the Claude Code backend, the turn is handled by your Claude Code installation under your own account.
- API keys and MCP secrets are encrypted for the current Windows user, are not intentionally written to logs, and are omitted from exported MCP configuration.
- Workspace tools are restricted to the open solution and the project folders you added to that conversation by name; the list belongs to the conversation and removing a folder takes effect at once.
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

Confirm the executable under **Tools > Options > LiteLLM > General**, category **Claude Code**, resolves on `PATH` (or give its full path) and that the CLI is signed in. The turn's failure row and **View > Output > LiteLLM** report what the process said.

### Claude remains on Thinking or reports authentication failure

Inspect progress and failure rows. Startup hooks/MCP can delay startup; `authentication_failed` API retries now stop with an explanation. Check Claude's own selected model, provider and credentials. A successful `claude auth status` does not prove a custom provider accepts the request; LiteLLM keys do not configure Claude.

### Request context exceeds the safety budget

No request was sent. The full serialized request exceeds its estimated model allowance or the 300000-character ceiling. Start a new chat, remove attachments or narrow MCP tools using `#`; verify the Context fallback/overrides against your provider. Fixed instructions and protected recent rounds cannot always be reduced by compaction.

### RTK does not appear

Find **RTK output** under **Tools > Options > LiteLLM > General**, category **Terminal**. It has no dedicated Dashboard tab. It is LiteLLM-only and does not filter small/unsupported commands; check the RTK installation path and tool-row details.

### Class and method links do not appear

Links need an open solution with at least one `.cs` file and a finished background index; the first build can take a moment on a large tree. A name the solution does not declare stays plain text by design.

### The agent does not edit files

Confirm that Agent mode is selected, file editing is enabled, and the configured model supports tool calling. Ask mode and Plan mode are intentionally non-editing, and the Claude backend follows Claude's own permissions.

### A file in another project is not found

Use **Project** in the header to add that folder to *this* conversation; the list is per conversation, so a folder added to another chat does not carry over. Its files are addressed with the folder's name in front (`OtherApp/src/Foo.cs`). A path with no prefix is looked for in the solution first, and a name the solution already has resolves there. Commands and the automatic build/test check stay anchored to the solution even with a folder added.

## Support and feedback

Please report bugs and feature requests in the project's GitHub repository:

<https://github.com/esmailh/LiteLLMChat.VisualStudio/issues>

When reporting a problem, include the Visual Studio version, extension version, backend (LiteLLM or Claude), model/gateway type, and relevant LiteLLM Output diagnostics. Never include API keys or confidential source code.

## License

MIT. The full text ships with the extension as `LICENSE.txt` and is available at:

<https://github.com/esmailh/LiteLLMChat.VisualStudio/blob/main/LICENSE.txt>
