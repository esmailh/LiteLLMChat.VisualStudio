# Working in this repository

A Visual Studio extension (VSIX) that puts an assistant into a tool window. It has two execution
backends and they are not the same thing:

- **LiteLLM** (the default) — the extension runs its own agent loop against an OpenAI-compatible
  server. `AgentHarness.RunAsync` is that loop; `ChatControl.SendAsync` sets the turn up, hands it
  over and draws the outcome.
- **Claude** — `ChatControl.SendClaudeAsync` launches the Claude Code CLI and hands it the turn.
  It has its own harness, so a change to the loop must not be made there.

## Building

.NET Framework 4.8 with the Visual Studio SDK. `dotnet build` does **not** work on these projects;
they need MSBuild from the Visual Studio install:

```powershell
$msbuild = (Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" |
    Where-Object { $_.FullName -notmatch '\\amd64\\' } | Select-Object -First 1).FullName
& $msbuild "src\LiteLLM.Core\LiteLLM.Core.csproj" /t:Build /p:Configuration=Debug
```

MSBuild takes **one project per invocation** — two in one call fails with MSB1008. These four are
the ones to build:

```
src\LiteLLM.Core\LiteLLM.Core.csproj
src\LiteLLM.GitWrapper\LiteLLM.GitWrapper.csproj
src\LiteLLM.VisualStudio\LiteLLM.VisualStudio.csproj
tests\LiteLLM.Core.Tests\LiteLLM.Core.Tests.csproj
```

`LiteLLM.VisualStudio.sln` cannot be built as a whole: it references
`..\LiteLLM.CrossPlatform\src\LiteLLM.Cli\LiteLLM.Cli.csproj`, which is not part of this checkout.
The Error List is therefore rarely empty, and an entry about that missing project is not yours to
fix — editing around it is worse than leaving it alone.

The extension project carries pre-existing `async void` and VSTHRD analyzer warnings. Leave the
warning count where you found it rather than silencing them wholesale.

## Tests

xunit, net48, no mocking library and no FluentAssertions — hand-rolled fakes and plain `Assert.*`.
Run them against the built assembly, not against the project:

```powershell
$vstest = (Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" |
    Select-Object -First 1).FullName
& $vstest "tests\LiteLLM.Core.Tests\bin\Debug\net48\LiteLLM.Core.Tests.dll"
```

`vstest.console.exe` does not accept `/nologo`.

