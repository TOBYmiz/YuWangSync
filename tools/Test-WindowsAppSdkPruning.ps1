param()
$ErrorActionPreference = 'Stop'
$prune = Join-Path $PSScriptRoot 'Prune-WindowsAppSdkPayload.ps1'
$policy = & $prune -GetPolicy
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('YuWangSdkPrune-' + [guid]::NewGuid().ToString('N'))
$checks = 0
function Check([bool]$Passed, [string]$Label) {
    if (-not $Passed) { throw ('FAIL: ' + $Label) }
    $script:checks++
    Write-Output ('PASS: ' + $Label)
}
function Write-FixtureFile([string]$Path) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, 'fixture:' + [IO.Path]::GetFileName($Path))
}
function New-Bundle([string]$Directory) {
    foreach ($name in $policy.RequiredRuntimeFiles + $policy.ExpectedRuntimeFiles + @('YuWangCloudSync.dll', 'YuWangCloudSync.pdb', 'e_sqlite3.dll', 'resources.pri', 'App.xbf')) {
        Write-FixtureFile (Join-Path $Directory $name)
    }
    foreach ($language in @('zh-CN', 'en-us', 'fr-FR')) { Write-FixtureFile (Join-Path $Directory ($language + '\Microsoft.UI.Xaml.dll.mui')) }
    Write-FixtureFile (Join-Path $Directory 'runtimes\win-x64\native\onnxruntime.dll')
    Write-FixtureFile (Join-Path $Directory 'Assets\fr-FR\keep.txt')
}
function Expect-Failure([scriptblock]$Action) {
    try { & $Action | Out-Null; return $false } catch { return $true }
}
try {
    $bundle = Join-Path $fixture 'debug output 中文'
    New-Bundle $bundle
    & $prune -PayloadDirectory ($bundle + '\.') -RuntimeMode Bundled -StrictInventory | Out-Null
    Check (-not (Test-Path -LiteralPath (Join-Path $bundle 'DirectML.dll'))) 'optional runtime DLL is removed'
    Check (-not (Test-Path -LiteralPath (Join-Path $bundle 'runtimes\win-x64\native\onnxruntime.dll'))) 'optional DLLs in nested output directories are removed'
    Check (-not (Test-Path -LiteralPath (Join-Path $bundle 'fr-FR'))) 'unselected top-level WinUI MUI directory is removed'
    Check ((Test-Path -LiteralPath (Join-Path $bundle 'zh-CN\Microsoft.UI.Xaml.dll.mui')) -and (Test-Path -LiteralPath (Join-Path $bundle 'en-us\Microsoft.UI.Xaml.dll.mui'))) 'Chinese and English fallback MUI resources survive'
    Check (@($policy.RequiredRuntimeFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $bundle $_)) }).Count -eq 0) 'all native WinUI and MRT dependencies survive'
    Check ((Test-Path -LiteralPath (Join-Path $bundle 'YuWangCloudSync.pdb')) -and (Test-Path -LiteralPath (Join-Path $bundle 'YuWangCloudSync.dll'))) 'managed Debug assembly and PDB survive'
    Check ((Test-Path -LiteralPath (Join-Path $bundle 'resources.pri')) -and (Test-Path -LiteralPath (Join-Path $bundle 'App.xbf')) -and (Test-Path -LiteralPath (Join-Path $bundle 'e_sqlite3.dll'))) 'application resources and SQLite survive'
    Check (Test-Path -LiteralPath (Join-Path $bundle 'Assets\fr-FR\keep.txt')) 'unrelated nested assets are preserved'
    & $prune -PayloadDirectory $bundle | Out-Null
    Check (Test-Path -LiteralPath (Join-Path $bundle 'YuWangCloudSync.pdb')) 'pruning an already-pruned incremental build succeeds'

    $framework = Join-Path $fixture 'framework'
    Write-FixtureFile (Join-Path $framework 'de-DE\Microsoft.UI.Xaml.dll.mui')
    Write-FixtureFile (Join-Path $framework 'YuWangCloudSync.pdb')
    & $prune -PayloadDirectory $framework -RuntimeMode Framework | Out-Null
    Check (-not (Test-Path -LiteralPath (Join-Path $framework 'de-DE'))) 'framework runtime mode prunes local MUI without requiring bundled runtime files'
    Check (Test-Path -LiteralPath (Join-Path $framework 'YuWangCloudSync.pdb')) 'framework runtime mode preserves debug symbols'
    & $prune -PayloadDirectory $framework -RuntimeMode Framework | Out-Null
    Check (Test-Path -LiteralPath $framework) 'framework mode permits absent local MUI directories'

    $invalid = Join-Path $fixture 'invalid'
    New-Bundle $invalid
    Remove-Item -LiteralPath (Join-Path $invalid 'Microsoft.Windows.ApplicationModel.Resources.dll')
    Check (Expect-Failure { & $prune -PayloadDirectory $invalid }) 'missing MRT dependency fails before pruning'
    Check (Test-Path -LiteralPath (Join-Path $invalid 'DirectML.dll')) 'failed validation leaves optional files intact'
    Write-FixtureFile (Join-Path $invalid 'Microsoft.Windows.ApplicationModel.Resources.dll')
    Remove-Item -LiteralPath (Join-Path $invalid 'DirectML.dll')
    Check (Expect-Failure { & $prune -PayloadDirectory $invalid -StrictInventory }) 'release strict inventory still detects missing expected optional files'
    Check (Test-Path -LiteralPath (Join-Path $invalid 'Microsoft.Web.WebView2.Core.dll')) 'strict inventory failure leaves the remaining files intact'
    Check (Expect-Failure { & $prune -PayloadDirectory ([IO.Path]::GetPathRoot($fixture)) }) 'filesystem root is refused'

    $linked = Join-Path $fixture 'links'
    New-Bundle $linked
    $outside = Join-Path $fixture 'outside'
    Write-FixtureFile (Join-Path $outside 'DirectML.dll')
    $runtimeLink = Join-Path $linked 'external'
    New-Item -ItemType Junction -Path $runtimeLink -Target $outside | Out-Null
    try {
        & $prune -PayloadDirectory $linked | Out-Null
        Check (Test-Path -LiteralPath (Join-Path $outside 'DirectML.dll')) 'runtime enumeration does not follow directory links'
        $muiLink = Join-Path $linked 'it-IT\external'
        Write-FixtureFile (Join-Path $linked 'it-IT\Microsoft.UI.Xaml.dll.mui')
        New-Item -ItemType Junction -Path $muiLink -Target $outside | Out-Null
        try {
            Check (Expect-Failure { & $prune -PayloadDirectory $linked }) 'recursive MUI deletion refuses a linked subtree'
            Check (Test-Path -LiteralPath (Join-Path $outside 'DirectML.dll')) 'linked content remains intact after refused MUI cleanup'
        } finally { [IO.Directory]::Delete($muiLink) }
    } finally { [IO.Directory]::Delete($runtimeLink) }
    Write-Output ('Pruning regression: ' + $checks + ' passed.')
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'YuWangSdkPrune-*') {
        throw 'Unsafe pruning fixture cleanup path.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
