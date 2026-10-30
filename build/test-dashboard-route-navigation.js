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
console.log("Section 05 dashboard route: 4 redirect cases and HTML contract PASS");
