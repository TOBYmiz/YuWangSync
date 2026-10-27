param(
    [string]$ProjectPath = "$PSScriptRoot\..\src\YuWangCloudSync\YuWangCloudSync.csproj"
)
$ErrorActionPreference = 'Stop'
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryRoot ('YuWangCloudSync-ResourcePruning-' + [guid]::NewGuid().ToString('N'))
$buildDirectory = Join-Path $testRoot 'build'
$publishDirectory = Join-Path $testRoot 'publish'

function Invoke-PruningTarget([string]$Target, [bool]$ExpectSuccess) {
    $arguments = @('msbuild', $ProjectPath, '-nologo', '-verbosity:minimal',
        ('-target:' + $Target), ('-p:OutDir=' + $buildDirectory + '\'),
        ('-p:PublishDir=' + $publishDirectory + '\'))
    $output = & dotnet @arguments 2>&1
    $succeeded = $LASTEXITCODE -eq 0
    if ($succeeded -ne $ExpectSuccess) {
        throw "Unexpected result from ${Target}: $($output -join [Environment]::NewLine)"
    }
    if (-not $ExpectSuccess -and ($output -join '') -notmatch 'MRT Core native implementation is missing') {
        throw "The missing-runtime guard did not report the expected error: $output"
    }
}

try {
    foreach ($directory in @($buildDirectory, $publishDirectory)) {
        foreach ($relative in @('Microsoft.Windows.ApplicationModel.Resources.dll',
                'Microsoft.Windows.Workloads.Resources.dll', 'Root.resources.dll',
                'zh-Hans\Example.resources.dll', 'fr-FR\Example.resources.dll')) {
            $path = Join-Path $directory $relative
            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($path)) | Out-Null
            [System.IO.File]::WriteAllText($path, 'resource-pruning-regression-sentinel')
        }
    }

    Invoke-PruningTarget 'PruneBuildSatelliteResources' $true
    Invoke-PruningTarget 'PrunePublishSatelliteResources' $true
    foreach ($directory in @($buildDirectory, $publishDirectory)) {
        foreach ($relative in @('Microsoft.Windows.ApplicationModel.Resources.dll',
                'Microsoft.Windows.Workloads.Resources.dll', 'Root.resources.dll',
                'zh-Hans\Example.resources.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $directory $relative) -PathType Leaf)) {
                throw "Required resource was deleted: $directory\$relative"
            }
        }
        if (Test-Path -LiteralPath (Join-Path $directory 'fr-FR\Example.resources.dll')) {
            throw "Unselected language satellite was retained: $directory"
        }
    }

    Remove-Item -LiteralPath (Join-Path $buildDirectory 'Microsoft.Windows.ApplicationModel.Resources.dll')
    Invoke-PruningTarget 'PruneBuildSatelliteResources' $false
    Remove-Item -LiteralPath (Join-Path $publishDirectory 'Microsoft.Windows.ApplicationModel.Resources.dll')
    Invoke-PruningTarget 'PrunePublishSatelliteResources' $false
    Write-Output 'PASS: build and publish preserve native Resources DLLs and Chinese satellites, remove other satellites, and reject a missing MRT Core implementation.'
}
finally {
    $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    if (-not $resolvedTestRoot.StartsWith($temporaryRoot.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($resolvedTestRoot).StartsWith('YuWangCloudSync-ResourcePruning-')) {
        throw 'Temporary test directory validation failed; no directory was deleted.'
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
