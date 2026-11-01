# CU_TransUpdater 一键编译
# 用法: powershell -File build.ps1
$ErrorActionPreference = 'Stop'

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$g   = "J:\SteamLibrary\steamapps\common\Casualties Unknown Demo"
$bep = Join-Path $g "BepInEx\core"
$man = Join-Path $g "CasualtiesUnknown_Data\Managed"
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $src "bin"
New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir "CU_TransUpdater.dll"

$refs = @(
  (Join-Path $bep "BepInEx.dll"),
  (Join-Path $man "UnityEngine.dll"),
  (Join-Path $man "UnityEngine.CoreModule.dll"),
  (Join-Path $man "UnityEngine.UIModule.dll"),
  (Join-Path $man "UnityEngine.UI.dll"),
  (Join-Path $man "UnityEngine.TextRenderingModule.dll"),
  (Join-Path $man "UnityEngine.UnityWebRequestModule.dll"),
  (Join-Path $man "Unity.TextMeshPro.dll"),
  (Join-Path $man "Newtonsoft.Json.dll"),
  (Join-Path $man "netstandard.dll")
)

$optList = @()
$optList += "/nologo", "/target:library", "/platform:anycpu", "/optimize+", "/langversion:5", "/codepage:65001", "/out:$out"
foreach ($r in $refs) { $optList += "/reference:$r" }

$cmd = $optList + (Join-Path $src "TransUpdaterPlugin.cs")
Write-Host "编译 -> $out"
& $csc @cmd
if ($LASTEXITCODE -ne 0) { Write-Host "编译失败 (exit $LASTEXITCODE)" -ForegroundColor Red; exit $LASTEXITCODE }
Write-Host ("编译成功! " + (Get-Item $out).Length + " 字节") -ForegroundColor Green
