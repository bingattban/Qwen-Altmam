"use strict";
// Phase 11 audit-screen route visibility; exercised by both Phase 01 Windows CI workflows.
// Server-side RBAC remains the independent, authoritative enforcement boundary.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const js = fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/js/functional-screens.js"), "utf8");
const begin = js.indexOf("  function canSeeRoute(route) {");
const end = js.indexOf("\n  document.addEventListener(", begin);
assert.ok(begin >= 0 && end > begin, "route permission guard missing");
const guard = js.slice(begin, end);

const routes = [
  ["AuditLog", "screens/Administration/AuditLog/index.html"],
  ["EditAuditEntry", "screens/Administration/AuditLog/EditAuditEntry/index.html"],
  ["DeleteAuditEntry", "screens/Administration/AuditLog/DeleteAuditEntry/index.html"],
  ["ModifiedDeletedAuditEntries",
    "screens/Administration/AuditLog/ModifiedDeletedAuditEntries/index.html"],
  ["Users", "screens/Administration/Users/index.html"],
  ["ResetApplicationData", "screens/Settings/ResetApplicationData/index.html"],
  ["ScheduledBackups", "screens/Settings/ScheduledBackups/index.html"],
  ["EightPmScheduler", "screens/SharedScreens/EightPmScheduler/index.html"]
];

const expectations = {
  GeneralAdministration: [true, true, true, true, true, true, true, true],
  OrdinaryAdministration: [true, true, true, true, true, false, true, true],
  Supervisor: [true, false, false, false, false, false, false, false],
  unauthenticated: [false, false, false, false, false, false, false, false]
};

for (const [role, expected] of Object.entries(expectations)) {
  const STATE = {session: role === "unauthenticated" ? null : {role}};
  const visible = vm.runInNewContext(guard + "\ncanSeeRoute",
    {STATE}, {timeout: 1000});
  routes.forEach(([key, routePath], index) => {
    assert.strictEqual(visible({key, path: routePath}), expected[index],
      role + " must have correct route visibility for " + key);
  });
}
// Execute the real deep-link resolver with synthetic session/route state.
// It must pass the canonical route to Settings, not land on generic settings,
// and must reject a forged Supervisor scheduler/reset link before rendering it.
const openStart=js.indexOf("  function openRoute(routeKey){");
const openEnd=js.indexOf("\n  async function boot()",openStart);
assert.ok(openStart>=0&&openEnd>openStart,"real canonical route dispatcher missing");
const openSource=js.slice(openStart,openEnd);
const forwarded=[];
const mockSession={role:"GeneralAdministration"};
const routeByKey=Object.fromEntries([
 "CreateBackup","ScheduledBackups","RestoreBackup","AppearanceAndDiagnostics",
 "EightPmScheduler","ResetApplicationData","Logout","AboutApplication"
].map(key=>[key,{key,path:key==="EightPmScheduler"?
 "screens/SharedScreens/EightPmScheduler/index.html":
 "screens/Settings/"+key+"/index.html"}]));
const nav={getRoute:key=>routeByKey[key]||null,sectionForRoute:()=>"settings"};
const STATE={session:mockSession};
const dispatcher=vm.runInNewContext(guard+"\n"+openSource+"\nopenRoute",
 {STATE,nav,render:(...args)=>forwarded.push(args),view:()=>({appendChild:()=>{}}),
 message:()=>({}),openAutoPreparedList:()=>{},
 window:{AltamamAdminSettings:{openOwnedRoute:()=>true}}}, {timeout:1000});
for(const key of Object.keys(routeByKey)){
 assert.strictEqual(dispatcher(key),true,key+" should dispatch to a real settings owner");
 assert.deepEqual(Array.from(forwarded.at(-1)),["settings",key],
   key+" should preserve its intent to the authenticated settings controller");
}
assert.strictEqual(dispatcher("UnknownRoute"),false,"unknown routes fail closed");
mockSession.role="Supervisor";
const before=forwarded.length;
assert.strictEqual(dispatcher("ScheduledBackups"),true);
assert.deepEqual(Array.from(forwarded.at(-1)),["admin"],
 "Supervisor scheduler deep link must be denied");
assert.strictEqual(dispatcher("EightPmScheduler"),true);
assert.deepEqual(Array.from(forwarded.at(-1)),["admin"],
 "Supervisor 08:00 policy link must be denied");
assert.strictEqual(dispatcher("ResetApplicationData"),true);
assert.deepEqual(Array.from(forwarded.at(-1)),["admin"],
 "Supervisor reset must never be reachable");
assert.strictEqual(forwarded.length,before+3);
console.log("AUDIT ROUTE RBAC PASS: 32 route/role checks including both audit display writes");
