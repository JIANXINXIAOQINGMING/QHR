$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectDirectory 'QHR.csproj'

dotnet publish $projectPath -p:PublishProfile=WinX64
if ($LASTEXITCODE -ne 0) {
    throw "QHR 发布失败，退出代码：$LASTEXITCODE"
}

$packagePath = Join-Path $projectDirectory 'artifacts\QHR.Overtime-win-x64-Release.zip'
if (-not (Test-Path -LiteralPath $packagePath)) {
    throw "发布已完成，但没有找到分发包：$packagePath"
}

Get-Item -LiteralPath $packagePath | Select-Object FullName, Length, LastWriteTime
