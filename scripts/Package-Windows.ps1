param(
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$IncludeInstaller,
    [string]$AppVersion = "1.0.0"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot "app\FarmPhantom.csproj"
$distDirectory = Join-Path $projectRoot "dist"
$installerScript = Join-Path $PSScriptRoot "FarmPhantom.iss"
$installerCompiler = $null

if ($IncludeInstaller) {
    $compilerCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($compilerCommand) {
        $installerCompiler = $compilerCommand.Source
    }
    else {
        $innoSetupDirectory = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6"
        $candidate = Join-Path $innoSetupDirectory "ISCC.exe"
        if (Test-Path -LiteralPath $candidate) {
            $installerCompiler = $candidate
        }
        else {
            $innoSetupDirectory = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6"
            $candidate = Join-Path $innoSetupDirectory "ISCC.exe"
            if (Test-Path -LiteralPath $candidate) {
                $installerCompiler = $candidate
            }
        }
    }

    if (-not $installerCompiler) {
        throw "Inno Setup 6 bulunamadı. Kur ve tekrar dene: https://jrsoftware.org/isdl.php"
    }
}

$tempRoot = [IO.Path]::GetFullPath((Join-Path $env:TEMP ("FarmPhantom-release-" + [Guid]::NewGuid().ToString("N"))))
$publishDirectory = Join-Path $tempRoot "publish"
$packageDirectory = Join-Path $tempRoot "package"
$archivePath = Join-Path $distDirectory "FarmPhantom-$RuntimeIdentifier.zip"

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $distDirectory -Force | Out-Null

try {
    & dotnet publish $projectFile -c Release -r $RuntimeIdentifier --self-contained true -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish başarısız oldu (kod $LASTEXITCODE)." }

    foreach ($nativeRuntime in @("steam_api64.dll", "libsteam_api.so", "libsteam_api.dylib")) {
        $nativeRuntimePath = Join-Path $publishDirectory $nativeRuntime
        if (Test-Path -LiteralPath $nativeRuntimePath) {
            Remove-Item -LiteralPath $nativeRuntimePath -Force
        }
    }
    Get-ChildItem -Path $publishDirectory -Filter "*.pdb" -File -Recurse |
        Remove-Item -Force

    Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $packageDirectory -Recurse -Force
    foreach ($name in @("README.md", "LICENSE", "THIRD-PARTY-NOTICES.md")) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $packageDirectory
    }

    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal
    Write-Output "Paylaşım paketi hazır: $archivePath"

    if ($IncludeInstaller) {
        & $installerCompiler "/DAppVersion=$AppVersion" "/DPublishDir=$publishDirectory" "/DRuntimeIdentifier=$RuntimeIdentifier" "/O$distDirectory" $installerScript
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup derlemesi başarısız oldu (kod $LASTEXITCODE)." }

        $installerPath = Join-Path $distDirectory "FarmPhantom-Setup-$RuntimeIdentifier.exe"
        Write-Output "Kurulum dosyası hazır: $installerPath"
    }
}
finally {
    $tempFullPath = [IO.Path]::GetFullPath($tempRoot)
    $allowedTempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($tempFullPath.StartsWith($allowedTempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $tempFullPath)) {
        Remove-Item -LiteralPath $tempFullPath -Recurse -Force
    }
}
