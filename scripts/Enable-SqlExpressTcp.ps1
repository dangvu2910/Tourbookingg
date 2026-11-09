param([int]$Port = 14335)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$log = Join-Path $root 'artifacts/sqlexpress-network-setup.log'
try {
    $instanceMap = Get-ItemProperty 'HKLM:/SOFTWARE/Microsoft/Microsoft SQL Server/Instance Names/SQL'
    $instanceId = $instanceMap.SQLEXPRESS
    if (-not $instanceId) { throw 'SQLEXPRESS instance is missing.' }
    $server = "HKLM:/SOFTWARE/Microsoft/Microsoft SQL Server/$instanceId/MSSQLServer"
    $tcp = "$server/SuperSocketNetLib/Tcp"
    $old = @{ LoginMode=(Get-ItemProperty $server).LoginMode; Tcp=(Get-ItemProperty $tcp).Enabled;
        TcpPort=(Get-ItemProperty "$tcp/IPAll").TcpPort; Dynamic=(Get-ItemProperty "$tcp/IPAll").TcpDynamicPorts }
    $old | ConvertTo-Json | Set-Content (Join-Path $root 'artifacts/sqlexpress-network-before.json')
    Set-ItemProperty $server -Name LoginMode -Value 2
    Set-ItemProperty $tcp -Name Enabled -Value 1
    Set-ItemProperty "$tcp/IPAll" -Name TcpDynamicPorts -Value ''
    Set-ItemProperty "$tcp/IPAll" -Name TcpPort -Value ([string]$Port)
    if (-not (Get-NetFirewallRule -Name 'Tourbooking-SQLExpress-Docker' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -Name 'Tourbooking-SQLExpress-Docker' -DisplayName 'Tourbooking SQL Express Docker local access' `
            -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Any -RemoteAddress LocalSubnet | Out-Null
    }
    Restart-Service -Name 'MSSQL$SQLEXPRESS' -Force
    (Get-Service -Name 'MSSQL$SQLEXPRESS').WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    'SQLEXPRESS TCP and mixed authentication enabled; service restarted.' | Set-Content $log
} catch {
    ('SQLExpress setup failed: ' + $_.Exception.Message) | Set-Content $log
    exit 1
}
