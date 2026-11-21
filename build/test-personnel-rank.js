"use strict";
const assert=require("node:assert/strict");
const fs=require("node:fs");
const path=require("node:path");
const root=path.resolve(__dirname,"..");
const read=p=>fs.readFileSync(path.join(root,p),"utf8");
const vm=require("node:vm");
const context={window:{}};
vm.runInNewContext(read("src/AlTamam.WebUI/assets/js/roster-columns.js"),context);
const roster=context.window.AltamamRosterColumns;
assert.equal(roster.rank({rank:"نقيب"}),"نقيب");
assert.equal(roster.rank({rank:""}),"—","Unknown ranks must never be fabricated");
assert.equal(roster.rank({}),"—");

const consumerPaths=[
  "src/AlTamam.WebUI/assets/js/functional-screens.js",
  "src/AlTamam.WebUI/assets/js/admin-settings.js",
  "src/AlTamam.WebUI/assets/js/attendance-ui.js",
  "src/AlTamam.WebUI/assets/js/personnel-movements.js",
  "src/AlTamam.WebUI/assets/js/personnel-separation.js",
  "src/AlTamam.WebUI/assets/js/personnel-report.js"
];
for(const p of consumerPaths) {
  const source=read(p);
  assert.match(source,/AltamamRosterColumns\?\.rank\(/,
    p+" must show the same server-projected rank, never a local fake value");
  assert.match(source,/الرتبة/,p+" must label the rank in its person tables");
}
const css=read("src/AlTamam.WebUI/assets/css/altamam.css");
assert.match(css,/\.monthly-scroll \.fixed-five\s*\{/,
  "Monthly attendance has five pinned identity columns");
const schema=read("src/AlTamam.Infrastructure/Persistence/Migrations/V017_ProtectedPersonRank.sql");
assert.match(schema,/FLD-SYS-RANK/);
assert.match(schema,/sys_rank/);
assert.match(schema,/IsSystemProtected/);
console.log("PERSONNEL RANK UI/SCHEMA PASS: fixed third rank and protected persisted value");
