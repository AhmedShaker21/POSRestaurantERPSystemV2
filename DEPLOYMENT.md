# نشر النظام على شبكة محلية (LAN) — Production Runbook

## لماذا لا نستخدم ngrok / tunnel هنا

النفق كان الحل الصحيح وقت التطوير لما كنت عايز تشوف السيرفر من موبايل بره الشبكة.
لكن في المطعم الوضع مختلف تمامًا:

| | Tunnel | LAN مباشر |
|---|---|---|
| لو النت فصل | النظام كله يقف | يشتغل عادي |
| زمن الاستجابة | 100–400ms (بيروح لسيرفر بره ويرجع) | أقل من 5ms |
| نقطة فشل | خدمة خارجية مش تحت سيطرتك | لا يوجد |
| الأمان | بياناتك بتعدي على طرف تالت | مقفولة جوه المحل |
| التكلفة | اشتراك شهري | صفر |

الكاشير لازم يشتغل والنت مقطوع. **مفيش تنجول.** السيرفر والأجهزة على نفس السويتش.

---

## 1. المعمارية

```
                    ┌──────────────────────────────┐
                    │  Workstation (السيرفر)        │
                    │  Windows + IIS + SQL Server  │
                    │  IP ثابت: 192.168.1.10       │
                    └──────────────┬───────────────┘
                                   │  كابل شبكة (مش واي فاي)
                          ┌────────┴────────┐
                          │  Switch / Router │
                          └────────┬────────┘
              ┌──────────┬─────────┼─────────┬──────────┐
           كاشير 1     كاشير 2   المطبخ    الطاولات    مكتب المدير
        (touchscreen) (touch)   (شاشة)   (تابلت)      (لابتوب)
```

الأجهزة مش محتاجة تنصب حاجة — بتفتح المتصفح على عنوان السيرفر وخلاص.

> **مهم:** وصّل الكاشيرات بكابل مش واي فاي. الواي فاي بيقطع لحظيًا وده معناه
> فاتورة ضايعة في نص الطلب.

---

## 2. تجهيز السيرفر

### 2.1 IP ثابت
لو الـ IP اتغير، كل الأجهزة هتفقد السيرفر. اعمل واحد من الاتنين:
- **الأفضل:** DHCP Reservation في الراوتر (اربط MAC بـ IP) — التغيير من مكان واحد.
- أو IP ثابت من إعدادات كارت الشبكة: `192.168.1.10 / 255.255.255.0`.

### 2.2 SQL Server
1. نزّل **SQL Server 2022 Express** (مجاني) أو Standard.
2. مهم: Express محدود بـ **10 GB لكل قاعدة**. لمطعم واحد ده يكفي سنين،
   لكن لو 3 فروع بحجم كبير راقب الحجم وخطط للترقية.
3. نزّل **SSMS** (SQL Server Management Studio).
4. فعّل **Mixed Mode Authentication** (اسم مستخدم وباسورد، مش Windows بس).
5. اعمل مستخدم مخصص للتطبيق — **متستخدمش `sa` أبدًا**:

```sql
CREATE LOGIN posapp WITH PASSWORD = 'ضع_باسورد_قوي_هنا';
CREATE DATABASE RestaurantERP;
GO
USE RestaurantERP;
CREATE USER posapp FOR LOGIN posapp;
ALTER ROLE db_owner ADD MEMBER posapp;
```

### 2.3 IIS + ASP.NET Core Hosting Bundle
1. Control Panel → Turn Windows features on → **Internet Information Services**
2. نزّل ونصّب **ASP.NET Core 8 Hosting Bundle** من موقع مايكروسوفت
3. `iisreset` من موجه أوامر Administrator

**ليه IIS ومش `dotnet run`؟** IIS بيشغل التطبيق تلقائيًا مع تشغيل الجهاز،
بيعيد تشغيله لو وقع، وبيتعامل مع عدة طلبات في نفس الوقت. `dotnet run` بيقف
أول ما تقفل النافذة أو تعمل logout.

---

## 3. نشر التطبيق

على جهاز التطوير:

```powershell
dotnet publish -c Release -o C:\publish
```

انسخ محتويات `C:\publish` إلى السيرفر في `C:\POS\app`.

### إعداد الملفات على السيرفر

1. اعمل المجلدات:
   ```
   C:\POS\app      ← ملفات التطبيق
   C:\POS\keys     ← مفاتيح التشفير (مهم جدًا)
   C:\POS\backups  ← النسخ الاحتياطية
   C:\POS\logs     ← السجلات
   ```
