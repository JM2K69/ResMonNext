#Requires -Version 7.0
<#
  Publie ResMon Next, sans runtime .NET requis sur le poste cible.

  pwsh .\build.ps1                    # exe unique autonome, non compressé (~62 Mo, aucun prérequis en plus du SDK .NET 10)
  pwsh .\build.ps1 -Aot               # NativeAOT : exe natif + 3 DLL Avalonia (outils C++ de Visual Studio requis)
  pwsh .\build.ps1 -RuntimeIdentifier win-arm64

  Le workflow GitHub Actions (.github/workflows/release.yml) publie les deux formats à chaque tag v*
  (ses runners ont les outils C++ requis par NativeAOT).
#>
param(
    [switch]$Aot,
    [ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier = 'win-x64',
    [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$project = Join-Path $PSScriptRoot 'ResMonNext.csproj'
$kind = if ($Aot) { 'aot' } else { 'single' }
$output = Join-Path $PSScriptRoot "artifacts\$RuntimeIdentifier-$kind"
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }

$common = @('-c', 'Release', '-r', $RuntimeIdentifier, '--self-contained', 'true', "-p:Version=$Version", '-o', $output)
if ($Aot) {
    dotnet publish $project @common -p:DebugType=none
    # Symboles de débogage des bibliothèques natives (Skia, HarfBuzz) : ~100 Mo inutiles à l'exécution.
    Get-ChildItem $output -Filter *.pdb | Remove-Item
}
else {
    # Fichier unique : runtime .NET + DLL natives intégrés, code taillé et précompilé (ReadyToRun).
    # Non compressé : le code est lu directement depuis l'exe (pas de copie décompressée en mémoire privée, ~23 Mo de moins).
    dotnet publish $project @common -p:PublishAot=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false -p:PublishTrimmed=true -p:PublishReadyToRun=true -p:DebugType=none
}

$exe = Join-Path $output 'ResMonNext.exe'
$report = Join-Path $output 'selftest.txt'
$process = Start-Process $exe -ArgumentList "--selftest --out `"$report`"" -Wait -PassThru
Get-Content $report | Select-Object -Last 3
if ($process.ExitCode -ne 0) { throw "Autotest en échec ($($process.ExitCode))" }
Remove-Item $report
Write-Host "Exécutable : $exe ($([Math]::Round((Get-Item $exe).Length / 1MB, 1)) Mo)"
