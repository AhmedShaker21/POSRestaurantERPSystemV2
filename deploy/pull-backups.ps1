<#
    pull-backups.ps1  —  يشتغل على جهازك التاني (مش السيرفر) كل يوم 1:30 صباحًا
    -------------------------------------------------------------------------
    ليه "سحب" مش "دفع"؟
      لو السيرفر هو اللي بيدفع النسخ لجهازك، يبقى السيرفر عنده صلاحية كتابة
      ومسح على مجلد النسخ. لو أصابته فدية (ransomware) هيشفّر النسخ كمان
      وتبقى خسرت الاتنين.
      لما جهازك هو اللي بيسحب، السيرفر مش عارف أصلاً إن المجلد ده موجود،
      ومش قادر يوصله. النسخ في أمان حتى لو السيرفر اتصاب بالكامل.

    Task Scheduler على الجهاز التاني:
      Program  : powershell.exe
      Arguments: -ExecutionPolicy Bypass -File "D:\POS-Backups\pull-backups.ps1"
      Trigger  : يوميًا 1:30 ص
      Run whether user is logged on or not
#>

$ErrorActionPreference = 'Stop'

$ServerHost   = '192.168.1.10'                 # IP السيرفر
$SourceShare  = "\\$ServerHost\POSBackups$"    # مشاركة للقراءة فقط
$LocalDir     = 'D:\POS-Backups'
$LogFile      = 'D:\POS-Backups\pull.log'
$KeepDays     = 90                             # احتفظ بمدة أطول من السيرفر

function Write-Log([string]$msg, [string]$level = 'INFO') {
    $line = "{0} [{1}] {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $level, $msg
    Add-Content -Path $LogFile -Value $line
    Write-Host $line
}

try {
    New-Item -ItemType Directory -Force -Path $LocalDir, "$LocalDir\keys" | Out-Null
    Write-Log "بداية السحب من $SourceShare"

    if (-not (Test-Connection -ComputerName $ServerHost -Count 2 -Quiet)) {
        throw "السيرفر $ServerHost مش راد على الشبكة"
    }

    # /MIR ممنوع هنا عن قصد: مش عايزين مسح على السيرفر يمسح نسخنا
    # /XO = متنسخش ملف أقدم من اللي عندنا
    $rc = Start-Process robocopy -Wait -PassThru -NoNewWindow -ArgumentList @(
        "`"$SourceShare`"", "`"$LocalDir`"", '*.bak',
        '/E', '/XO', '/R:3', '/W:10', '/NP', "/LOG+:$LogFile", '/TEE'
    )
    # robocopy: أي كود أقل من 8 معناه نجاح
    if ($rc.ExitCode -ge 8) { throw "robocopy فشل بكود $($rc.ExitCode)" }

    # مفاتيح التشفير
    $rcKeys = Start-Process robocopy -Wait -PassThru -NoNewWindow -ArgumentList @(
        "`"$SourceShare\keys`"", "`"$LocalDir\keys`"", '*.xml',
        '/E', '/XO', '/R:3', '/W:10', '/NP', "/LOG+:$LogFile"
    )
    if ($rcKeys.ExitCode -ge 8) { Write-Log "تحذير: فشل نسخ المفاتيح" 'WARN' }

    # ── تحقق إن نسخة النهارده وصلت فعلاً ──────────────────────────────
    $today  = (Get-Date).Date
    $latest = Get-ChildItem "$LocalDir\*.bak" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if (-not $latest) {
        throw "مفيش أي نسخة احتياطية في $LocalDir"
    }
    if ($latest.LastWriteTime.Date -lt $today) {
        Write-Log "أحدث نسخة تاريخها $($latest.LastWriteTime) — النسخ على السيرفر يمكن يكون فشل" 'WARN'
    } else {
        $mb = [math]::Round($latest.Length / 1MB, 1)
        Write-Log "وصلت نسخة النهارده: $($latest.Name) ($mb MB)"
    }

    # ── تنظيف ────────────────────────────────────────────────────────
    $cutoff  = (Get-Date).AddDays(-$KeepDays)
    $removed = Get-ChildItem "$LocalDir\*.bak" | Where-Object { $_.LastWriteTime -lt $cutoff }
    $removed | Remove-Item -Force
    if ($removed) { Write-Log "تم حذف $($removed.Count) نسخة أقدم من $KeepDays يوم" }

    $count = (Get-ChildItem "$LocalDir\*.bak" | Measure-Object).Count
    Write-Log "اكتمل — إجمالي النسخ عندي: $count"
    exit 0
}
catch {
    Write-Log "فشل السحب: $($_.Exception.Message)" 'ERROR'
    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists('POSBackupPull')) {
            New-EventLog -LogName Application -Source 'POSBackupPull'
        }
        Write-EventLog -LogName Application -Source 'POSBackupPull' -EntryType Error `
            -EventId 901 -Message "POS backup pull failed: $($_.Exception.Message)"
    } catch { }
    exit 1
}
