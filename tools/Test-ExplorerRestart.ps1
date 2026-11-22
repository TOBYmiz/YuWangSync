param([string]$MakensisPath = 'D:\Program Files (x86)\NSIS\makensis.exe')
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ExplorerRestartRegression-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    # Execute the production NSIS functions, replacing only schtasks with a recorder.
    # No task is registered and the real Explorer process is never stopped.
    $mockDirectory = Join-Path $testRoot 'mock'
    [IO.Directory]::CreateDirectory($mockDirectory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $mockDirectory 'Mock.csproj'), @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>
'@)
    [IO.File]::WriteAllText((Join-Path $mockDirectory 'Program.cs'), @'
using System.Text.Json;
File.AppendAllText(Environment.GetEnvironmentVariable("EXPLORER_RESTART_TEST_LOG")!, JsonSerializer.Serialize(args) + "\n");
return int.Parse(Environment.GetEnvironmentVariable("EXPLORER_RESTART_TEST_" + args[0].TrimStart('/').ToUpperInvariant()) ?? "0");
'@)
    & dotnet build (Join-Path $mockDirectory 'Mock.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Mock command build failed.' }
    $mockExe = Join-Path $mockDirectory 'bin\Release\net10.0\Mock.exe'
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'nsis\Setup.nsi'))
    $match = [regex]::Match($source, '(?ms)^!macro DEFINE_EXPLORER_RESTART_FUNCTIONS UN\r?$.*?(?=^; Called right after)')
    if (-not $match.Success) { throw 'Explorer restart functions are missing.' }
    $functions = $match.Value.Replace('$SYSDIR\schtasks.exe', $mockExe.Replace('$', '$$')) + "`r`n!macroend"
    $fixture = @'
Unicode true
RequestExecutionLevel user
SilentInstall silent
OutFile "@@OUTPUT@@"
!include "LogicLib.nsh"
Var ExplorerInteractiveUser
@@FUNCTIONS@@
!insertmacro DEFINE_EXPLORER_RESTART_FUNCTIONS ""
!insertmacro DEFINE_EXPLORER_RESTART_FUNCTIONS "un."
Section
  Call RestartExplorerNow
  WriteUninstaller "$EXEDIR\UninstallFixture.exe"
SectionEnd
Section "Uninstall"
  Call un.RestartExplorerNow
SectionEnd
'@
    $executable = Join-Path $testRoot 'RestartFixture.exe'
    $fixture = $fixture.Replace('@@OUTPUT@@', $executable.Replace('$', '$$')).Replace('@@FUNCTIONS@@', $functions)
    $script = Join-Path $testRoot 'RestartFixture.nsi'
    [IO.File]::WriteAllText($script, $fixture, [Text.UTF8Encoding]::new($true))
    & $MakensisPath /V2 $script
    if ($LASTEXITCODE -ne 0) { throw 'Production restart function compilation failed.' }
    $scenarios = @(
        @{ Name = 'success'; Create = 0; Run = 0; Delete = 0; Expected = '/create,/run,/delete' },
        @{ Name = 'create failure'; Create = 5; Run = 0; Delete = 0; Expected = '/create,/delete' },
        @{ Name = 'run failure'; Create = 0; Run = 5; Delete = 0; Expected = '/create,/run,/delete' },
        @{ Name = 'delete failure'; Create = 0; Run = 0; Delete = 5; Expected = '/create,/run,/delete' }
    )
    foreach ($scenario in $scenarios) {
        $env:EXPLORER_RESTART_TEST_CREATE = [string]$scenario.Create
        $env:EXPLORER_RESTART_TEST_RUN = [string]$scenario.Run
        $env:EXPLORER_RESTART_TEST_DELETE = [string]$scenario.Delete
        foreach ($mode in @('install', 'uninstall')) {
            $env:EXPLORER_RESTART_TEST_LOG = Join-Path $testRoot ($scenario.Name.Replace(' ', '-') + '-' + $mode + '.jsonl')
            $target = if ($mode -eq 'install') { $executable } else { Join-Path $testRoot 'UninstallFixture.exe' }
            $arguments = if ($mode -eq 'install') { '/S' } else { '/S _?=' + $testRoot }
            $process = Start-Process -FilePath $target -ArgumentList $arguments -PassThru -WindowStyle Hidden
            if (-not $process.WaitForExit(30000)) { throw 'Restart fixture timed out.' }
            if ($process.ExitCode -ne 0) { throw "Fixture exited with $($process.ExitCode)." }
            $commands = [Collections.Generic.List[object]]::new()
            foreach ($line in [IO.File]::ReadAllLines($env:EXPLORER_RESTART_TEST_LOG)) {
                $commands.Add((ConvertFrom-Json -InputObject $line))
            }
            $order = ($commands | ForEach-Object { $_[0] }) -join ','
            if ($order -ne $scenario.Expected) { throw "Unexpected command order: $order" }
            $create = $commands[0]
            $action = $create[[array]::IndexOf($create, '/tr') + 1]
            $expectedAction = '"' + $env:WINDIR + '\System32\cmd.exe" /d /c "taskkill /f /im explorer.exe & start explorer.exe"'
            if ($action -ne $expectedAction) { throw "Task action quoting is incorrect: $action" }
            if ($create -contains '/rl' -or $create -notcontains '/it') { throw 'Incorrect task user context flags.' }
            $userName = $create[[array]::IndexOf($create, '/ru') + 1]
            if (-not $userName) { throw 'Interactive username is empty.' }
            Write-Output "PASS: $mode $($scenario.Name); user=$userName; $order"
        }
    }
}
finally {
    foreach ($name in @('EXPLORER_RESTART_TEST_LOG', 'EXPLORER_RESTART_TEST_CREATE', 'EXPLORER_RESTART_TEST_RUN', 'EXPLORER_RESTART_TEST_DELETE')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
