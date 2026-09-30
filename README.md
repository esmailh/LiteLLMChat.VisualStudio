# LiteLLM Chat for Visual Studio 2022

See [MARKETPLACE.md](MARKETPLACE.md) for the complete user-facing feature guide, setup instructions, privacy notes, and troubleshooting information.

A native WPF VSIX for chatting with any OpenAI-compatible LiteLLM gateway without leaving Visual Studio. It supports multiple servers, model discovery, streaming, reasoning display, read and edit tools, VS Code–style context attachments, diagnostics, DPAPI-protected API keys, and a local conversation history.

## Requirements

- Windows and Visual Studio 2022 17.x with the **Visual Studio extension development** workload
- .NET Framework 4.8 targeting pack

The VSIX is produced below `src\LiteLLM.VisualStudio\bin\Release`. Run it to install, then restart Visual Studio. F5 from the extension project launches the experimental instance.

## Use

Open **Tools > LiteLLM: Open Chat**, choose a conversation and model, and enter a prompt. Enter sends; Shift+Enter inserts a line. **Stop** cancels generation. **New chat** starts a fresh conversation and **Delete** removes the one the history drop-down is showing, after a confirmation; the buttons are live only when there is something for them to act on, and the open draft and its attachments survive a deletion. Deleting the last conversation leaves one empty conversation behind rather than an empty list, so the pane always has something to render. The view renders basic Markdown, fenced code with Copy Code, optional reasoning, tool progress, and request failures. It does not force-scroll after you scroll away from the bottom. Right-click an assistant response and choose **Retry response** to regenerate the latest completed or failed turn.

A reply is ordinary selectable text: drag over the part you want and press **Ctrl+C**. Each paragraph and each code block is its own selection, so a selection does not run across a paragraph break — the right-click menu's **Copy message**, and **Copy last reply** in the composer, are what copy a whole answer.

The highlight is one of the few colours Visual Studio publishes no token for, so it is derived from the theme's accent — but not trusted to it: it is held to a measured contrast against the page, the card and the code block it can be drawn on, and the text under it is recoloured to read on the highlight rather than being left the accent's guess. On a theme whose accent sits close to its own background the highlight is pushed away from it until it separates. The highlight also stays painted once the box loses focus, so what you selected is still marked when you go to copy it.

The header and the composer reflow with the pane. Below about 820px the conversation and model drop-downs and the composer's buttons move onto their own wrapping rows, so a narrow tool window stays readable instead of drawing controls over each other. Each drop-down is the width of its box: an entry longer than that ends in an ellipsis rather than stretching the open list past the width of the header, and a conversation's name is one line by the same rule the store keeps for titles, so it is shortened rather than wrapped onto a second row.

## Context attachments

Attach things to a message the way VS Code does. The 📎 button in the composer opens **Add context**, and typing `#` at the start of a word opens the same picker:

| Menu item | What it does |
| --- | --- |
| **Files & Folders…** | Searchable multi-select picker over the solution. Only files the read tools can open are listed. |
| **Selection** | The current editor selection. |
| **Problems** | Errors and warnings from the Error List. Empty until the solution has been built. |
| **Symbols** / **Source Control** / **Terminal Selection** / **Codebase** | Listed but disabled, with the reason in the tooltip. |

The file you have open in the editor is attached automatically, the way VS Code keeps the active editor in the chat's context: it appears as the first chip, outlined in the accent colour, and follows you as you move between tabs. The **◎ Auto** button in the composer turns it off and on, and so does the chip's `×`. The choice is remembered. The button shows which state it is in with an accent outline when the file is riding along, and leaves its label in the ordinary text colour, so the label is readable whatever the theme's accent happens to be.

Each attachment becomes a chip above the composer: click its name to open the file, `×` to remove it. A file chip sends the **path**, and the agent reads the file through the read tools when it needs it — file contents are never pasted into the composer or stored in the transcript, so a large file costs one tool call rather than a wall of context. With tool calling switched off there is nothing that could open the path, so a bounded excerpt of the file goes with that one request instead; the transcript still records only the path. **Send to LiteLLM Chat** from the editor's context menu attaches the selection the same way.

**Retry response** puts back what the turn was asked with — the prompt *and* its attachments — rather than the words alone.

## Tools

Tool results are shown as compact progress rows and added to history; send another message to continue after a tool result. Reading tools: `read_file`, `list_files`, `search_files`, `open_file`, `get_current_file_path`, `get_selected_text`, `get_solution_name`, `get_project_name`.

