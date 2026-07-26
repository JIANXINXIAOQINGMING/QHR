$ErrorActionPreference = 'Stop'

function Reset-SafeDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootPrefix = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理允许目录以外的路径：$fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    [IO.Directory]::CreateDirectory($fullPath) | Out-Null
    return $fullPath
}

$projectDirectory = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectDirectory 'QHR.csproj'
$binDirectory = Join-Path $projectDirectory 'bin'
$artifactsDirectory = Join-Path $projectDirectory 'artifacts'
[IO.Directory]::CreateDirectory($artifactsDirectory) | Out-Null

$publishDirectory = Reset-SafeDirectory `
    -Path (Join-Path $binDirectory 'Release\net8.0-windows\win-x64\publish') `
    -AllowedRoot $binDirectory
$launcherBuildDirectory = Reset-SafeDirectory `
    -Path (Join-Path $binDirectory 'Release\launcher-win-x64') `
    -AllowedRoot $binDirectory
$packageStageDirectory = Reset-SafeDirectory `
    -Path (Join-Path $artifactsDirectory 'package-stage') `
    -AllowedRoot $artifactsDirectory

dotnet publish $projectPath -p:PublishProfile=WinX64
if ($LASTEXITCODE -ne 0) {
    throw "QHR 主程序发布失败，退出代码：$LASTEXITCODE"
}

$gcc = Get-Command gcc -ErrorAction Stop
$windres = Get-Command windres -ErrorAction Stop
$resourceObject = Join-Path $launcherBuildDirectory 'QHRLauncher.res.o'
$launcherPath = Join-Path $launcherBuildDirectory 'QHR.exe'

& $windres.Source '--codepage=65001' `
    -i (Join-Path $projectDirectory 'Launcher\QHRLauncher.rc') `
    -O coff `
    -o $resourceObject
if ($LASTEXITCODE -ne 0) {
    throw "QHR 原生启动器资源编译失败，退出代码：$LASTEXITCODE"
}

& $gcc.Source `
    (Join-Path $projectDirectory 'Launcher\QHRLauncher.c') `
    $resourceObject `
    '-Os' `
    '-s' `
    '-mwindows' `
    '-municode' `
    '-static-libgcc' `
    '-Wl,--dynamicbase' `
    '-Wl,--high-entropy-va' `
    '-Wl,--nxcompat' `
    '-o' $launcherPath
if ($LASTEXITCODE -ne 0) {
    throw "QHR 原生启动器编译失败，退出代码：$LASTEXITCODE"
}

$applicationPath = Join-Path $publishDirectory 'QHR.Overtime.exe'
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw '发布目录中没有 QHR.Overtime.exe'
}

$applicationVersion = [Version](Get-Item -LiteralPath $applicationPath).VersionInfo.FileVersion
$launcherVersion = [Version](Get-Item -LiteralPath $launcherPath).VersionInfo.FileVersion
if ($launcherVersion -ne $applicationVersion) {
    throw "启动器版本 $launcherVersion 与主程序版本 $applicationVersion 不一致"
}
$displayVersion = "$($applicationVersion.Major).$($applicationVersion.Minor).$($applicationVersion.Build)"

$applicationStageDirectory = Join-Path $packageStageDirectory 'app'
[IO.Directory]::CreateDirectory($applicationStageDirectory) | Out-Null
Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $applicationStageDirectory -Recurse -Force
Copy-Item -LiteralPath $launcherPath -Destination (Join-Path $packageStageDirectory 'QHR.exe') -Force

$packagePath = Join-Path $artifactsDirectory "QHR.Overtime-v$displayVersion-win-x64-Release.zip"
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}
[IO.Compression.ZipFile]::CreateFromDirectory(
    $packageStageDirectory,
    $packagePath,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)

Remove-Item -LiteralPath $packageStageDirectory -Recurse -Force
Get-Item -LiteralPath $packagePath | Select-Object FullName, Length, LastWriteTime
