[CmdletBinding()]
param(
    [ValidateSet('0.111.1', '0.113.1')][string]$GameVersion = '0.111.1',
    [ValidatePattern('^[A-Za-z0-9-]+$')][string]$Session = 'review',
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root ".local/$Session/$GameVersion"
$runtime = Join-Path $work 'runtime'
$exe = Join-Path $runtime 'SlayTheSpire2.exe'
$override = Join-Path $runtime 'override.cfg'
$profile = Join-Path $work 'profile'
if (!(Test-Path -LiteralPath $exe) -or !(Test-Path -LiteralPath $override)) {
    throw "Missing isolated runtime. Stage $GameVersion with scripts/native-test.py first."
}
if (!(Select-String -LiteralPath $override -SimpleMatch "ReplayAcceptance-$GameVersion" -Quiet)) {
    throw 'The runtime is missing the isolated user directory configuration.'
}
$release = Get-Content -LiteralPath (Join-Path $runtime 'release_info.json') -Raw | ConvertFrom-Json
if ($release.version.TrimStart('v') -ne $GameVersion) { throw 'The staged game version differs from the requested version.' }
$plugin = Join-Path $runtime 'mods/SpireReplay/SpireReplay.dll'
if (!(Test-Path -LiteralPath $plugin)) { throw 'The staged replay mod is missing.' }
$library = Join-Path $profile "appdata/ReplayAcceptance-$GameVersion/ReplayTheSpire"
if (!(Test-Path -LiteralPath $library)) { throw 'The isolated replay library is missing.' }
if ($VerifyOnly) {
    return [pscustomobject]@{ Game=$release.version; Commit=$release.commit; GameDirectory=$runtime; ReplayLibrary=$library; Mod=(Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash }
}
if (Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue) {
    throw 'Close the running game before opening the review copy.'
}
$process = New-Object System.Diagnostics.ProcessStartInfo
$process.FileName = $exe
$process.WorkingDirectory = $runtime
$process.UseShellExecute = $false
$process.Arguments = '--path "' + $runtime + '" --force-steam off --windowed --resolution 1280x720 --rendering-driver d3d12'
foreach ($key in @('APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP')) {
    $process.EnvironmentVariables[$key] = Join-Path $profile $key.ToLowerInvariant()
}
foreach ($key in @('SPIRE_REPLAY_TEST', 'SPIRE_REPLAY_PHASE', 'SPIRE_REPLAY_PERF', 'SPIRE_REPLAY_PERF_OUTPUT')) {
    $process.EnvironmentVariables.Remove($key)
}
[Diagnostics.Process]::Start($process) | Out-Null
