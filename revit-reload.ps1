# revit-reload.ps1
# Находит окно Revit, переключается на него, откатывает изменения и запускает плагин

$logFile = Join-Path $PSScriptRoot "logs\revit-reload.log"
$logDir = Split-Path $logFile
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

function Log($msg) {
    $ts = Get-Date -Format "HH:mm:ss.fff"
    $line = "$ts $msg"
    Write-Host $line
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

Log "=== revit-reload START ==="

Add-Type @"
using System;
using System.Runtime.InteropServices;

public class Win32 {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
}
"@

Add-Type -AssemblyName System.Windows.Forms

# Найти процесс Revit
$revitProcess = Get-Process -Name "Revit" -ErrorAction SilentlyContinue

if ($null -eq $revitProcess) {
    Log "FAIL: Revit process not found"
    exit 1
}

Log "Revit PID=$($revitProcess.Id)"

$hwnd = $revitProcess.MainWindowHandle
Log "Revit MainWindowHandle=$hwnd"

if ($hwnd -eq [IntPtr]::Zero) {
    Log "FAIL: MainWindowHandle is Zero"
    exit 1
}

# Заголовок окна Revit
$sb = New-Object System.Text.StringBuilder 256
[Win32]::GetWindowText($hwnd, $sb, 256) | Out-Null
Log "Revit window title: $($sb.ToString())"

# Текущее окно на переднем плане
$fgBefore = [Win32]::GetForegroundWindow()
$sbFg = New-Object System.Text.StringBuilder 256
[Win32]::GetWindowText($fgBefore, $sbFg, 256) | Out-Null
Log "Foreground BEFORE: hwnd=$fgBefore title='$($sbFg.ToString())'"

# Восстановить если свёрнуто
if ([Win32]::IsIconic($hwnd)) {
    Log "Window is minimized, restoring (SW_RESTORE=9)"
    [Win32]::ShowWindow($hwnd, 9) | Out-Null
    Start-Sleep -Milliseconds 500
} else {
    Log "Window is not minimized"
}

# Метод 1: AttachThreadInput + BringWindowToTop + SetForegroundWindow
Log "Method 1: AttachThreadInput approach"
$fgHwnd = [Win32]::GetForegroundWindow()
$dummy = [uint32]0
$fgThread = [Win32]::GetWindowThreadProcessId($fgHwnd, [ref]$dummy)
$curThread = [Win32]::GetCurrentThreadId()
Log "  fgThread=$fgThread curThread=$curThread"

$attached = [Win32]::AttachThreadInput($curThread, $fgThread, $true)
Log "  AttachThreadInput(attach)=$attached"

$bwt = [Win32]::BringWindowToTop($hwnd)
Log "  BringWindowToTop=$bwt"

$sfw1 = [Win32]::SetForegroundWindow($hwnd)
Log "  SetForegroundWindow=$sfw1"

[Win32]::AttachThreadInput($curThread, $fgThread, $false) | Out-Null
Start-Sleep -Milliseconds 500

# Проверяем
$fgAfter1 = [Win32]::GetForegroundWindow()
Log "  Foreground after method1: hwnd=$fgAfter1 (match=$(($fgAfter1 -eq $hwnd)))"

# Метод 2: Alt-трюк + SetForegroundWindow
if ($fgAfter1 -ne $hwnd) {
    Log "Method 2: Alt-key trick"
    [Win32]::keybd_event(0x12, 0, 0, 0)
    Start-Sleep -Milliseconds 50
    [Win32]::keybd_event(0x12, 0, 2, 0)
    Start-Sleep -Milliseconds 50
    $sfw2 = [Win32]::SetForegroundWindow($hwnd)
    Log "  SetForegroundWindow=$sfw2"
    Start-Sleep -Milliseconds 500

    $fgAfter2 = [Win32]::GetForegroundWindow()
    Log "  Foreground after method2: hwnd=$fgAfter2 (match=$(($fgAfter2 -eq $hwnd)))"
}

# Метод 3: WScript.Shell AppActivate
$fgNow = [Win32]::GetForegroundWindow()
if ($fgNow -ne $hwnd) {
    Log "Method 3: WScript.Shell AppActivate"
    $wsh = New-Object -ComObject Wscript.Shell
    $result = $wsh.AppActivate($revitProcess.Id)
    Log "  AppActivate=$result"
    Start-Sleep -Milliseconds 500

    $fgAfter3 = [Win32]::GetForegroundWindow()
    Log "  Foreground after method3: hwnd=$fgAfter3 (match=$(($fgAfter3 -eq $hwnd)))"
}

# Финальная проверка
$fgFinal = [Win32]::GetForegroundWindow()
$sbFinal = New-Object System.Text.StringBuilder 256
[Win32]::GetWindowText($fgFinal, $sbFinal, 256) | Out-Null
Log "Foreground FINAL: hwnd=$fgFinal title='$($sbFinal.ToString())' isRevit=$(($fgFinal -eq $hwnd))"

if ($fgFinal -ne $hwnd) {
    Log "WARNING: Could not switch focus to Revit! Sending keys anyway..."
}

# Отправить шорткат "02"
Log "Sending keys: 0, 2"
[System.Windows.Forms.SendKeys]::SendWait("0")
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait("2")
Start-Sleep -Milliseconds 500

# Проверка после отправки
$fgEnd = [Win32]::GetForegroundWindow()
$sbEnd = New-Object System.Text.StringBuilder 256
[Win32]::GetWindowText($fgEnd, $sbEnd, 256) | Out-Null
Log "Foreground AFTER keys: hwnd=$fgEnd title='$($sbEnd.ToString())'"
Log "=== revit-reload END ==="

Write-Host "Revit: откат выполнен, плагин запущен"
exit 0
