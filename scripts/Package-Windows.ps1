param(
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot "app\FarmPhantom.csproj"
$distDirectory = Join-Path $projectRoot "dist"

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

    Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $packageDirectory -Recurse -Force
    foreach ($name in @("README.md", "LICENSE", "THIRD-PARTY-NOTICES.md")) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $packageDirectory
    }

    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal
    Write-Output "Paylaşım paketi hazır: $archivePath"
}
finally {
    $tempFullPath = [IO.Path]::GetFullPath($tempRoot)
    $allowedTempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($tempFullPath.StartsWith($allowedTempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $tempFullPath)) {
        Remove-Item -LiteralPath $tempFullPath -Recurse -Force
    }
}
