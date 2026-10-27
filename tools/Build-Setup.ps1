param(
    [string]$MSBuildPath,
    [string]$MakensisPath,
    [string]$OutputDirectory = "$PSScriptRoot\..\_buildtmp\installer"
)
$ErrorActionPreference = 'Stop'
$versionPropsPath = Join-Path $PSScriptRoot '..\src\Directory.Build.props'
[xml]$versionProps = [System.IO.File]::ReadAllText([System.IO.Path]::GetFullPath($versionPropsPath))
$version = [string]$versionProps.Project.PropertyGroup.ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Installer version must have four numeric components.' }
$productIdentityPath = Join-Path $PSScriptRoot '..\src\ProductIdentity.props'
[xml]$productIdentityProps = [System.IO.File]::ReadAllText([System.IO.Path]::GetFullPath($productIdentityPath))
$productProperties = $productIdentityProps.Project.PropertyGroup
$productNameZh = [string]$productProperties.ProductDisplayNameZh
$productNameEn = [string]$productProperties.ProductDisplayNameEn
$productIdentifier = [string]$productProperties.ProductIdentifier
$productExecutableName = $productIdentifier + '.exe'
if (-not $productNameZh -or -not $productNameEn -or $productIdentifier -notmatch '^[A-Za-z][A-Za-z0-9]*$') {
    throw 'Product identity properties must define Chinese and English names and a filesystem-safe identifier.'
}
$setupFileName = '{0}.exe' -f $productIdentifier
$installVariant = 'standard'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.VC.ATL -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath)) {
    throw 'Visual Studio 2022 C++ x64 tools, ATL v143 and Windows SDK are required.'
}
if (-not $MakensisPath) {
    $makensisCommand = Get-Command 'makensis.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    $makensisCandidates = @()
    if ($makensisCommand -and $makensisCommand.Source) { $makensisCandidates += $makensisCommand.Source }
    if ($env:NSIS_HOME) { $makensisCandidates += (Join-Path $env:NSIS_HOME 'makensis.exe') }
    $makensisCandidates += @(
        'D:\Program Files (x86)\NSIS\makensis.exe',
        (Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'),
        (Join-Path $env:ProgramFiles 'NSIS\makensis.exe')
    )
    foreach ($candidate in $makensisCandidates) {
        if (-not $candidate -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $candidateVersion = (& $candidate /VERSION 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -eq 0 -and $candidateVersion -match '(?:^|\D)3\.\d+') {
            $MakensisPath = $candidate
            break
        }
    }
}
if (-not $MakensisPath -or -not (Test-Path -LiteralPath $MakensisPath -PathType Leaf)) {
    throw 'NSIS 3.x is required; install NSIS or pass -MakensisPath with the path to makensis.exe.'
}
$makensisVersion = (& $MakensisPath /VERSION 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $makensisVersion -notmatch '(?:^|\D)3\.\d+') {
    throw "NSIS 3.x is required to package the installer payload. Detected: $makensisVersion"
}
function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable build failed ($LASTEXITCODE)." }
}
function Escape-NsisString([string]$Value) {
    return $Value.Replace('$', '$$').Replace('"', '$"')
}
$stage = Join-Path $repository ('_buildtmp\setup-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'payload'
$shellExtensionFileName = $productIdentifier + 'ShellExt-' + [guid]::NewGuid().ToString('N') + '.dll'
$shellExtensionPath = Join-Path $stage ($productIdentifier + 'ShellExt.dll')
$mainBuildOutput = Join-Path $stage 'main-build'
$installedFilesPath = Join-Path $stage 'installed-files.txt'
$installedDirectoriesPath = Join-Path $stage 'installed-directories.txt'
$generatedNsisScript = Join-Path $stage 'Setup.generated.nsi'
$stagedSetup = Join-Path $stage $setupFileName
[System.IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    & (Join-Path $PSScriptRoot 'New-OverlayIcon.ps1')
    Invoke-Checked $MSBuildPath @((Join-Path $repository 'src\YuWangCloudSync.ShellExt\YuWangCloudSync.ShellExt.vcxproj'), '/p:Configuration=Release', '/p:Platform=x64', ('/p:ProductVersion=' + $version), '/nologo')

    # A prior packaged or differently configured WinUI build can leave an incompatible
    # PRI/XBF set in obj. The unpackaged app-local Windows App SDK payload must generate
    # its own application resource index instead of reusing those intermediates.
    $appProject = [System.IO.Path]::GetFullPath((Join-Path $repository 'src\YuWangCloudSync'))
    $appIntermediate = [System.IO.Path]::GetFullPath((Join-Path $appProject 'obj'))
    $appProjectPrefix = $appProject.TrimEnd('\') + '\'
    if (-not $appIntermediate.StartsWith($appProjectPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($appIntermediate) -ne 'obj') {
        throw 'WinUI intermediate directory validation failed; stale resources were not removed.'
    }
    if (Test-Path -LiteralPath $appIntermediate) {
        Write-Output '清理主程序 WinUI 中间产物，确保生成 unpackaged PRI…'
        Remove-Item -LiteralPath $appIntermediate -Recurse -Force
    }

    # GenerateDepsFile writes into OutDir before every project reference has
    # necessarily created it, so prepare the shared custom output directory first.
    [System.IO.Directory]::CreateDirectory($mainBuildOutput) | Out-Null
    Invoke-Checked 'dotnet' @('publish', (Join-Path $repository 'src\YuWangCloudSync\YuWangCloudSync.csproj'), '-c', 'Release', '-r', 'win-x64', '--no-self-contained', ('-p:ProductVersion=' + $version), '-p:WindowsAppSDKSelfContained=true', '-p:DebugType=None', '-p:DebugSymbols=false', ('-p:OutDir=' + $mainBuildOutput + '\'), '-o', $publish)

    # The project publish target copies compiled XAML and the app's root resources.pri.
    foreach ($requiredResource in @('App.xbf', 'resources.pri')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $requiredResource) -PathType Leaf)) {
            throw "程序载荷缺少 WinUI 应用资源：$requiredResource"
        }
    }

    # These exact files appeared in the win-x64 publish inventory and have no source references.
    # They belong to optional Windows App SDK AI/ML, Search, Widgets and WebView2 features.
    # Their removal is experimental because framework components can load files dynamically.
    $unusedRuntimeNames = @(
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
    $unusedRuntimeFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Name -in $unusedRuntimeNames })
    $expectedRuntimeFiles = @('DirectML.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Windows.Search.dll', 'Microsoft.Windows.Widgets.dll', 'PerceptiveStreaming.dll')
    foreach ($name in $expectedRuntimeFiles) {
        if (-not ($unusedRuntimeFiles | Where-Object Name -EQ $name)) { throw "預期裁剪的可选运行时文件不存在：$name" }
    }
    $unusedRuntimeBytes = ($unusedRuntimeFiles | Measure-Object -Property Length -Sum).Sum
    foreach ($file in $unusedRuntimeFiles) { Write-Output ('裁剪未使用运行时文件：' + $file.Name) }
    foreach ($file in $unusedRuntimeFiles) { Remove-Item -LiteralPath $file.FullName -Force }
    Write-Output ('试验性裁剪 Windows App SDK 可选功能文件：{0} 个，未压缩 {1:N2} MiB。' -f $unusedRuntimeFiles.Count, ($unusedRuntimeBytes / 1MB))

    # Keep simplified Chinese and English fallback resources. Remove the other
    # WinUI MUI language directories enumerated in the publish inventory.
    $unselectedLanguageDirectories = @(Get-ChildItem -LiteralPath $publish -Directory | Where-Object {
        $_.Name -notin @('zh-CN', 'en-us') -and @(Get-ChildItem -LiteralPath $_.FullName -Filter '*.mui' -File).Count -gt 0
    })
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'zh-CN') -PathType Container) -or
        -not (Test-Path -LiteralPath (Join-Path $publish 'en-us') -PathType Container)) {
        throw '主程序载荷缺少中文或英文 WinUI 本地化资源。'
    }
    $languageFiles = @($unselectedLanguageDirectories | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Recurse -File })
    $languageBytes = ($languageFiles | Measure-Object -Property Length -Sum).Sum
    foreach ($file in $languageFiles) { Write-Output ('裁剪未使用语言资源：' + $file.FullName.Substring($publish.Length + 1)) }
    foreach ($directory in $unselectedLanguageDirectories) { Remove-Item -LiteralPath $directory.FullName -Recurse -Force }
    Write-Output ('裁剪 WinUI 非中文/英文资源目录：{0} 个文件，未压缩 {1:N2} MiB。' -f $languageFiles.Count, ($languageBytes / 1MB))

    # These are part of the app-local Windows App SDK deployment. In particular,
    # the native XAML resource DLLs provide MRT Core and must survive optional-file pruning.
    $requiredRuntimeFiles = @(
        'Microsoft.WindowsAppRuntime.dll'
        'Microsoft.WindowsAppRuntime.Bootstrap.dll'
        'Microsoft.ui.xaml.dll'
        'Microsoft.UI.Xaml.Controls.dll'
        'Microsoft.UI.Xaml.Controls.pri'
        'Microsoft.UI.pri'
        'Microsoft.ui.xaml.resources.19h1.dll'
        'Microsoft.ui.xaml.resources.common.dll'
        'Microsoft.Windows.ApplicationModel.Resources.dll'
        'MRM.dll'
        'Microsoft.Windows.Workloads.Resources.dll'
    )
    foreach ($name in $requiredRuntimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $name) -PathType Leaf)) {
            throw "主程序载荷缺少 Windows App Runtime 文件：$name"
        }
    }
    $legacyUiAssemblies = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {
        $_.Name -in @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll', 'System.Windows.Forms.dll', 'WindowsFormsIntegration.dll')
    }
    if ($legacyUiAssemblies) { throw '主程序载荷包含已移除的 WPF/WinForms UI 程序集。' }
    Copy-Item -LiteralPath (Join-Path $repository ('src\YuWangCloudSync.ShellExt\bin\Release\' + $productIdentifier + 'ShellExt.dll')) -Destination $shellExtensionPath
    $privateFiles = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Name -in @('appsettings.json', 'cookies.json', 'captcha.png') -or $_.Extension -in @('.db', '.log') }
    if ($privateFiles) { throw 'The publish directory contains private user data. Packaging stopped.' }
    $payloadFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File)
    $shellExtensionBytes = (Get-Item -LiteralPath $shellExtensionPath).Length
    $payloadFileBytes = ($payloadFiles | Measure-Object -Property Length -Sum).Sum + $shellExtensionBytes
    foreach ($requiredFile in (@($productExecutableName, 'App.xbf', 'resources.pri') + $requiredRuntimeFiles)) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $requiredFile) -PathType Leaf)) {
            throw "主程序载荷缺少必需文件：$requiredFile"
        }
    }
    if (-not (Test-Path -LiteralPath $shellExtensionPath -PathType Leaf)) { throw '主程序载荷缺少资源管理器集成 DLL。' }
    foreach ($prunedName in $unusedRuntimeNames) {
        if ($payloadFiles | Where-Object Name -EQ $prunedName) {
            throw "主程序载荷意外包含已裁剪的可选 Windows App SDK 文件：$prunedName"
        }
    }
    $payloadFileNames = @($payloadFiles | ForEach-Object { $_.FullName.Substring($publish.Length + 1).Replace('/', '\') })
    $payloadDirectories = @(Get-ChildItem -LiteralPath $publish -Recurse -Directory |
        ForEach-Object { $_.FullName.Substring($publish.Length + 1).Replace('/', '\') } |
        Sort-Object { $_.Length } -Descending)
    $installedFiles = @($payloadFileNames + @($shellExtensionFileName, 'uninstall.exe', 'installed-files.txt',
        'installed-directories.txt', 'uninstall-core.exe', 'installed-files.json'))
    [System.IO.File]::WriteAllLines($installedFilesPath, $installedFiles, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllLines($installedDirectoriesPath, $payloadDirectories, [System.Text.UTF8Encoding]::new($false))
    Write-Output ('WinUI/Windows App SDK 文件已直接纳入 NSIS 安装包；应用文件：{0} 个，载荷：{1:N2} MiB。' -f
        ($payloadFiles.Count + 1), ($payloadFileBytes / 1MB))

    $nsisSource = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'nsis\Setup.nsi'))
    $setupLoginScriptPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'nsis\SetupLogin.ps1'))
    $iconPath = [System.IO.Path]::GetFullPath((Join-Path $repository 'src\YuWangCloudSync\Assets\yw_setup.ico'))
    $uninstallIconPath = [System.IO.Path]::GetFullPath((Join-Path $repository 'src\YuWangCloudSync\Assets\yw_uninstall.ico'))
    if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) { throw "安装程序图标不存在：$iconPath" }
    if (-not (Test-Path -LiteralPath $uninstallIconPath -PathType Leaf)) {
        throw "卸载程序图标不存在：$uninstallIconPath"
    }
    $nsisText = [System.IO.File]::ReadAllText($nsisSource, [System.Text.Encoding]::UTF8)
    $nsisText = $nsisText.Replace('@@SETUP_OUTPUT_PATH@@', (Escape-NsisString $stagedSetup))
    $nsisText = $nsisText.Replace('@@APP_PAYLOAD_DIRECTORY@@', (Escape-NsisString $publish))
    $nsisText = $nsisText.Replace('@@SHELL_EXTENSION_FILE_NAME@@', (Escape-NsisString $shellExtensionFileName))
    $nsisText = $nsisText.Replace('@@SHELL_EXTENSION_PATH@@', (Escape-NsisString $shellExtensionPath))
    $nsisText = $nsisText.Replace('@@INSTALLED_FILES_PATH@@', (Escape-NsisString $installedFilesPath))
    $nsisText = $nsisText.Replace('@@INSTALLED_DIRECTORIES_PATH@@', (Escape-NsisString $installedDirectoriesPath))
    $nsisText = $nsisText.Replace('@@SETUP_LOGIN_SCRIPT_PATH@@', (Escape-NsisString $setupLoginScriptPath))
    $nsisText = $nsisText.Replace('@@PRODUCT_ICON_PATH@@', (Escape-NsisString $iconPath))
    $nsisText = $nsisText.Replace('@@PRODUCT_UNINSTALL_ICON_PATH@@', (Escape-NsisString $uninstallIconPath))
    $nsisText = $nsisText.Replace('@@PRODUCT_VERSION@@', (Escape-NsisString $version))
    $nsisText = $nsisText.Replace('@@PRODUCT_NAME_ZH@@', (Escape-NsisString $productNameZh))
    $nsisText = $nsisText.Replace('@@PRODUCT_NAME_EN@@', (Escape-NsisString $productNameEn))
    $nsisText = $nsisText.Replace('@@PRODUCT_IDENTIFIER@@', (Escape-NsisString $productIdentifier))
    $nsisText = $nsisText.Replace('@@INSTALL_VARIANT@@', (Escape-NsisString $installVariant))
    $nsisText = $nsisText.Replace('@@SETUP_FILE_NAME@@', (Escape-NsisString $setupFileName))
    if ($nsisText.Contains('@@APP_PAYLOAD_DIRECTORY@@') -or $nsisText.Contains('@@SHELL_EXTENSION_FILE_NAME@@') -or
        $nsisText.Contains('@@SHELL_EXTENSION_PATH@@') -or $nsisText.Contains('@@INSTALLED_FILES_PATH@@') -or
        $nsisText.Contains('@@INSTALLED_DIRECTORIES_PATH@@') -or $nsisText.Contains('@@SETUP_LOGIN_SCRIPT_PATH@@') -or
        $nsisText.Contains('@@SETUP_OUTPUT_PATH@@') -or
        $nsisText.Contains('@@PRODUCT_ICON_PATH@@') -or $nsisText.Contains('@@PRODUCT_UNINSTALL_ICON_PATH@@') -or
        $nsisText.Contains('@@PRODUCT_VERSION@@') -or $nsisText.Contains('@@PRODUCT_NAME_ZH@@') -or
        $nsisText.Contains('@@PRODUCT_NAME_EN@@') -or $nsisText.Contains('@@PRODUCT_IDENTIFIER@@') -or
        $nsisText.Contains('@@INSTALL_VARIANT@@') -or $nsisText.Contains('@@SETUP_FILE_NAME@@')) {
        throw 'NSIS 安装脚本存在未替换的构建路径。'
    }
    [System.IO.File]::WriteAllText($generatedNsisScript, $nsisText, [System.Text.UTF8Encoding]::new($true))
    Invoke-Checked $MakensisPath @('/V2', $generatedNsisScript)
    if (-not (Test-Path -LiteralPath $stagedSetup -PathType Leaf)) { throw "NSIS 未生成 $setupFileName。" }

    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
    [System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
    $finalSetup = Join-Path $resolvedOutput $setupFileName
    Copy-Item -LiteralPath $stagedSetup -Destination $finalSetup -Force
    Write-Output ('{0} created: {1} ({2:N2} MiB; app payload files {3:N2} MiB uncompressed).' -f
        $setupFileName,
        [System.IO.Path]::GetFullPath($finalSetup), ((Get-Item -LiteralPath $finalSetup).Length / 1MB), ($payloadFileBytes / 1MB))
} finally {
    $resolvedStage = [System.IO.Path]::GetFullPath($stage)
    $intendedRoot = [System.IO.Path]::GetFullPath((Join-Path $repository '_buildtmp')).TrimEnd('\') + '\'
    if (-not $resolvedStage.StartsWith($intendedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or -not [System.IO.Path]::GetFileName($resolvedStage).StartsWith('setup-')) {
        throw 'Temporary directory validation failed. No directory was deleted.'
    }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
