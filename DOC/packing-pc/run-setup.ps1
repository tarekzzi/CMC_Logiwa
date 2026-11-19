# Creates the CMC_Logiwa DEV database, the login CMC_Logiwa_app_dev, all tables/procedures, event codes and the 3 test cartons.
# Run ON THE CMC (PACKING) PC, as a Windows user that is sysadmin on the SQL Server (or with a sysadmin SQL login):
#   powershell -ExecutionPolicy Bypass -File ".\run-setup.ps1"
# It asks for the password to give the new login (masked, typed twice). Nothing is saved to disk with the password.
# It only creates NEW things (database Local_CMC_Logiwa_DEV, login CMC_Logiwa_app_dev). It never touches the V1 database (CMC).
# Safe to run again.
param(
    [string]$Server = '',
    [string]$Mode = '',
    [System.Security.SecureString]$AppPassword = $null
)
$ErrorActionPreference = 'Stop'
$dir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$sqlFile = Join-Path $dir 'packing-pc-dev-setup.sql'
if (-not (Test-Path $sqlFile)) { throw "packing-pc-dev-setup.sql must be in the same folder as this script ($dir)." }

function ToPlain([System.Security.SecureString]$s) {
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringAuto($b) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

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
    $csb['Password'] = ToPlain (Read-Host 'Admin password' -AsSecureString)
} else {
    $csb['Integrated Security'] = $true
}

if ($null -eq $AppPassword) {
    Write-Host ''
    Write-Host 'Choose the password for the new SQL login CMC_Logiwa_app_dev (12+ characters with upper and lower case, a digit and a symbol).'
    Write-Host 'You will type it again on the app Database settings screen, so remember it.'
    $p1 = Read-Host 'New password' -AsSecureString
    $p2 = Read-Host 'Repeat it' -AsSecureString
    if ((ToPlain $p1) -ne (ToPlain $p2)) { throw 'The two passwords are not the same. Run the script again.' }
    $AppPassword = $p1
}
$pwd = ToPlain $AppPassword
if ($pwd.Length -lt 12 -or $pwd -cnotmatch '[a-z]' -or $pwd -cnotmatch '[A-Z]' -or $pwd -notmatch '\d' -or $pwd -notmatch '[^A-Za-z0-9]') {
    throw 'The password must be at least 12 characters with upper and lower case letters, a digit and a symbol.'
}

# put the password into the script text in memory only (the DECLARE line), never on disk
$sql = [IO.File]::ReadAllText($sqlFile)
$token = "N'CHANGE_ME_BEFORE_RUNNING';"
if (-not $sql.Contains($token)) { throw 'The setup script has no password placeholder (was it edited?).' }
$sql = $sql.Replace($token, "N'" + $pwd.Replace("'", "''") + "';")

$conn = New-Object System.Data.SqlClient.SqlConnection $csb.ConnectionString
$conn.add_InfoMessage({ param($s, $e) if ($e.Message) { Write-Host ("  " + $e.Message) } })
$conn.FireInfoMessageEventOnUserErrors = $false
Write-Host ""
Write-Host "Connecting to $Server ..."
$conn.Open()
Write-Host "Connected. Running the setup (this takes a few seconds)..."

$batches = [regex]::Split($sql, '(?im)^\s*GO\s*$') | Where-Object { $_.Trim().Length -gt 0 }
$n = 0
foreach ($b in $batches) {
    $n++
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $b
    $cmd.CommandTimeout = 120
    try {
        $r = $cmd.ExecuteReader()
        # show the final listing of test cartons (the only batch that returns rows)
        while ($r.Read()) {
            $vals = @(); for ($i = 0; $i -lt $r.FieldCount; $i++) { $vals += [string]$r.GetValue($i) }
            Write-Host ("  " + ($vals -join '   '))
        }
        $r.Close()
    } catch {
        Write-Host ""
        Write-Host "STOPPED at step $n of $($batches.Count): $($_.Exception.Message)" -ForegroundColor Red
        $conn.Close()
        exit 1
    }
}
$conn.Close()

# verify, and test the new login the way the app will use it
Write-Host ""
Write-Host "Checking the result..."
$vc = New-Object System.Data.SqlClient.SqlConnection $csb.ConnectionString
$vc.Open(); $vc.ChangeDatabase('Local_CMC_Logiwa_DEV')
$c = $vc.CreateCommand()
$c.CommandText = "SELECT (SELECT COUNT(*) FROM sys.tables), (SELECT COUNT(*) FROM sys.procedures), (SELECT COUNT(*) FROM dbo.Events), (SELECT COUNT(*) FROM dbo.PackJob)"
$r = $c.ExecuteReader(); [void]$r.Read()
Write-Host ("  tables=" + $r[0] + " procedures=" + $r[1] + " event codes=" + $r[2] + " cartons=" + $r[3] + "   (expected 8 / 27 / 33 / 3)")
$r.Close(); $vc.Close()

$tc = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$tc['Data Source'] = $Server; $tc['Initial Catalog'] = 'Local_CMC_Logiwa_DEV'
$tc['User ID'] = 'CMC_Logiwa_app_dev'; $tc['Password'] = $pwd; $tc['Connect Timeout'] = 15
try {
    $t = New-Object System.Data.SqlClient.SqlConnection $tc.ConnectionString
    $t.Open()
    $q = $t.CreateCommand(); $q.CommandText = "SELECT HAS_PERMS_BY_NAME('dbo.sp_ENQ_Get_Data','OBJECT','EXECUTE')"
    $ok = $q.ExecuteScalar(); $t.Close()
    if ($ok -eq 1) { Write-Host "  The login CMC_Logiwa_app_dev can connect and run the procedures. All good." -ForegroundColor Green }
    else { Write-Host "  The login connected but cannot execute the procedures." -ForegroundColor Yellow }
} catch {
    Write-Host ("  The login CMC_Logiwa_app_dev could not connect: " + $_.Exception.Message) -ForegroundColor Yellow
    Write-Host "  If the message says the login failed, the SQL Server may allow Windows logins only: server Properties > Security >"
    Write-Host "  'SQL Server and Windows Authentication mode', restart the SQL Server service, then run this script again."
}
Write-Host ""
Write-Host "Done. Next: unzip CMC_Logiwa_app.zip and run CMC_Logiwa.exe (see SETUP-STEPS.txt, step 2)."
Write-Host "Server for the app: $Server   Database: Local_CMC_Logiwa_DEV   Login: CMC_Logiwa_app_dev"
