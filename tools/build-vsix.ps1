# Builds the Release VSIX that is uploaded to the Visual Studio Marketplace and copies it to the
# repository root.
#
# A plain Build does not create the container here. Microsoft.VsSDK.targets is imported from the
# project file before the SDK's own targets, so when it evaluates its defaults IntermediateOutputPath,
# OutDir and TargetName are still empty: the file manifest would land at the drive root, and packaging
# fails because TemplateOutputDirectory is empty. Passing the paths this script does after the SDK has
# supplied them makes the command line behave like the IDE build.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\LiteLLM.VisualStudio\LiteLLM.VisualStudio.csproj'
$intermediate = "obj/$Configuration/net48"
$container = "bin/$Configuration/net48/LiteLLM.VisualStudio.vsix"

$msbuild = (Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" |
    Where-Object { $_.FullName -notmatch '\\amd64\\' } | Select-Object -First 1).FullName
if (-not $msbuild) { throw 'MSBuild.exe was not found under Program Files.' }

& $msbuild $project '/t:Build;CreateVsixContainer' `
    "/p:Configuration=$Configuration" `
    "/p:TemplateOutputDirectory=$intermediate/" `
    "/p:FileManifest=$intermediate/files.json" `
    "/p:TemplateFileManifest=$intermediate/templateFiles.json" `
    "/p:ResourceManifest=$intermediate/resources.json" `
    "/p:CtoFileManifest=$intermediate/ctoFiles.json" `
    "/p:CtoCacheFile=$intermediate/mergeCto.cache" `
    "/p:CreateTemplateManifestsCacheFile=$intermediate/createTemplateManifests.cache" `
    "/p:TargetVsixContainer=$container" `
    /v:m /nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$built = Join-Path (Split-Path -Parent $project) $container | Resolve-Path
$target = Join-Path $root 'LiteLLM.VisualStudio.vsix'
Copy-Item $built $target -Force

$file = Get-Item $target
Write-Host ("wrote " + $file.FullName + "  " + $file.Length + " bytes")
