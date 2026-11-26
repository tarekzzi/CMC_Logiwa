<#
Installs or updates the CMC_Logiwa local database for DEV or PRO from ONE set of scripts (the *_dev.sql files in this folder).
The database and login names are changed on the fly (Local_CMC_Logiwa_DEV -> Local_CMC_Logiwa_PRO, CMC_Logiwa_app_dev -> CMC_Logiwa_app_pro),
so DEV and PRO can never drift apart. Run it on the packing PC (or any PC that can reach the SQL instance) as a SQL sysadmin.

  powershell -ExecutionPolicy Bypass -File .\Install-LocalDb.ps1 -Environment PRO
  powershell -ExecutionPolicy Bypass -File .\Install-LocalDb.ps1 -Environment PRO -Auth Sql -AdminLogin sa
  powershell -ExecutionPolicy Bypass -File .\Install-LocalDb.ps1 -Environment PRO -ScriptsOnly      (only writes the PRO scripts, for SSMS)
  powershell -ExecutionPolicy Bypass -File .\Install-LocalDb.ps1 -Environment PRO -VerifyOnly       (changes nothing, just checks)

What it does, in this order (every script is safe to re-run; nothing is deleted):
  001 schema (creates the database), 000 app login (asks for the password, never written to disk), 002 .. 011 procedures and tables,
  events-data.sql (machine event texts, only when the Events table is empty), then a check that every procedure exists and the app login can run them.
A new script (009, ...) only has to be added to $Scripts below.
#>
param(
    [Parameter(Mandatory)] [ValidateSet('DEV', 'PRO')] [string]$Environment,
    [string]$Server = 'CMC_CONTROL\CMC_CONTROL',
    [ValidateSet('Windows', 'Sql')] [string]$Auth = 'Windows',
    [string]$AdminLogin = '',
    [switch]$ScriptsOnly,
    [switch]$VerifyOnly,
    [switch]$SkipEvents
)
$ErrorActionPreference = 'Stop'
$dir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }

$envLower = $Environment.ToLower()
$db       = "Local_CMC_Logiwa_$Environment"
$login    = "CMC_Logiwa_app_$envLower"

# The order matters: the login script needs the database that 001 creates.
$Scripts = @(
    '001_schema_dev.sql',
    '000_create_app_login_dev.sql',
    '002_procs_dev.sql',
    '003_procs_machine_dev.sql',
    '004_procs_screens_dev.sql',
    '005_procs_dashboard_dev.sql',
    '006_machine_settings_dev.sql',
    '007_shipped_check_dev.sql',
    '008_machine_warehouse_dev.sql',
    '009_user_roles_dev.sql',
    '010_single_sku_dev.sql',
    '011_date_fields_dev.sql'
)
$EventsFile = 'events-data.sql'
$Placeholder = 'CHANGE_ME_BEFORE_RUNNING'

function ConvertTo-Environment([string]$text) {
    $t = $text -replace 'Local_CMC_Logiwa_DEV', $db -replace 'CMC_Logiwa_app_dev', $login -replace '\(DEV\)', "($Environment)"
    if ($Environment -ne 'DEV' -and $t -match 'Local_CMC_Logiwa_DEV|CMC_Logiwa_app_dev') {
        throw "A DEV name is still in the converted script: the installer would touch the DEV database. Stopped."
    }
    return $t
}

function Read-Source([string]$name) {
    $path = Join-Path $dir $name
    if (-not (Test-Path $path)) { throw "Script not found: $path" }
    return [System.IO.File]::ReadAllText($path)
}

function Split-Batches([string]$text) {
    return [regex]::Split($text, '(?im)^\s*GO\s*$') | Where-Object { $_.Trim().Length -gt 0 }
}

