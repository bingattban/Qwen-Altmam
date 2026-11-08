"use strict";
// Red on 06b52ce: native Logout + SessionEnded renders two concurrent login forms.
// Extract actual shipped logout and invalidation code, with only the native
// response transport and pending login result controlled by the test.
const assert=require("node:assert/strict");
const fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
const src=fs.readFileSync(path.join(__dirname,"../src/AlTamam.WebUI/assets/js/functional-screens.js"),"utf8");
const singleStart=src.indexOf("  let loginGateInFlight=null;");
const singleEnd=src.indexOf("\n  async function loadSites()",singleStart);
const helper=singleStart>=0?src.slice(singleStart,singleEnd):"";
const logoutStart=src.indexOf("  async function logout()");
const logoutEnd=src.indexOf("\n  document.getElementById(\"logoutAction\")?.addEventListener",logoutStart);
const invalidationStart=src.indexOf("  bridge().onInvalidation(evt=>{");
const invalidationEnd=src.indexOf("\n})();",invalidationStart);
assert.ok(logoutStart>=0&&logoutEnd>logoutStart&&invalidationStart>=0&&invalidationEnd>invalidationStart);
const script=`
let gateCalls=0,settleGate=null;
async function loginGate(){
  gateCalls++;
  await new Promise(resolve=>{settleGate=resolve;});
}
${helper}
${src.slice(logoutStart,logoutEnd)}
${src.slice(invalidationStart,invalidationEnd)}
({logout,fire:()=>invalidation("SessionEnded"),
 getCalls:()=>gateCalls,resolveGate:()=>settleGate?.()})
`;
async function main(){
 let invalidation=()=>{},logouts=0,clearNonces=0;
 const app={session:{username:"founder"}};
 const api={
   invoke:async name=>{
     assert.equal(name,"Logout");
     logouts++;
     invalidation("SessionEnded");
     return {data:{loggedOut:true}};
   },
   setSessionNonce:()=>{clearNonces++},
   onInvalidation:fn=>{invalidation=fn}
 };
 const ctx={
   bridge:()=>api,STATE:app,window:{
     AltamamReminders:{stop:()=>{}},
     AltamamIdleGuard:{stop:()=>{}},
     AltamamTheme:{clearAuthorization:()=>{}}
   },dismissAutoPrepared:()=>{},sections:new Set(),location:{hash:""},
   render:()=>{},console
 };
 const x=vm.runInNewContext(script,ctx,{timeout:1000});
 const completion=x.logout();
 await new Promise(resolve=>setImmediate(resolve));
 assert.equal(x.getCalls(),1,
   "one native Logout must create one bound login form even with SessionEnded invalidation");
 x.resolveGate();await completion;
 assert.equal(logouts,1);
 assert.equal(app.session,null);
 assert.ok(clearNonces>=1);
 console.log("LOGIN GATE SINGLE-FLIGHT PASS: one logout + SessionEnded => one auth form");
}
main().catch(error=>{console.error(error);process.exitCode=1});
