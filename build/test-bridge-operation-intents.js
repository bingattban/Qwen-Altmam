"use strict";
// Pure local bridge regression: every mutable manifest action gets one stable
// intent per payload, explicit IDs survive retries, read-only/logout stay untouched.
const assert=require("node:assert/strict");
const fs=require("node:fs");
const vm=require("node:vm");
const {webcrypto}=require("node:crypto");
const manifest=JSON.parse(fs.readFileSync("src/AlTamam.WebUI/action-manifest.json","utf8"));
const source=fs.readFileSync("src/AlTamam.WebUI/assets/js/bridge-client.js","utf8");
const sent=[], handlers={};
let automaticReply=true,storedNonce="a".repeat(32);
const webview={
  addEventListener(name,fn){handlers[name]=fn;},
  postMessage(raw){
    const data=JSON.parse(raw);
    sent.push(data);
    if(automaticReply)queueMicrotask(()=>handlers.message({data:JSON.stringify({
      requestId:data.requestId,success:true,data:{}
    })}));
  }
};
const window={chrome:{webview}};
const document={
  addEventListener(){}, dispatchEvent(){},
  getElementById(){return null;}, querySelector(){return null;}
};
const localStorage={getItem(){return storedNonce;},setItem(key,value){storedNonce=value;},removeItem(){storedNonce=null;}};
const context={window,document,crypto:webcrypto,localStorage,setTimeout,clearTimeout,
  CustomEvent:class{constructor(name,options){this.type=name;this.detail=options.detail;}}};
vm.runInNewContext(source,context,{filename:"bridge-client.js"});
const bridge=window.AltamamBridge;
(async()=>{
  const mutations=manifest.actions.filter(a=>a.isMutating&&a.action!=="Logout");
  assert.equal(mutations.length,43,"all 43 current mutation actions covered");
  for(const {action} of mutations){
    const payload={example:"value"};
    await bridge.invoke(action,payload);
    const first=sent.at(-1);
    assert.match(first.payload.operationId,/^[A-Za-z0-9_-]{16,128}$/);
    assert.equal(payload.operationId,first.payload.operationId,
      "operation ID retained on original user intent "+action);
    await bridge.invoke(action,payload);
    const retry=sent.at(-1);
    assert.equal(retry.payload.operationId,first.payload.operationId,
      "lost-reply retry must keep operation ID "+action);
    assert.notEqual(retry.requestId,first.requestId,
      "requestId is different from durable intent ID");
  }
  const provided={operationId:"user-approved-intent-20261029"};
  await bridge.invoke("CreatePerson",provided);
  assert.equal(sent.at(-1).payload.operationId,provided.operationId);
  await bridge.invoke("QueryPersons",{});
  assert.equal(sent.at(-1).payload.operationId,undefined);
  await bridge.invoke("Logout",{});
  assert.equal(sent.at(-1).payload.operationId,undefined);
  // Run real response correlation and invalidation logic, including late frames.
  automaticReply=false;
  const invalidations=[];
  bridge.onInvalidation(event=>invalidations.push(typeof event==="string"?event:event.eventName));
  const respond=(request,success,code,data={})=>handlers.message({data:JSON.stringify({
    requestId:request.requestId,success,code,messageAr:code,data
  })});
  bridge.setSessionNonce("b".repeat(32));
  const expired=bridge.invoke("GetActiveAttendanceStatuses",{}).catch(error=>error);
  const expiredRequest=sent.at(-1);
  const pendingSearch=bridge.invoke("SearchPersons",{term:"123",take:30}).catch(error=>error);
  const pendingRequest=sent.at(-1);
  respond(expiredRequest,false,"SessionExpired");
  assert.equal((await expired).errorCode,"SessionExpired");
  assert.equal(storedNonce,null,"native expiry clears the current browser nonce");
  assert.equal((await pendingSearch).errorCode,"SessionExpired","expiry cancels other requests of that session");
  assert.equal(invalidations.filter(x=>x==="SessionEnded").length,1,"expiry informs actual login/UI consumers once");
  respond(pendingRequest,true,"OK",{items:[{personId:"OLD-PRIVATE"}]});
  bridge.setSessionNonce("c".repeat(32));
  const denied=bridge.invoke("SearchActivePersonsForAccount",{term:"123",take:30}).catch(error=>error);
  respond(sent.at(-1),false,"Unauthorized");
  assert.equal((await denied).errorCode,"Unauthorized");
  assert.equal(bridge.getSessionNonce(),"c".repeat(32),"role denial does not end a valid session");
  const old=bridge.invoke("SearchPersons",{term:"123",take:30}).catch(error=>error);
  const oldRequest=sent.at(-1);
  bridge.setSessionNonce("d".repeat(32));
  assert.equal((await old).errorCode,"SessionExpired","account switch cancels requests belonging to the previous nonce");
  respond(oldRequest,false,"SessionExpired");
  assert.equal(bridge.getSessionNonce(),"d".repeat(32),"a late expired frame cannot log out the new account");
  assert.equal(invalidations.filter(x=>x==="SessionEnded").length,1);
  automaticReply=true;
  await bridge.invoke("GetActiveAttendanceStatuses",{});
  assert.equal(sent.at(-1).sessionNonce,"d".repeat(32),"fresh login can read again");
  console.log("SECTION02 mutation UI bridge PASS: 42 actions, persistent intent, explicit ID, reads and logout");
})().catch(e=>{console.error(e);process.exitCode=1;});
