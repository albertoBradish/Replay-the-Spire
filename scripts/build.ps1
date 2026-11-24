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
$out = [IO.Path]::GetFullPath($OutputDirectory)
if ($out -eq $game -or $out.StartsWith($game + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Select an output directory outside the source installation.'
}
$build = Join-Path $root ".local/build/$gameVersion"
dotnet build (Join-Path $root 'src/SpireReplay.csproj') -c Release --artifacts-path $build "-p:GameDataDir=$data" "-p:GameVersion=$gameVersion" --nologo
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
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $out $name), "SpireReplay/$name") | Out-Null
            }
        } finally { $archive.Dispose() }
        Move-Item -LiteralPath $stage -Destination $zip -Force
    } finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage } }
    Get-Item -LiteralPath $zip
} else {
    Get-Item -LiteralPath $out
}