**Where testable code may live.** The test project's only `ProjectReference` is `LiteLLM.Core`, and
there is no `InternalsVisibleTo` anywhere in the repository. Logic that needs a test therefore goes
in `src/LiteLLM.Core` with a matching `<Name>Tests.cs` in `tests\LiteLLM.Core.Tests\`. Anything that
touches `DTE`, `AsyncPackage`, `ContextTools`, `EditTools` or `SolutionBuild` is beyond the test
project's reach and has to be checked by hand in Visual Studio.

## MCP

MCP lives in `src/LiteLLM.Core/Mcp/` and is hand-rolled: no SDK, no new NuGet package, because a
net48 VSIX cannot reliably win the binding conflicts an SDK's modern dependencies would bring into
the Visual Studio process. `McpClient` owns the connections and the aggregated tool snapshot;
`McpSession` owns one server's era, request correlation and notifications; the transports are the
test seam, and the tests drive them with fakes.

- **Two eras.** A server is probed with `server/discover` (modern, 2026-07-28) and falls back to
  `initialize` + `notifications/initialized` (2025-11-25 and earlier). `ProtocolVersion` on the
  server config overrides the probe.
- **Tools are named `mcp__{server}__{tool}`.** `McpToolNaming` is the only authority for the reverse
  map; the UI always shows the server's own names. MCP calls are never disk-only, so they stay
  serial — approvals and side effects must keep the order they were announced in.
- **The approval card is the LiteLLM path's.** `McpApproval.Confirm` is wired in
  `ChatControl.OnLoaded` beside the edit and terminal handlers; a missing handler denies. A denial
  still answers the announced call with a `tool` message, so `Sanitize` stays satisfied.
- **The Claude backend is not this loop.** `SendClaudeAsync` hands the run to the CLI and
  `McpClaudeConfig` writes a per-run `--mcp-config` under `%LOCALAPPDATA%`; the CLI's
  `--permission-mode dontAsk` cannot show the extension's card, so only tools the policy already
  allows are added to `--allowedTools`. Do not add approval UI inside that path.
- **Nothing under the solution executes by itself.** A repository `.mcp.json` is never auto-loaded,
  and the Dashboard's import saves entries disabled and connects nothing. What *does* start is what
  the user enabled: `ChatControl.OnLoaded` calls `McpClient.ConnectEnabledAsync` so a restart does
  not mean re-enabling every server, and the row checkbox connects or disconnects at once. Both are
  LiteLLM-only — the Claude CLI starts its own processes. The row ↻ is `ReconnectAsync`, the only
  path that drops a live session and starts it again (`ConnectAsync` reuses an unchanged one).
- **Secrets are DPAPI-protected** through `McpSecrets`/`ApiKeyProtector` (CurrentUser). Exports omit
  them, logs redact them, and the token store is a separate file, never `settings.json`.
- **Every bound says so.** A clipped result or a list the automatic offer shortened reports it; a
  server that cannot connect draws its own row and never fails the turn. A session that ends by
  itself is a row as soon as `ServerClosed` fires, and a turn that starts with MCP on but no
  enabled server for the solution says so before the first round.
- **A close belongs to the session that owns it.** `OnClosed` removes a registration only when the
  map still holds that same connection, and only then raises `ServerClosed` — a deliberate
  `Disconnect` must not be reported as a server ending, nor evict the session that replaced it.
  `Disconnect` bumps a per-server generation and `ConnectOneAsync` captures it, so a handshake that
  finishes after the user turned the server off disposes its session instead of registering it.
  Without both, a fast enable/disable leaves the process running while the window says *not
  connected*, and the next attempts look like they cannot connect.
- **The message selects the turn's MCP tools.** `McpToolMentions` is the grammar and
  `McpToolSelection` resolves it against the snapshot `PrepareTurnAsync` just produced: an exposed
  name, `#server.tool` (or `#server/tool`), a raw name only one server offers, and a backticked name
  as a hint that never narrows. Narrowing applies to the MCP definitions sent in that turn only; the
  built-in tools stay. `AgentHarness.RunToolAsync` refuses an unselected call before policy and
  before the transport and still answers it with a `tool` message. A name that resolves to nothing
  stays a row and a sentence in the prompt but does not narrow — a typo must not strip the turn of
  every MCP tool. The selection is read once, from the active turn's first user message.
  With `McpToolsOnlyWhenMentioned` on (the default), a message that says nothing about MCP is offered
  no MCP tool at all — an empty definition list, not the snapshot; naming a tool, or the word MCP in
  prose, takes the path above. That is what keeps an ordinary turn from carrying every server's
  schemas and paying for them before the model is asked. The prompt is told, and `McpAvailability` says
  it in the chip's tooltip rather than in a row: a row per turn for a state is the sentence
  `McpAvailability` was introduced to stop repeating.
- **The cap bounds the offer, never the list.** `McpToolSnapshot` holds every tool the servers listed —
  `Tools`, `Definitions`, `TryResolve` and dispatch reach all of them — and `AutomaticDefinitions` is
  only what a message that names no tool is *offered*; `McpClient.ToolDefinitions` returns that list.
  A named tool is in the request however far down the list it sits. Before, the cap was applied while
  the snapshot was built, so it kept the alphabetical first sixteen of Grafana's sixty-five and the
  reading tools a dashboard question needs (`query_prometheus`, `query_loki_logs`, `search_dashboards`,
  `get_dashboard_by_uid`, `list_datasources`) were never sent — and the model answered, truthfully,
  that the tool was not available in this environment. The default is
  `McpToolSnapshot.DefaultAutomaticTools` (128, a whole ordinary server) and it has to stay a bound:
  every offered schema is prompt space in every round. `McpToolCap.Upgrade` lifts a saved 16 once,
  because sixteen was the old default and every save wrote it down; `UserSettings.McpToolsPerTurnUpgraded`
  is what makes it once, so a 16 chosen afterwards stays 16, and `ExtensionServices.ApplyOptions` writes
  the lifted value back to the options page or the next launch reads the old default again. Never put
  the cap back into `Build`.
