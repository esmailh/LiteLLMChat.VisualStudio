# Packages the LiteLLM extension into a VSIX that Visual Studio will actually load.
# Every property below works around a specific failure; see the notes in the file's history.
$ErrorActionPreference = 'Stop'
$root = 'F:\New folder (3)'
$proj = Join-Path $root 'src\LiteLLM.VisualStudio\LiteLLM.VisualStudio.csproj'
$bin  = Join-Path $root 'src\LiteLLM.VisualStudio\bin\Release\net48'
$tools = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.vssdk.buildtools\17.12.2069\tools'
$tpl = Join-Path $env:TEMP 'vsix-tpl'

# Whatever Visual Studio is installed here, 17 or 18, Insiders or release.
$msbuild = (Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\amd64\\' } | Select-Object -First 1).FullName
if (-not $msbuild) { throw 'MSBuild.exe not found under Program Files\Microsoft Visual Studio' }
Write-Host "MSBuild: $msbuild"

# The packaging step writes a normalised copy of the manifest here and packs *that*; if a stale one is already
# present it is reused as-is, so the VSIX would carry the previous Identity/@Version while the source manifest and the
# DLL are both current, and Visual Studio would install it as the old version. Deleting it forces the regeneration.
$generated = Join-Path $root 'src\LiteLLM.VisualStudio\extension.vsixmanifest'
if (Test-Path $generated) { Remove-Item $generated -Force }

# The container is filled from the *referenced project's* output folder, and both the Core project and the extension
# project write a copy of it. An up-to-date check that decides Core is unchanged leaves the previous DLL in both
# places, the package then ships a Core the extension was not compiled against, and the installed build fails at the
# first call into a member that moved — which surfaces as "method not found" in a dialog, not as a build error.
# Deleting the Core output first makes the rebuild unconditional.
foreach ($stale in @(
    (Join-Path $root 'src\LiteLLM.Core\bin\Release\net48\LiteLLM.Core.dll'),
    (Join-Path $root 'src\LiteLLM.VisualStudio\bin\Release\net48\LiteLLM.Core.dll'))) {
  if (Test-Path $stale) { Remove-Item $stale -Force }
}

# Build the referenced project into its normal output directory first. The packaging invocation below overrides the
# extension OutDir, and the SDK otherwise assumes the referenced DLL already exists at the source project's path.
& $msbuild (Join-Path $root 'src\LiteLLM.Core\LiteLLM.Core.csproj') -p:Configuration=Release -v:q -nologo
if ($LASTEXITCODE -ne 0) { throw "core build failed with exit code $LASTEXITCODE" }

& $msbuild $proj `
  '-t:Build;GeneratePkgDef;CreateVsixContainer' `
  -p:Configuration=Release `
  -p:VSToolsPath="$tools" `
  -p:TemplateOutputDirectory="$tpl" `
  -p:OutDir="$bin\\" `
  -p:TargetVsixContainerName=LiteLLM.VisualStudio.vsix `
  -p:CreatePkgDefAssemblyToProcess="$bin\LiteLLM.VisualStudio.dll" `
  -v:m -nologo
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

$vsix = Get-ChildItem $bin -Filter *.vsix | Select-Object -First 1
if (-not $vsix) { throw "no VSIX was produced in $bin" }
Copy-Item $vsix.FullName (Join-Path $root 'LiteLLM.VisualStudio.vsix') -Force
Get-ChildItem (Join-Path $root 'LiteLLM.VisualStudio.vsix') | Select-Object Name, Length, LastWriteTime | Format-Table
