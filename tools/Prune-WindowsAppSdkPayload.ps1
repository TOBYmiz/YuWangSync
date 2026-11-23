[CmdletBinding(DefaultParameterSetName = 'Prune')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Prune')][string]$PayloadDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Policy')][switch]$GetPolicy,
    [Parameter(ParameterSetName = 'Prune')][ValidateSet('Bundled', 'Framework')][string]$RuntimeMode = 'Bundled',
    [Parameter(ParameterSetName = 'Prune')][switch]$StrictInventory
)
$ErrorActionPreference = 'Stop'

# Exact filenames shared by installer publishing and Debug/Release output pruning.
# Keep PDBs, managed debug assemblies and the native WinUI/MRT dependencies.
$policy = [pscustomobject]@{
    OptionalRuntimeFiles = @(
        'DirectML.dll'
        'Microsoft.Asg.SemanticIndex.AiFabric.Compatibility.dll'
        'Microsoft.Graphics.Imaging.dll'
        'Microsoft.Graphics.Imaging.Projection.dll'
        'Microsoft.Graphics.Imaging.winmd'
        'Microsoft.Graphics.ImagingInternal.ImageObjectRemover.winmd'
        'Microsoft.Graphics.ImagingInternal.winmd'
        'Microsoft.Graphics.Internal.Imaging.winmd'
        'Microsoft.ML.OnnxRuntime.dll'
        'System.Numerics.Tensors.dll'
        'Microsoft.Web.WebView2.Core.dll'
        'Microsoft.Web.WebView2.Core.Projection.dll'
        'Microsoft.Windows.AI.AICapabilities.dll'
        'Microsoft.Windows.AI.ContentModerationInternal.winmd'
        'Microsoft.Windows.AI.ContentSafety.dll'
        'Microsoft.Windows.AI.ContentSafety.Projection.dll'
        'Microsoft.Windows.AI.ContentSafety.winmd'
        'Microsoft.Windows.AI.Foundation.Projection.dll'
        'Microsoft.Windows.AI.Foundation.winmd'
        'Microsoft.Windows.AI.FoundationInternal.winmd'
        'Microsoft.Windows.AI.GenerativeInternal.winmd'
        'Microsoft.Windows.AI.Imaging.dll'
        'Microsoft.Windows.AI.Imaging.Projection.dll'
        'Microsoft.Windows.AI.Imaging.winmd'
        'Microsoft.Windows.AI.MachineLearning.dll'
        'Microsoft.Windows.AI.MachineLearning.Projection.dll'
        'Microsoft.Windows.AI.Projection.dll'
        'Microsoft.Windows.AI.Search.Experimental.dll'
        'Microsoft.Windows.AI.Text.dll'
        'Microsoft.Windows.AI.Text.Projection.dll'
        'Microsoft.Windows.AI.Text.winmd'
        'Microsoft.Windows.AI.Video.dll'
        'Microsoft.Windows.AI.Video.Projection.dll'
        'Microsoft.Windows.AI.Video.winmd'
        'Microsoft.Windows.AI.winmd'
        'Microsoft.Windows.Internal.AI.ContentModeration.winmd'
        'Microsoft.Windows.Internal.AI.Generative.winmd'
        'Microsoft.Windows.Internal.SemanticSearch.winmd'
        'Microsoft.Windows.Search.dll'
        'Microsoft.Windows.Search.Projection.dll'
        'Microsoft.Windows.Search.winmd'
        'Microsoft.Windows.SemanticSearch.winmd'
        'Microsoft.Windows.SemanticSearchInternal.winmd'
        'Microsoft.Windows.Widgets.dll'
        'Microsoft.Windows.Widgets.Projection.dll'
        'Microsoft.Windows.Widgets.winmd'
        'NPUDetect.dll'
        'onnxruntime.dll'
        'PerceptiveStreaming.dll'
        'WebView2Loader.dll'
    )
    ExpectedRuntimeFiles = @('DirectML.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Windows.Search.dll', 'Microsoft.Windows.Widgets.dll', 'PerceptiveStreaming.dll')
    RequiredRuntimeFiles = @(
        'Microsoft.WindowsAppRuntime.dll'
        'Microsoft.WindowsAppRuntime.Bootstrap.dll'
        'Microsoft.ui.xaml.dll'
        'Microsoft.UI.Xaml.Controls.dll'
        # Microsoft.UI.Windowing.Core.dll imports CoreMessagingXP.dll; without it, loading
        # Microsoft.ui.xaml.dll fails with DllNotFoundException during XamlCheckProcessRequirements.
        'Microsoft.UI.Windowing.Core.dll'
        'CoreMessagingXP.dll'
        'Microsoft.UI.Xaml.Controls.pri'
        'Microsoft.UI.pri'
        'Microsoft.ui.xaml.resources.19h1.dll'
        'Microsoft.ui.xaml.resources.common.dll'
        'Microsoft.Windows.ApplicationModel.Resources.dll'
        'MRM.dll'
        'Microsoft.Windows.Workloads.Resources.dll'
    )
    KeptLanguages = @('zh-CN', 'en-us')
}
if ($GetPolicy) { return $policy }

