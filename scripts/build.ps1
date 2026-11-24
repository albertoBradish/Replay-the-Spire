param([Parameter(Mandatory)][string]$GameDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
$data = Join-Path $game 'data_sts2_windows_x86_64'
if (!(Test-Path -LiteralPath (Join-Path $data 'sts2.dll'))) { throw 'Missing game SDK' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/SpireReplay' }
dotnet build (Join-Path $root 'src/SpireReplay.csproj') -c Release "-p:GameDataDir=$data"
if ($LASTEXITCODE) { throw "Build failed ($LASTEXITCODE)" }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src/bin/Release/net9.0/SpireReplay.dll'),(Join-Path $root 'mod_manifest.json'),(Join-Path $root 'LICENSE') -Destination $OutputDirectory
