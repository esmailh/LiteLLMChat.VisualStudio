# Working in this repository

A Visual Studio extension (VSIX) that puts an assistant into a tool window. It has two execution
backends and they are not the same thing:

- **LiteLLM** (the default) — the extension runs its own agent loop against an OpenAI-compatible
  server. `ChatControl.SendAsync` is that loop.
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
