# Runs one .sql file (batches separated by GO) on a SQL Server. Used for small database updates.
# Run in PowerShell in this folder:
#   & ([scriptblock]::Create(((Get-Content -Raw .\run-sql-file.ps1) -replace 'Split-Path.*Path','(Get-Location).Path')))
# or:  powershell -ExecutionPolicy Bypass -File ".\run-sql-file.ps1"
# It asks for the server and your admin login. It runs update-1-machine-procs.sql by default.
param(
    [string]$Server = '',
    [string]$Mode = '',
    [string]$File = 'update-1-machine-procs.sql'
)
$ErrorActionPreference = 'Stop'
$dir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$path = if ([IO.Path]::IsPathRooted($File)) { $File } else { Join-Path $dir $File }
if (-not (Test-Path $path)) { throw "File not found: $path" }

if ([string]::IsNullOrWhiteSpace($Server)) {
    $Server = Read-Host 'SQL Server name (press Enter for CMC_CONTROL\CMC_CONTROL)'
    if ([string]::IsNullOrWhiteSpace($Server)) { $Server = 'CMC_CONTROL\CMC_CONTROL' }
}
if ([string]::IsNullOrWhiteSpace($Mode)) { $Mode = Read-Host 'Log in as admin with your Windows account (W) or a SQL login (S)? [W/S]' }

$csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$csb['Data Source'] = $Server
$csb['Initial Catalog'] = 'master'
$csb['Connect Timeout'] = 15
if ($Mode -match '^[sS]') {
    $csb['User ID'] = Read-Host 'Admin SQL login name'
    $sec = Read-Host 'Admin password' -AsSecureString
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    $csb['Password'] = [Runtime.InteropServices.Marshal]::PtrToStringAuto($b)
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b)
} else {
    $csb['Integrated Security'] = $true
}

$sql = [IO.File]::ReadAllText($path)
$conn = New-Object System.Data.SqlClient.SqlConnection $csb.ConnectionString
$conn.add_InfoMessage({ param($s, $e) if ($e.Message) { Write-Host ("  " + $e.Message) } })
Write-Host "Connecting to $Server ..."
$conn.Open()
Write-Host "Running $([IO.Path]::GetFileName($path)) ..."

$batches = [regex]::Split($sql, '(?im)^\s*GO\s*$') | Where-Object { $_.Trim().Length -gt 0 }
$n = 0
foreach ($b in $batches) {
    $n++
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $b; $cmd.CommandTimeout = 120
    try { [void]$cmd.ExecuteNonQuery() }
    catch {
        Write-Host "STOPPED at step $n of $($batches.Count): $($_.Exception.Message)" -ForegroundColor Red
        $conn.Close(); exit 1
    }
}
$conn.Close()
Write-Host "Done. $($batches.Count) steps ran without errors." -ForegroundColor Green
