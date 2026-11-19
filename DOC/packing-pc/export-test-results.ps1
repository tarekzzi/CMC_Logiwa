# Exports what the machine test did (read-only) so Claude can check it. Run it ON THE PACKING PC after the test.
#   powershell -ExecutionPolicy Bypass -File ".\export-test-results.ps1"
# Writes test-results.txt next to this script. The label text itself is NOT exported (only its length).
param(
    [string]$Server   = '',
    [string]$Database = 'Local_CMC_Logiwa_DEV',
    [string]$Mode     = ''
)
$ErrorActionPreference = 'Stop'
$outDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }

if ([string]::IsNullOrWhiteSpace($Server)) {
    $Server = Read-Host 'SQL Server name (press Enter for CMC_CONTROL\CMC_CONTROL)'
    if ([string]::IsNullOrWhiteSpace($Server)) { $Server = 'CMC_CONTROL\CMC_CONTROL' }
}
if ([string]::IsNullOrWhiteSpace($Mode)) { $Mode = Read-Host 'Log in with Windows account (W) or SQL login (S)? [W/S]' }

$csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$csb['Data Source'] = $Server
$csb['Initial Catalog'] = $Database
$csb['Connect Timeout'] = 15
if ($Mode -match '^[sS]') {
    $csb['User ID'] = Read-Host 'SQL login name'
    $sec = Read-Host 'Password' -AsSecureString
    $csb['Password'] = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
} else {
    $csb['Integrated Security'] = $true
}

$conn = New-Object System.Data.SqlClient.SqlConnection $csb.ConnectionString
$conn.Open()

function Get-Table($sql) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql
    $t = New-Object System.Data.DataTable; $t.Load($cmd.ExecuteReader()); return ,$t
}
function Show($title, $table) {
    "=== $title ($($table.Rows.Count) rows)"
    if ($table.Rows.Count -gt 0) {
        # one line per row, every column, ';' separated (a wide table is not cut off this way)
        $cols = @($table.Columns | ForEach-Object { $_.ColumnName })
        (($table.Rows | Select-Object -Property $cols | ConvertTo-Csv -NoTypeInformation -Delimiter ';') -join "`r`n")
    } else { "(none)" }
    ""
}

$report = New-Object System.Collections.Generic.List[string]
$report.Add("CMC_Logiwa test results, exported " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + " from $Server / $Database")
$report.Add("")

$report.Add((Show 'PackJob (all rows, label text left out)' (Get-Table @"
SELECT PackJobID, OrderNumber, OrderStatus, PackID, MachineId, ProcessedByUser, JobCreatedDateTime AS Created,
       ProcessStartedDateTime AS Started, ProcessCompletedDateTime AS Completed,
       BoxEventID, BoxEventText, HasBoxIssue, BoxIssueComment,
       BoxSizeH, BoxSizeL, BoxSizeW, BoxArea, WeightCarton, WeightScale,
       LabelDataLen, IsCurrent, ResetFromPackJobID, PushStatus, PushAttempts
FROM dbo.PackJob ORDER BY PackJobID
"@)) -join "`n")

$report.Add((Show 'PackJobEvent (history, newest last)' (Get-Table @"
SELECT EventId, EventAt, PackJobID, OrderNumber, EventType, MachineId, UserName, Detail
FROM dbo.PackJobEvent ORDER BY EventId
"@)) -join "`n")

$report.Add((Show 'LabelRequest' (Get-Table "SELECT RequestId, OrderNumber, MachineId, RequestedAt, Status, Attempts, LastError FROM dbo.LabelRequest ORDER BY RequestId")) -join "`n")
$report.Add((Show 'Outbox (results waiting to be sent)' (Get-Table "EXEC dbo.sp_Outbox_GetNext 50")) -join "`n")
$report.Add((Show 'Users (names only)' (Get-Table "SELECT Username, IsActive, CreatedAt, LastLoginAt FROM dbo.Users ORDER BY UserId")) -join "`n")

$conn.Close()

$file = Join-Path $outDir 'test-results.txt'
[IO.File]::WriteAllText($file, ($report -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ""
Write-Host "Done. Written: $file"
Write-Host "Copy that file back (USB) to the dev PC DOC folder and tell Claude."
