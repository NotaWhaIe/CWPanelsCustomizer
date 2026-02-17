# revit-reload.ps1
# Находит окно Revit, переключается на него, откатывает изменения и запускает плагин

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
}
"@

Add-Type -AssemblyName System.Windows.Forms

$revitProcess = Get-Process -Name "Revit" -ErrorAction SilentlyContinue

if ($null -eq $revitProcess) {
    Write-Error "Revit не запущен — пропускаем перезапуск плагина"
    exit 1
}

$hwnd = $revitProcess.MainWindowHandle

if ($hwnd -eq [IntPtr]::Zero) {
    Write-Error "Не удалось получить дескриптор главного окна Revit"
    exit 1
}

# Восстановить окно если свёрнуто (SW_RESTORE = 9)
if ([Win32]::IsIconic($hwnd)) {
    [Win32]::ShowWindow($hwnd, 9) | Out-Null
    Start-Sleep -Milliseconds 300
}

[Win32]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500

# Запуск плагина через keyboard shortcut "02"
# Плагин сам удаляет предыдущие экземпляры перед размещением новых
[System.Windows.Forms.SendKeys]::SendWait("0")
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait("2")

Write-Host "Revit: откат выполнен, плагин запущен"
exit 0