- **A chat can be told once.** `McpChatInstruction.Read` is the grammar, and
  `ChatSessionState.McpEveryTurn` is what it writes. One sentence has to name MCP, carry a scope that
  makes it about the conversation ("this chat", "from now on", "always", «این چت», «همیشه», «از این به
  بعد») and ask to use, offer, enable or switch it off; a negation in that same sentence is the
  opposite instruction, so a chat can be talked out of the state as well as into it. With the flag on,
  `AgentHarness` resolves the turn's selection as if `McpToolsOnlyWhenMentioned` were off, for the
  chat alone. The flag belongs to the conversation, is read once at the start of a turn like
  `IgnoreHistory`, and is honored only when tool calling and MCP are both on. That sentence is the
  only control — there is deliberately no switch, so the transcript row and the chip's tooltip are
  where the state is disclosed, including the sentence that clears it. Fenced code is stripped before
  reading, because a pasted configuration is a quotation; a passing mention and a question stay
  `None`, because a miss costs one repeated sentence and a wrong hit charges every later turn for
  schemas the user never asked for. Persian words are matched as stems (suffixes and the ZWNJ split a
  compound) and letter variants are folded; Latin words are matched whole, so "use" does not match
  "useful".
- **A list is read once per session.** `PrepareConnectionsAsync` reads `tools/list`,
  `resources/list`, `resources/templates/list` and `prompts/list` only for a list this connection has
  not read; the flags live on `Connection`, so a new config, a reconnect or a `list_changed`
  notification is what brings a list back. Re-reading them on every turn was the second half of the
  same slowness: each is a round trip, four per server, and a remote server answers them over the
  network before the request is even built. A read that failed is not marked, so the next turn tries
  again; the transcript says nothing about a list that was simply reused.
