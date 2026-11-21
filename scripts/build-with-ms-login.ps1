<#
.SYNOPSIS
  带微软登录客户端 ID 构建 PCL CE：生成的包在任何设备上都能直接使用正版登录。

.DESCRIPTION
  PCL CE 的微软 OAuth 客户端 ID 通过编译期环境变量注入：
    PCL_WRITE_SECRET=1  允许源生成器把 PCL_* 变量写入产物；
    PCL_MS_CLIENT_ID    你的 Microsoft Entra 应用（客户端 ID）。
  注入后，Release 产物运行时不再读取环境变量，任何设备运行该构建都能正版登录。

  客户端 ID 获取方式见仓库根目录 AI-改造说明.md 的「让所有设备直接可用正版登录」一节。

.EXAMPLE
  ./scripts/build-with-ms-login.ps1 -ClientId 12345678-1234-1234-1234-1234567890ab

.EXAMPLE
  ./scripts/build-with-ms-login.ps1 -ClientId 12345678-1234-1234-1234-1234567890ab -Dotnet "C:\path\to\dotnet.exe"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ClientId,

    [string]$Configuration = "Release",

    [string]$Dotnet = "dotnet",

    [switch]$NoRebuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if ([string]::IsNullOrWhiteSpace($ClientId)) {
        throw "ClientId 不能为空。"
    }

    $env:PCL_WRITE_SECRET = "1"
    $env:PCL_MS_CLIENT_ID = $ClientId

    $buildArgs = @(
        "build",
        "PCL.Launcher/PCL.Launcher.csproj",
        "-c", $Configuration,
        "-p:UseSharedCompilation=false"
    )
    if (-not $NoRebuild) {
        $buildArgs += "-t:Rebuild"
    }

    & $Dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "构建失败，退出码 $LASTEXITCODE。"
    }

    # 确认客户端 ID 已经写进 PCL.Core.dll（.NET 字符串在元数据里是 UTF-16）。
    $dll = Get-ChildItem -Path "PCL.Core/bin" -Filter "PCL.Core.dll" -Recurse |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($dll) {
        $bytes = [IO.File]::ReadAllBytes($dll.FullName)
        $needle = [Text.Encoding]::Unicode.GetBytes($ClientId)
        $found = $false
        for ($i = 0; $i -le $bytes.Length - $needle.Length; $i++) {
            if ($bytes[$i] -ne $needle[0]) { continue }
            $matched = $true
            for ($j = 1; $j -lt $needle.Length; $j++) {
                if ($bytes[$i + $j] -ne $needle[$j]) {
                    $matched = $false
                    break
                }
            }
            if ($matched) {
                $found = $true
                break
            }
        }

        if ($found) {
            Write-Host "已确认客户端 ID 嵌入产物：$($dll.FullName)" -ForegroundColor Green
        }
        else {
            Write-Warning "没有在 $($dll.FullName) 中找到客户端 ID。请删除 PCL.Core/obj 与 PCL.Core/bin 后重试。"
        }
    }

    Write-Host ""
    Write-Host "构建完成。分发 PCL.Launcher/bin/$Configuration 下的产物即可，目标设备无需配置环境变量。" -ForegroundColor Green
}
finally {
    Remove-Item Env:PCL_WRITE_SECRET -ErrorAction SilentlyContinue
    Remove-Item Env:PCL_MS_CLIENT_ID -ErrorAction SilentlyContinue
    Pop-Location
}
