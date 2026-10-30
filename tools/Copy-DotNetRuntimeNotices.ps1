param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectAssetsFile,
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$assetsPath = [System.IO.Path]::GetFullPath($ProjectAssetsFile)
$publishPath = [System.IO.Path]::GetFullPath($PublishDirectory)
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw "NuGet assets file is missing: $assetsPath"
}
if (-not (Test-Path -LiteralPath $publishPath -PathType Container)) {
    throw "Publish directory is missing: $publishPath"
}

$assets = [System.IO.File]::ReadAllText($assetsPath) | ConvertFrom-Json
$runtimeDependencies = @($assets.project.frameworks.PSObject.Properties |
    ForEach-Object { $_.Value.downloadDependencies })
$runtimePack = @($runtimeDependencies |
    Where-Object Name -EQ 'Microsoft.NETCore.App.Runtime.win-x64') | Select-Object -First 1
if (-not $runtimePack -or $runtimePack.version -notmatch '^\[(?<version>[^,]+),') {
    throw 'Could not determine the self-contained .NET Runtime pack version from project.assets.json.'
}
$runtimeVersion = $Matches.version
$packageDirectory = $null
foreach ($packageFolder in $assets.packageFolders.PSObject.Properties.Name) {
    $candidate = Join-Path $packageFolder "microsoft.netcore.app.runtime.win-x64\$runtimeVersion"
    if ((Test-Path -LiteralPath (Join-Path $candidate 'LICENSE.TXT') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $candidate 'THIRD-PARTY-NOTICES.TXT') -PathType Leaf)) {
        $packageDirectory = $candidate
        break
    }
}
if (-not $packageDirectory) {
    throw "Runtime pack license files for .NET $runtimeVersion were not found in restored package folders."
}

$licenseDirectory = Join-Path $publishPath 'licenses\dotnet-runtime'
[System.IO.Directory]::CreateDirectory($licenseDirectory) | Out-Null
Copy-Item -LiteralPath (Join-Path $packageDirectory 'LICENSE.TXT') `
    -Destination (Join-Path $licenseDirectory 'LICENSE.TXT') -Force
Copy-Item -LiteralPath (Join-Path $packageDirectory 'THIRD-PARTY-NOTICES.TXT') `
    -Destination (Join-Path $licenseDirectory 'THIRD-PARTY-NOTICES.TXT') -Force
Write-Output "Bundled .NET Runtime $runtimeVersion license and notices."
