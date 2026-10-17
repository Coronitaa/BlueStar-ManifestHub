param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Args
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $scriptDir
try {
    $exePath = Join-Path $scriptDir "bin\Harvester\BlueStar.ManifestHarvester.Cli.exe"
    if (Test-Path $exePath) {
        & $exePath $Args
    } else {
        dotnet run --project "src\BlueStar.ManifestHarvester.Cli" -- $Args
    }
}
finally {
    Pop-Location
}
