# A smarter agent loop for the LiteLLM backend — 7 phases

## Context

The extension's LiteLLM backend runs its own agent loop: `ChatControl.SendAsync`
(`src/LiteLLM.VisualStudio/ChatControl.xaml.cs:297-419`) streams a reply, runs the tool calls
in-process, verifies the build, and loops. That loop works, but it is missing the three things
that make a real harness feel intelligent — **what is in the prompt**, **whether the context
stays clean**, and **what the loop does when something fails**.

Today it scores badly on the first two:

- **No context management at all.** Line 306 sends the *whole* in-memory history every round.
  `MaxMessagesPerTurn = 160` is applied only when a session is written to disk
  (`ChatSessionStore.cs:107`), never on the way out — so a live turn can reach roughly
  160 × 12 000 characters. Nothing counts tokens and nothing summarizes. A ten-file refactor
  either dies on a context-length error or, worse, silently stops seeing what it read in
  round 3 and reads it again.
- **The system prompt is 100 % static** (`AgentPrompt.cs:109-172`). Nothing reads `AGENTS.md`,
  `CLAUDE.md` or `.github/copilot-instructions.md` — confirmed by grep. The model never learns
  that this repo is net48 + VSSDK, that `dotnet build` does not work here, or that the solution
  references a project that does not exist.
- **A tool call with malformed arguments gets a wrong diagnosis.** `ContextTools.Parse`
  (`ContextTools.cs:245`) swallows invalid JSON into an empty `JObject`, so a missing brace
  comes back as `No such file: <repo root>` and the model chases a phantom bug.
- **The only automatic self-check is "the build is clean."**

The outcome we want: a loop that keeps its head over a long session, knows the conventions of
the repository it is working in, sees the errors that currently exist before it is asked to
build, and can verify itself against tests and not only against the compiler.

This plan is written to be **resumed**. See the next section.

---

## How to resume this work — the prompt

Copy this file into the repository as `.claude/agent-loop-plan.md`, then paste the prompt below
into any session to continue. It always picks up at the first unfinished phase.

```
Continue the "smarter agent loop" work in this repository.

Read .claude/agent-loop-plan.md first, from the top. It is a 7-phase plan and it carries its
own progress table.

Then do exactly one phase per turn:

1. Find the first phase in the Progress table whose Status is not "done".
2. Implement only that phase. Follow its "What to change" and "Reuse" notes.
3. Verify it before claiming anything:
   - build all four projects with MSBuild (see Ground rules) and confirm zero errors;
   - run the test suite and confirm every test passes;
   - run the phase's own manual check, if it has one.
4. Update that phase's row in the Progress table: Status "done", today's date, and a one-line
   note saying what changed and what the verification showed. Put real numbers in the note
   (build errors, test pass count).
5. Stop and report. Do NOT start the next phase in the same turn.

Rules that hold for every phase:

- A phase whose Status is "done" but whose note lacks a verification result counts as not done:
  verify it first.
- If a phase is wrong, impossible, or turns out to need a decision the plan does not make,
  set its Status to "blocked", write why, and stop. Do not skip it silently and do not invent
  a different design.
- Never end a turn with a broken build.
- Every phase must leave the extension usable: no phase may leave the LiteLLM loop
  half-rewired.
```

---

## Ground rules

These are the facts a cold session needs. They are not all written down anywhere in the repo.

**Building.** net48 + VSSDK projects — `dotnet build` does **not** work. Use MSBuild from the
Visual Studio install:

```powershell
$msbuild = (Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" |
    Where-Object { $_.FullName -notmatch '\\amd64\\' } | Select-Object -First 1).FullName
```

MSBuild takes **one project per invocation**, so build these four in a loop (about 4 s each):

```
src\LiteLLM.Core\LiteLLM.Core.csproj
src\LiteLLM.GitWrapper\LiteLLM.GitWrapper.csproj
src\LiteLLM.VisualStudio\LiteLLM.VisualStudio.csproj
tests\LiteLLM.Core.Tests\LiteLLM.Core.Tests.csproj
```

`LiteLLM.VisualStudio.sln` **cannot be built as a whole**: it references
`..\LiteLLM.CrossPlatform\src\LiteLLM.Cli\LiteLLM.Cli.csproj`, which is not on this machine.

**Tests.** xunit 2.9.2 + `xunit.runner.visualstudio` 3.0.0, net48, ~151 cases, no mocking
library and no FluentAssertions — hand-rolled fakes and plain `Assert.*`. Run with
`vstest.console.exe` against the built DLL (it does not accept `/nologo`).

