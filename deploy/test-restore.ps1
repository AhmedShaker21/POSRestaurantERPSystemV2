<#
    test-restore.ps1  —  اختبار الاستعادة (شغّله شهريًا على الجهاز التاني)
    --------------------------------------------------------------------
    بيستعيد أحدث نسخة في قاعدة بيانات مؤقتة اسمها RestaurantERP_RestoreTest
    وبيعد الصفوف في أهم الجداول، وبعدين بيمسحها.

    ده الفرق بين "عندي نسخ احتياطية" و"عندي نسخ احتياطية بتشتغل".
    لازم يكون على الجهاز التاني عنده SQL Server Express (مجاني) — مش لازم
    يكون نفس إصدار السيرفر، بس ميكونش أقدم منه.

    Task Scheduler: شهريًا، أول جمعة مثلاً.
#>

$ErrorActionPreference = 'Stop'

$LocalSql   = 'localhost\SQLEXPRESS'      # SQL على جهازك التاني
$BackupDir  = 'D:\POS-Backups'
$TestDb     = 'RestaurantERP_RestoreTest'
$RestorePath= 'D:\POS-Backups\restore-test'
$LogFile    = 'D:\POS-Backups\restore-test.log'

function Write-Log([string]$msg, [string]$level = 'INFO') {
    $line = "{0} [{1}] {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $level, $msg
    Add-Content -Path $LogFile -Value $line
    Write-Host $line
}

try {
    New-Item -ItemType Directory -Force -Path $RestorePath, (Split-Path $LogFile) | Out-Null

    $latest = Get-ChildItem "$BackupDir\*.bak" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) { throw "مفيش نسخ احتياطية في $BackupDir" }

    $ageDays = [math]::Round(((Get-Date) - $latest.LastWriteTime).TotalDays, 1)
    Write-Log "اختبار استعادة: $($latest.Name) (عمرها $ageDays يوم)"
    if ($ageDays -gt 2) { Write-Log "النسخة قديمة أكتر من يومين — راجع مهمة النسخ" 'WARN' }

    # ── أسماء الملفات المنطقية جوه النسخة ────────────────────────────
    $files = Invoke-Sqlcmd -ServerInstance $LocalSql -Query `
        "RESTORE FILELISTONLY FROM DISK = N'$($latest.FullName)';"

    $moves = ($files | ForEach-Object {
        $ext = if ($_.Type -eq 'L') { 'ldf' } else { 'mdf' }
        "MOVE N'$($_.LogicalName)' TO N'$RestorePath\$TestDb`_$($_.LogicalName).$ext'"
    }) -join ",`n     "

    # ── الاستعادة ────────────────────────────────────────────────────
    Invoke-Sqlcmd -ServerInstance $LocalSql -QueryTimeout 0 -Query @"
IF DB_ID('$TestDb') IS NOT NULL
BEGIN
    ALTER DATABASE [$TestDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$TestDb];
END
RESTORE DATABASE [$TestDb]
FROM DISK = N'$($latest.FullName)'
WITH $moves,
     REPLACE, RECOVERY, STATS = 10;
"@
    Write-Log "تمت الاستعادة بنجاح"

    # ── فحص المحتوى: نسخة فاضية بتستعيد تمام لكنها مش نافعة ──────────
    $counts = Invoke-Sqlcmd -ServerInstance $LocalSql -Database $TestDb -Query @"
SELECT
  (SELECT COUNT(*) FROM Orders)          AS Orders,
  (SELECT COUNT(*) FROM OrderItems)      AS OrderItems,
  (SELECT COUNT(*) FROM Products)        AS Products,
  (SELECT COUNT(*) FROM AspNetUsers)     AS Users,
  (SELECT MAX(CreatedAt) FROM Orders)    AS LastOrder;
"@

    Write-Log ("المحتوى → طلبات: {0} | أصناف مباعة: {1} | منتجات: {2} | مستخدمين: {3} | آخر طلب: {4}" -f `
        $counts.Orders, $counts.OrderItems, $counts.Products, $counts.Users, $counts.LastOrder)

    if ($counts.Products -eq 0 -or $counts.Users -eq 0) {
        throw "الاستعادة نجحت لكن القاعدة فاضية — النسخة مش سليمة!"
    }

    # ── تنظيف ────────────────────────────────────────────────────────
    Invoke-Sqlcmd -ServerInstance $LocalSql -Query @"
ALTER DATABASE [$TestDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [$TestDb];
"@
    Remove-Item "$RestorePath\*" -Force -ErrorAction SilentlyContinue

    Write-Log "✅ اختبار الاستعادة نجح — النسخ الاحتياطية موثوقة"
    exit 0
}
catch {
    Write-Log "❌ فشل اختبار الاستعادة: $($_.Exception.Message)" 'ERROR'
    Write-Log "معناها إن نسخك الاحتياطية مش مضمونة — حل المشكلة دلوقتي" 'ERROR'
    exit 1
}
