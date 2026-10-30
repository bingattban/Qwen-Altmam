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
});