**Where testable code may live.** The test project's only `ProjectReference` is
`LiteLLM.Core`, and there is **no `InternalsVisibleTo` anywhere in the repo**. So:

- logic that needs a test goes in `src/LiteLLM.Core`, as a new file with `<Name>Tests.cs` under
  `tests\LiteLLM.Core.Tests\`;
- anything touching `DTE`, `AsyncPackage`, `ContextTools` or `EditTools` is out of the test
  project's reach and must be verified by hand in Visual Studio.

This constraint is why phases 1, 2, 3, 5 and 6 all put their logic in Core.

**Scope.** Every change is in the **LiteLLM** path (`SendAsync`). `SendClaudeAsync` runs the real
Claude Code CLI and must not be touched — it has its own harness.

**Style.** The repo's own conventions: one concept per file, comments explaining *why* rather
than what, `internal` in the extension project, `public` in Core, nullable enabled, and a
visible transcript row for anything the loop does on its own. Nothing happens silently.

---

## Progress

| # | Phase | Status | Note |
|---|---|---|---|
| 1 | Give the request a budget, and stub what falls out of it | done | 2026-10-14 — `ChatStateLimits.MaxPromptChars = 200000` + `KeepRecentToolRounds = 2`; new `Core/ContextBudget.cs` (`Trim`, with an `out int shortened` overload) replaces the body of an over-budget **old** tool result with a one-line note naming the call it answered, keeping `Role`/`ToolCallId`/`ToolName` so the call/result pairing `Sanitize` and the server require survives. `ChatControl.xaml.cs:308` trims before sanitizing and writes one transcript row per turn (`Context · N older tool results shortened…`). Verified: MSBuild 0 errors on all 4 projects (no new warnings); 165/165 tests pass, 7 of them new in `ContextBudgetTests`. |
| 2 | A tool call with bad arguments must say so | done | 2026-10-14 — new `Core/ToolArguments.cs`: `TryParse`/`TryText`/`TryNumber` report what arrived and what to send, instead of emptying the object. `ContextTools.cs` returns `Fail(…, "invalid arguments")` before dispatch, every tool reads its own arguments through it, `read_file` names a missing `path`, and the swallowing `Parse`/`Text`/`Number` helpers are deleted. Deliberate narrowing: `"200"` for an integer argument is now refused rather than converted. Verified: MSBuild 0 errors; warnings still exactly the baseline (Core 21, VS 72, tests 0 — none from the new code); **177/177** tests pass, 12 new in `ToolArgumentsTests`. One run in eight reported a single failure in an unrelated settings test that did not reproduce in the other 7 (see report). |
| 3 | Tell the model where it is (AGENTS.md) | done | 2026-10-14 — new `Core/RepoInstructions.cs` reads `AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md` under the solution root, each under a `From <file>:` heading, the whole block capped at `MaxChars = 8000` by `ChatStateLimits.Clip`; an unreadable file reads as null, so a locked one costs a section and not the turn. `AgentPrompt`'s prose moved to `BaseInstructions` and the block is appended as a last `Repository instructions` section (the four existing prompt promises untouched; 2 wiring tests added). `ChatControl.SendAsync` loads it once per turn beside the other turn setup and passes it to `Build`. Wrote this repository's own `AGENTS.md`. Verified: MSBuild 0 errors; warnings still exactly the baseline (Core 21, VS 72, tests 0); **189/189** tests pass (10 new in `RepoInstructionsTests`, 2 in `EditTests`); loaded the built Core in PowerShell against this repo — the block is 3958 chars, untruncated, and the section appears in the real system prompt. |
| 4 | Put the live Error List in front of the model | done | 2026-10-14 — new `BuildDiagnostics.ToStandingNote(errorListText)`: counts over the **whole** list, then at most `MaxStandingLines = 20` lines and `MaxStandingChars = 2000` of detail, headed `Current errors in the solution` and appended last by `AgentPrompt.WithErrors` (new optional `solutionErrors` on `Instructions`/`Build`). `ChatControl.SendAsync` reads `SolutionBuild.ReadErrorListAsync()` **every round** and passes it to `Build`; system prompt only, never `_history`, so no `Sanitize` handling, nothing stored, and it vanishes with the errors. Empty list → `""` → the system message is byte-identical to before this phase. Deviation from the plan worth knowing: the plan said "no unit test", but the bounded note is Core logic and **7 tests were added** (5 in `BuildTests`, 2 in `EditTests`) — and running the built Core over a real-shaped 65-entry list caught a real defect: the note claimed *"the first 20 lines"* while the character cap had cut it after 9, i.e. it understated the list. Fixed by counting the lines that actually went in. Verified: MSBuild 0 errors on all four projects; warnings exactly baseline (Core 21, VS 72, tests 0); **196/196** tests pass. Measured block size in the built assembly: 1339 chars for a typical 31-entry list (`the first 20 of 31`), 2248 worst case for 64 long-line entries (`the first 9 of 65`). **Manual Visual Studio check not run** — needs a live VS session. |
| 5 | Summarize instead of only stubbing | done | 2026-10-14 — new `Core/ContextSummary.cs` folds the oldest part of an over-budget transcript into one briefing, so a long turn keeps the thread instead of only losing it. The covered part is always a **prefix ending on a round boundary** (the now-public `ContextBudget.RecentFrom`), replaced by a single `user` message at the head — the only position `Sanitize` will not cut — and only in the *request copy*: `_history`, the chat and the stored session keep every message. Hysteresis (`MinNewMessages = 6`) is what makes "summarize once, reuse the text" true rather than a call per round; the span (40 000), each message (2 000) and the summary (4 000) are each bounded, and the summary is bounded again on the way to disk. `UtilityModelKey` setting added in all three required places; the utility call carries its own `MaxTokens = 700`, no tools, and `record: false` so it cannot overwrite the exchange the *Copy request JSON* menu is showing. `ChatTurnState.Summaries` keeps the rows, so a reloaded conversation draws back the same ones. Verified: MSBuild 0 errors on all four projects; warnings exactly baseline (Core 21, VS 72, tests 0); **207/207** tests pass, 11 new in `ContextSummaryTests`. Measured end to end on the built Core: a 61-message / **79 796-char** transcript summarizes 39 messages into a 40 680-char request, and the merged-then-trimmed-then-sanitized transcript is **31 584 chars — 60.4 % smaller**, 23 messages, opens on `user`, `Sanitize` repaired nothing, 0 unanswered calls, 0 orphaned results. **Manual Visual Studio check not run** — needs a live VS session. |
| 6 | Run the tests, not just the build | done | 2026-10-14 — new `Core/TestDiagnostics.cs` maps the runner's console output onto the existing `BuildReport` (failing tests → `Errors`, skipped → `Warnings`, so `report.Errors == 0` and the repair loop keep working unchanged), keeps at most `MaxFailures = 5` failure blocks and says how many it left out, and has its own `RepairReminder` — including "do not delete, skip or weaken a test to make it pass". `Core/TestProject.cs` decides from a project file's text whether it is a test project and finds the newest assembly it built under `bin`; `WindowsExecutableResolver` learned `vstest.console.exe` (it is not on PATH — one copy per VS install). New `VisualStudio/SolutionTests.cs` gathers the solution's project files from DTE, runs the assemblies in one `CommandExecutor` invocation with a 5-minute timeout, and returns a reason instead of a report when nothing was verified. `VerifyTestsAfterEdits` added in all three places, **default off**, and it runs only on a round whose build came back clean — stale binaries are worse than no result. `ChatTurnState.Tests` keeps the row for a reloaded conversation. Two deviations worth knowing: `ToModelNote` could not be reused unchanged (its opening sentence is about a build, and a false one is exactly what phase 4 already refused to write), so `TestDiagnostics` has its own; and the parser reads `- Failed: …` pair lines with the `Total tests:` block as a fallback. Verified: MSBuild 0 errors on all four projects; warnings exactly baseline (Core 21, GitWrapper 0, VS 72, tests 0); **221/221** tests pass, 14 new in `TestDiagnosticsTests` and `TestProjectTests`. Ran the real path end to end against this repo's own test project from the built Core: resolver → assembly discovery → `CommandExecutor` → parse gave `Tests · all 221 passed`; then ran it against a deliberately failing copy and got `Tests · 2 failed, 221 passed, 1 skipped` with both stack traces in the note — which caught a real defect the unit tests had not: the runner's result line was being swallowed into the last failure block. Fixed and covered by a regression test. Manual Visual Studio check not run — needs a live VS session, so **the option has never been exercised through the dialog**. |
| 7 | Read in parallel, and move the loop out of the UI | done | 2026-10-17 — the round loop now lives in new `VisualStudio/AgentHarness.cs` (`RunAsync`), the summary helper and `UtilityModel` with it; it speaks to the window only through new `VisualStudio/IAgentView.cs`, which `ChatControl` implements explicitly, and the `Bubble` it hands back moved to its own `Bubble.cs` (move, not rewrite: every note, row and order is the transcript the control drew before). Two deviations the plan's member list did not survive: the edit/command confirmations were already wired through the static `EditTools.Confirm`/`TerminalTools.Confirm` hooks and the loop never calls them directly, so they stayed there; and `SetBusy` brackets the turn rather than the loop, so it stayed in `SendAsync` with the token and the ticker. The disk-only run — new `ContextTools.IsDiskOnly` (`read_file`, `list_files`, `search_files`) — runs through `Task.WhenAll` and appends messages and rows in the announced order; every other tool stays serial. Verified: MSBuild 0 errors on all four projects; warnings exactly baseline (Core 21, GitWrapper 0, VS 72, tests 0 — none from the new files); **221/221** tests pass (no new tests: the moved code needs DTE/WPF, which the test assembly cannot reach). **Manual side-by-side in Visual Studio not run** — needs a live VS session, so the identical-transcript comparison is by construction (the loop was transcribed statement for statement) rather than observed. |

Status values: `todo` · `done` · `blocked`.

---

## Phase 1 — Give the request a budget, and stub what falls out of it

**Why.** The loop sends everything it has, every round. This is the single biggest reason the
model looks unintelligent late in a long turn.

**What to change**

- `src/LiteLLM.Core/ChatState.cs` — add the limits beside the existing ones:
  `MaxPromptChars` (start at `200_000`) and `KeepRecentToolRounds` (start at `2`).
- New `src/LiteLLM.Core/ContextBudget.cs` with one entry point:

  ```csharp
  public static List<ChatMessage> Trim(IReadOnlyList<ChatMessage> messages, int maxChars)
  ```

  Walk the list from newest to oldest, accumulating characters. Once the budget is spent, an
  old **tool result** keeps its place in the list but its `Content` is replaced by a one-line
  stub:

  ```
  read_file {"path":"src/Foo.cs"} — result dropped to save context; call it again if needed.
  ```

- The stub must keep `Role`, `ToolCallId` and `ToolName`. **Dropping the message instead would
  break the transcript**: `ChatStateLimits.Sanitize` (`ChatState.cs:105`) and the API itself
  require every announced call to be answered.
- The argument text for the stub is not on the tool message. Recover it by joining
  `ToolCallId` to the sibling assistant message's `ToolCalls[].Arguments` — see how the tool
  result is built at `ChatControl.xaml.cs:368-372`.
- Never stub inside the newest `KeepRecentToolRounds` rounds. Always keep the first `user`
  message, whatever the budget says.
- `ChatControl.xaml.cs:306` — call `Trim` **before** `Sanitize`:

  ```csharp
  var history = ChatStateLimits.Sanitize(ContextBudget.Trim(_history.Take(_history.Count - 1).ToList(), ChatStateLimits.MaxPromptChars));
  ```

**Reuse.** `ChatStateLimits.Clip` and `TruncationMarker` (`ChatState.cs:152-154`),
`ChatStateLimits.Sanitize`.

**Verify.** New `tests/LiteLLM.Core.Tests/ContextBudgetTests.cs`: an over-budget transcript
keeps every call/result pair intact; the stub names the file it replaced; the newest rounds and
the first user message are untouched; an under-budget list comes back unchanged. Then build +
tests. Manual: a long editing session in Visual Studio should keep working past round 15.

---

## Phase 2 — A tool call with bad arguments must say so

**Why.** A real bug, not a polish item. `ContextTools.Parse` (`ContextTools.cs:245`) returns an
empty `JObject` for malformed JSON, so the model is told `No such file: <repo root>`
and goes looking for a file it never named. `Number` (`:247`) silently substitutes the default
for a wrong type, so the model never learns it made a mistake.

**What to change**

- New `src/LiteLLM.Core/ToolArguments.cs` — parsing that reports instead of swallowing:

  ```csharp
  public static bool TryParse(string? json, out JObject args, out string error)
  public static bool TryText(JObject args, string name, out string value, out string error)
  public static bool TryNumber(JObject args, string name, int fallback, out int value, out string error)
  ```

  The messages name the problem and the fix: *"The call arguments were not valid JSON. Send a
  JSON object. Parser said: …"*, *"`line_count` must be an integer; it arrived as a string."*
- `src/LiteLLM.VisualStudio/ContextTools.cs` — on a parse failure return
  `Fail(error, "invalid arguments")` so the model sees the real reason. Keep the silent
  fallback only where a missing argument is legitimately optional.

**Reuse.** `ToolResult`, `Result`/`Fail` (`ContextTools.cs:249-250`).

**Verify.** New `ToolArgumentsTests.cs`: valid JSON; malformed JSON; a wrong-typed number; a
missing required argument; a valid but empty object. Then build + tests.

---

## Phase 3 — Tell the model where it is (AGENTS.md)

**Why.** The system prompt is fixed prose, so the model does not know this repository's
conventions. This is the cheapest intelligence there is, and the same file is read by Claude Code
and Copilot, so it pays off three times.

**What to change**

- New `src/LiteLLM.Core/RepoInstructions.cs` — find and load, in order, under the solution root:
  `AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md`. Join what is found under a short
  heading each and cap the whole block with `Clip`. Take the root and a file reader as
  parameters so tests can point it at temp files.
- `src/LiteLLM.Core/AgentPrompt.cs` — `Build` and `Instructions` take the block and append it as
  its own section. The existing promises must survive: four tests assert them
  (`EditTests.cs:147-177`).
- `ChatControl.xaml.cs` — load it **once per turn**, not per round, next to the existing
  `SolutionRoot` call, and pass it into `AgentPrompt.Build`.
- Write this repository's own `AGENTS.md` as part of this phase, encoding the Ground rules
  above: net48 + VSSDK, MSBuild not `dotnet build`, the four projects, the missing
  `LiteLLM.Cli` project, tests via `vstest.console.exe`, and Core-only testability.

**Verify.** New `RepoInstructionsTests.cs`: picks up each filename; prefers `AGENTS.md` order;
caps an oversized file and marks the truncation; returns empty for a directory with none. Then
build + tests. Manual: put a unique marker line in `AGENTS.md`, start a new chat, ask a
question, and confirm the answer reflects it.

---

## Phase 4 — Put the live Error List in front of the model

**Why.** The model only learns about errors when it asks for a build. `SolutionBuild` can
already read the Error List without building (`SolutionBuild.cs:76`), and the extension has a
unique advantage here that Copilot does not.

**What to change**

- `ChatControl.SendAsync` — read `SolutionBuild.ReadErrorListAsync()` once per round, and append
  a **bounded summary** (counts plus the first ~20 lines, never the full 12 000 characters) to
  the **system prompt text for that round**.
- Append to the system prompt, **not** to `_history`. The system prompt is rebuilt at request
  time and never stored (`AgentPrompt.cs:8-13`, `ChatControl.xaml.cs:307-311`), so this costs
  nothing in the transcript, needs no `Sanitize` handling, and disappears by itself once the
  errors are gone.
- Skip the block when the list is empty, and reuse the framing already written for this in
  `BuildDiagnostics.ToModelNote` (`BuildDiagnostics.cs:84-94`) — including *"if an entry is not
  caused by your change, say so plainly and leave it alone."* This matters here: the solution
  file in this repo references a project that does not exist, so the Error List is rarely empty.

**Verify.** No unit test — it needs Visual Studio. Manual: open the solution, send a message in
Agent mode, and confirm the round's request (Copy request JSON) carries the error block;
introduce a real error and confirm the block follows it.

---

## Phase 5 — Summarize instead of only stubbing

**Why.** Phase 1 frees tokens by throwing information away. A summary keeps the thread instead,
and it is only affordable with a cheap model — which is exactly what having a LiteLLM proxy in
the middle buys you.

**What to change**

- Settings, in the three places this repo always requires (`OptionsPage.cs`, `Models.cs`,
  `ExtensionServices.ApplyOptions`) — miss the third and the value is silently dropped on the
  next save:
  `UtilityModelKey` (plain `string`, the `PermissionMode` pattern), resolved through
  `ModelCatalog.Match`. **Empty means "use the session's selected model"**, so the phase works
  with no configuration at all.
- New `src/LiteLLM.Core/ContextSummary.cs` — build the summarization request and shape the
  single message that replaces the dropped span.
- `src/LiteLLM.Core/ChatState.cs` — `ChatTurnState` gains a summary field beside `BuildReport`
  (`ChatState.cs:38`), so a reloaded conversation keeps it. Cache it: summarize the span **once**
  and reuse the text on later rounds rather than re-summarizing.
- The call goes through `LiteLlmClient.CompleteAsync` (`LiteLlmClient.cs:45`) with
  `tools: null`, and with a `UserSettings` copy carrying a small `MaxTokens` — `max_tokens` and
  `temperature` come from that object (`LiteLlmClient.cs:155`), so passing the user's settings
  spends the full 8192 tokens on a summary.
- `CompleteAsync` overwrites the client's single `LastExchange` slot (`LiteLlmClient.cs:105`),
  which the *Copy request/response (JSON)* menu reads (`ChatControl.xaml.cs:727`). Add a
  way to skip that record for internal calls, so a background summary does not destroy the
  JSON the user was looking at.
- Draw one transcript row: `Context summarized · N messages → summary`. Nothing in this loop
  happens silently.

**Verify.** New `ContextSummaryTests.cs` for the prompt shape and the merge. Then build +
tests. Manual: a long session shows the row and keeps working without the model re-reading
files it already read.

---

## Phase 6 — Run the tests, not just the build

**Why.** "The build is clean" is the only automatic check the loop has. A failing test is the
signal that a change compiled and is still wrong.

**What to change**

- New `src/LiteLLM.Core/TestDiagnostics.cs` — map `vstest.console.exe` output onto the existing
  `BuildReport` shape (`BuildDiagnostics.cs:9-17`), so the loop's reporting path and
  `ToModelNote` are reused unchanged rather than duplicated.
- A runner in `src/LiteLLM.VisualStudio` beside `SolutionBuild.cs`, resolving
  `vstest.console.exe` with the existing `WindowsExecutableResolver`.
- `ChatControl.SendAsync` — after the build step, if the solution contains a test project, run
  the tests and feed a bounded failure summary back the same way the build report is fed back.
- Guard rails: a new `VerifyTestsAfterEdits` setting, **default off** (a test run costs far more
  time than a build); skip when the build already failed; enforce a timeout; and say so plainly
  when the run could not be verified, rather than implying the tests passed.

**Verify.** `TestDiagnosticsTests.cs` against a captured `vstest.console.exe` output sample,
including a run with failures and a run with none. Then build + tests. Manual: break a test
deliberately, confirm the loop reports the failure, then revert.

---

## Phase 7 — Read in parallel, and move the loop out of the UI

**Why.** Two things at once, because they are the same change. The loop lives inside the WPF
control, so nothing about it can be reused or tested; and the `foreach` at
`ChatControl.xaml.cs:361` awaits every tool call in series, so six file reads cost six
round-trips.

**What to change**

- Move the round loop (`ChatControl.xaml.cs:297-419`) into a new
  `src/LiteLLM.VisualStudio/AgentHarness.cs` behind a small `IAgentView` interface covering
  exactly what the loop asks of the UI today: `AddAssistantBubble`, `SetContent`,
  `AddToolNote`, `AskToApplyEditAsync`, `AskToRunCommandAsync`, `SetBusy`, and `_phase`. This is
  a **move, not a rewrite** — the transcript must look identical afterwards. It stays in the VS
  project because it needs `DTE`; the pure parts already live in Core after phases 1-6.
- Then parallelize only the disk-only tools — `read_file`, `list_files`, `search_files` — with
  `Task.WhenAll`, appending their results in the announced order. Everything else stays serial:
  the rest switch to the UI thread anyway, and edits must keep their confirmation cards
  readable and in order.

**Verify.** Build + tests, then a manual side-by-side: the same prompt before and after must
produce the same transcript rows in the same order — only faster.

---

## Verification (all phases)

Per phase, in order:

1. `MSBuild.exe` on each of the four projects — **zero errors**. Warnings are acceptable; the
   extension project already carries ~21 pre-existing nullable warnings in Core.
2. `vstest.console.exe` on the built test DLL — every test passes (baseline: 151 cases; the
   count should only grow).
3. The phase's own manual check, where it has one.
4. Update the Progress table with the date, what changed, and the numbers.

Across phases:

- The Claude backend (`SendClaudeAsync`) is untouched.
- At the end of every phase the extension still builds as a VSIX
  (`build-vsix.ps1`) and the LiteLLM loop still runs end to end.
- Nothing in the loop is silent: a stub, a summary, a test run and an unverifiable check each
  get a transcript row.
