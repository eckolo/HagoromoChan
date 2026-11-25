[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateSet('Enable','Disable','Status')][string]$Action)
. (Join-Path $PSScriptRoot 'Runtime.ps1')
# A Windows login shortcut must not depend on a transient PowerShell environment.
$root = Get-HagoromoRoot
$defaultRoot = Join-Path $env:LOCALAPPDATA 'HagoromoChan'
if (-not [string]::Equals([IO.Path]::GetFullPath($root), [IO.Path]::GetFullPath($defaultRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Startup registration is supported only for the normal per-user data directory.'
}
$launcher = Get-HagoromoLauncher $defaultRoot
$startup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$link = Join-Path $startup 'HagoromoChan.lnk'
$shell = New-Object -ComObject WScript.Shell
try {
    if (Test-Path -LiteralPath $link) {
        $shortcut = $shell.CreateShortcut($link)
        if (-not [string]::Equals($shortcut.TargetPath, $launcher, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::IsNullOrEmpty($shortcut.Arguments)) {
            throw 'Existing HagoromoChan.lnk has a different target or arguments. It was not modified.'
        }
    }
    switch ($Action) {
        Enable {
            if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw 'Run Update.ps1 before registering startup.' }
            $shortcut = $shell.CreateShortcut($link)
            $shortcut.TargetPath = $launcher
            $shortcut.WorkingDirectory = Split-Path $launcher -Parent
            $shortcut.Arguments = ''
            $shortcut.Description = 'HagoromoChan desktop application'
            $shortcut.Save()
            Write-Host "Startup shortcut registered: $link"
        }
        Disable {
            if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link }
            Write-Host 'Startup shortcut removed.'
        }
        Status {
            Write-Output "Startup shortcut exists: $(Test-Path -LiteralPath $link)"
            Write-Host 'Windows Startup Apps may also disable a registered shortcut.'
        }
    }
} finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
