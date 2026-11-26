param(
    [string]$SetupScriptPath = "$PSScriptRoot\nsis\Setup.nsi",
    [string]$MakensisPath = 'D:\Program Files (x86)\NSIS\makensis.exe'
)
$ErrorActionPreference = 'Stop'
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryRoot ('YuWangCloudSync-SetupManifest-升级 ' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null

function Write-TestLines([string]$Path, [string[]]$Lines) {
    [System.IO.File]::WriteAllLines($Path, $Lines, [System.Text.UTF8Encoding]::new($false))
}

try {
    # Compile the production manifest/cleanup functions into an isolated, silent
    # fixture. No real installation, registry, login, Explorer or sync root is used.
    $source = [System.IO.File]::ReadAllText([System.IO.Path]::GetFullPath($SetupScriptPath))
    $functions = foreach ($name in @('AddExistingShortcutIconsToManifest', 'ManifestContainsPath',
            'ValidatePreviousInstallRelativePath', 'RemoveObsoleteInstalledFiles', 'RemoveObsoleteInstalledDirectories')) {
        $match = [regex]::Match($source, '(?ms)^Function ' + [regex]::Escape($name) + '\r?$.*?^FunctionEnd\r?$')
        if (-not $match.Success) { throw "Production function is missing: $name" }
        $match.Value
    }
    $harness = @'
Unicode true
RequestExecutionLevel user
SilentInstall silent
OutFile "@@OUTPUT@@"
!include "LogicLib.nsh"
!include "StrFunc.nsh"
${Using:StrFunc} StrRep
!define PRODUCT_IDENTIFIER "YuWangCloudSync"
!define PRODUCT_VERSION "1.0.2.0"
Var PreviousInstallDirectory
Var PreviousShellExtensionFileName
Var IsInstalled
Var NewInstalledFilesManifest
Var NewInstalledDirectoriesManifest
Var PreviousManifestHandle
Var ManifestSearchPath
Var ManifestSearchFile
Var ManifestPathFound
Var RelativePathFull
Var RelativePathValid
@@FUNCTIONS@@
Section
  InitPluginsDir
  StrCpy $INSTDIR "$EXEDIR\fixture"
  StrCpy $PreviousInstallDirectory $INSTDIR
  StrCpy $PreviousShellExtensionFileName "YuWangCloudSyncShellExt-old.dll"
  StrCpy $IsInstalled "1"
  CopyFiles /SILENT "$EXEDIR\new-files.txt" "$PLUGINSDIR\installed-files.new.txt"
  StrCpy $NewInstalledFilesManifest "$PLUGINSDIR\installed-files.new.txt"
  StrCpy $NewInstalledDirectoriesManifest "$EXEDIR\new-directories.txt"
  Call AddExistingShortcutIconsToManifest
  ${If} $R9 != "0"
    SetErrorLevel 2
    Quit
  ${EndIf}
  Call RemoveObsoleteInstalledFiles
  CopyFiles /SILENT "$PLUGINSDIR\installed-files.new.txt" "$INSTDIR\installed-files.txt"
SectionEnd
'@
    $testExecutable = Join-Path $testRoot 'ManifestFixture.exe'
    $harness = $harness.Replace('@@OUTPUT@@', $testExecutable.Replace('$', '$$'))
    $harness = $harness.Replace('@@FUNCTIONS@@', ($functions -join [Environment]::NewLine))
    $harnessPath = Join-Path $testRoot 'ManifestFixture.nsi'
    [System.IO.File]::WriteAllText($harnessPath, $harness, [System.Text.UTF8Encoding]::new($true))
    & $MakensisPath /V2 $harnessPath
    if ($LASTEXITCODE -ne 0) { throw "NSIS regression fixture compilation failed ($LASTEXITCODE)." }

    $currentIcon = 'YuWangCloudSync-shortcut-1.0.2.0.ico'
    $appFiles = @('App.xbf', 'CoreMessagingXP.dll', 'dcompi.dll', 'dwmcorei.dll',
        'DwmSceneI.dll', 'DWriteCore.dll', 'e_sqlite3.dll', 'marshal.dll',
        'YuWangCloudSync.exe', ('Assets\' + $currentIcon))
    $scenarios = @(
        [pscustomobject]@{ Name = 'no old icons'; Icons = @(); Corrupted = $false },
        [pscustomobject]@{ Name = 'one old icon'; Icons = @('YuWangCloudSync-shortcut-1.0.1.0.ico'); Corrupted = $false },
        [pscustomobject]@{ Name = 'two old icons'; Icons = @('YuWangCloudSync-shortcut-1.0.1.0.ico', 'YuWangCloudSync-shortcut-1.0.1.1.ico'); Corrupted = $false },
        [pscustomobject]@{ Name = 'repair corrupted manifest'; Icons = @('YuWangCloudSync-shortcut-1.0.1.0.ico', 'YuWangCloudSync-shortcut-1.0.1.1.ico'); Corrupted = $true }
    )
    foreach ($scenario in $scenarios) {
        $scenarioRoot = Join-Path $testRoot $scenario.Name
        $installRoot = Join-Path $scenarioRoot 'fixture'
        [System.IO.Directory]::CreateDirectory((Join-Path $installRoot 'Assets')) | Out-Null
        [System.IO.Directory]::CreateDirectory((Join-Path $installRoot 'ObsoleteDirectory')) | Out-Null
        Copy-Item -LiteralPath $testExecutable -Destination (Join-Path $scenarioRoot 'ManifestFixture.exe')
        $oldIcons = @($scenario.Icons | ForEach-Object { 'Assets\' + $_ })
        foreach ($relative in ($appFiles + $oldIcons + @('obsolete.dll', 'user-owned.txt', 'YuWangCloudSyncShellExt-old.dll'))) {
            [System.IO.File]::WriteAllText((Join-Path $installRoot $relative), "sentinel:$relative")
        }
        Write-TestLines (Join-Path $scenarioRoot 'new-files.txt') $appFiles
        Write-TestLines (Join-Path $scenarioRoot 'new-directories.txt') @('Assets')
        $previousFiles = $appFiles + $oldIcons + @('obsolete.dll', 'YuWangCloudSyncShellExt-old.dll')
        if ($scenario.Corrupted) {
            # Reproduce the already-installed manifest reported with this crash.
            $previousFiles = $oldIcons + @('qlite3.dll') + $appFiles[7..($appFiles.Count - 1)] + @('obsolete.dll', 'YuWangCloudSyncShellExt-old.dll')
        }
        Write-TestLines (Join-Path $installRoot 'installed-files.txt') $previousFiles
        Write-TestLines (Join-Path $installRoot 'installed-directories.txt') @('Assets', 'ObsoleteDirectory')
        # Repeat the upgrade: appended icon entries must not corrupt the next run.
        foreach ($iteration in 1..2) {
            $process = Start-Process -FilePath (Join-Path $scenarioRoot 'ManifestFixture.exe') -WindowStyle Hidden -PassThru
            if (-not $process.WaitForExit(15000)) {
                $process.Kill()
                throw "NSIS manifest fixture timed out: $($scenario.Name)."
            }
            if ($process.ExitCode -ne 0) { throw "NSIS fixture failed: $($process.ExitCode)." }
            $manifestPath = Join-Path $installRoot 'installed-files.txt'
            $bytes = [System.IO.File]::ReadAllBytes($manifestPath)
            $prefix = [System.IO.File]::ReadAllBytes((Join-Path $scenarioRoot 'new-files.txt'))
            if ($bytes.Length -lt $prefix.Length -or
                [Convert]::ToBase64String($bytes, 0, $prefix.Length) -ne [Convert]::ToBase64String($prefix)) {
                throw "Payload manifest prefix was overwritten: $($scenario.Name), iteration $iteration."
            }
            $lines = [System.IO.File]::ReadAllLines($manifestPath)
            if ($lines.Length -ne ($appFiles.Count + $oldIcons.Count)) { throw 'Unexpected manifest entry count.' }
            foreach ($relative in ($appFiles + $oldIcons)) {
                if ($relative -notin $lines -or -not [System.IO.File]::Exists((Join-Path $installRoot $relative))) {
                    throw "Upgrade deleted or untracked a required file: $relative."
                }
            }
            if ([System.IO.File]::Exists((Join-Path $installRoot 'obsolete.dll')) -or
                [System.IO.Directory]::Exists((Join-Path $installRoot 'ObsoleteDirectory'))) {
                throw 'Obsolete payload cleanup did not run.'
            }
            if ([System.IO.File]::ReadAllText((Join-Path $installRoot 'user-owned.txt')) -ne 'sentinel:user-owned.txt') {
                throw 'Upgrade changed an untracked user file.'
            }
            if (-not [IO.File]::Exists((Join-Path $installRoot 'YuWangCloudSyncShellExt-old.dll'))) {
                throw 'Manifest cleanup deleted the old shell extension before the Explorer restart prompt.'
            }
        }
        Write-Output "PASS: $($scenario.Name); two upgrades preserve payload/SQLite DLL and icon entries, remove obsolete files, and keep user files."
    }
}
finally {
    $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    if (-not $resolvedTestRoot.StartsWith($temporaryRoot.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($resolvedTestRoot).StartsWith('YuWangCloudSync-SetupManifest-')) {
        throw 'Temporary test directory validation failed; no directory was deleted.'
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force }
}
