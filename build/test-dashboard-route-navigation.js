"use strict";
// Section 05.05: legacy Dashboard route must open the actual authenticated
// main-window dashboard, never a fake REFRESH / placeholder shell.
const assert=require("node:assert/strict");
const fs=require("node:fs");
const path=require("node:path");
const vm=require("node:vm");
const root=path.resolve(__dirname,"..");
const html=fs.readFileSync(path.join(root,
  "src/AlTamam.WebUI/screens/Dashboard/index.html"),"utf8");
const script=fs.readFileSync(path.join(root,
  "src/AlTamam.WebUI/assets/js/auto-attendance-route.js"),"utf8");
assert.match(html,/data-route-key="Dashboard"/);
assert.match(html,/assets\/js\/auto-attendance-route\.js/);
assert.doesNotMatch(html,/يجري ربط الشاشة خلال مراحل تنفيذ المشروع/);
assert.doesNotMatch(html,/data-bridge-action="REFRESH"/);
assert.match(html,/\.\.\/\.\.\/index\.html#dashboard/);
function run(route){
  const visited=[];
  const context={document:{body:{dataset:{routeKey:route}}},
    window:{location:{replace:(url)=>visited.push(url)}}};
  vm.runInNewContext(script,context,{timeout:1000});
  return visited;
}
assert.deepEqual(run("Dashboard"),["../../index.html#dashboard"]);
assert.deepEqual(run("UnpreparedPersonnel"),["../../../index.html#auto-prepared"]);
assert.deepEqual(run("SelectStatusAndConfirm"),["../../../../index.html#auto-prepared"]);
assert.deepEqual(run("UnknownRoute"),[]);

// Canonical four-destination routing must include the three root index.html
// files, not only their nested descendants. Before this regression, all three
// accidentally fell through to Settings in the production route resolver.
const mainScript=fs.readFileSync(path.join(root,
  "src/AlTamam.WebUI/assets/js/altamam-app.js"),"utf8");
const navContext={document:{addEventListener:()=>{}},
  window:{addEventListener:()=>{},location:{assign:()=>{}}},
  history:{replaceState:()=>{}}};
vm.runInNewContext(mainScript,navContext,{timeout:1000,filename:"altamam-app.js"});
const navigation=navContext.window.AltamamNavigation;
assert.ok(navigation?.getRoute && navigation?.sectionForRoute,
  "the real unified main-window resolver must be available");
const roots={Dashboard:"dashboard",MonthlyAttendance:"monthly",
  Administration:"admin",Settings:"settings"};
for(const [key,section] of Object.entries(roots)){
  const route=navigation.getRoute(key);
  assert.ok(route,"missing canonical root "+key);
  assert.equal(navigation.sectionForRoute(route),section,
    key+" must open its actual top-level destination, not Settings by default");
}
const classified=[
 ["UnpreparedPersonnel","dashboard"],["SelectStatusAndConfirm","dashboard"],
 ["SelectArchive","monthly"],["ConfirmAttendance","admin"],
 ["Users","admin"],["CreateBackup","settings"],
 ["EightPmScheduler","settings"]
];
for(const [key,destination] of classified)
  assert.equal(navigation.sectionForRoute(navigation.getRoute(key)),destination,
    "child route regression "+key);
assert.equal(navigation.getRoute("MissingRoute"),null,
  "unknown routes may not become a synthetic destination");
const functional=fs.readFileSync(path.join(root,
  "src/AlTamam.WebUI/assets/js/functional-screens.js"),"utf8");
const guardStart=functional.indexOf("  function canSeeRoute(route) {");
const guardEnd=functional.indexOf("\n  document.addEventListener(",guardStart);
const dispatchStart=functional.indexOf("  function openRoute(routeKey){");
const dispatchEnd=functional.indexOf("\n  async function boot()",dispatchStart);
assert.ok(guardStart>=0&&guardEnd>guardStart&&dispatchStart>=0&&dispatchEnd>dispatchStart);
const active={session:{role:"GeneralAdministration"}},actual=[];
const dispatcher=vm.runInNewContext(
  functional.slice(guardStart,guardEnd)+"\n"+
  functional.slice(dispatchStart,dispatchEnd)+"\nopenRoute",
  {STATE:active,nav:navigation,render:(...args)=>actual.push(args),
    view:()=>({appendChild:()=>{}}),message:()=>({}),
    openAutoPreparedList:()=>{},window:{AltamamAdminSettings:{
      openOwnedRoute:()=>true}}},{timeout:1000});
for(const [key,destination] of Object.entries(roots)){
  assert.equal(dispatcher(key),true);
  assert.equal(actual.at(-1)?.[0],destination,
    "authenticated "+key+" deep link must render its own real destination");
}
assert.equal(dispatcher("UnknownRoot"),false);
console.log("SECTION05 ROOT ROUTING PASS: 4 roots + 7 child routes + 4 authenticated dispatches");

console.log("Section 05 dashboard route: 4 redirect cases and HTML contract PASS");
