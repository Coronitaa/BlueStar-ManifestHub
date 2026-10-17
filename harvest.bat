@echo off
setlocal
cd /d "%~dp0"

if exist "bin\Harvester\BlueStar.ManifestHarvester.Cli.exe" (
    "bin\Harvester\BlueStar.ManifestHarvester.Cli.exe" %*
) else (
    dotnet run --project "src\BlueStar.ManifestHarvester.Cli" -- %*
)
