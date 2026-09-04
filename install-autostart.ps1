# Keel Assistant 開機自動啟動安裝腳本（工作排程器，登入後延遲 30 秒啟動）
# 作者：Arlen
# 用法：在本資料夾按 ! 或於 PowerShell 執行： .\install-autostart.ps1
#       發佈正式版後，把 $exe 改成發佈目錄的 KeelAssistant.exe

$ErrorActionPreference = 'Stop'
$taskName = 'KeelAssistant'
# 指向 publish 版（自含 .NET 單檔）。若你改放別處，改這行即可。
$exe = 'C:\Users\arlen\stress_Memory\KeelAssistant-publish\KeelAssistant.exe'

if (-not (Test-Path $exe)) {
    Write-Error "找不到執行檔：$exe（請先 dotnet build 或改成發佈路徑）"
    return
}

$action   = New-ScheduledTaskAction -Execute $exe
$trigger  = New-ScheduledTaskTrigger -AtLogOn
$trigger.Delay = 'PT30S'   # 登入後延遲 30 秒，避開開機尖峰
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Settings $settings -Description 'Keel Assistant 系統匣常駐（鬧鐘/音樂/Claude用量）' -Force | Out-Null

Write-Host "已註冊開機自動啟動：$taskName"
Write-Host "移除： Unregister-ScheduledTask -TaskName $taskName -Confirm:`$false"
