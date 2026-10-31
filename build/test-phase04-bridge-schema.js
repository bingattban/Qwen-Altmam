"use strict";
// Phase 04: detect schema/manifest drift for every typed WebView2 request.
// Runtime validation remains in BridgeRequestParser and the owning C# service.
const assert=require("node:assert/strict"),fs=require("node:fs"),path=require("node:path");
const base=path.resolve(__dirname,"..");
const schema=JSON.parse(fs.readFileSync(path.join(base,"src/AlTamam.Contracts/Schemas/bridge-v1.schema.json"),"utf8"));
const actions=JSON.parse(fs.readFileSync(path.join(base,"src/AlTamam.WebUI/action-manifest.json"),"utf8")).actions;
assert.equal(schema.$schema,"https://json-schema.org/draft/2020-12/schema");
assert.equal(schema.properties.protocolVersion.const,1);
assert.deepEqual(schema.required,["requestId","protocolVersion","action","sessionNonce","payload"]);
assert.equal(schema.additionalProperties,false);
assert.equal(schema.$defs.response.properties.success.type,"boolean");
assert.deepEqual(schema.properties.action.enum,actions.map(a=>a.action));
assert.equal(schema.allOf.length,103);
for(const a of actions){
  const branch=schema.allOf.find(x=>x.if.properties.action.const===a.action);
  assert.ok(branch,a.action+" missing");
  const ref=branch.then.properties.payload.$ref;
  const shape=schema.$defs[ref.split("/").at(-1)];
  assert.ok(shape?.type==="object",a.action+" lacks typed payload object");
  if(a.isMutating&&a.action!=="Logout"){
    assert.ok(shape.required.includes("operationId"),a.action+" does not require operationId");
    assert.equal(shape.properties.operationId.minLength,16);
  }
}
for(const action of ["AuthenticateLocalUser","CreatePerson","PreviewArchivedIdentity",
  "QueryPersons","QueryMonthlyGrid","SubmitAttendance","CommitInternalTransfer",
  "CommitExternalTransfer","CommitSeparation","BuildPersonLifetimeReport",
  "SelectImportWorkbook","ExportPersonLifetimeExcel","InspectBackup"]){
  const payload=schema.$defs["payload_"+action];
  assert.ok(Object.keys(payload.properties).some(x=>x!=="operationId"),
    action+" must declare real action fields");
}
const validDate=new RegExp(schema.$defs.payload_BuildPersonLifetimeReport.properties.dateFrom.pattern);
assert.ok(validDate.test("2026-11-01"),"ISO date pattern must accept yyyy-MM-dd");
assert.equal(validDate.test("20261101"),false,"date pattern must reject non-ISO dates");
assert.ok(!JSON.stringify(schema).includes("unsafe-inline"));
console.log("PHASE04 VERSIONED TYPED BRIDGE SCHEMA PASS — "+actions.length+" actions");
