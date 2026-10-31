"use strict";
/* Screen modules use the real WebView2 bridge-client.js for read-only refresh.
   Never display a fake success or duplicate the bridge's click listener. */
document.addEventListener("keydown", function (event) {
  if (event.key !== "Escape") return;
  const returnLink = document.querySelector('a[href*="index.html"]');
  if (returnLink) window.location.assign(returnLink.href);
});

// Section 05.05: a return link must not promise a fully implemented action.
// Preserve every existing href and the Escape-to-main navigation.
document.addEventListener("DOMContentLoaded", function () {
  document.querySelectorAll('a[href*="index.html#"]').forEach(function (link) {
    const label = link.textContent.trim();
    if (label !== "تشغيل التفاعل الكامل" && label !== "↗ فتح في المنظومة الكاملة") return;
    link.textContent = "العودة إلى الواجهة الرئيسية";
    link.title = "فتح الواجهة الرئيسية؛ لا يعني اكتمال وظيفة هذا المسار المستقل";
  });
  // Screens without an independent business owner must not display empty shells.
  // Reopen the existing authenticated owner by its registered route key. Existing
  // read-only screens and the dedicated dashboard register retain their behavior.
  const independentlyWired = new Set([
    "Administration","Sites","Statuses","PersonnelManagement",
    "PersonPickerAndSearch","DraftsAndTemporarySave",
    "Dashboard","UnpreparedPersonnel","SelectStatusAndConfirm",
    "ConfirmationAndWarning","ConflictsAndNotifications"
  ]);
  const key = document.body?.dataset?.routeKey;
  if(!key || independentlyWired.has(key))return;
  const link = document.querySelector('a[href*="index.html#"]');
  const relative = link?.getAttribute?.("href");
  if(typeof relative!=="string" || !/^(?:\.\.\/)+index\.html#[^#]*$/.test(relative))
    return; // Fail closed rather than navigating an unverified origin.
  window.location.replace(relative.split("#")[0]+"#"+encodeURIComponent(key));
});
