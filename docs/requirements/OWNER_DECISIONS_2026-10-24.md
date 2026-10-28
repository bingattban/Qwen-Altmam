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

## 7. Section 03 final OWNER decision — optional founding account full name

- **Resolved by owner, 2026-10-24:** The founding GeneralAdmin owner's full name is **optional** at first startup; this explicitly overrides the incompatible mandatory-name wording in Section 03.02 / 03.04. Username, strong password, and matching confirmation are mandatory.
- **New binding decision, 2026-10-25 (supersedes all earlier optional-link wording):** the founding GeneralAdmin account is NEVER linked to a person in the personnel roster. `Users.LinkedPersonId` must always be `NULL` for Role=1, regardless of whether a full name was optionally entered. There is no (ع) number requirement and no personnel lifecycle dependency. No user, including GeneralAdmin, can invoke a linking command.
- In the user accounts display, ALWAYS show `الإدارة العامة - username` for the GeneralAdmin. When an optional real founder name was entered, append ` (الاسم الكامل)`. This display is independent of any roster name, personnel number, transfer, or separation.
- The optional supplied founder name is stored in the encrypted database as `FoundingFullName`; the creation audit takes an immutable name snapshot. Without it, the audit snapshot uses `الإدارة العامة` and the actual username remains preserved separately.
- Do not invent a fictitious personnel number, real roster person or second founding account. V009 automatically detaches any legacy founder account previously linked to a roster `PersonId` and bumps only its account revision, preserving its UserId, username, Argon2id credential, role, activation flag, and historical audit entries. V009 database triggers permanently prevent re-linking, including direct SQL calls. The old `LinkFoundingAdministrator` bridge command has been removed from both the compiled and WebUI manifests; 96 commands remain.

## 8. OWNER-APPROVED TEST-FIRST GITHUB PR AND MERGE POLICY

- **Never implement and directly push ordinary work to `main`.** Create a feature branch and an open pull request targeting `main`. All subsequent changes remain on the PR branch.
- On `pull_request` open / synchronize, start the two independent **Windows Core Build** and **Windows Release Packages** workflows concurrently for the candidate branch. Neither workflow waits for the other to begin.
- Within **Core**, execute UI/manifest guards → production compile → full C# tests. Within **Release**, execute independent UI/manifest/dependency preflight → production compile and full tests → only after all Release tests pass, publish and package **both** Portable ZIP and Installer EXE with validation and SHA-256 evidence.
- **Never merge until BOTH workflows have conclusively succeeded on the current PR head SHA AND the Release run has uploaded the Portable and Installer artifacts.** On failures, skips, cancellation, missing builds or differing SHAs: do not merge. Fix the feature branch and rerun both workflows. After verified success, merge the PR into `main`. The main branch also runs both workflows post-merge to verify the integrated commit.
- The existing GitHub `main` branch was checked and was **not protected** as of this change. GitHub branch protection / repository rulesets requiring the two checks are **not writable through the active connector**, so they must be configured by the repository owner in GitHub Settings to forbid bypass by other push-capable users. A documented policy by itself is not an enforced branch protection rule.
- Candidate artifacts are **not** final release approval. Manual/tagged acceptance gates and owner-approved signing remain separate.
- Production icon `src/AlTamam.DesktopHost/Assets/AlTamam.ico` is the owner-selected previously generated picture, not a replacement design.

## 9. OWNER DECISION — Portable direct-run with existing Windows AppData (2026-10-28)

- **Decision explicitly approved:** The 32-bit/64-bit Portable ZIP packages run directly from the extracted executable without an installer; **both Portable and Installer use exactly the same per-Windows-user data root**: `%LOCALAPPDATA%\\AlTamam`.
- Existing encrypted database, DPAPI-protected key, application WebView2 profile and **internal rescue backup directory** stay at their original user-specific paths. This is NOT a portable-on-USB database model.
- This decision does **not** change the previously approved user-selected backup/export directory policy: prefer `E:\AlTamamAlYawmi\Backups` only when writable, otherwise require the visible Windows folder selection; do not silently redirect files.
- **Do not move, copy, migrate, delete, rename or import any existing user data automatically.** Changing extraction directory, drive or distribution type must not change the database location.
- The executable resource directory remains read-only and separate from the writable AppData storage. Cross-PC database restoration remains subject to the existing protected recovery workflow and acceptance gates.
- Remove the legacy unused `AppDataLayout.IsPortable` storage switch and its misleading adjacent-`data` documentation, without altering the logical contents, database schema, keys, operation logs or permission model.
- Include a regression test that verifies Portable and Installer share a single root and that no alternate storage switch remains exposed.
- Any other policy conflict must be reported to the owner and left undecided until explicit approval.
