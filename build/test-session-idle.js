"use strict";
const assert=require("node:assert/strict"),fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
let clock=1_000_000,expired=0,heartbeats=0,nonce="A".repeat(32),timer=null,
  deadlineFn=null,deadlineDelay=null;
const saved=new Map(),docListeners=new Map(),winListeners=new Map();
const localStorage={
 getItem:k=>saved.get(k)||null,
 setItem:(k,v)=>saved.set(k,String(v)),
 removeItem:k=>saved.delete(k)
};
const document={
 addEventListener:(name,handler)=>docListeners.set(name,handler),
 removeEventListener:name=>docListeners.delete(name)
};
const window={
 AltamamBridge:{
  getSessionNonce:()=>nonce,hasBridge:()=>true,
  invoke:async(name)=>{assert.equal(name,"GetCurrentSession");heartbeats++;return{data:{sessionNonce:nonce}};}
 },
 addEventListener:(name,handler)=>winListeners.set(name,handler),
 removeEventListener:name=>winListeners.delete(name)
};
const FakeDate={now:()=>clock};
const context={window,document,localStorage,Date:FakeDate,
 setInterval:fn=>{timer=fn;return 1},
 clearInterval:()=>{timer=null},
 setTimeout:(fn,ms)=>{deadlineFn=fn;deadlineDelay=ms;return 2},
 clearTimeout:()=>{deadlineFn=null;deadlineDelay=null}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
 "../src/AlTamam.WebUI/assets/js/session-idle.js"),"utf8"),context);
const guard=window.AltamamIdleGuard;
const tick=()=>new Promise(resolve=>setImmediate(resolve));
async function main(){
 assert.equal(guard.shouldLogout(),true,"old persisted session without last activity must reauthenticate");
 guard.start(true,async()=>{expired++;});
 assert.equal(deadlineDelay,300000,"exactly 300 seconds scheduled from initial sign-in");
 assert.equal(guard.shouldLogout(),false);
 clock+=4*60*1000+59*1000;
 docListeners.get("keydown")({isTrusted:true});
 assert.equal(guard.shouldLogout(),false,"trusted activity updates last user time");
 assert.equal(heartbeats,1,"trusted activity sends the server refresh");
 clock+=5*60*1000;
 winListeners.get("focus")();
 await tick();
 assert.equal(expired,1,"resume at exactly five idle minutes locks the session");
 assert.equal(timer,null);
 assert.equal(saved.size,0,"lock clears persisted last activity");
 guard.start(true,async()=>{expired++;});
 clock+=5*60*1000+1;
 docListeners.get("pointerdown")({isTrusted:true});
 await tick();
 assert.equal(expired,2,"input AFTER timeout must not reactivate expired session");
 guard.start(true,async()=>{expired++;});
 const timestamp=clock;
 guard.stop();
 saved.set("altamam.lastUserActivity.v1",String(timestamp));
 guard.start(false,async()=>{expired++;});
 assert.equal(guard.shouldLogout(),false,"boot can restore an already valid active session");
 guard.stop();
 guard.start(true,async()=>{expired++;});
 assert.equal(deadlineDelay,300000,"fresh session schedules full 300-second delay");
 clock+=299999;
 assert.equal(guard.shouldLogout(),false,"299.999 seconds of real inactivity stays unlocked");
 clock+=1;
 assert.equal(guard.shouldLogout(),true,"300.000 seconds crosses the exact lock boundary");
 deadlineFn();
 await tick();
 assert.equal(expired,3,"timer locks precisely at 300 seconds without requiring UI focus");
 guard.stop();
 console.log("IDLE GUARD UI PASS: 300-second timer + resume lock, WPF/WebView human activity, server renewal, persisted gate");
}
main().catch(e=>{console.error(e);process.exitCode=1;});
