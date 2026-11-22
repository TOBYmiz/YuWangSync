param([Parameter(Mandatory)][string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$identity = [IO.File]::ReadAllText((Join-Path $repository 'src\ProductIdentity.props'))
$executableName = [string]$identity.Project.PropertyGroup.ProductIdentifier + 'Admin.exe'
$stage = Join-Path $repository ('_buildtmp\admin-helper-' + [guid]::NewGuid().ToString('N'))
try {
    & dotnet publish (Join-Path $repository 'src\YuWangCloudSync.Admin\YuWangCloudSync.Admin.csproj') `
        -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true `
        -p:DebugType=None -p:DebugSymbols=false -o $stage
    if ($LASTEXITCODE -ne 0) { throw '权限辅助程序发布失败。' }
    $executable = Join-Path $stage $executableName
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '权限辅助程序输出缺失。' }
    Copy-Item -LiteralPath $executable -Destination (Join-Path $PayloadDirectory $executableName) -Force
    # The app project reference can contribute apphost metadata. The replacement
    # NativeAOT helper is standalone and must not advertise a .NET framework dependency.
    foreach ($suffix in @('.runtimeconfig.json', '.deps.json')) {
        $metadata = Join-Path $PayloadDirectory ([IO.Path]::GetFileNameWithoutExtension($executableName) + $suffix)
        if (Test-Path -LiteralPath $metadata -PathType Leaf) { Remove-Item -LiteralPath $metadata -Force }
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($stage)
    $allowed = [IO.Path]::GetFullPath((Join-Path $repository '_buildtmp')).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe helper cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
