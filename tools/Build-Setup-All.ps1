param(
    [string]$MSBuildPath,
    [string]$MakensisPath,
    [string]$OutputDirectory = "$PSScriptRoot\..\_buildtmp\installer"
)

$ErrorActionPreference = 'Stop'

$builds = @(
    [pscustomobject]@{
        Name = '标准版'
        Script = Join-Path $PSScriptRoot 'Build-Setup.ps1'
    }
    [pscustomobject]@{
        Name = '需要预装 Windows App Runtime 版'
        Script = Join-Path $PSScriptRoot 'Build-Setup-WinAppSDKRequired.ps1'
    }
)

foreach ($build in $builds) {
    if (-not (Test-Path -LiteralPath $build.Script -PathType Leaf)) {
        throw "安装包构建脚本不存在：$($build.Script)"
    }
}

$buildArguments = @{
    OutputDirectory = $OutputDirectory
}
if (-not [string]::IsNullOrWhiteSpace($MSBuildPath)) {
    $buildArguments['MSBuildPath'] = $MSBuildPath
}
if (-not [string]::IsNullOrWhiteSpace($MakensisPath)) {
    $buildArguments['MakensisPath'] = $MakensisPath
}

# Both scripts clear and regenerate the app's obj directory, so run them in sequence.
foreach ($build in $builds) {
    Write-Output ("开始构建{0}安装包…" -f $build.Name)
    & $build.Script @buildArguments
    Write-Output ("{0}安装包构建完成。" -f $build.Name)
}

Write-Output ("两种安装包均已输出到：{0}" -f [System.IO.Path]::GetFullPath($OutputDirectory))