2. افتح `C:\POS\app\appsettings.Production.json` وعدّل:
   - `DefaultConnection` → باسورد `posapp` الحقيقي
   - `Hosting:UseHttps` → حسب قرارك في القسم 5
   - `Hosting:AutoMigrate` → `false`

### إعداد موقع IIS
1. IIS Manager → Sites → **Add Website**
   - Site name: `POS`
   - Physical path: `C:\POS\app`
   - Port: `80`
2. Application Pools → `POS` → Advanced Settings:
   - **.NET CLR Version = No Managed Code**
   - **Start Mode = AlwaysRunning**
   - **Idle Time-out = 0** (عشان أول كاشير الصبح ميستناش التطبيق يقوم)
3. Sites → POS → Configuration Editor → `system.applicationHost/sites` →
   موقعك → `applicationDefaults` → **preloadEnabled = True**
4. صلاحيات المجلدات — امنح `IIS AppPool\POS` صلاحية:
   - `C:\POS\keys` → **Modify**
   - `C:\POS\logs` → **Modify**
   - `C:\POS\app\wwwroot\uploads` → **Modify** (لو بترفع صور منتجات)

### تشغيل الـ Migrations
مرة واحدة عند النشر، ومن جهاز التطوير أو بأمر:

```powershell
dotnet ef database update --connection "Server=localhost;Database=RestaurantERP;User Id=posapp;Password=...;TrustServerCertificate=True;"
```

---

## 4. الشبكة والوصول

### جدار الحماية
افتح المنفذ للشبكة المحلية فقط:

```powershell
New-NetFirewallRule -DisplayName "POS HTTP" -Direction Inbound `
  -Protocol TCP -LocalPort 80 -Action Allow -Profile Private
```

> **متفتحش المنفذ ده على الإنترنت.** لو محتاج وصول من بره، استخدم VPN.

### اسم بدل الـ IP
بدل ما الكاشير يكتب `192.168.1.10`، خلّيه `pos.club.local`:

**الأسهل (أجهزة قليلة):** على كل جهاز، عدّل
`C:\Windows\System32\drivers\etc\hosts` كـ Administrator وأضف:
```
192.168.1.10   pos.club.local
```

**الأفضل:** أضف A Record في الـ DNS بتاع الراوتر — تظبطها مرة واحدة للكل.

الفايدة الحقيقية: لو غيّرت السيرفر بعد سنتين، بتغيّر سطر واحد بدل ما
تلف على كل جهاز.

---

## 5. HTTPS — قرار لازم تاخده

عندك مسارين، وكل واحد له ثمن:

**(أ) HTTP عادي على شبكة سلكية معزولة**
- `Hosting:UseHttps = false`
- بساطة تامة، مفيش شهادات تنتهي وتوقّف المحل
- مقبول **بشرط** إن الشبكة معزولة فعلًا: مفيش ضيوف عليها، والواي فاي
  بتاع العملاء على شبكة منفصلة تمامًا
- العيب الحقيقي: أي حد يوصل للسويتش يقدر يشوف الباسوردات

**(ب) HTTPS بشهادة داخلية** ← الأنسب لو فيه واي فاي للعملاء على نفس الشبكة
- ولّد شهادة لـ `pos.club.local`
- نصّب شهادة الـ CA على كل جهاز كـ Trusted Root
- `Hosting:UseHttps = true` واربط الشهادة في IIS
- العيب: الشهادة بتنتهي — **حط تذكير في التقويم قبل انتهائها بشهر**،
  لأن انتهاءها معناه توقف الكاشير في وش العملاء

نصيحتي: لو الشبكة سلكية ومعزولة تمامًا ابدأ بـ (أ) وشغّل المحل، وحوّل
لـ (ب) لما تستقر. الأهم إنك **تفصل شبكة العملاء عن شبكة الكاشير** — دي
أهم من HTTPS نفسه.

---

## 6. أجهزة الكاشير (Touchscreen)

### تشغيل المتصفح في وضع Kiosk
اعمل اختصار على سطح المكتب:

```
"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --kiosk http://pos.club.local/Cashier/Index --edge-kiosk-type=fullscreen --no-first-run
```

للمطبخ: نفس الأمر مع `/Kitchen/Index`.
للطاولات: `/Waiter/Tables`.

### تشغيل تلقائي مع الويندوز
حط الاختصار في:
```
shell:startup
```
(اكتبها في Run)

### إعدادات مهمة للشاشات اللمسية
- **Settings → System → Power → Screen: Never** (الشاشة متطفيش وسط الشغل)
- عطّل Sleep و Hibernate
- عطّل تحديثات ويندوز التلقائية في أوقات الذروة
  (Settings → Windows Update → Active hours)
- فعّل تسجيل دخول تلقائي لمستخدم ويندوز محدود الصلاحيات

---

## 7. النسخ الاحتياطي — ده مش اختياري

بيانات المبيعات لو ضاعت، ضاعت. اعمل نسخة يومية:

احفظ الملف ده في `C:\POS\backup.ps1`:

```powershell
$date = Get-Date -Format "yyyyMMdd-HHmm"
$backupPath = "C:\POS\backups\RestaurantERP-$date.bak"

