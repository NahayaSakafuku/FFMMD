# PowerShell 7; all downloaded/build inputs stay in the ignored .build directory.
[CmdletBinding()]
param(
    [string]$ToolchainDirectory,
    [string]$BulletDirectory,
    [string]$OutputDirectory,
    [int]$Parallelism = [Math]::Min([Environment]::ProcessorCount, 8)
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$buildDirectory = Join-Path $projectDirectory '.build'
$bulletCommit = '2c204c49e56ed15ec5fcfa71d199ab6d6570b3f5'
$toolchainRelease = 'llvm-mingw-20261006-ucrt-x86_64'
$toolchainSha256 = '317492c456aa27ee607a5919f1d2d38dcdc1112516a24d0bf4b00d078f52d17a'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7 to build the native worker.' }
if ($Parallelism -lt 1 -or $Parallelism -gt 32) { throw 'Parallelism must be in the range 1..32.' }
New-Item -ItemType Directory -Force $buildDirectory | Out-Null
if (!$ToolchainDirectory) {
    $toolchainCache = Join-Path $buildDirectory 'bullet-toolchain'
    New-Item -ItemType Directory -Force $toolchainCache | Out-Null
    $archive = Join-Path $toolchainCache "$toolchainRelease.zip"
    $ToolchainDirectory = Join-Path $toolchainCache $toolchainRelease
    if (!(Test-Path -LiteralPath $ToolchainDirectory)) {
        if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $toolchainSha256) {
            Invoke-WebRequest -Uri "https://github.com/mstorsjo/llvm-mingw/releases/download/20261006/$toolchainRelease.zip" -OutFile $archive
        }
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $toolchainSha256) { throw 'LLVM-MinGW download hash mismatch.' }
        & tar.exe -xf $archive -C $toolchainCache
        if ($LASTEXITCODE -ne 0) { throw 'LLVM-MinGW extraction failed.' }
    }
}
if (!$BulletDirectory) {
    $BulletDirectory = Join-Path $buildDirectory 'bullet-core'
    if (!(Test-Path -LiteralPath (Join-Path $BulletDirectory '.git'))) {
        & git clone --filter=blob:none --no-checkout --depth 1 --branch 3.25 https://github.com/bulletphysics/bullet3.git $BulletDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Bullet source download failed.' }
    }
    & git -C $BulletDirectory sparse-checkout set --no-cone /src/BulletCollision/ /src/BulletDynamics/ /src/LinearMath/ /src/btBulletDynamicsCommon.h /src/btBulletCollisionCommon.h /LICENSE.txt
    if ($LASTEXITCODE -ne 0) { throw 'Bullet sparse checkout setup failed.' }
    & git -C $BulletDirectory checkout
    if ($LASTEXITCODE -ne 0) { throw 'Bullet source checkout failed.' }
    $actualCommit = (& git -C $BulletDirectory rev-parse HEAD).Trim()
    if ($actualCommit -ne $bulletCommit) { throw "Bullet source commit mismatch: $actualCommit" }
}
$ToolchainDirectory = [IO.Path]::GetFullPath($ToolchainDirectory)
$BulletDirectory = [IO.Path]::GetFullPath($BulletDirectory)
$compiler = Join-Path $ToolchainDirectory 'bin/x86_64-w64-mingw32-clang++.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'x64 LLVM-MinGW clang++ was not found.' }
if (!(Test-Path -LiteralPath (Join-Path $BulletDirectory 'src/btBulletDynamicsCommon.h'))) { throw 'Bullet core sources were not found.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'artifacts/x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$objectsDirectory = Join-Path $buildDirectory 'bullet-objects'
New-Item -ItemType Directory -Force $objectsDirectory, $OutputDirectory | Out-Null
$includeDirectory = Join-Path $BulletDirectory 'src'
$sources = @('BulletCollision', 'BulletDynamics', 'LinearMath') | ForEach-Object {
    Get-ChildItem -LiteralPath (Join-Path $includeDirectory $_) -Recurse -Filter '*.cpp' | Sort-Object FullName
}
$sources = @($sources.FullName) + @((Join-Path $PSScriptRoot 'SkirtBullet.cpp'))
$workItems = $sources | ForEach-Object {
    $sourcePath = $_
    $relativeName = if ($sourcePath.StartsWith($includeDirectory)) { $sourcePath.Substring($includeDirectory.Length + 1) } else { 'SkirtBullet.cpp' }
    [pscustomobject]@{ Source = $sourcePath; Object = Join-Path $objectsDirectory ($relativeName.Replace('/', '_').Replace('\', '_') + '.o') }
}
Write-Host "Compiling $($workItems.Count) sources with $Parallelism workers."
$workItems | ForEach-Object -Parallel {
    $compileOutput = & $using:compiler -std=c++17 -O2 -DNDEBUG -DBT_NO_PROFILE -fvisibility=hidden -I $using:includeDirectory -c $_.Source -o $_.Object 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $($_.Source)`n$($compileOutput -join [Environment]::NewLine)" }
} -ThrottleLimit $Parallelism
# Use a response file to stay below the Windows command-line length limit.
$responsePath = Join-Path $objectsDirectory 'link.rsp'
$dllPath = Join-Path $OutputDirectory 'FFMMD.Bullet.dll'
$linkArguments = @('-shared', '-static', '-Wl,--exclude-all-symbols', '-Wl,--no-insert-timestamp', '-o', ('"' + $dllPath.Replace('\', '/') + '"'))
$linkArguments += $workItems | ForEach-Object { '"' + $_.Object.Replace('\', '/') + '"' }
[IO.File]::WriteAllLines($responsePath, $linkArguments, [Text.UTF8Encoding]::new($false))
& $compiler ("@" + $responsePath)
if ($LASTEXITCODE -ne 0) { throw 'Native worker linking failed.' }
Copy-Item -LiteralPath (Join-Path $BulletDirectory 'LICENSE.txt') -Destination (Join-Path $PSScriptRoot 'licenses/Bullet-LICENSE.txt') -Force
$toolchainLicense = Join-Path $ToolchainDirectory 'LICENSE.TXT'
if (Test-Path -LiteralPath $toolchainLicense) { Copy-Item -LiteralPath $toolchainLicense -Destination (Join-Path $PSScriptRoot 'licenses/LLVM-MinGW-LICENSE.txt') -Force }
$mingwLicenseDirectory = Join-Path $ToolchainDirectory 'x86_64-w64-mingw32/share/mingw32'
foreach ($licenseName in @('COPYING.MinGW-w64-runtime.txt', 'COPYING.MinGW-w64.txt', 'COPYING.winpthreads.txt', 'COPYING.winstorecompat.txt')) {
    $licensePath = Join-Path $mingwLicenseDirectory $licenseName
    if (Test-Path -LiteralPath $licensePath) { Copy-Item -LiteralPath $licensePath -Destination (Join-Path $PSScriptRoot "licenses/$licenseName") -Force }
}
$inspector = Join-Path $ToolchainDirectory 'bin/llvm-readobj.exe'
& $inspector --coff-imports --coff-exports $dllPath | Set-Content -LiteralPath (Join-Path $OutputDirectory 'FFMMD.Bullet.imports.txt')
if ($LASTEXITCODE -ne 0) { throw 'DLL import/export inspection failed.' }
Get-Item -LiteralPath $dllPath | Select-Object FullName, Length
Get-FileHash -LiteralPath $dllPath -Algorithm SHA256
