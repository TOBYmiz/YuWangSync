param(
    [string]$SetupScriptPath = "$PSScriptRoot\nsis\Setup.nsi",
    [string]$MakensisPath = 'D:\Program Files (x86)\NSIS\makensis.exe'
)
$ErrorActionPreference = 'Stop'
$root = [System.IO.Directory]::CreateDirectory((Join-Path ([System.IO.Path]::GetTempPath()) (
    'YuWangCloudSync-SetupSessionCleanup-' + [Guid]::NewGuid().ToString('N')))).FullName
try {
    $source = [System.IO.File]::ReadAllText($SetupScriptPath)
    $function = [regex]::Match($source, '(?ms)^Function un\.DeleteSessionFilesInDirectory\r?$.*?^FunctionEnd\r?$')
    if (-not $function.Success) { throw 'Production cleanup function is missing.' }
    $harness = @"
Unicode true
RequestExecutionLevel user
SilentInstall silent
SilentUnInstall silent
OutFile "$root\Fixture.exe"
Section
  WriteUninstaller "`$EXEDIR\Cleanup.exe"
SectionEnd
Section "Uninstall"
  StrCpy `$R4 "legacy"
  StrCpy `$R5 "`$EXEDIR\data"
  Call un.DeleteSessionFilesInDirectory
  StrCpy `$R5 "`$EXEDIR\install"
  Call un.DeleteSessionFilesInDirectory
  StrCpy `$R4 "protected"
  StrCpy `$R5 "`$EXEDIR\data\credentials"
  Call un.DeleteSessionFilesInDirectory
SectionEnd
$($function.Value)
"@
    $nsi = Join-Path $root 'Fixture.nsi'
    [System.IO.File]::WriteAllText($nsi, $harness, [System.Text.UTF8Encoding]::new($true))
    & $MakensisPath /V2 $nsi
    if ($LASTEXITCODE -ne 0) { throw 'NSIS session cleanup fixture did not compile.' }
    $fixture = Start-Process -FilePath (Join-Path $root 'Fixture.exe') -WindowStyle Hidden -Wait -PassThru
    if ($fixture.ExitCode -ne 0) { throw 'Could not write test uninstaller.' }
    $data = [System.IO.Directory]::CreateDirectory((Join-Path $root 'data')).FullName
    $install = [System.IO.Directory]::CreateDirectory((Join-Path $root 'install')).FullName
    $credentials = [System.IO.Directory]::CreateDirectory((Join-Path $data 'credentials')).FullName
    $key = '0123456789ABCDEF0123'
    $id = '0123456789abcdef0123456789abcdef'
    $legacy = @('cookies.json', "cookies-$key.json", "cookies-$key.json.$id.tmp", "cookies-backup-$id.json")
    $protected = @("session-$key.dpapi", "session-$key.dpapi.$id.tmp", "session-$key.dpapi.$id.bak")
    $remove = @(
        $legacy | ForEach-Object { Join-Path $data $_; Join-Path $install $_ }
        $protected | ForEach-Object { Join-Path $credentials $_ }
    )
    $keep = @(
        Join-Path $data 'appsettings.json'
        Join-Path $data 'cookies-manual.json'
        Join-Path $credentials 'unrelated.dpapi'
        Join-Path $credentials "session-Z123456789ABCDEF0123.dpapi"
        Join-Path $credentials "session-$key.dpapi.manual.tmp"
        Join-Path $credentials "session-$key.dpapi.Z123456789abcdef0123456789abcdef.bak"
    )
    foreach ($file in ($remove + $keep)) { [System.IO.File]::WriteAllText($file, 'synthetic fixture') }
    $cleanup = Start-Process -FilePath (Join-Path $root 'Cleanup.exe') -ArgumentList ('/S _?=' + $root) -WindowStyle Hidden -Wait -PassThru
    if ($cleanup.ExitCode -ne 0 -or @($remove | Where-Object { Test-Path -LiteralPath $_ }).Count -ne 0 -or
        @($keep | Where-Object { -not (Test-Path -LiteralPath $_) }).Count -ne 0) {
        throw 'Session cleanup did not remove only the recognized session files.'
    }
    'PASS: uninstaller removes protected sessions and old Cookie residue while preserving unrelated files.'
}
finally {
    $temp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($root.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($root).StartsWith('YuWangCloudSync-SetupSessionCleanup-')) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