Invoke-Sqlcmd -ServerInstance "localhost" -Query @"
BACKUP DATABASE RestaurantERP
TO DISK = '$backupPath'
WITH FORMAT, COMPRESSION, STATS = 10;
"@

# احتفظ بآخر 30 نسخة فقط
Get-ChildItem C:\POS\backups\*.bak |
  Sort-Object CreationTime -Descending |
  Select-Object -Skip 30 |
  Remove-Item -Force

# انسخ مفاتيح التشفير كمان
Copy-Item C:\POS\keys\*.xml "C:\POS\backups\keys\" -Force
```

في Task Scheduler: شغّله يوميًا بعد قفل المحل، بصلاحيات Administrator.

**والأهم:** النسخة لازم تخرج من الجهاز. لو عندك جهاز تاني على نفس الشبكة،
شوف **`deploy/BACKUP.md`** — فيه سكربتات جاهزة تسحب النسخة يوميًا لجهازك،
واختبار استعادة شهري تلقائي.

النسخ الاحتياطية على نفس الجهاز اللي ممكن يحترق مش نسخة احتياطية،
و**نسخة احتياطية متجربتش = مش موجودة**.

---

## 8. UPS

الوركستيشن **لازم** يكون على UPS. انقطاع كهرباء وسط كتابة في قاعدة
البيانات ممكن يفسدها. UPS بسيط يكفي 10 دقايق عشان النظام يقفل بأمان.
اربط الـ UPS بالسيرفر بكابل USB وظبّط إغلاق تلقائي عند 20% بطارية.

---

## 9. إجراء التحديث

لما تبعت نسخة جديدة:

1. اعمل **backup للقاعدة** الأول (سطر واحد من السكربت فوق)
2. اقفل المحل أو استنى بعد آخر أوردر
3. IIS Manager → Site → **Stop**
4. انسخ ملفات النشر الجديدة فوق `C:\POS\app`
   - **متمسحش** `appsettings.Production.json`
   - **متمسحش** مجلد `keys`
5. لو فيه migrations جديدة: `dotnet ef database update`
6. IIS → **Start**
7. افتح `http://pos.club.local/health` — لازم ترجع `Healthy`
8. اعمل أوردر تجريبي واطبعه قبل ما تسيب المكان

---

## 10. الفحص السريع عند أي مشكلة

| العرض | الفحص |
|---|---|
| الكاشير مش بيفتح الصفحة | `ping 192.168.1.10` من الجهاز |
| بيفتح من السيرفر بس | جدار الحماية (القسم 4) |
| خطأ 500 | Event Viewer → Windows Logs → Application |
| الكل خرج من الحساب فجأة | مجلد `keys` مش موجود أو مفيش صلاحية كتابة عليه |
| بطء مفاجئ | حجم القاعدة، أو Idle Time-out رجع لـ 20 دقيقة |
| `/health` بيرجع Unhealthy | خدمة SQL Server واقفة |

سجلات التطبيق: **Event Viewer → Windows Logs → Application**، فلتر على
مصدر `IIS AspNetCore Module V2`.

---

## 11. حاجات تعملها قبل ما تفتح للعملاء

- [ ] غيّر باسورد حساب `admin@restaurant.com` الافتراضي
- [ ] امسح أو عطّل أي مستخدمين تجريبيين
- [ ] راجع صلاحيات كل مستخدم (Admin/Manager/Cashier/Kitchen)
- [ ] اضبط بيانات المحل في `/Admin/Settings` (الاسم، الهاتف، العنوان، الضريبة)
- [ ] جرّب طباعة فاتورة حقيقية على الطابعة الحرارية
- [ ] جرّب فتح وردية، أوردر، مرتجع، وقفل وردية من البداية للنهاية
- [ ] اتأكد إن النسخة الاحتياطية اشتغلت فعلًا (شوف الملف في `backups`)
- [ ] جرّب اقفل السيرفر وشغّله — لازم كل حاجة ترجع لوحدها من غير تدخل
