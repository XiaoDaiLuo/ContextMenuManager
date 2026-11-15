# 生成可分发的程序目录：dist\ContextMenuManager
# 用法：.\publish.ps1 [-Dotnet <dotnet 可执行文件>]
param([string]$Dotnet = 'dotnet')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'dist\ContextMenuManager'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', $out, '--nologo', '-v', 'q', '-p:DebugType=none', '-m:2')
& $Dotnet publish (Join-Path $root 'src\ContextMenuManager.App') @common
if ($LASTEXITCODE -ne 0) { throw '界面程序发布失败' }
& $Dotnet publish (Join-Path $root 'src\ContextMenuManager.Cli') @common
if ($LASTEXITCODE -ne 0) { throw '命令行程序发布失败' }

# 依赖包附带的 XML 文档对运行没有用
Get-ChildItem $out -Filter *.xml | Remove-Item -Force
Copy-Item (Join-Path $root 'README.md') $out

Get-ChildItem $out | Sort-Object Name | Format-Table Name, Length -AutoSize
"已生成：$out"
