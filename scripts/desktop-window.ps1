param(
    [Parameter(Mandatory)][int]$ProcessId,
    [ValidateSet('State', 'Close', 'DeclineClose', 'SaveExport')][string]$Action = 'State',
    [string]$Destination
)
$ErrorActionPreference = 'Stop'
$appProcess = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
if (-not $appProcess) { '{"exists":false}'; exit 0 }
if ($Action -eq 'State') {
    @{ exists = $true; handle = $appProcess.MainWindowHandle.ToInt64(); title = $appProcess.MainWindowTitle } | ConvertTo-Json -Compress
} elseif ($Action -eq 'Close') {
    @{ requested = $appProcess.CloseMainWindow() } | ConvertTo-Json -Compress
} else {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DesktopCheckWindow {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] public static extern IntPtr SendText(IntPtr window, uint message, IntPtr wParam, string text);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@
    $dialogTitle = [string]::Concat([char]0x9000, [char]0x51FA, ' AxisLab')
    if ($Action -eq 'SaveExport') { $dialogTitle = -join [char[]]@(0x5BFC, 0x51FA, 0x6587, 0x4EF6) }
    $dialog = [DesktopCheckWindow]::FindWindow('#32770', $dialogTitle)
    [uint32]$dialogProcess = 0
    if ($dialog -ne [IntPtr]::Zero) { [void][DesktopCheckWindow]::GetWindowThreadProcessId($dialog, [ref]$dialogProcess) }
    $found = $dialog -ne [IntPtr]::Zero -and $dialogProcess -eq $ProcessId
    if ($found -and $Action -eq 'SaveExport') {
        if (-not $Destination) { throw 'Destination is required.' }
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $window = [System.Windows.Automation.AutomationElement]::FromHandle($dialog)
        $hostControl = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'FileNameControlHost'))
        if (-not $hostControl) { throw 'File name host was not found.' }
        $fileName = $hostControl.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1001'))
        if (-not $fileName -or -not $fileName.Current.NativeWindowHandle) { throw 'Native file name edit was not found.' }
        [void][DesktopCheckWindow]::SendText([IntPtr]$fileName.Current.NativeWindowHandle, 0x000C, [IntPtr]::Zero, $Destination)
        [void][DesktopCheckWindow]::PostMessage($dialog, 0x0111, [IntPtr]1, [IntPtr]::Zero)
    } elseif ($found) { [void][DesktopCheckWindow]::SendMessage($dialog, 0x0111, [IntPtr]7, [IntPtr]::Zero) }
    @{ found = $found } | ConvertTo-Json -Compress
}
