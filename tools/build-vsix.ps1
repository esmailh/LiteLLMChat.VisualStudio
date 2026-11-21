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

# The generated manifest is what Visual Studio reads as the installed version, and the packaging pass writes it by
# detokenizing source.extension.vsixmanifest into $(IntermediateOutputPath). Those paths are evaluated when
# Microsoft.VsSDK.targets is imported from the project file — before the SDK's own targets have defined
# IntermediateOutputPath — so the file name comes out as a bare "extension.vsixmanifest", the intermediate copy is never
# rewritten, and a stale one from an earlier release is packed instead: the VSIX then installs as the previous version,
# with the DLL and source manifest on one version and extension.vsixmanifest, manifest.json and catalog.json on another.
# Passing the path below is what makes the command line behave like the IDE build; the delete covers a machine where an
# earlier run of this script wrote the old copy. The copy beside the project file is generated too, and is left alone:
# with IntermediateOutputPath set, nothing in the container reads it.
$intermediateDir = Join-Path $root "src\LiteLLM.VisualStudio\obj\$Configuration\net48"
$staleManifest = Join-Path $intermediateDir 'extension.vsixmanifest'
if (Test-Path $staleManifest) { Remove-Item $staleManifest -Force }

& $msbuild $project '/t:Build;CreateVsixContainer' `
    "/p:Configuration=$Configuration" `
    "/p:IntermediateOutputPath=obj/$Configuration/net48/" `
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
