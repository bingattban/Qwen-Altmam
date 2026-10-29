// Section 02.14: exercise the REAL offline bridge-client against the canonical
// manifest. Every state/data write must send a durable, caller-owned operationId.
"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const { webcrypto } = require("node:crypto");

const manifest = JSON.parse(fs.readFileSync("src/AlTamam.WebUI/action-manifest.json", "utf8"));
const js = fs.readFileSync("src/AlTamam.WebUI/assets/js/bridge-client.js", "utf8");
const except = new Set(["Logout","ExportPersonLifetimeExcel","ExportImportIssues",
  "ExportMonthlyWorkbookSet","CancelExport"]);
const required = manifest.actions.filter(a=>a.isMutating&&!except.has(a.action)).map(a=>a.action);
const match = js.match(/const OPERATION_ID_ACTIONS = new Set\(\[([\s\S]*?)\]\);/);
assert.ok(match, "UI must expose its explicit contract set");
const actual = Array.from(match[1].matchAll(/"([^"]+)"/g), m=>m[1]);
assert.deepEqual(actual.slice().sort(), required.slice().sort(), "manifest/UI mutation drift");

let listener;
const outgoing = [];
const webview = {
  addEventListener: (name, cb) => {
    assert.equal(name,"message");
    listener=cb;
  },
  postMessage: wire => outgoing.push(JSON.parse(wire))
};
const sandbox={
  window:{ chrome:{webview}, crypto:webcrypto },
  document:{
    addEventListener:()=>{},
    dispatchEvent:()=>{},
    getElementById:()=>null
  },
  localStorage:{getItem:()=> "a".repeat(32), setItem:()=>{},removeItem:()=>{}},
  crypto:webcrypto,
  CustomEvent:class{ constructor(name,opts){this.type=name;this.detail=opts?.detail;} },
  setTimeout,clearTimeout,console
};
vm.runInNewContext(js,sandbox,{filename:"bridge-client.js"});
const api=sandbox.window.AltamamBridge;
assert.equal(api.hasBridge(),true);
async function send(action,payload) {
  const pending=api.invoke(action,payload);
  const request=outgoing.shift();
  assert.ok(request, "bridge must send "+action);
  listener({data:JSON.stringify({requestId:request.requestId,success:true,data:{}})});
  await pending;
  return request;
}
(async()=>{
  assert.equal(required.length,37);
  for(const action of required){
    const supplied={};
    const sent=await send(action,supplied);
    assert.match(sent.payload.operationId,/^[a-z0-9_-]{16,128}$/i,action);
    assert.equal(sent.payload.operationId,supplied.operationId,
      "client must retain ID on same request object for retries");
  }
  const shared={personId:"test-id"};
  const original=await send("UpdatePersonIdentity",shared);
  const retry=await send("UpdatePersonIdentity",shared);
  assert.equal(retry.payload.operationId,original.payload.operationId,
    "lost-response retry of SAME intent must reuse operationId");
  assert.notEqual(retry.requestId,original.requestId,
    "bridge requestId remains per transmission");
  const fresh=await send("UpdatePersonIdentity",{personId:"test-id"});
  assert.notEqual(fresh.payload.operationId,original.payload.operationId,
    "new intent gets independent operationId");

  const explicit={operationId:"chosen-stable-id-123456"};
  const preserved=await send("SubmitAttendance",explicit);
  assert.equal(preserved.payload.operationId,explicit.operationId);
  const malformed={operationId:"unsafe!"};
  const forwarded=await send("SubmitAttendance",malformed);
  assert.equal(forwarded.payload.operationId,"unsafe!",
    "only C# decides validity; UI may not silently replace caller intent");
  for(const action of ["SearchPersons","Logout","ExportImportIssues",
       "ExportMonthlyWorkbookSet","CancelExport","ExportPersonLifetimeExcel"]){
    const payload={};
    const sent=await send(action,payload);
    assert.equal(Object.hasOwn(sent.payload,"operationId"),false,
      "non-data operation should not receive an invented data write ID");
  }
  console.log("Section 02 operationId bridge client: 37 mandatory actions and retry stability PASS");
})().catch(e=>{console.error(e);process.exitCode=1;});
