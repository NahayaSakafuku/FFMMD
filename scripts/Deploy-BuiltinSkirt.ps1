[CmdletBinding()]
param([string]$ExpectedVersion = '1.1.0.0')
$ErrorActionPreference = 'Stop'
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Expected a four-part plugin version.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packagePath = Join-Path $projectRoot 'FFMMD/bin/Release/FFMMD/latest.zip'
$pluginDirectory = Join-Path $env:APPDATA 'XIVLauncherCN/installedPlugins/FFMMD'
$installationPath = Join-Path $pluginDirectory $ExpectedVersion
$configDirectory = Join-Path $env:APPDATA 'XIVLauncherCN/pluginConfigs/FFMMD'
$configPath = Join-Path $configDirectory 'FFMMD.json'
function Assert-GameClosed {
    if (@(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'ffxiv*' }).Count) {
        throw 'Close FF14 before installing the local test build.'
    }
}
Assert-GameClosed
$timestamp = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow, 'China Standard Time').ToString('yyyyMMdd-HHmmss')
$stagingPath = Join-Path $projectRoot ".build/builtin-skirt-install-$timestamp"
$backupPath = Join-Path (Split-Path -Parent $projectRoot) "FFMMD-installed-backup-$timestamp"
$configBackup = Join-Path (Split-Path -Parent $projectRoot) "FFMMD-config-backup-$timestamp"
foreach ($path in @($stagingPath, $backupPath, $configBackup)) { if (Test-Path -LiteralPath $path) { throw "Path exists: $path" } }
Expand-Archive -LiteralPath $packagePath -DestinationPath $stagingPath
$manifest = Get-Content -LiteralPath (Join-Path $stagingPath 'FFMMD.json') -Raw | ConvertFrom-Json
if ($manifest.InternalName -ne 'FFMMD' -or $manifest.AssemblyVersion -ne $ExpectedVersion) { throw 'Unexpected package manifest.' }
foreach ($required in @('FFMMD.dll', 'FFMMD.Bullet.dll', 'Bullet-LICENSE.txt', 'FFMMD.Bullet.NOTICES.txt', 'LLVM-MinGW-LICENSE.txt', 'COPYING.MinGW-w64-runtime.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $stagingPath $required))) { throw "Missing package component: $required" }
}
if (@(Get-ChildItem -LiteralPath $stagingPath -Directory).Count) { throw 'Expected a flat plugin package.' }
$files = @(Get-ChildItem -LiteralPath $stagingPath -File)
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
Assert-GameClosed
Copy-Item -LiteralPath $pluginDirectory -Destination $backupPath -Recurse
Copy-Item -LiteralPath $configDirectory -Destination $configBackup -Recurse
Assert-GameClosed
New-Item -ItemType Directory -Path $installationPath -Force | Out-Null
foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $installationPath $file.Name) -Force }
foreach ($file in $files) {
    if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $installationPath $file.Name) -Algorithm SHA256).Hash) { throw "Hash mismatch: $($file.Name)" }
}
$config['AutoSkirtPhysics'] = $true
$config['SkirtPhysicsPmxPath'] = $null
$config.Remove('SkirtBlenderPath') | Out-Null
if ($config.Cal) { $config.Cal.Remove('SkirtSimEnabled') | Out-Null; $config.Cal.Remove('SkirtSwing') | Out-Null }
Assert-GameClosed
$config | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $configPath -Encoding utf8
$verifiedConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
if (!$verifiedConfig.AutoSkirtPhysics -or $null -ne $verifiedConfig.SkirtPhysicsPmxPath) { throw 'Builtin configuration verification failed.' }
$receipt = [ordered]@{
    InstalledAtHongKong = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow, 'China Standard Time').ToString('yyyy-MM-dd HH:mm:ss')
    Installation = $installationPath; Backup = $backupPath; ConfigBackup = $configBackup
    Package = $packagePath; PackageSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    BulletSha256 = (Get-FileHash -LiteralPath (Join-Path $installationPath 'FFMMD.Bullet.dll') -Algorithm SHA256).Hash
    AssemblyVersion = $manifest.AssemblyVersion; DiagnosticSchema = 9; FilesVerified = $files.Count
    AutoSkirtPhysics = $verifiedConfig.AutoSkirtPhysics; Reference = 'Bundled skeleton and physics data'; BlenderRequired = $false
}
$receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $projectRoot '.build/builtin-skirt-deployment.json') -Encoding utf8
$receipt | ConvertTo-Json -Depth 10
