# Checks a built VSIX against the repository before it is uploaded to the Marketplace.
#
# Every failure this can report has already happened once. The 1.8.16 release was the pkgdef: the container carried
# the registration of an earlier build, so the installed extension named an options page GUID its assembly could not
# answer for, and Tools > Options showed "An error occurred loading this property page" while automation, which names
# the page instead of its GUID, kept working. A version that disagrees between the project, the source manifest and
# the packed manifest is the same class of mistake: the file someone just tested is not the file that gets installed.
#
# Usage: powershell -File tools\verify-vsix.ps1 [-Path <vsix>] [-Configuration Release]
[CmdletBinding()]
param(
    [string]$Path,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
if (-not $Path) { $Path = Join-Path $root 'LiteLLM.VisualStudio.vsix' }
if (-not (Test-Path $Path)) { throw ('No VSIX at ' + $Path + '. Run tools\build-vsix.ps1 first.') }

$project = Join-Path $root 'src\LiteLLM.VisualStudio\LiteLLM.VisualStudio.csproj'
$sourceManifest = Join-Path $root 'src\LiteLLM.VisualStudio\source.extension.vsixmanifest'
$generatedPkgdef = Join-Path $root ('src\LiteLLM.VisualStudio\obj\' + $Configuration + '\net48\LiteLLM.VisualStudio.pkgdef')

$failures = New-Object 'System.Collections.Generic.List[string]'
$notes = New-Object 'System.Collections.Generic.List[string]'
function Fail([string]$message) { $script:failures.Add($message) }
function Note([string]$message) { $script:notes.Add($message) }

$staging = Join-Path ([IO.Path]::GetTempPath()) ('vsix-verify-' + [guid]::NewGuid().ToString('N'))
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($Path, $staging)

    # --- the version three files have to agree on ------------------------------------------------------------
    $packed = ([xml][IO.File]::ReadAllText((Join-Path $staging 'extension.vsixmanifest'))).PackageManifest.Metadata.Identity
    $source = ([xml][IO.File]::ReadAllText($sourceManifest)).PackageManifest.Metadata.Identity
    $projectVersion = ([xml][IO.File]::ReadAllText($project)).SelectSingleNode('//Version').InnerText.Trim()

    Note ('package ' + (Get-Item $Path).Length + ' bytes, version ' + $packed.Version)
    if ($packed.Version -ne $projectVersion) {
        Fail ('The package is ' + $packed.Version + ' but the project says ' + $projectVersion + '. Visual Studio keys upgrades on this number, so the install would keep the older extension.')
    }
    if ($packed.Version -ne $source.Version) {
        Fail ('The package is ' + $packed.Version + ' but source.extension.vsixmanifest says ' + $source.Version + '. The next build would go back to the older one.')
    }
    if ($packed.Id -ne $source.Id) {
        Fail ('The package identity is ' + $packed.Id + ' but the source manifest says ' + $source.Id + '.')
    }
    Note ('publisher ' + $packed.Publisher + ', identity ' + $packed.Id)

    # --- the files someone installing it would need ----------------------------------------------------------
    foreach ($required in @('LiteLLM.VisualStudio.dll', 'LiteLLM.Core.dll', 'LiteLLM.GitWrapper.exe', 'Newtonsoft.Json.dll',
                            'LiteLLM.VisualStudio.pkgdef', 'LICENSE.txt', 'Resources\Icon.png', 'README.md')) {
        $full = Join-Path $staging $required
        if (-not (Test-Path $full)) { Fail ('The package is missing ' + $required + '.') }
        elseif ((Get-Item $full).Length -eq 0) { Fail ('The package carries an empty ' + $required + '.') }
    }

    # --- the assembly says which build it is ----------------------------------------------------------------
    $dll = Join-Path $staging 'LiteLLM.VisualStudio.dll'
    if (Test-Path $dll) {
        $fileVersion = (Get-Item $dll).VersionInfo.FileVersion
        Note ('LiteLLM.VisualStudio.dll ' + $fileVersion)
        if (-not $fileVersion.StartsWith($projectVersion)) {
            Fail ('The packaged assembly is ' + $fileVersion + ' but the project says ' + $projectVersion + '. The container was filled from an earlier build output.')
        }
    }
    $core = Join-Path $staging 'LiteLLM.Core.dll'
    if (Test-Path $core) { Note ('LiteLLM.Core.dll ' + (Get-Item $core).VersionInfo.FileVersion) }

    # --- the registration is the one this build generated ---------------------------------------------------
    # The page GUID lives in the pkgdef, and it is what Tools > Options asks the package for. A pkgdef left over in
    # obj from an earlier compilation names a page this assembly cannot answer for; comparing it with the file
    # GeneratePkgDef just wrote is the only way to tell from the container alone.
    $packedPkgdef = Join-Path $staging 'LiteLLM.VisualStudio.pkgdef'
    if (Test-Path $packedPkgdef) {
        $text = [IO.File]::ReadAllText($packedPkgdef)
        $pageLines = @(($text -split "`r?`n") | Where-Object { $_ -match '"Page"\s*=' })
        if ($pageLines.Count -ne 1) {
            Fail ('The pkgdef names ' + $pageLines.Count + ' options pages; exactly one is expected (Tools > Options > LiteLLM > General).')
        } else {
            Note ('options page ' + $pageLines[0].Trim())
        }
        if (Test-Path $generatedPkgdef) {
            if ([IO.File]::ReadAllText($generatedPkgdef) -ne $text) {
                Fail ('The packaged pkgdef is not the one in obj\' + $Configuration + '\net48. It was left by an earlier build, and the options page GUID in it belongs to that build.')
            }
        } else {
            Note ('skipped the build-consistency check: ' + $generatedPkgdef + ' is not there (a clean checkout or a different configuration).')
        }
    }

    # --- symbols ---------------------------------------------------------------------------------------------
    # This build ships LiteLLM.Core.pdb, as every earlier release did; it is listed rather than refused.
    $symbols = @(Get-ChildItem $staging -Recurse -Filter *.pdb)
    if ($symbols.Count -gt 0) { Note ('symbols shipped: ' + (($symbols | ForEach-Object { $_.Name }) -join ', ')) }
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
}

foreach ($note in $notes) { Write-Host ('  ' + $note) }
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host ('FAIL ' + $failure) }
    Write-Host ($failures.Count.ToString() + ' problem(s); do not upload this package.')
    exit 1
}

$hash = (Get-FileHash -Path $Path -Algorithm SHA256).Hash
Write-Host ('OK ' + $Path + ' (' + (Get-Item $Path).Length + ' bytes, sha256 ' + $hash + ')')
exit 0
