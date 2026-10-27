"use strict";
/* Preserve legacy dashboard links, routing them to the new authenticated,
   read-only automatic-attendance register. No mutations and no external URLs. */
(function(){
  const route=document.body?.dataset?.routeKey;
  const target=route==="UnpreparedPersonnel"
    ?"../../../index.html#auto-prepared"
    :route==="SelectStatusAndConfirm"
      ?"../../../../index.html#auto-prepared":null;
  if(target)window.location.replace(target);
})();
