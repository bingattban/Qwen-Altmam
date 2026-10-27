# Phase 12.08 — Startup failure classification

The Windows application distinguishes **missing or damaged DPAPI encryption
material** from an **invalid storage path** during startup.

- Missing original SQLCipher key produces the exact user-facing Arabic message
  `تعذر فتح قاعدة البيانات لعدم توفر مفتاح التشفير المطلوب؛ يمكن المحاولة باستخدام نسخة استعادة صالحة`.
- A missing/unwritable storage location produces
  `هذا المسار غير صالح أو غير متاح، يُرجى تحديد مسار سليم وصحيح`.
- Missing WebView2 prerequisites are NOT mislabeled as SQLCipher key loss.
- The program never creates a replacement key when it detects an older
  encrypted database, WAL/shm files, or recovery candidates.
- The application has no enabled automatic import/restore action as a
  side effect of showing the error. Native administrator authorization,
  password-protected portable recovery material and verified offline
  recovery procedures remain required before activation.

Regression tests assert the exact error contract and fail-closed key behavior.
