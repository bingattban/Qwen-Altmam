"use strict";
/* Legacy standalone links open their real authenticated main-window owners.
   No duplicate UI, synthetic refresh, mutations, or external navigation. */
(function(){
  const route=document.body?.dataset?.routeKey;
  const target=route==="Dashboard"
    ?"../../index.html#dashboard"
    :route==="UnpreparedPersonnel"
      ?"../../../index.html#auto-prepared"
      :route==="SelectStatusAndConfirm"
        ?"../../../../index.html#auto-prepared":null;
  if(target)window.location.replace(target);
})();
