"use strict";
/* Real human activity, not background polling, renews the 5-minute SQLCipher session.
   On sleep/resume we compare wall-clock time before accepting an input as activity. */
(function(){
  const LIMIT=5*60*1000,HEARTBEAT=60*1000,KEY="altamam.lastUserActivity.v1";
  let active=false,last=0,lastHeartbeat=0,interval=null,onExpire=null,expiring=false;
  function saved(){
    try{return Number(localStorage.getItem(KEY))||0;}catch{return 0;}
  }
  function persist(timestamp){
    try{if(timestamp)localStorage.setItem(KEY,String(timestamp));else localStorage.removeItem(KEY);}
    catch{/* inaccessible storage: server still enforces expiry */}
  }
  function shouldLogout(){
    const nonce=window.AltamamBridge?.getSessionNonce?.();
    if(!nonce)return false;
    const value=active?last:saved(),now=Date.now();
    return !value||value>now||now-value>=LIMIT;
  }
  async function expire(){
    if(!active||expiring)return;
    expiring=true;
    try{await onExpire?.();}finally{stop();}
  }
  function check(){
    if(active&&shouldLogout())void expire();
  }
  function activity(event){
    if(!active||expiring||event?.isTrusted!==true)return;
    // A first input after a suspended timer must NOT resurrect an idle session.
    if(shouldLogout()){void expire();return;}
    last=Date.now();persist(last);
    if(last-lastHeartbeat>=HEARTBEAT){
      lastHeartbeat=last;
      const bridge=window.AltamamBridge;
      if(bridge?.hasBridge?.()){
        void bridge.invoke("GetCurrentSession",{userActivity:true}).catch(()=>{if(active)void expire();});
      }
    }
  }
  function start(fresh,onIdle){
    // Read the persisted value BEFORE stop(), which intentionally clears it.
    const existing=fresh?Date.now():saved();
    stop();
    active=true;expiring=false;onExpire=onIdle;
    last=existing;
    if(last)persist(last);
    lastHeartbeat=Date.now();
    for(const name of ["pointerdown","keydown","wheel","input","touchstart"])
      document.addEventListener(name,activity,{passive:true});
    document.addEventListener("visibilitychange",check);
    window.addEventListener?.("focus",check);
    interval=setInterval(check,15000);
    check();
  }
  function stop(){
    active=false;
    if(interval!==null){clearInterval(interval);interval=null;}
    for(const name of ["pointerdown","keydown","wheel","input","touchstart"])
      document.removeEventListener(name,activity);
    document.removeEventListener("visibilitychange",check);
    window.removeEventListener?.("focus",check);
    onExpire=null;last=0;expiring=false;
    persist(0);
  }
  window.AltamamIdleGuard=Object.freeze({start,stop,shouldLogout,check});
})();
