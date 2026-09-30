<#
    backup-server.ps1  —  يشتغل على السيرفر (الوركستيشن) كل يوم 1:00 صباحًا
    ---------------------------------------------------------------------
    بيعمل:
      1. نسخة احتياطية مضغوطة من قاعدة البيانات
      2. يتحقق إن الملف سليم (RESTORE VERIFYONLY) — نسخة تالفة أسوأ من نسخة مش موجودة
      3. ينسخ مفاتيح التشفير (من غيرها الاستعادة هتخرّج كل الكاشيرات)
      4. يمسح النسخ الأقدم من 30 يوم
      5. يسجل كل حاجة في ملف log

    Task Scheduler:
      Program : powershell.exe
      Arguments: -ExecutionPolicy Bypass -File "C:\POS\deploy\backup-server.ps1"
      Run whether user is logged on or not  +  Run with highest privileges
#>

$ErrorActionPreference = 'Stop'

$Server      = 'localhost'
$Database    = 'RestaurantERP'
$BackupDir   = 'C:\POS\backups'
$KeysDir     = 'C:\POS\keys'
$LogFile     = 'C:\POS\logs\backup.log'
$KeepDays    = 30

function Write-Log([string]$msg, [string]$level = 'INFO') {
    $line = "{0} [{1}] {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $level, $msg
    Add-Content -Path $LogFile -Value $line
    Write-Host $line
}

try {
    New-Item -ItemType Directory -Force -Path $BackupDir, "$BackupDir\keys", (Split-Path $LogFile) | Out-Null

    $stamp = Get-Date -Format 'yyyyMMdd-HHmm'
    $file  = Join-Path $BackupDir "$Database-$stamp.bak"

    Write-Log "بداية النسخ الاحتياطي → $file"

    # ── 1. النسخ ─────────────────────────────────────────────────────
    Invoke-Sqlcmd -ServerInstance $Server -QueryTimeout 0 -Query @"
BACKUP DATABASE [$Database]
TO DISK = N'$file'
WITH FORMAT, INIT, COMPRESSION, CHECKSUM, STATS = 10,
     NAME = N'$Database nightly backup';
"@

    $sizeMb = [math]::Round((Get-Item $file).Length / 1MB, 1)
    Write-Log "تم إنشاء النسخة ($sizeMb MB)"

    # ── 2. التحقق ────────────────────────────────────────────────────
    # لو الملف اتكتب غلط، عايزين نعرف دلوقتي مش يوم الكارثة
    Invoke-Sqlcmd -ServerInstance $Server -QueryTimeout 0 -Query @"
RESTORE VERIFYONLY FROM DISK = N'$file' WITH CHECKSUM;
"@
    Write-Log "تم التحقق من سلامة النسخة"

    # ── 3. مفاتيح التشفير ────────────────────────────────────────────
    if (Test-Path $KeysDir) {
        Copy-Item "$KeysDir\*.xml" "$BackupDir\keys\" -Force -ErrorAction SilentlyContinue
        Write-Log "تم نسخ مفاتيح التشفير"
    } else {
        Write-Log "مجلد المفاتيح غير موجود: $KeysDir" 'WARN'
    }

    # ── 4. تنظيف القديم ──────────────────────────────────────────────
    $cutoff  = (Get-Date).AddDays(-$KeepDays)
    $removed = Get-ChildItem "$BackupDir\*.bak" | Where-Object { $_.CreationTime -lt $cutoff }
    $removed | Remove-Item -Force
    if ($removed) { Write-Log "تم حذف $($removed.Count) نسخة أقدم من $KeepDays يوم" }

    $total = (Get-ChildItem "$BackupDir\*.bak" | Measure-Object).Count
    Write-Log "اكتمل بنجاح — إجمالي النسخ المحفوظة: $total"
    exit 0
}
catch {
    Write-Log "فشل النسخ الاحتياطي: $($_.Exception.Message)" 'ERROR'

    # سجّل في Event Viewer كمان عشان تقدر تعمل تنبيه عليه
    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists('POSBackup')) {
            New-EventLog -LogName Application -Source 'POSBackup'
        }
        Write-EventLog -LogName Application -Source 'POSBackup' -EntryType Error `
            -EventId 900 -Message "POS backup failed: $($_.Exception.Message)"
    } catch { }

    exit 1
}
