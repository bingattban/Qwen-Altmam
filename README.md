# التمام اليومي — مشروع Windows المكتبي

تطبيق مكتبي محلي يعمل دون اتصال إنترنت، بواجهة عربية RTL: **WPF (.NET 10) + WebView2 محلي**، قاعدة بيانات **SQLCipher** ومفاتيح عشوائية محمية بواسطة Windows DPAPI.

**الشاشات الرئيسية:** الرئيسية، التمام الشهري، الإدارة، الإعدادات. توجد 69 هوية مسار تشمل الشاشات والمربعات والعمليات، من بينها التقرير الموحد (screen_025) وإضافة فرد داخل تعديل الأسماء (screen_070). المعرّف screen_020 ملغى.

## الوضع الحالي
هذا المستودع يحتوي على برنامج Windows قابل للبناء، جسر JS↔C# حقيقي، ومسارات إدارة وتحضير وتقارير واختبارات Windows آلية. **هذا لا يعني اكتمال فحص كل وظيفة أو جاهزية التسليم النهائي**. تتطلب المراحل التالية تنفيذًا وقبولًا مستقلًا خصوصًا أمان الترحيل/الاستعادة وتجربة التشغيل على أجهزة نظيفة.

## البناء
```powershell
dotnet restore AlTamam.slnx
dotnet build AlTamam.slnx -c Release --no-restore
dotnet test tests/AlTamam.Tests/AlTamam.Tests.csproj -c Release --no-restore
```

## إجراءات المراجعة والإصدار
أي تحديث يجري في Feature Branch وPull Request. يعمل مسارا **Windows Core Build** و**Windows Release Packages** بالتوازي؛ داخل Release تجري الفحوص والاختبارات **قبل** تعبئة Portable ZIP وInstaller EXE. لا يدمج PR إلا بعد نجاح المسارين على SHA واحد وتوفر الحزم الحقيقية. تختبر نسخة `main` بعد الدمج. انظر [CI](docs/ci/README.md).

## المتطلبات الملزمة
[قرارات المالك](docs/requirements/OWNER_DECISIONS_2026-10-24.md) تتقدم على التعليمات المتعارضة، مع الحفاظ على مراحل 00–15. انظر [تتبع المتطلبات](docs/requirements/REQUIREMENTS_TRACEABILITY.md) و[جرد المسارات](docs/requirements/SCREEN_MATRIX.csv) و[قرار المعمارية](docs/architecture/ADR-0001.md).

لا تضف بيانات أفراد أو ملفات `.db` أو مفاتيح DPAPI أو صادرات شخصية إلى Git. نسخ البيانات الاحتياطية ليست بديلًا عن حزمة استعادة مشفرة مستقلة لجهاز Windows آخر.
