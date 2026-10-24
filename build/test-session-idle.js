"use strict";
const assert=require("node:assert/strict"),fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
let clock=1_000_000,expired=0,heartbeats=0,nonce="A".repeat(32),timer=null;
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
 clearInterval:()=>{timer=null}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
 "../src/AlTamam.WebUI/assets/js/session-idle.js"),"utf8"),context);
const guard=window.AltamamIdleGuard;
const tick=()=>new Promise(resolve=>setImmediate(resolve));
async function main(){
 assert.equal(guard.shouldLogout(),true,"old persisted session without last activity must reauthenticate");
 guard.start(true,async()=>{expired++;});
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
 console.log("IDLE GUARD UI PASS: 5-minute lock, server renewal on user activity, sleep/resume, persisted session gate");
}
main().catch(e=>{console.error(e);process.exitCode=1;});
