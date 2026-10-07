[CmdletBinding()]
param(
    [string]$DalamudPath = '',
    [string]$TestVmd = '',
    [string]$TestPmx = '',
    [string]$DotnetPath = 'dotnet',
    [string]$PackageCachePath = ''
)
$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command $DotnetPath -ErrorAction Stop).Source
if (!$DalamudPath) {
    foreach ($candidate in @(
        (Join-Path $env:APPDATA 'XIVLauncherCN/addon/Hooks/dev'),
        (Join-Path $env:APPDATA 'XIVLauncher/addon/Hooks/dev')
    )) { if (Test-Path (Join-Path $candidate 'Dalamud.dll')) { $DalamudPath = $candidate; break } }
}
if (!$DalamudPath -or !(Test-Path (Join-Path $DalamudPath 'Dalamud.dll'))) { throw '请指定包含 Dalamud.dll 的开发库目录：-DalamudPath <目录>' }
$lib = (Resolve-Path -LiteralPath $DalamudPath).Path + [IO.Path]::DirectorySeparatorChar
if ($TestVmd) { $TestVmd = (Resolve-Path -LiteralPath $TestVmd).Path }
if ($TestPmx) { $env:FFMMD_TEST_PMX = (Resolve-Path -LiteralPath $TestPmx).Path }
if (!$PackageCachePath) { $PackageCachePath = Join-Path $PSScriptRoot '.build/packages' }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build/dotnet-home'
$env:NUGET_PACKAGES = $PackageCachePath
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DALAMUD_HOME = $lib
Push-Location $PSScriptRoot
try {
    & $dotnet restore FFMMD.Test/FFMMD.Test.csproj --configfile NuGet.Config
    if ($LASTEXITCODE) { throw '离线测试工程还原失败' }
    $testArgs = @('run', '--no-restore', '--project', 'FFMMD.Test', '-c', 'Release')
    if ($TestVmd) { $testArgs += @('--', '--verify', $TestVmd) }
    & $dotnet @testArgs
    if ($LASTEXITCODE) { throw '回归测试失败，停止打包' }
    $properties = @('-p:Platform=x64', "-p:DalamudLibPath=$lib", "-p:DalamudHome=$lib")
    & $dotnet restore FFMMD/FFMMD.csproj --configfile NuGet.Config @properties
    if ($LASTEXITCODE) { throw '插件工程还原失败' }
    & $dotnet build --no-restore FFMMD/FFMMD.csproj -c Release @properties
    if ($LASTEXITCODE) { throw '插件构建失败' }
    Write-Output (Join-Path $PSScriptRoot 'FFMMD/bin/Release/FFMMD/latest.zip')
} finally { Pop-Location }
