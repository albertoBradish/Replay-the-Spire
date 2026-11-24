[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [string]$OutputDirectory,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
$data = Join-Path $game 'data_sts2_windows_x86_64'
if (!(Test-Path -LiteralPath (Join-Path $data 'sts2.dll'))) { throw 'Choose the Windows game folder containing data_sts2_windows_x86_64.' }
$release = Get-Content -LiteralPath (Join-Path $game 'release_info.json') -Raw | ConvertFrom-Json
$gameVersion = $release.version.TrimStart('v')
if ($gameVersion -notin @('0.111.1', '0.113.1')) { throw "The supported game builds are 0.111.1 and 0.113.1. Found $gameVersion." }
$manifest = Get-Content -LiteralPath (Join-Path $root 'mod_manifest.json') -Raw | ConvertFrom-Json
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts/$gameVersion/SpireReplay" }
$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if ($out -eq $game -or $out.StartsWith($game + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Select an output directory outside the source installation.'
}
$build = Join-Path $root ".local/build/$gameVersion"
$buildFlags = @("-p:GameDataDir=$data", "-p:GameVersion=$gameVersion")
if ((Test-Path -LiteralPath (Join-Path $root '.git')) -and (Get-Command git -ErrorAction SilentlyContinue)) {
    $sources = @('src', 'mod_manifest.json', 'scripts/build.ps1')
    $revision = git -C $root log -1 --format=%H -- @sources
    if ($LASTEXITCODE -eq 0 -and $revision -match '^[a-f0-9]{40}$') {
        $changes = git -C $root status --porcelain -- @sources
        $versionStamp = $manifest.version + '+' + $revision
        if ($changes) { $versionStamp += '.dirty' }
        $buildFlags += "-p:InformationalVersion=$versionStamp", "-p:SourceRevisionId=$revision", "-p:EnableSourceLink=false", '-p:IncludeSourceRevisionInInformationalVersion=false'
    }
}
dotnet build (Join-Path $root 'src/SpireReplay.csproj') -c Release --artifacts-path $build @buildFlags --nologo
if ($LASTEXITCODE) { throw "Build failed ($LASTEXITCODE)" }
[IO.Directory]::CreateDirectory($out) | Out-Null
$dll = Join-Path $build 'bin/SpireReplay/release/SpireReplay.dll'
Copy-Item -LiteralPath $dll,(Join-Path $root 'mod_manifest.json'),(Join-Path $root 'LICENSE') -Destination $out -Force
if ($Package) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path (Split-Path $out -Parent) "Replay-the-Spire-$($manifest.version)-sts2-$gameVersion.zip"
    $stage = "$zip.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $archive = [IO.Compression.ZipFile]::Open($stage, 'Create')
        try {
            foreach ($name in @('SpireReplay.dll', 'mod_manifest.json', 'LICENSE')) {
                $entry = $archive.CreateEntry("SpireReplay/$name", [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [datetimeoffset]::new(2000, 1, 1, 0, 0, 0, [timespan]::Zero)
                $source = [IO.File]::OpenRead((Join-Path $out $name))
                try {
                    $dest = $entry.Open()
                    try { $source.CopyTo($dest) } finally { $dest.Dispose() }
                } finally { $source.Dispose() }
            }
        } finally { $archive.Dispose() }
        Move-Item -LiteralPath $stage -Destination $zip -Force
    } finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage } }
    Get-Item -LiteralPath $zip
} else {
    Get-Item -LiteralPath $out
}