function ConvertTo-Plain([System.Security.SecureString]$s) {
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringAuto($b) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

# ---------- only write the scripts (for SSMS) ----------
if ($ScriptsOnly) {
    $out = Join-Path $dir "generated\$Environment"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    foreach ($name in ($Scripts + $EventsFile)) {
        $text = ConvertTo-Environment (Read-Source $name)
        if ($name -eq $EventsFile) { $text = "USE [$db];`r`nGO`r`n" + $text }
        $target = Join-Path $out ($name -replace '_dev\.sql$', "_$envLower.sql")
        [System.IO.File]::WriteAllText($target, $text, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "written $target"
    }
    Write-Host "Run them in SSMS in this order: 001, 000 (replace $Placeholder with the login password first, do not commit it), 002 .. 009, events-data."
    return
}

# ---------- connect ----------
$csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$csb['Data Source'] = $Server
$csb['Initial Catalog'] = 'master'
$csb['Connect Timeout'] = 15
if ($Auth -eq 'Sql') {
    if ([string]::IsNullOrWhiteSpace($AdminLogin)) { $AdminLogin = Read-Host 'Admin SQL login name' }
    $csb['User ID'] = $AdminLogin
    $csb['Password'] = ConvertTo-Plain (Read-Host 'Admin password' -AsSecureString)
} else {
    $csb['Integrated Security'] = $true
}

$conn = New-Object System.Data.SqlClient.SqlConnection($csb.ConnectionString)
$conn.add_InfoMessage({ param($s, $e) Write-Host ("   " + $e.Message) -ForegroundColor DarkGray })
$conn.FireInfoMessageEventOnUserErrors = $false
$conn.Open()

function Invoke-Sql([string]$sql, [int]$timeout = 120) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $cmd.CommandTimeout = $timeout
    try { return $cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Get-Scalar([string]$sql) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    try { return $cmd.ExecuteScalar() } finally { $cmd.Dispose() }
}

function Invoke-Script([string]$label, [string]$text) {
    Write-Host "== $label" -ForegroundColor Cyan
    $n = 0
    foreach ($batch in (Split-Batches $text)) {
        $n++
        try { [void](Invoke-Sql $batch) }
        catch {
            $preview = ($batch.Trim() -split "`n" | Select-Object -First 3) -join ' / '
            throw "Batch $n of $label failed: $($_.Exception.Message)`n  near: $preview"
        }
    }
    Write-Host "   $n batch(es) ok"
}

Write-Host ""
Write-Host "Server      : $Server"
Write-Host "Environment : $Environment"
Write-Host "Database    : $db"
Write-Host "App login   : $login"
Write-Host ""

# ---------- verify only ----------
function Show-Verification {
    Write-Host "== Check" -ForegroundColor Cyan
    if ($null -eq (Get-Scalar "SELECT DB_ID(N'$db')")) { Write-Host "   Database $db does not exist." -ForegroundColor Red; return $false }

    $expected = @()
    foreach ($name in $Scripts) {
        $expected += [regex]::Matches((Read-Source $name), '(?i)CREATE\s+OR\s+ALTER\s+PROCEDURE\s+dbo\.(\w+)') | ForEach-Object { $_.Groups[1].Value }
    }
    $expected = $expected | Sort-Object -Unique

    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT name FROM [$db].sys.procedures"
    $have = @(); $r = $cmd.ExecuteReader(); while ($r.Read()) { $have += $r.GetString(0) }; $r.Close(); $cmd.Dispose()
    $missing = $expected | Where-Object { $have -notcontains $_ }

    $ok = $true
    if ($missing) { Write-Host "   Missing procedures: $($missing -join ', ')" -ForegroundColor Red; $ok = $false }
    else { Write-Host "   All $($expected.Count) procedures exist." -ForegroundColor Green }

    foreach ($t in 'PackJob', 'PackJobArchive', 'PackJobEvent', 'LabelRequest', 'Events', 'Users', 'SyncState', 'MachineSettings', 'MachineSettingsLog') {
        if ($null -eq (Get-Scalar "SELECT OBJECT_ID(N'[$db].dbo.$t', N'U')")) { Write-Host "   Missing table dbo.$t" -ForegroundColor Red; $ok = $false }
    }

    $events = Get-Scalar "SELECT COUNT(*) FROM [$db].dbo.Events"
    Write-Host "   Machine event texts: $events" -ForegroundColor $(if ($events -gt 0) { 'Green' } else { 'Yellow' })

    $canRun = Get-Scalar "USE [$db]; EXECUTE AS USER = N'$login'; SELECT HAS_PERMS_BY_NAME(N'dbo.sp_Dashboard_Counts', N'OBJECT', N'EXECUTE'); REVERT;"
    if ($canRun -eq 1) { Write-Host "   $login can run the procedures." -ForegroundColor Green }
    else { Write-Host "   $login cannot run the procedures (login or permissions missing)." -ForegroundColor Red; $ok = $false }

    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT MachineId, IpAddress, Port, AutoReconnect FROM [$db].dbo.MachineSettings WHERE Id = 1"
    $r = $cmd.ExecuteReader()
    if ($r.Read()) { Write-Host ("   Machine settings: ID {0}, {1}:{2}, auto reconnect {3}  (change them in the app: Settings > Machine)" -f $r[0], $r[1], $r[2], $r[3]) }
    $r.Close(); $cmd.Dispose()
    return $ok
}

if ($VerifyOnly) {
    $result = Show-Verification
    $conn.Close()
    if (-not $result) { exit 1 }
    return
}

# ---------- confirm PRO ----------
if ($Environment -eq 'PRO') {
    Write-Host "This changes the PRODUCTION database $db on $Server." -ForegroundColor Yellow
    $answer = Read-Host "Type PRO to continue"
    if ($answer -ne 'PRO') { Write-Host 'Cancelled.'; $conn.Close(); return }
}

# ---------- run ----------
foreach ($name in $Scripts) {
    $text = ConvertTo-Environment (Read-Source $name)

    if ($name -like '000_*') {
        # The app login password is typed here, kept in memory only and put into the script text just before it runs.
        $exists = Get-Scalar "SELECT SUSER_ID(N'$login')"
        if ($null -ne $exists -and $exists -isnot [DBNull]) {
            Write-Host "   login $login already exists: its password is left as it is"
            $password = 'unused-because-the-login-exists-0000'
        } else {
            while ($true) {
                $p1 = ConvertTo-Plain (Read-Host "Password for the new SQL login $login (at least 12 characters)" -AsSecureString)
                $p2 = ConvertTo-Plain (Read-Host 'Password again' -AsSecureString)
                if ($p1 -ne $p2) { Write-Host 'The two passwords differ.' -ForegroundColor Red; continue }
                if ($p1.Length -lt 12) { Write-Host 'Use at least 12 characters.' -ForegroundColor Red; continue }
                $password = $p1
                break
            }
        }
        if (-not $text.Contains($Placeholder)) { throw "The login script no longer contains the $Placeholder placeholder: stopped." }
        $text = $text.Replace($Placeholder, $password.Replace("'", "''"))
    }

    Invoke-Script $name $text
}

if (-not $SkipEvents) {
    $events = Get-Scalar "SELECT COUNT(*) FROM [$db].dbo.Events"
    if ($events -eq 0) { Invoke-Script $EventsFile ("USE [$db];`r`nGO`r`n" + (ConvertTo-Environment (Read-Source $EventsFile))) }
    else { Write-Host "== $EventsFile skipped: the Events table already has $events row(s)" -ForegroundColor Cyan }
}

$result = Show-Verification
$conn.Close()
Write-Host ""
if ($result) { Write-Host "Done: $db is ready." -ForegroundColor Green } else { Write-Host "Done, but the check found problems (above)." -ForegroundColor Red; exit 1 }
