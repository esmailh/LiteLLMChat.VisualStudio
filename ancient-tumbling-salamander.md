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
  comes back as `No such file: F:\New folder (3) - Copy` and the model chases a phantom bug.
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
| 1 | Give the request a budget, and stub what falls out of it | todo | |
| 2 | A tool call with bad arguments must say so | todo | |
| 3 | Tell the model where it is (AGENTS.md) | todo | |
| 4 | Put the live Error List in front of the model | todo | |
| 5 | Summarize instead of only stubbing | todo | |
| 6 | Run the tests, not just the build | todo | |
| 7 | Read in parallel, and move the loop out of the UI | todo | |

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
empty `JObject` for malformed JSON, so the model is told `No such file: F:\New folder (3) - Copy`
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
