"use strict";
const assert=require("node:assert/strict");
const fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
const root=path.resolve(__dirname,"..");
const read=p=>fs.readFileSync(path.join(root,p),"utf8");

const context={window:{}};
vm.runInNewContext(read("src/AlTamam.WebUI/assets/js/roster-columns.js"),
  context,{filename:"roster-columns.js"});
const roster=context.window.AltamamRosterColumns;
assert.ok(roster&&Object.isFrozen(roster),"Shared offline renderer must be immutable");
const records={customColumns:[
  {columnId:"FLD-1",caption:"المؤهل",displayOrder:5},
  {columnId:"FLD-2",caption:"رقم آخر",displayOrder:6}
]};
const person={personId:"P-1",customFields:{"FLD-1":"بكالوريوس","FLD-2":"007"},
  "custom_legacy":"wrong"};
assert.equal(roster.columns(records).length,2);
assert.equal(roster.headings(roster.columns(records)).join("|"),"المؤهل|رقم آخر");
assert.equal(roster.values(person,roster.columns(records)).join("|"),"بكالوريوس|007");
assert.equal(roster.values({personId:"P-2"},roster.columns(records)).join("|"),"|");
records.customColumns[0].caption="المؤهل الجديد";
assert.equal(roster.value(person,roster.columns(records)[0]),"بكالوريوس",
  "Value identity may not depend on mutable caption");
assert.equal(roster.columns({customColumns:[]}).length,0);
assert.equal(roster.columns({}).length,0);

const consumers=[
"src/AlTamam.WebUI/assets/js/functional-screens.js",
"src/AlTamam.WebUI/assets/js/admin-settings.js",
"src/AlTamam.WebUI/assets/js/attendance-ui.js",
"src/AlTamam.WebUI/assets/js/personnel-report.js",
"src/AlTamam.WebUI/assets/js/personnel-movements.js",
"src/AlTamam.WebUI/assets/js/personnel-separation.js"
];
for(const file of consumers){
  const source=read(file);
  assert.match(source,/AltamamRosterColumns\?\.columns\(/,
    file+" must consume authoritative column metadata");
  assert.match(source,/AltamamRosterColumns\?\.values\(/,
    file+" must render the matching immutable ColumnId values");
  assert.doesNotMatch(source,/\.innerHTML\s*=/,
    file+" must not inject untrusted person-field HTML");
}

const index=read("src/AlTamam.WebUI/index.html");
assert.ok(index.indexOf("assets/js/roster-columns.js")>
  index.indexOf("assets/js/altamam-app.js"));
assert.ok(index.indexOf("assets/js/roster-columns.js")<
  index.indexOf("assets/js/functional-screens.js"));
assert.ok(index.indexOf("assets/js/roster-columns.js")<
  index.indexOf("assets/js/admin-settings.js"));

const application=[
"src/AlTamam.Application/Bridge/PersonRosterService.cs",
"src/AlTamam.Application/Bridge/DailyMonthlyReadService.cs",
"src/AlTamam.Application/Bridge/PersonnelReportService.cs",
"src/AlTamam.Application/Bridge/PersonnelMovementService.cs",
"src/AlTamam.Application/Bridge/PersonnelSeparationService.cs"
];
for(const file of application)
  assert.match(read(file),/RosterCustomFieldProjection\.AttachAsync\(/,
    file+" must query real SQLCipher person fields");
for(const file of [
"src/AlTamam.Application/Bridge/MonthlyExcelExportService.cs",
"src/AlTamam.Application/Bridge/PersonnelExcelExportService.cs"])
  assert.match(read(file),/RosterCustomFieldProjection\.(Cell|VisibleAsync)\(/,
    file+" must export the same visible columns as UI");
const backend=read("src/AlTamam.Application/Bridge/RosterCustomFieldProjection.cs");
assert.match(backend,/ActiveFlag=1 AND VisibleFlag=1 AND IsSystemProtected=0/);
assert.match(backend,/ORDER BY DisplayOrder, FieldDefinitionId/);
assert.match(backend,/WHERE PersonId IN \(/);
assert.match(backend,/CustomFieldValues/);
assert.match(backend,/\["personId"\]/);
assert.doesNotMatch(backend,/UPDATE CustomFieldValues|DELETE FROM CustomFieldValues/,
  "Read-only global propagation may not rewrite historical/person fields");
console.log("GLOBAL ROSTER COLUMNS PASS: shared ColumnId semantics, all person screens, native Excel exporters, hidden-field guards");
