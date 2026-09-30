# النسخ الاحتياطي على جهاز تاني في نفس الشبكة

## أول حاجة: نقطة مهمة

قاعدة بيانات واحدة بتشيل **كل حاجة** — كل الكاشيرات، كل الفروع، المخزن،
الموردين، المستخدمين. مفيش "backup لكل كاشير" منفصل. نسخة واحدة من
`RestaurantERP` = كل بيانات المحل.

---

## سحب ولا دفع؟

الفرق ده مش تفصيلة — ده الفرق بين إنك تنجو من هجوم فدية أو تخسر كل حاجة.

**دفع (Push):** السيرفر بينسخ الملفات لجهازك.
معناه إن السيرفر عنده صلاحية **كتابة ومسح** على مجلد النسخ عندك.
لو السيرفر اتصاب بفدية، الفيروس هيشفّر النسخ اللي عندك كمان — لأن
السيرفر واصل لها. خسرت الاتنين في نفس اللحظة.

**سحب (Pull):** جهازك هو اللي بيروح ياخد الملفات من السيرفر. ← **ده اللي هنعمله**
السيرفر مش عارف المجلد ده موجود أصلاً، ومش قادر يكتب فيه.
حتى لو السيرفر اتصاب بالكامل، نسخك سليمة.

الفرق في المجهود؟ ولا حاجة تقريبًا. الفرق في الحماية؟ كبير جدًا.

---

## الخطوات

### 1. على السيرفر — المشاركة (للقراءة فقط)

اعمل حساب ويندوز مخصص للنسخ الاحتياطي:

```powershell
# على السيرفر، PowerShell كـ Administrator
$pw = Read-Host -AsSecureString "باسورد حساب النسخ"
New-LocalUser -Name "backupreader" -Password $pw -PasswordNeverExpires `
              -Description "قراءة النسخ الاحتياطية فقط"

# شارك المجلد — لاحظ $ في الآخر: بتخلي المشاركة مخفية من جيران الشبكة
New-SmbShare -Name "POSBackups$" -Path "C:\POS\backups" `
             -ReadAccess "backupreader"
```

لاحظ `-ReadAccess` مش `-FullAccess`. الحساب ده يقرا وبس. حتى لو اتسرب،
محدش يقدر يمسح بيه نسخك.

### 2. على السيرفر — مهمة النسخ (1:00 ص)

انسخ `deploy\backup-server.ps1` إلى `C:\POS\deploy\`.

Task Scheduler → Create Task:
- **General:** Run whether user is logged on or not + Run with highest privileges
- **Triggers:** Daily, 1:00 AM
- **Actions:** Program `powershell.exe`
  Arguments: `-ExecutionPolicy Bypass -File "C:\POS\deploy\backup-server.ps1"`
- **Settings:** ✅ If the task fails, restart every 10 minutes, up to 3 times

### 3. على جهازك التاني — مهمة السحب (1:30 ص)

نصف ساعة بعد النسخ عشان يكون خلص. انسخ `deploy\pull-backups.ps1` إلى
`D:\POS-Backups\` وعدّل `$ServerHost` لـ IP السيرفر.

خزّن بيانات الدخول مرة واحدة عشان المهمة تشتغل من غير تدخل:

```cmd
cmdkey /add:192.168.1.10 /user:backupreader /pass
```

Task Scheduler → Create Task:
- **General:** Run whether user is logged on or not
- **Triggers:** Daily, 1:30 AM
  ✅ **Wake the computer to run this task** ← مهم لو الجهاز بينام
- **Actions:** `powershell.exe`
  Arguments: `-ExecutionPolicy Bypass -File "D:\POS-Backups\pull-backups.ps1"`

> **لو الجهاز بيتقفل بالليل:** خيار "Wake the computer" مش هيشتغل مع
> الإغلاق الكامل (بس مع Sleep). في الحالة دي غيّر التوقيت لوقت الجهاز
> شغال فيه — مثلاً 10:00 صباحًا. الأهم إنها تشتغل، مش إنها بالليل.

### 4. اختبار الاستعادة (شهريًا)

نصّب SQL Server Express على جهازك التاني، وانسخ `deploy\test-restore.ps1`.

Task Scheduler → شهريًا (أول جمعة مثلاً):
`-ExecutionPolicy Bypass -File "D:\POS-Backups\test-restore.ps1"`

السكربت بيستعيد أحدث نسخة في قاعدة مؤقتة، بيعد الصفوف في الجداول المهمة
عشان يتأكد إنها مش فاضية، وبعدين بيمسحها. **نسخة بتستعيد وهي فاضية بتعدي
على أي فحص سطحي** — عشان كده بنعد الصفوف مش بس بنشوف هل الاستعادة نجحت.

---

## طمّن نفسك إنها شغالة

بعد أول ليلة، اتأكد من:

```powershell
# على جهازك التاني
Get-ChildItem D:\POS-Backups\*.bak | Sort LastWriteTime -Desc | Select -First 5
Get-Content D:\POS-Backups\pull.log -Tail 20
```

**راجعها كل أسبوع.** أسوأ سيناريو إن المهمة وقفت من شهرين ومحدش واخد باله،
وتكتشف ده يوم ما تحتاج النسخة فعلاً.

للتنبيه على الفشل: السكربتات بتكتب في Event Viewer (المصدر `POSBackup`
و `POSBackupPull`). تقدر تعمل Task تانية مربوطة بالحدث ده تبعت إيميل.

---

## الحدود — كن واقعي

النسخة على جهاز تاني بتحميك من:
- ✅ عطل الهارد في السيرفر
- ✅ حذف بالغلط
- ✅ فدية على السيرفر (بفضل السحب مش الدفع)
- ✅ فساد قاعدة البيانات

بس **مش بتحميك** من: حريق، سرقة، غرق، صاعقة كهربائية تحرق الاتنين —
لأن الجهازين في نفس المكان.

عشان كده لسه محتاج **نسخة تخرج من المبنى**:
- هارد خارجي تاخده معاك البيت أسبوعيًا (وترجّع واحد تاني مكانه)
- أو رفع تلقائي مشفّر على تخزين سحابي

النسخة اليومية على الشبكة بتحل 90% من المشاكل الواقعية. الـ 10% الباقية
هي اللي بتقفل محلات — فمتسبهاش مفتوحة.

---

## لو احتجت تستعيد فعلاً

```powershell
# 1. وقّف الموقع في IIS الأول
Stop-WebSite -Name "POS"

# 2. استعد من أحدث نسخة
$bak = "D:\POS-Backups\RestaurantERP-20260804-0100.bak"
Invoke-Sqlcmd -ServerInstance "localhost" -QueryTimeout 0 -Query @"
ALTER DATABASE [RestaurantERP] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [RestaurantERP] FROM DISK = N'$bak' WITH REPLACE, RECOVERY;
ALTER DATABASE [RestaurantERP] SET MULTI_USER;
"@

# 3. رجّع مفاتيح التشفير كمان — من غيرها كل الكاشيرات هتتطرد
Copy-Item "D:\POS-Backups\keys\*.xml" "C:\POS\keys\" -Force

# 4. شغّل الموقع واتأكد
Start-WebSite -Name "POS"
Invoke-RestMethod http://localhost/health
```

اطبع الخطوات دي وحطها جنب السيرفر. يوم ما تحتاجها هتكون متوتر ومستعجل،
ومش وقت البحث عن الأوامر.
