# قرارات مالك مشروع التمام اليومي — 2026-10-24

**الحالة:** قرارات إلزامية تتقدم على أي شرط قديم يخالفها في خطة التنفيذ أو شيفرة المشروع.  
**التطبيق:** المستودع الحالي `bingattban2/Qwen-Altmam`، مع بقاء نطاق مراحل التنفيذ 00–15 دون تغيير.

## 1. الرقم (ع) — الحد الأدنى أربع خانات

- الرقم (ع) معرّف مؤسسي مستقل عن تسلسل الصفوف، يُخزَّن كنص حتى لا تضيع الأصفار الأولى.
- يُقبل من **أربع خانات رقمية على الأقل**. يبقى الحد الأعلى الحالي 70 خانة حتى يصدر قرار آخر.
- تحويل الأرقام العربية (٠–٩) والفارسية (۰–۹) تلقائيًا إلى أرقام ASCII (0–9) عند الإدخال والتحقق والاستيراد.
- أي قيد سابق يقول «خمس خانات على الأقل» يُستبدل بهذا القرار، دون تغيير آلية فحص التفرد للأفراد النشطين.
- لا يُغيَّر `PersonId` عند تغيير الرقم (ع)، وتبقى الهوية التاريخية محفوظة.

## 2. المجلد الافتراضي للنسخ والتصدير

- المسار المفضّل عند عدم اختيار مسار آخر: `E:\AlTamamAlYawmi\Backups` **إذا كان القرص E متاحًا وقابلًا للكتابة**.
- إذا لم يتوفر E، أو أصبح المسار الذي اختاره المستخدم غير متاح/غير قابل للكتابة، يظهر **حوار منبثق مطابق لواجهة التطبيق**، يشرح الخلل ويتيح اختيار مجلد محلي آخر من منتقي Windows.
- لا تُكتب ملفات في مسار بديل خفي، ولا يُعلن نجاح نسخ أو تصدير إلى مجلد لم يُستخدم فعليًا.
- قاعدة البيانات الفعلية تبقى في مسار بيانات Windows الآمن، ولا تُنقل إلى القرص E تبعًا لهذا الخيار.
- هذا القرار يخصص قاعدة SECTION 01 السابقة التي كانت تشير إلى مجلد بيانات تطبيق افتراضي عند غياب E؛ اختيار موقع النسخ/التصدير يحتاج إقرار المستخدم في هذه الحالة.

## 3. حساب التأسيس (الإدارة العامة)

- عند التشغيل الأول وإنشاء حساب الإدارة العامة، **لا يُشترط إدخال الاسم الكامل** ولا رقم (ع) ولا ربط الحساب بفرد وهمي.
- يُعرض الاسم بدل ذلك بصيغة **«الإدارة العامة - اسم المستخدم»** في واجهات إدارة الحسابات.
- في سجل العمليات يُحفظ اسم المستخدم الأصلي ووسم **«الإدارة العامة»** في لقطة هوية المنفذ ما دام الحساب المؤسس غير مرتبط بكشف القوة.
- يمنع إنشاء حساب تأسيس ثانٍ حتى لو تم تعطيل الحساب الأول، ويظل مسار تهيئة القاعدة لمرة واحدة فقط.
- هذا استثناء صريح من شرط الاسم الكامل لحساب التأسيس في الوثيقة الأساسية؛ بقية الحسابات تتبع اشتراط الارتباط الحقيقي بكشف القوة.

## 4. قاعدة اتخاذ القرار والاختبارات

- **عند ظهور أي تعارض جديد** بين الفكرة الأساسية أو المرحلة المرسلة أو التطبيق القائم، يجب إبلاغ المالك بوصف واضح للتعارض وأثره، وعدم حسمه دون قرار المالك.
- راجع نتائج الاختبارات الفعلية وصحّح أخطاء الشيفرة دون طلب إذن إضافي إذا لم يتضمن التصحيح قرار أعمال متعارضًا.
- لا يجوز إعلان اكتمال مرحلة أو نجاح اختبار Windows/CI دون سجل تنفيذ ناجح لآخر Commit والاختبارات المعنية.
- تُنفَّذ أجزاء المراحل كما يرسلها المالك، مع الحفاظ على الوظائف القائمة التي لا تتعارض مع المتطلبات الملزمة.

## 5. حالة التحقق

هذه الوثيقة **توثق قرارات المالك** ولا تشكل دليلًا على أن البناء أو اختبارات Windows نجحت. لا يُعد التنفيذ مقبولًا إلا بعد فحص النتائج من آخر مصدر وCommit.

## 6. Permanent surrogate identifiers (owner clarification)

- Every person has a non-transferable permanent `PersonId`; sites, statuses and accounts each have distinct stable identifiers (`SiteId`, `StatusId`, `UserId`).
- Do not expose technical surrogate identifiers in printed attendance, historical reports, or Excel exports. Continue showing effective-dated name, personnel number and site instead.
- Archival, separation and external movement **never** assign a previous `PersonId` to another person. Database migration V007 prevents normal physical person deletion or ID mutation.
- Historic reports resolve identity by effective date; the row-header presentation when an identity changes mid-month remains a display question for the dedicated screen phase.

## 7. Section 03 conflict awaiting owner decision (do NOT invent a policy)

- Former approved instruction: the founding GeneralAdmin is **exempt** from providing a full name on first launch.
- Later Section 03.02 and 03.04: the founder **must enter** a full name at first launch.
- Until the owner explicitly chooses, the first-launch form prompts for a real name but does **not require** it. If supplied it is saved in the encrypted Users record as `FoundingFullName` and immutable audit snapshots; if omitted the previously approved `الإدارة العامة` role label is retained.
- No fabricated PersonId/service number may be created in either variant.

## 8. Automatic Windows build and distribution candidates

- Both Core Build and Portable+Installer candidate workflows run automatically on every push to `main`. New commits cancel obsolete same-workflow runs to avoid running obsolete code.
- Candidate artifacts are uploaded only after successful Windows tests and publish packaging; *automatic packaging is not final release approval*.
- Production icon `src/AlTamam.DesktopHost/Assets/AlTamam.ico` is a resized Windows icon extracted from the exact owner-selected previously generated image, not a replacement design.
- Manual/tagged acceptance runs retain their separate mandatory acceptance criteria.
