$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& dotnet build (Join-Path $repository 'src\YuWangCloudSync.Admin\YuWangCloudSync.Admin.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Admin helper build failed.' }
[xml]$identity = [IO.File]::ReadAllText((Join-Path $repository 'src\ProductIdentity.props'))
$identifier = [string]$identity.Project.PropertyGroup.ProductIdentifier
$assembly = Join-Path $repository ('src\YuWangCloudSync.Admin\bin\Release\net10.0-windows10.0.19041.0\win-x64\' + $identifier + 'Admin.dll')
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$root = Join-Path ([IO.Path]::GetTempPath()) ('AdminHelper-NoSuchRoot-' + [guid]::NewGuid().ToString('N'))
$registration = $identifier + '!' + $sid + '!AdminHelperTest-' + [guid]::NewGuid().ToString('N')
function Check-Command([string]$Name, [int]$Expected, [string[]]$Arguments) {
    # Execute the managed entry point without a UAC prompt. Tests reject invalid
    # requests or address missing registrations; no protected data is changed.
    & dotnet $assembly @Arguments
    if ($LASTEXITCODE -ne $Expected) { throw "$Name returned $LASTEXITCODE; expected $Expected." }
    Write-Output "PASS: $Name (exit=$Expected)"
}
Check-Command 'no operation rejected' 2 @()
Check-Command 'arbitrary commands rejected' 2 @('--exec', '--user-sid', $sid, '--sync-root', $root)
Check-Command 'arbitrary registry paths rejected' 2 @('--write-registry', '--user-sid', $sid, '--sync-root', $root)
Check-Command 'foreign registration rejected' 2 @('--remove-machine-shell-registration', '--user-sid', $sid, '--sync-root', $root, '--registration-id', 'ForeignProduct!Default')
Check-Command 'relative path rejected' 2 @('--ensure-search-index', '--user-sid', $sid, '--sync-root', 'relative')
Check-Command 'unowned search root rejected' 2 @('--ensure-search-index', '--user-sid', $sid, '--sync-root', $root)
Check-Command 'unexpected parameters rejected' 2 @('--remove-search-index', '--user-sid', $sid, '--sync-root', $root, '--exec', 'cmd.exe')
Check-Command 'absent own registration is a no-op' 0 @('--remove-machine-shell-registration', '--user-sid', $sid, '--sync-root', $root, '--registration-id', $registration)
