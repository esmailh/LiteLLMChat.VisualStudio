# Contributing

Thanks for taking the time to improve LiteLLM Chat for Visual Studio. By participating you agree
to the [Code of Conduct](CODE_OF_CONDUCT.md). Please report security issues through the process in
[SECURITY.md](SECURITY.md), never in a public issue.

## Requirements

- Windows 10 or 11.
- Visual Studio 2022 (17.x) or later with the **Visual Studio extension development** workload.
- .NET Framework 4.8 targeting pack.

`dotnet build` does **not** work for these projects. Use MSBuild from the Visual Studio install:

```powershell
$msbuild = (Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" |
    Where-Object { $_.FullName -notmatch '\\amd64\\' } | Select-Object -First 1).FullName
& $msbuild "src\LiteLLM.Core\LiteLLM.Core.csproj" /t:Build /p:Configuration=Debug
```

MSBuild takes **one project per invocation**; passing two fails with MSB1008. The four projects to
build are:

```
src\LiteLLM.Core\LiteLLM.Core.csproj
src\LiteLLM.GitWrapper\LiteLLM.GitWrapper.csproj
src\LiteLLM.VisualStudio\LiteLLM.VisualStudio.csproj
tests\LiteLLM.Core.Tests\LiteLLM.Core.Tests.csproj
```

`LiteLLM.VisualStudio.sln` references a project that is not part of this checkout, so the whole
solution does not build. Build the projects above instead; an Error List entry about the missing
project is not yours to fix.

## Tests

The tests are xunit on net48, with hand-rolled fakes and plain `Assert.*`. Run them from the built
assembly:

```powershell
$vstest = (Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" |
    Select-Object -First 1).FullName
& $vstest "tests\LiteLLM.Core.Tests\bin\Debug\net48\LiteLLM.Core.Tests.dll"
```

`vstest.console.exe` does not accept `/nologo`. A change should leave the suite green.

## Pull requests

- Keep one concept per file, and match the surrounding style. Comments explain *why*, not *what*.
- Nullable reference types are enabled; `public` in Core, `internal` in the VisualStudio project.
- Do not add a mocking library or a new MCP SDK: the test project references only `LiteLLM.Core`,
  and MCP is intentionally hand-rolled so a net48 VSIX does not inherit an SDK's binding conflicts.
- Do not silence the existing `async void`/VSTHRD analyzer warnings wholesale; leave the warning
  count where you found it.
- Never commit secrets, `.mcp.json`, `settings.json`, token stores, or logs.
- The agent loop must answer every announced call with a `tool` message and must never answer a
  call that was not announced. Keep the approval paths intact, and keep the Claude CLI path
  separate from the LiteLLM loop.
- Describe how you verified the change. UI work that touches `DTE`, WPF, or the experimental
  instance also needs a manual check in Visual Studio; say what you did.