Writing tools, using the same names and parameters as GitHub Copilot Chat's agent mode: `apply_patch`, `create_file`, `replace_string_in_file`, `multi_replace_string_in_file`, `create_directory`. They exist so the chat edits your files instead of printing a corrected snippet for you to paste.

- **Edits apply straight away**, as they do in VS Code, and are made through the editor when the file is open, so **Ctrl+Z** takes them back. A file that was closed is written and then opened so you can see the change.
- **Tools ▸ Options ▸ LiteLLM ▸ Features ▸ Enable file editing** turns the writing tools off; the model is then told plainly that it cannot change files. **Confirm before each edit** shows the diff in the chat with **Allow** and **Skip** first. Both are off/on respectively by default.
- Every applied edit row offers **Copy diff** and **Undo this edit** on its right-click menu. The diff is never printed into the transcript — it is there when you ask for it.
- Reads and writes are confined to the solution root: a path outside it is refused outright, and `..\..` traversal is resolved before the check. Nothing runs a command, a task, or a network request.

A turn may use up to **Maximum tool rounds** (Tools ▸ Options ▸ LiteLLM, default 25, ceiling 200) before the chat stops and asks *Continue to iterate?* — the same checkpoint Copilot Chat uses. **Continue** raises the budget by half (25 → 38 → 57 → …) and keeps working from the results already gathered; **Stop** ends the turn with those results intact, so your next message continues from them. Because the budget is only consulted once a round's tool results are in, the model is never handed a half-finished exchange.

The chat, the dashboard and the two dialogs follow the Visual Studio theme, and they follow it the way Visual Studio's own windows do: the XAML names no colour of its own, it asks the shell for its brush — the same brush the shell paints its own chrome with — and WPF re-resolves it in the same moment the shell repaints. There is no snapshot of the theme to go stale and no handler in the path, so switching under **Tools > Options > Environment > General** repaints the tool windows exactly when it repaints the IDE.

That covers the controls that are easy to get wrong. Scroll bars, check boxes and text boxes are drawn from the shell's own `ScrollBar*`, `CheckBox*` and `TextBox*` brushes, because the stock WPF templates paint themselves from Windows system colours — a light grey scroll bar under a dark IDE, which is where the two used to disagree. Drop-downs own their template for the same reason: the stock one paints both the closed box and the open list from Windows. Menus take their highlight from the shell's list-row colours, since Visual Studio publishes no menu brush of its own; the stock `MenuItem` template would otherwise draw the Windows-blue band. Progress bars keep the shell's own template, so its indeterminate animation is the shell's.

What Visual Studio publishes no token for — the selection highlight, the text on it, and the error colour — is derived from the theme's accent and re-derived on every theme change. Where the shell does publish its own control style, the extension uses that style rather than a copy. **View > Output > LiteLLM** records, once, which of those styles the running shell published and how many colour tokens it did not answer — so the question of whether the extension is standing in for anything is answered from inside the IDE rather than assumed.

The dialogs take Visual Studio's own dialog styles as well, so their buttons, text boxes and check boxes — the default button in particular — are the shell's rather than an imitation. There is one honest limit: Visual Studio's internal control templates are not public, so "identical" means every colour is the shell's own brush for that role and every control the shell publishes a style for wears that style, not that the templates were copied.

Diagnostics appear in **View > Output > LiteLLM**. The status bar reports discovered models and enabled servers.

## Troubleshooting

- **No models:** verify the base URL and that `<base>/v1/models` is reachable; use Dashboard **Test**.
- **401/403:** update the key. A blank key field retains the previous value; remove and recreate a server to remove a key.
- **404:** confirm the URL does not already include `/v1` and that the model route exists.
- **Timeout/network errors:** increase the timeout in Options and inspect the LiteLLM Output pane.
- **VSIX build fails outside Visual Studio:** install the VS extension development workload and build from its Developer Command Prompt; the Core project/tests can be built independently with `dotnet`.

## Architecture note

The chat behavior adapts public VS Code concepts—session/request/response state, incremental response parts, explicit context items, cancellation, retry, model selection, and bounded history—to the existing Visual Studio/WPF and LiteLLM architecture. It does not copy GitHub Copilot proprietary implementation or introduce a webview/React chat application. Advanced VS Code features such as remote sessions, virtualized editor-backed content parts, participant ecosystems, and provider-specific confirmation protocols remain platform/provider-specific.
