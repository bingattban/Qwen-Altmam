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
const webview={
  addEventListener(name,fn){handlers[name]=fn;},
  postMessage(raw){
    const data=JSON.parse(raw);
    sent.push(data);
    queueMicrotask(()=>handlers.message({data:JSON.stringify({
      requestId:data.requestId,success:true,data:{}
    })}));
  }
};
const window={chrome:{webview}};
const document={
  addEventListener(){}, dispatchEvent(){},
  getElementById(){return null;}, querySelector(){return null;}
};
const localStorage={getItem(){return "a".repeat(32);},setItem(){},removeItem(){}};
const context={window,document,crypto:webcrypto,localStorage,setTimeout,clearTimeout,
  CustomEvent:class{constructor(name,options){this.type=name;this.detail=options.detail;}}};
vm.runInNewContext(source,context,{filename:"bridge-client.js"});
const bridge=window.AltamamBridge;
(async()=>{
  const mutations=manifest.actions.filter(a=>a.isMutating&&a.action!=="Logout");
  assert.equal(mutations.length,41,"all 41 current mutation actions covered");
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
  console.log("SECTION02 mutation UI bridge PASS: 41 actions, persistent intent, explicit ID, reads and logout");
})().catch(e=>{console.error(e);process.exitCode=1;});
