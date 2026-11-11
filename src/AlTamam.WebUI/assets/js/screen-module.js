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
    link.title = "فتح الواجهة الرئيسية";
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
  // This document is about to leave for its canonical authenticated owner.
  // The owner boots reminders itself; loading a second copy during the handoff
  // adds needless script/CSS fetches and native polling to each redirected page.
  window.__altamamRedirectToMain = true;
  window.location.replace(relative.split("#")[0]+"#"+encodeURIComponent(key));
});


// Keep reminders alive while the user visits any standalone application screen.
// The canonical GetCurrentSession action still validates the native user session.
// All notification data stays in the independent JSON file; never in WebView storage.
(function enableStandaloneReminders(){
  const source = document.currentScript?.src;
  if(!source)return;
  document.addEventListener("DOMContentLoaded",function(){
    if (window.__altamamRedirectToMain) return;
    if(!window.AltamamBridge?.hasBridge?.())return;
    const css=document.createElement("link");
    css.rel="stylesheet";
    css.href=new URL("../css/reminders.css",source).href;
    document.head.appendChild(css);
    const script=document.createElement("script");
    script.src=new URL("reminders.js",source).href;
    script.addEventListener("load",async function(){
      try {
        const current=await window.AltamamBridge.invoke("GetCurrentSession",{});
        if(current?.data?.sessionNonce && window.AltamamReminders)
          window.AltamamReminders.start();
      } catch {
        // Logged-out standalone screens must not display personal notifications.
      }
    });
    document.head.appendChild(script);
  });
})();
