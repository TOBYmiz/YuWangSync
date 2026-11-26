param([string]$MakensisPath = 'D:\Program Files (x86)\NSIS\makensis.exe')
$ErrorActionPreference = 'Stop'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryRoot ('ShellExtensionCleanup-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'nsis\Setup.nsi'))
    # Compile the complete installer too, with a synthetic payload; never execute it.
    $payloadRoot = Join-Path $testRoot 'payload'
    [IO.Directory]::CreateDirectory($payloadRoot) | Out-Null
    $dummy = Join-Path $payloadRoot 'sentinel.txt'
    [IO.File]::WriteAllText($dummy, 'compile-only payload')
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $tokens = @{
        PRODUCT_NAME_ZH = '雨望云同步'; PRODUCT_NAME_EN = 'YuWangCloudSync'; PRODUCT_IDENTIFIER = 'YuWangCloudSync'
        PRODUCT_VERSION = '1.0.2.0'; INSTALL_VARIANT = 'standard'; SETUP_FILE_NAME = 'YuWangCloudSync.exe'
        SETUP_OUTPUT_PATH = (Join-Path $testRoot 'YuWangCloudSync.exe'); APP_PAYLOAD_DIRECTORY = $payloadRoot
        SHELL_EXTENSION_FILE_NAME = 'YuWangCloudSyncShellExt-test.dll'; SHELL_EXTENSION_PATH = $dummy
        INSTALLED_FILES_PATH = $dummy; INSTALLED_DIRECTORIES_PATH = $dummy; SETUP_LOGIN_SCRIPT_PATH = $dummy
        PRODUCT_ICON_PATH = (Join-Path $repository 'src\YuWangCloudSync\Assets\yw_setup.ico')
        PRODUCT_UNINSTALL_ICON_PATH = (Join-Path $repository 'src\YuWangCloudSync\Assets\yw_uninstall.ico')
    }
    $fullScript = $source
    foreach ($key in $tokens.Keys) { $fullScript = $fullScript.Replace(('@@' + $key + '@@'), $tokens[$key].Replace('$', '$$')) }
    if ($fullScript -match '@@[A-Z_]+@@') { throw 'Unresolved full-installer fixture token.' }
    $fullScriptPath = Join-Path $testRoot 'Setup.generated.nsi'
    [IO.File]::WriteAllText($fullScriptPath, $fullScript, [Text.UTF8Encoding]::new($true))
    & $MakensisPath /V2 $fullScriptPath
    if ($LASTEXITCODE -ne 0) { throw 'Full installer source compilation failed.' }
    Write-Output 'PASS: full Setup.nsi compilation with synthetic payload (installer not executed).'
    $functions = foreach ($name in @('RemovePreviousShellExtension', 'SchedulePreviousShellExtensionCleanup', '.onGUIEnd', '.onInstSuccess')) {
        $match = [regex]::Match($source, '(?ms)^Function ' + [regex]::Escape($name) + '\r?$.*?^FunctionEnd\r?$')
        if (-not $match.Success) { throw "Missing production function: $name" }
        $match.Value
    }
    # Record the deferred-deletion request instead of modifying the system's pending
    # rename operations. Direct Delete, retry timing, and setup callbacks stay real.
    $functions = ($functions -join "`r`n").Replace(
        'Delete /REBOOTOK "$PreviousInstallDirectory\$PreviousShellExtensionFileName"',
        'StrCpy $Scheduled "1"')
    $functions = $functions.Replace('Call SchedulePreviousShellExtensionCleanup',
        "Call SchedulePreviousShellExtensionCleanup`r`n  Call WriteResult")
    $fixture = @'
Unicode true
RequestExecutionLevel user
OutFile "@@OUTPUT@@"
AutoCloseWindow true
Page instfiles
!include "LogicLib.nsh"
Var PreviousInstallDirectory
Var PreviousShellExtensionFileName
Var PreviousShellExtensionCleanupPending
Var ExplorerRestartStarted
Var QuickUpdateRequested
Var IsInstalled
Var Scheduled
Var Mode
@@FUNCTIONS@@
Function LaunchAppViaTask
  Pop $0
FunctionEnd
Function WriteResult
  FileOpen $0 "$EXEDIR\result.txt" w
  FileWrite $0 "$PreviousShellExtensionCleanupPending|$Scheduled"
  FileClose $0
FunctionEnd
Section
  StrCpy $PreviousInstallDirectory "$EXEDIR"
  StrCpy $PreviousShellExtensionFileName "old-shell.dll"
  StrCpy $PreviousShellExtensionCleanupPending "1"
  StrCpy $Scheduled "0"
  ReadEnvStr $Mode "SHELL_CLEANUP_TEST_MODE"
  ReadEnvStr $ExplorerRestartStarted "SHELL_CLEANUP_TEST_STARTED"
  StrCpy $QuickUpdateRequested "0"
  StrCpy $IsInstalled "1"
  Call RemovePreviousShellExtension
  FileOpen $0 "$EXEDIR\before-exit.txt" w
  FileWrite $0 "$PreviousShellExtensionCleanupPending|$Scheduled"
  FileClose $0
  ${If} $Mode == "finish"
    StrCpy $ExplorerRestartStarted "1"
    Call RemovePreviousShellExtension
  ${ElseIf} $Mode == "quick"
    StrCpy $QuickUpdateRequested "1"
  ${EndIf}
  Call WriteResult
SectionEnd
'@
    $executable = Join-Path $testRoot 'CleanupFixture.exe'
    $fixture = $fixture.Replace('@@OUTPUT@@', $executable.Replace('$', '$$')).Replace('@@FUNCTIONS@@', $functions)
    $script = Join-Path $testRoot 'CleanupFixture.nsi'
    [IO.File]::WriteAllText($script, $fixture, [Text.UTF8Encoding]::new($true))
    & $MakensisPath /V2 $script
    if ($LASTEXITCODE -ne 0) { throw 'Cleanup fixture compilation failed.' }
    $scenarios = @(
        @{ Name = 'restart unlocked'; Started = '1'; Lock = $false; Release = $false; Mode = ''; Silent = $true; Before = '0|0'; Result = '0|0'; Exists = $false },
        @{ Name = 'restart waiting for release'; Started = '1'; Lock = $true; Release = $true; Mode = ''; Silent = $true; Before = '0|0'; Result = '0|0'; Exists = $false },
        @{ Name = 'restart still locked'; Started = '1'; Lock = $true; Release = $false; Mode = ''; Silent = $true; Before = '1|0'; Result = '0|1'; Exists = $true },
        @{ Name = 'deferred unlocked'; Started = '0'; Lock = $false; Release = $false; Mode = ''; Silent = $true; Before = '0|0'; Result = '0|0'; Exists = $false },
        @{ Name = 'deferred silent'; Started = '0'; Lock = $true; Release = $false; Mode = ''; Silent = $true; Before = '1|0'; Result = '0|1'; Exists = $true },
        @{ Name = 'deferred GUI exit'; Started = '0'; Lock = $true; Release = $false; Mode = ''; Silent = $false; Before = '1|0'; Result = '0|1'; Exists = $true },
        @{ Name = 'deferred quick update'; Started = '0'; Lock = $true; Release = $false; Mode = 'quick'; Silent = $false; Before = '1|0'; Result = '0|1'; Exists = $true },
        @{ Name = 'finish page restart'; Started = '0'; Lock = $true; Release = $true; Mode = 'finish'; Silent = $true; Before = '1|0'; Result = '0|0'; Exists = $false },
        @{ Name = 'finish page restart still locked'; Started = '0'; Lock = $true; Release = $false; Mode = 'finish'; Silent = $true; Before = '1|0'; Result = '0|1'; Exists = $true }
    )
    foreach ($scenario in $scenarios) {
        $env:SHELL_CLEANUP_TEST_MODE = $scenario.Mode
        $env:SHELL_CLEANUP_TEST_STARTED = $scenario.Started
        $dll = Join-Path $testRoot 'old-shell.dll'
        [IO.File]::WriteAllText($dll, 'synthetic old shell extension')
        Remove-Item -LiteralPath (Join-Path $testRoot 'result.txt'), (Join-Path $testRoot 'before-exit.txt') -Force -ErrorAction SilentlyContinue
        $lock = $null
        $process = $null
        try {
            if ($scenario.Lock) { $lock = [IO.File]::Open($dll, 'Open', 'Read', 'Read') }
            $start = @{ FilePath = $executable; PassThru = $true; WindowStyle = 'Hidden' }
            if ($scenario.Silent) { $start.ArgumentList = '/S' }
            $process = Start-Process @start
            if ($scenario.Release) {
                if ($process.WaitForExit(1000)) { throw 'Fixture did not wait for the DLL lock.' }
                $lock.Dispose()
                $lock = $null
            }
            if (-not $process.WaitForExit(30000)) { throw 'Cleanup fixture timed out.' }
            if ($process.ExitCode -ne 0) { throw "Cleanup fixture exited with $($process.ExitCode)." }
            $before = [IO.File]::ReadAllText((Join-Path $testRoot 'before-exit.txt'))
            $result = [IO.File]::ReadAllText((Join-Path $testRoot 'result.txt'))
            if ($before -ne $scenario.Before -or $result -ne $scenario.Result) {
                throw "Incorrect cleanup state for $($scenario.Name): before=$before; result=$result"
            }
            if ([IO.File]::Exists($dll) -ne $scenario.Exists) { throw "Incorrect DLL existence: $($scenario.Name)" }
            Write-Output "PASS: $($scenario.Name); before-exit=$before; final=$result; exists=$($scenario.Exists)"
        }
        finally {
            if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
            if ($lock) { $lock.Dispose() }
        }
    }
}
finally {
    foreach ($name in @('SHELL_CLEANUP_TEST_MODE', 'SHELL_CLEANUP_TEST_STARTED')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($temporaryRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('ShellExtensionCleanup-')) { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
