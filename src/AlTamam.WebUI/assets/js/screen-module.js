"use strict";
/* Screen modules use the real WebView2 bridge-client.js for read-only refresh.
   Never display a fake success or duplicate the bridge's click listener. */
document.addEventListener("keydown", function (event) {
  if (event.key !== "Escape") return;
  const returnLink = document.querySelector('a[href*="index.html#"]');
  if (returnLink) window.location.assign(returnLink.href);
});
