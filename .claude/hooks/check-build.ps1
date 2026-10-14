# Stop hook: a turn may not end while the code does not compile.
#
# Claude Code pipes the hook payload in on stdin, so the hook prints nothing on the happy path.
# Exit 0 lets the turn end. Exit 2 blocks it and hands stderr back to the model as the reason,
# which is what makes "check the Error List and keep fixing until it is clean" enforceable rather
# than a promise.
#
# Cost control: the check is skipped when no source file has been touched since the last check that
# found everything clean, so a turn that only answers a question builds nothing.

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # .claude/hooks/ -> repository root
if (-not (Test-Path (Join-Path $root 'src'))) { $root = (Get-Location).Path }

$payload = [Console]::In.ReadToEnd()
$session = 'nosession'
try { if ($payload) { $session = "$((ConvertFrom-Json $payload).session_id)" } } catch { }
if (-not $session) { $session = 'nosession' }

$stateDir = Join-Path $env:TEMP 'claude-build-check'
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
$stamp = Join-Path $stateDir "$session.ok"      # written by a check that has nothing left to report
$tries = Join-Path $stateDir "$session.tries"   # consecutive blocks, so a hopeless fix cannot loop forever

# Newest source file. bin/ and obj/ are left out: the build rewrites them, and the stamp below would
# otherwise always look stale.
$watched = Get-ChildItem (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -File `
    -Include *.cs, *.csproj, *.xaml, *.vsct -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
    Sort-Object LastWriteTime -Descending
if (-not $watched) { exit 0 }

$checked = if (Test-Path $stamp) { (Get-Item $stamp).LastWriteTime } else { [datetime]::MinValue }
if ($watched[0].LastWriteTime -le $checked) { exit 0 }

$msbuild = (Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\amd64\\' } | Select-Object -First 1).FullName
if (-not $msbuild) { exit 0 }   # no toolchain on this machine; nothing useful to report

# The solution file also references a project that lives outside this repository, so it cannot be
# built as a whole. MSBuild takes one project per invocation, hence the loop.
$projects = @(
    'src\LiteLLM.Core\LiteLLM.Core.csproj',
    'src\LiteLLM.GitWrapper\LiteLLM.GitWrapper.csproj',
    'src\LiteLLM.VisualStudio\LiteLLM.VisualStudio.csproj',
    'tests\LiteLLM.Core.Tests\LiteLLM.Core.Tests.csproj'
)

$errors = @()
foreach ($relative in $projects) {
    $project = Join-Path $root $relative
    if (-not (Test-Path $project)) { continue }
    # The console logger writes errors to stdout. stderr is deliberately not merged in: PowerShell 5.1
    # wraps a native command's stderr in ErrorRecords and can turn it into a terminating error.
    $output = & $msbuild $project -p:Configuration=Debug -v:q -nologo
    if ($LASTEXITCODE -ne 0) {
        $found = @($output | Where-Object { "$_" -match ': error [A-Z]+\d+|: fatal error |MSB\d{4}:' })
        if ($found.Count -eq 0) { $found = @("$relative failed to build (msbuild exit code $LASTEXITCODE)") }
        $errors += $found
    }
}

if ($errors.Count -eq 0) {
    New-Item -ItemType File -Path $stamp -Force | Out-Null
    Remove-Item $tries -ErrorAction SilentlyContinue
    exit 0
}

$attempt = 0
if (Test-Path $tries) { $attempt = [int](Get-Content $tries -Raw) }
$attempt++
Set-Content -Path $tries -Value $attempt -Encoding ascii

# A cap, because an unfixable error would otherwise re-block every stop for the rest of the session.
# Hitting it stamps the check as done - the sources have stopped changing - and tells the user instead.
$maxAttempts = 5
if ($attempt -gt $maxAttempts) {
    New-Item -ItemType File -Path $stamp -Force | Out-Null
    Remove-Item $tries -ErrorAction SilentlyContinue
    @{
        systemMessage  = "Build still has errors after $maxAttempts attempts; the check will stay quiet until the sources change again."
        suppressOutput = $true
    } | ConvertTo-Json -Compress
    exit 0
}

$shown = @($errors | Select-Object -First 40)
$message = @(
    "BUILD FAILED (attempt $attempt of $maxAttempts). The build must be clean before the turn ends.",
    "These are the errors from the Debug build - fix every one of them, then rebuild to confirm:",
    ""
) + $shown
if ($errors.Count -gt $shown.Count) { $message += "... and $($errors.Count - $shown.Count) more" }

[Console]::Error.Write(($message -join "`n") + "`n")
exit 2
