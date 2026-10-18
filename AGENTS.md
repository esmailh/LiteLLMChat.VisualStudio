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
- **Every bound says so.** A clipped result or a list the per-turn cap shortened reports it; a
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
- **UI additions** are `McpDashboardControl` (the Dashboard's MCP tab), `McpServerDialog`,
  `McpBrowserDialog` and `McpPromptDialog`. Composer `@` lists connected resources; `/` lists MCP
  prompts on the LiteLLM backend and the CLI's own commands on the Claude one; `#` lists connected
  tools on the LiteLLM backend, with a **Files & Folders…** row that keeps the file picker, and
  keeps the old file-picker meaning with no tools and on the Claude backend.

## Style

- One concept per file.
- Comments explain *why*, not what. The existing code is written that way; match it.
- `public` in Core, `internal` in the VisualStudio project.
- Nullable reference types are enabled in every project.
- Nothing in the agent loop happens silently: a shortened result, a summary, a test run and a check
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
  rather than dropping the message — dropping it would break the pairing above;
- the current Error List goes into the **system prompt** of each round and never into `_history`.
  It is read rather than built, and the block says so, so an entry predating the turn is not read
  as its result. In the history it would need `Sanitize` handling, would be written to the session,
  and would outlive the errors it describes;
- `ContextSummary` folds the oldest part of an over-budget transcript into one briefing, and what
  it covers is always a **prefix ending on a round boundary** (`ContextBudget.RecentFrom`) —
  cutting inside a round would take a call away and leave its result behind. The replacement is a
  `user` message at the head, because `Sanitize` cuts everything before the first one. It is put
  into the *request copy* only: `_history`, the chat and the stored session keep every message;
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