$payload = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd([char[]]'\/')
if ($payload -eq [IO.Path]::GetPathRoot($payload).TrimEnd([char[]]'\/')) {
    throw 'Payload pruning cannot target a filesystem root.'
}
$root = Get-Item -LiteralPath $payload
if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Payload pruning requires a regular output directory.'
}
$payloadPrefix = $payload + [IO.Path]::DirectorySeparatorChar
function Assert-PayloadChild([string]$Target) {
    if (-not [IO.Path]::GetFullPath($Target).StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Pruning target is outside the output directory: ' + $Target)
    }
}
function Get-PayloadFiles([string]$Directory, [switch]$RejectLinks) {
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Force) {
        Assert-PayloadChild $entry.FullName
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            if ($RejectLinks) { throw ('A language directory contains a link: ' + $entry.FullName) }
            continue
        }
        if ($entry.PSIsContainer) { Get-PayloadFiles $entry.FullName -RejectLinks:$RejectLinks }
        else { $entry }
    }
}
$unusedRuntimeFiles = @()
if ($RuntimeMode -eq 'Bundled') {
    foreach ($name in $policy.RequiredRuntimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) {
            throw ('主程序载荷缺少 Windows App Runtime 文件：' + $name)
        }
    }
    $unusedRuntimeFiles = @(Get-PayloadFiles $payload | Where-Object { $_.Name -in $policy.OptionalRuntimeFiles })
    if ($StrictInventory) {
        foreach ($name in $policy.ExpectedRuntimeFiles) {
            if (-not ($unusedRuntimeFiles | Where-Object Name -EQ $name)) {
                throw ('预期裁剪的可选运行时文件不存在：' + $name)
            }
        }
        foreach ($language in $policy.KeptLanguages) {
            if (-not (Test-Path -LiteralPath (Join-Path $payload $language) -PathType Container)) {
                throw '主程序载荷缺少中文或英文 WinUI 本地化资源。'
            }
        }
    }
}
$unselectedLanguageDirectories = @(Get-ChildItem -LiteralPath $payload -Directory | Where-Object {
    -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $_.Name -notin $policy.KeptLanguages -and
        @(Get-ChildItem -LiteralPath $_.FullName -Filter '*.mui' -File).Count -gt 0
})
# Validate every directory before recursive deletion; never follow links into other trees.
$languageFiles = @($unselectedLanguageDirectories | ForEach-Object { Get-PayloadFiles $_.FullName -RejectLinks })
$unusedRuntimeBytes = ($unusedRuntimeFiles | Measure-Object -Property Length -Sum).Sum
foreach ($file in $unusedRuntimeFiles) {
    Assert-PayloadChild $file.FullName
    Write-Output ('裁剪未使用运行时文件：' + $file.Name)
    Remove-Item -LiteralPath $file.FullName -Force
}
if ($RuntimeMode -eq 'Bundled') {
    Write-Output ('裁剪 Windows App SDK 可选功能文件：{0} 个，未压缩 {1:N2} MiB。' -f $unusedRuntimeFiles.Count, ($unusedRuntimeBytes / 1MB))
}
$languageBytes = ($languageFiles | Measure-Object -Property Length -Sum).Sum
foreach ($file in $languageFiles) { Write-Output ('裁剪未使用语言资源：' + $file.FullName.Substring($payloadPrefix.Length)) }
foreach ($directory in $unselectedLanguageDirectories) {
    Assert-PayloadChild $directory.FullName
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
}
Write-Output ('裁剪 WinUI 非中文/英文资源目录：{0} 个文件，未压缩 {1:N2} MiB。' -f $languageFiles.Count, ($languageBytes / 1MB))