- **UI additions** are `McpDashboardControl` (the Dashboard's MCP tab), `McpServerDialog`,
  `McpBrowserDialog` and `McpPromptDialog`. Composer `@` lists connected resources; `/` lists MCP
  prompts on the LiteLLM backend and the CLI's own commands on the Claude one; `#` lists connected
  tools on the LiteLLM backend, with a **Files & Folders…** row that keeps the file picker, and
  keeps the old file-picker meaning with no tools and on the Claude backend.

## Projects beyond the solution

`Workspace` (`src/LiteLLM.Core/Workspace.cs`) is the one place that decides which folders a
conversation's tools may read and change, and how a path names one of them.

- **The solution is root zero**, and every rule that existed before this does still: an unprefixed
  relative path is the solution's, and a solution on its own produces exactly the labels and
  prompt text it did when there was only one root.
- **An added project is addressed by its folder name**: `OtherApp/src/Foo.cs` going out and coming
  back. The solution wins a path it already has, so a folder of its own that shares the name stays
  reachable and a path written before the project was added keeps pointing at the file it meant.
  Two checkouts with the same folder name are numbered (`App (2)`) rather than shadowed.
- **The list belongs to the conversation** (`ChatSessionState.ExtraRoots`), not to the settings: a
  new chat must not inherit access to a folder added for another one. It is bounded
  (`Workspace.MaxExtraRoots`), persisted normalized, and refused when it would add nothing —
  `Workspace.WhyNotAdd` says why, in the user's own terms.
- **Everything that resolves a path goes through it**: `ContextTools`, `EditTools` (which records
  the owning root on each edit so undo re-checks the right folder), the file picker, the `@` list
  and `open_file`. `PathGuard` still does the confinement and the reparse-point check, per root.
  The turn's own `Workspace` is what a tool call is answered against — not whatever conversation
  happens to be selected when the call runs.
- **Commands and the build stay on the solution**, and both the prompt and the row say so: a
  working directory is one folder, and `SolutionBuild`/`SolutionTests` cover the open solution. To
  verify an added project the model runs its own build command.
- **The Claude backend is handed `--add-dir`** for each added project (`ClaudeCodeArguments.Build`)
  and a sentence naming them in the prompt; the CLI's own tools do the reading there.

## File references in a reply

A reply names a file, not a path: `StoreGroupService.cs`, not `src/StoreGrouper/Services/StoreGroupService.cs`.

- **`FileReferenceResolver` (Core) is the only answer to "what file is this name?"** Joining the name to
  the solution root alone answers "not found" for every file that sits inside a folder, and a link that
  is drawn and then refuses to open is worse than no link. The order is exact path, then a path written
  from part way down (on a folder boundary), then a bare name; the shortest workspace path wins two files
  of the same name and an alphabetical tie-break keeps two runs of the same reply agreeing.
- **The list is read once per workspace** (`ChatControl.ReferenceFiles`), keyed on every root plus
  `EditTools.Revisions`, and it does **not** filter by `ContextTools.ScannableExtensions` — the parser
  knows extensions the `@` list does not offer. The render-time predicate and the click both read that
  list; a reply is redrawn for every streamed chunk, so the predicate must never touch the disk.
- **An absolute path is never substituted.** The model named one place, and a same-named file elsewhere is
  a different file; only a non-rooted name falls back to the name search.
- **The key is why a click still works mid-turn.** A file a turn creates bumps the edit count, so the list
  is rebuilt rather than the click giving up on a name it has not seen; when a solution is closed the
  index is dropped by the same root comparison the symbol index uses.

## Running the project and really testing it

Reading a solution is not the same as running one, and until 1.8.12 there was no way to call a service at all. The
flow is opencode's, not a set of extra tools: **one command, a `background` flag, a log file, and the same tools to
read it and to test what it started.** Start the program, read its log until it says it is listening, send real
requests, correlate them with the log, stop it.

- **`background: true` is the same tool, not a second one.** `run_powershell` and `run_cmd` in
  `TerminalTools` return an exit code when they wait for the end, and an id, a pid and the path of a log file
  when they do not. There is no `shell` / `stop_shell` pair: killing is `taskkill /PID <pid> /T /F` through
  `run_cmd`, which is also what the model is told. `workdir` and `timeout_seconds` are the other two arguments,
  and a timeout applies only to the foreground case.
- **`BackgroundShells` (Core) owns them, once, through `ExtensionServices.Shells`.** A server outlives the tool
  call and the turn that started it — that is what makes "test this endpoint" possible in the next message — so
  it is not stopped in `RunAsync`'s `finally`. `ChatControl` disposes it with the extension.
- **A shell belongs to a conversation.** `Start` records the session id, `Running(owner)` and `Drain(owner)`
  filter by it, and `ChatControl.ShellCompleted` draws a row only for the chat that started the shell. Two
  conversations share one manager; a notice about one chat's server must not appear in another.
- **Output is redirected by the shell, not piped.** `CommandExecutor.StartInfoFor(shell, command, workdir, log)`
  is the single quoting authority: PowerShell gets `& { <command> } *> '<log>'` through `-EncodedCommand`, cmd
  gets `cmd /d /s /c "<command> > "<log>" 2>&1"`. A pipe is a handle every child inherits, and a server holding
  one open keeps a reader waiting forever; a file has no reader and is still readable after the shell is gone.
- **A log is read from its end.** `ShellLog.Read` opens it `FileShare.ReadWrite | Delete`, detects UTF-16 from
  the file (PowerShell's `*>` writes UTF-16LE with a BOM) and `read_file` on a log returns the tail unless
  `start_line` is given, because the last line is what says a program is ready. `ShellLogTests` is where those
  cases live, including a live file and a tail window that starts past the BOM.
- **`read_file` recognises its own logs first.** `BackgroundShells.TryResolveLogPath` accepts a full path or a
  bare `shell-1.log`, and only under the session's own folder. A log is outside the workspace by design, so the
  workspace check would otherwise refuse the one file the extension just told the model to read.
- **A completion notice goes into the system prompt, never into `_history`.** `ShellNotice` builds the
  `<shells>` block, `AgentPrompt.WithShells` appends it, and `AgentHarness` drains at each round boundary
  (`_shellNotices` keeps what was already delivered, so a failed round does not lose the news). Same reasoning as
  the Error List: it is a fact about the outside world, and a message would need `Sanitize` handling, would be
  stored in the session, and would outlive the shell it describes. The transcript gets **one row** when the
  event fires, so the user sees it too.
- **A deliberate stop is silent.** `Stop` sets `Silent` before the kill, so the exit event it causes is not a
  completion: the turn asked for it, the answer it has is the call's own, and announcing it would make the model
  read a log it chose to close. `Dispose` does the same for everything still running.
- **Starting is a command and uses the command card** (`TerminalTools.Confirm`). A background command's card says
  so, and its button says *Start*: "run this" and "leave this running" are different answers. A missing handler
  denies, as everywhere. The Claude backend never sees any of this — it runs its own harness.
- **`ToolExecutionPolicy` keeps the background case under `EnableTerminalTools`**, because it is the same tool:
  the switch that gates a command gates what a command could leave running.
- **Every bound says so.** `MaxShells` is refused with a sentence naming the shells to stop; a missing working
  directory is refused with its path; a command that died before it was registered still reports its exit code.
  A shell that ends is never silent, and neither is a start that could not happen.
- **`workdir` is confined like a path.** It resolves through `Workspace.Resolve` — solution-relative, `Label/...`
  for an added project, or an absolute path inside a root — and defaults to `Workspace.Primary`.
- **Unlike opencode, a completion does not start a turn by itself.** The notice is delivered with the next
  message; a shell ending while the chat is idle must not spend a model call the user did not make. The row in
  the transcript is what keeps it from being silent.
- **The turn-end row names what is still running** instead of stopping it: a shell is the conversation's, and a
  server the user watched start must not disappear unexplained. What the turn owes them is the pid that ends it.
- **The guidance is in `AgentPrompt.BaseInstructions`**, next to the command rules it corrects: discover how the
  project starts from `launchSettings.json` / `appsettings` / `Program.cs` rather than guessing, ask only for what
  the repository cannot say, start it with `background`, read the log for the readiness line instead of sleeping,
  send requests with `curl.exe`, treat 4xx/5xx as results, correlate a request with the log line it produced, and
  stop the program or say which shell is left running.

## The agent's browser

`browser` is the one way a turn reaches outside the workspace into a window: a Grafana login, a consent screen, a key
that is shown once — and, unlike what shipped in 1.8.14, everything that comes after it. The page it opens is a page
the agent can read, the buttons it sees are buttons it can press, and the session it signs in to is a session it can
use. It is the answer to opencode's `bash`-opening-a-browser, given a protocol instead of a shell.

- **One tool, five actions.** `BrowserTools` (Core) words the call and `BrowserSession` (Core) drives the page:
  `open` (url), `read`, `click` (target), `type` (target, text), `script` (code). One definition rather than five keeps
  the prompt small and the card honest — the card says which action it is, with the address and the model's own
  selector or script on it. Every action answers with the page afterwards: address, title, visible text, clipped and
  said to be clipped.
- **The profile is the feature.** `BrowserProfile` starts Chromium with `--user-data-dir` under
  `%LOCALAPPDATA%\LiteLLM.VisualStudio\browser` and `--remote-debugging-port=0`. So a sign-in done in that window is
  still signed in on the next message and after a restart, and nothing here can see or change the browser the user
  browses in — Chromium holds one profile per directory. Driving the user's own profile was refused: it is locked
  while their Chrome runs, and reaching into it would expose every site they are signed in to, not the one page this
  turn was given.
- **Port 0, and the file that names it.** The browser picks a free port and writes it, with its browser-level socket
  path, to `DevToolsActivePort` in the profile. `DevToolsEndpoint` parses it, builds `/json/version`, `/json/list` and
  `/json/new`, and decides which target a call means — the tab this session was driving, then the address it asked
  for, then the first real page rather than a blank one. The socket path in that file is what proves a port still
  belongs to this profile; a probe that answers with a different path means another process owns it, so the session
  starts its own window instead of talking to a stranger. Reuse is what makes the second turn cheap; a stale file is
  what makes a launch necessary, and neither is a failure.
- **The protocol is hand-rolled on purpose.** `ClientWebSocket` in the framework, JSON in and JSON out, requests
  numbered and answered by number: no SDK, no new package, for the same reason MCP is hand-rolled — a net48 VSIX in a
  Visual Studio process cannot afford the binding conflicts. `ICdpConnection` and `IDevTools` are the test seams; the
  three HTTP endpoints are `HttpDevTools`, and every fake in the tests answers the JSON a browser would.
- **A page is read, never a cookie store.** The extension's only reach into the session is the page: `script` runs in
  the page's own origin, so `await fetch('/api/…', {credentials:'same-origin'})` carries the cookies the browser holds
  — including the HttpOnly ones no client can read — and CORS never applies. That is how an account is used without
  its password ever reaching the chat, and it is why nothing here parses Chrome's encrypted cookie database.
- **A page is a document, so actions are serial.** One gate per session, and one action at a time: two calls racing
  on one document would mean the second reading a page the first was replacing.
- **Waiting is bounded and stated.** `WaitForReadyAsync` polls `document.readyState` (the context is destroyed by the
  navigation that just started, so a refusal is "ask again") and then settles briefly, because a single-page app says
  "complete" with an empty shell. Twenty seconds, one constructor argument, then the page is read anyway and the note
  says it was read early. The browser's own start has the same bound, and a launch that never reports an endpoint says
  which profile to close.
- **Every failure keeps its own sentence.** A page that refused the script answers with the page's own first line
  (`BrowserScripts.Describe`), not a protocol number; an element that is not there says so; a click that was found but
  threw says that instead. A failed call drops the socket so the next one attaches again rather than failing on a
  connection that will never answer.
- **Only http and https.** `TryParseUrl` trims, refuses whitespace/quotes/control characters, caps the address at
  `MaxUrlLength`, and refuses `file:`, `javascript:`, `ms-settings:` and anything else. A host written without a
  scheme is answered with the corrected address, because that is the mistake a model makes most often.
- **A machine with no Chromium still gets the page.** `BrowserLauncher` is unchanged and is now only the fallback:
  association, `cmd /c start`, then the installed browsers. The result says plainly that this window is the user's own
  and no tool can read it — the sentence 1.8.14 had for every launch is now true only of this one.
- **The card is the command card's sibling.** `ChatControl.AskToUseBrowserAsync` names the action, the address and the
  model's selector, value or script, offers **Skip** and the action's own button, and honours `_disposed`,
  `AllowAllPermissions` and `ConfirmEachCommand` the same way `AskToRunCommandAsync` does. A missing handler denies.
  Its caption says the one thing a user cannot infer: this is a profile of its own, not the Chrome they are signed in
  to. Every action asks, not only the first: acting inside a window is the side effect being approved.
- **Gated with the terminal tools.** `ToolExecutionPolicy.Terminal` names it, and `ExtensionServices.ToolsFor`
  offers `BrowserTools.Definition` inside the `EnableTerminalTools` branch: driving a window on the user's desktop is
  the same decision as running a command. `ExtensionServices.Browser` owns the one session, and `Dispose` drops the
  socket and never the window — closing Visual Studio must not close a browser the user is working in.
- **The prompt carries the rules, the result carries the facts.** `AgentPrompt` says what the browser is for, that the
  sign-in done in it is kept, that `script` is how the session is used, and that a password, cookie or token never
  goes into the chat. `/compact` and the rest of the loop are untouched; nothing about the browser is stored in a
  session.
- **The Claude backend is not this tool.** The CLI runs its own harness and its own tools; `browser` is LiteLLM-only
  in the same way every other built-in tool is.

## Style

- One concept per file.
- Comments explain *why*, not what. The existing code is written that way; match it.
- `public` in Core, `internal` in the VisualStudio project.
- Nullable reference types are enabled in every project.
- Nothing in the agent loop happens silently: a shortened result, a checkpoint, a test run and a check
  that could not be made each get their own row in the transcript.

## Changing the agent loop

An OpenAI-compatible server rejects the **whole** transcript when these break, so the loop cannot
take them lightly:

- every call the assistant announces must be answered by a `tool` message carrying that call's id,
  and no `tool` message may answer a call that was never announced. `ChatStateLimits.Sanitize`
  enforces this and repairs a transcript that has drifted out of step;
- a tool's arguments arrive from a model, not from a person. Read them through `ToolArguments`,
  which reports a malformed or wrong-typed call instead of emptying it and producing a sentence
  about a path the model never sent;
- a long turn is bounded by `ContextBudget.Trim`, which shortens the *body* of an old tool result
  rather than dropping the message — dropping it would break the pairing above. The oldest result gives way
  first, and the sum is what is bounded: the largest message in a request is its system prompt and it sits at
  the head, so a transcript can go over on a message that may not be shortened, and shortening only the one
  that crosses the line leaves it over with every result in full;
- the budget a request is finally held to is measured on the request that was built (`RequestFitting.Fit`),
  not on the estimate taken before it existed. A few thousand characters of serialization and a re-serialized
  instruction block separate the two, and that is enough to refuse a finished turn — the worst answer
  available, since the work behind it is done. What is over comes out of old tool-result bodies in up to two
  passes, the second spending the measurement the first one's estimate missed. A request that still does not
  fit is rebuilt without the MCP schemas and retried, so the round continues on the built-in tools instead of
  the turn ending on schemas it may not need; the row says so once, because the transcript only grows and
  every later round takes the same path. Only then is the turn refused, and the message names the size reached
  and how much of it is messages versus tool schemas;
- the current Error List goes into the **system prompt** of each round and never into `_history`.
  It is read rather than built, and the block says so, so an entry predating the turn is not read
  as its result. In the history it would need `Sanitize` handling, would be written to the session,
  and would outlive the errors it describes;
- `ContextCompaction` folds the older part of a conversation into a structured checkpoint, the way
  opencode compacts a session. The ceiling it measures against is the model's window minus a buffer
  (`max(15% of the window, 16 000)` tokens, or `CompactionBufferTokens` when that leaves room), and
  the decision is made from the provider's own `PromptTokens` for the last call plus a **two
  characters per token** estimate of what has grown since — this build has no tokenizer, so that
  ratio is deliberately conservative and `RequestFitting` remains the hard character bound;
- a compaction has one of three triggers — `Auto` (the ceiling was reached), `Overflow` (the provider
  rejected the round), `Manual` (`/compact`), stored by name on `CompactionReport` — and each writes
  the same fixed template. A reply that does not carry the template's headings is a failed compaction:
  it gets one `ContextCompaction.Nudge` retry and then the earlier checkpoint stands, with a row and a
  log line. An existing checkpoint is **updated**, not replaced: the previous text takes the place of
  the messages it stands for in the request that rewrites it, so requirements and open questions are
  not lost one fold at a time. The newest `CompactionKeepTokens` (default 15 000) stay verbatim beside
  it, cut on a round boundary so a call is never taken away from its result;
- **the boundary is an index into the whole transcript**, so the same number names the same messages
  on the next round and on the next turn. The request is rebuilt each round as checkpoint + tail, and
  it is the *request copy* only: the chat and the stored session keep every message. A transcript the
  store head-sliced no longer agrees with a boundary from an earlier turn, so `ChatSessionStore`
  resets `CompactionCheckpoint.Covered` to 0 and keeps the text — trusting a stale boundary would skip
  messages the checkpoint never stood for. `TryBuildRequest` folds at most `MaxSpanChars` and never
  claims a message it did not render, and its span budget floors at `MinSpanChars = 8 000`, so a span
  cut only binds on conversations whose flattest form exceeds that;
- the checkpoint is a `user` message wrapping `<conversation-checkpoint>`, because `Sanitize` cuts
  everything before the first user message and a checkpoint that reads like an instruction gets
  answered instead of continued from. **The request outlives every checkpoint:**
  `ContextCompaction.RequestMessage` pins the message the turn is answering verbatim, and every path
  out of `CompactAsync` puts it back in front of the kept rounds — a fold may cost the thread, never
  the question. Pinning is also why the attachment block, attached to the last `user` message, lands on
  the request rather than on the checkpoint;
- the provider's own ceiling gets the last word. `ContextOverflow` recognises the one rejection
  compaction can answer (a 400/413 whose body says the context or prompt is too long), and the round is
  folded and **sent again** instead of ending the turn, up to `ShrinkSteps.Length` times
  (`.85, .7, .5, .35` of what the estimate believed, never above the ceiling), each attempt aiming
  further below the size that was refused. The bubble is reused and cleared, because a rejected request
  produced no answer. A fold also clears the anchor (`MeasuredPromptTokens`/`Chars`/`Model`) — it
  described the request before the fold and measures nothing after it;
- auto-compaction and the overflow fold are both skipped when `IgnoreHistory` is on: that request is
  already this turn only, so there is nothing a checkpoint could stand in for. `/compact` still folds
  the whole conversation, on the LiteLLM backend only, through `AgentHarness.CompactNowAsync`: it
  opens a turn of its own for the row, sends nothing, and the chat stays idle until the next message;
- automatic verification is two checks, and the second depends on the first: the build
  (`SolutionBuild`) and then, only once that has come back clean in **this** round, the tests
  (`SolutionTests`). Tests are never run against assemblies from an earlier round — that reports on
  code which has already been replaced. Neither check ever turns silence into a pass: a run that
  did not happen gets its own row saying so. `TestDiagnostics` reads the runner's output at the
  verbosity `SolutionTests` asks for (`/logger:console;verbosity=minimal`), so changing that flag
  changes what the parser sees.
- the loop itself is `AgentHarness.RunAsync`, and it speaks to the window only through `IAgentView`.
  Only the tools that read nothing but the disk (`ContextTools.IsDiskOnly`) run as a batch, and their
  results are appended in the order the calls were announced — never the order they finished.
  Everything else stays serial: edits and commands ask the user through a card, and a batch of those
  would put the questions in an order nobody chose.
- token counts are the server's (`LiteLlmClient.LastUsage`), not a local tokenizer's guess. A streamed
  call reports them only when the request asked with `stream_options.include_usage`; a direct Azure
  connection is not sent the field, because its endpoint may reject it. If any other server refuses
  the field with a 4xx, the same request is sent once more without it — a missing usage line must
  never become a failed turn. Reading a count can never throw either: an oversized or unreadable
  number is clamped or read as "not reported". A turn's total is one row at the end, never one per
  round.
- **A window is read from the entry the server actually sends.** `ModelRequestBudget.ReadContextTokens`
  accepts `max_input_tokens` flat on the entry — which is what a LiteLLM gateway answers — as well as
  `context_window`, `context_length`, `max_model_len` and `model_info.max_input_tokens`. Reading only
  the nested form made every model on a real gateway look unknown, so a 372 000-token window was
  budgeted at `FallbackContextTokens` (40 000): 55 616 characters, less than one real server's 73 047
  characters of Grafana tool schemas. `RequestFitting`'s last resort then dropped the MCP schemas on
  every round — a row saying so, and a model that truthfully had no such tool. Do not remove the flat
  spelling, and do not read `max_output_tokens` as a window.
- what each model has spent is kept in a ledger (`UsageLedger`, under
  `%LOCALAPPDATA%\LiteLLM.VisualStudio\token-usage.json`), not summed from conversations when the
  Dashboard asks. Conversations are scoped per solution and capped, so a total derived from them
  would shrink when a chat was deleted or another solution was opened. The ledger is seeded once
  from the conversations already on disk — marked so a later launch cannot add the same turns
  twice — and every finished LiteLLM turn adds to it. A turn whose server reported no usage is not
  a zero and contributes nothing; the Claude backend does not write to it.
- `EditTools.Revisions` is a monotonic counter, not `History.Count`. The undo history is capped, and
  both the build check and the edit rows compare the count across a tool call; a count taken from the
  capped list would stop moving and silently stop the check. A turn can put back all of its edits
  (`EditTools.UndoAll`), newest first — the only order in which each edit's saved `Before` is still the
  text that preceded it.
- a chat can be told to ignore its earlier turns (`ChatSessionState.IgnoreHistory`). Only the *request
  copy* is cut: the current turn's own messages are still sent — the tool call/result pairing depends
  on them — and the window and the stored session keep the whole conversation. The flag is read once
  at the start of a turn, so flipping it mid-turn changes the next one, not the running loop.
