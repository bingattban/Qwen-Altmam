"use strict";
/* Shared presentation-only handlers. The authenticated bridge is introduced in phase 04.
   Never pretend that a business action succeeded before the C# host returns a response. */
document.addEventListener("click", function (event) {
  const button = event.target.closest("[data-bridge-action]");
  if (!button) return;
  const status = document.getElementById("bridgeStatusText");
  if (status) status.textContent = "لا يمكن تنفيذ هذا الإجراء قبل اكتمال الربط الآمن بخدمات البرنامج.";
});
document.addEventListener("keydown", function (event) {
  if (event.key !== "Escape") return;
  const returnLink = document.querySelector('a[href*="index.html#"]');
  if (returnLink) window.location.assign(returnLink.href);
});
