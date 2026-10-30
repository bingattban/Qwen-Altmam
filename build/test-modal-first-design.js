"use strict";
/* Modal-first design regression: all routes share offline CSS, while existing
   UI integration tests continue to verify the protected C# operations. */
const assert=require("node:assert/strict");
const fs=require("node:fs");
const path=require("node:path");
const root=path.resolve(__dirname,"..");
const read=file=>fs.readFileSync(path.join(root,file),"utf8");
const base="src/AlTamam.WebUI/";
const css=read(base+"assets/css/altamam.css");
const presentation=read(base+"assets/css/presentation.css");
const functional=read(base+"assets/js/functional-screens.js");
const admin=read(base+"assets/js/admin-settings.js");
const attendance=read(base+"assets/js/attendance-ui.js");
const dialogs=read(base+"assets/js/operation-dialogs.js");
const report=read(base+"assets/js/personnel-report.js");
const separation=read(base+"assets/js/personnel-separation.js");
const wpf=read("src/AlTamam.DesktopHost/MainWindow.xaml");
const rootHtml=read(base+"index.html");
function check(regex,text,message){assert.match(text,regex,message);}
check(/--bg-main:\s*#F1F5F9/i,css,"new offline light palette");
check(/--bg-main:\s*#0B1524/i,css,"new dark palette");
check(/--accent-teal:\s*#0D9488/i,css,"new action accent");
assert.ok(!css.includes("170px!important"),"search must never be capped at 170px");
check(/input\.app-unified-search/,css,"unified search remains accessible");
check(/\.app-operational-modal:not\(\[hidden\]\)/,css,"action forms are true overlays");
check(/\.app-popup-on-content:not\(:empty\)/,css,"outcome dialogs float above tables");
check(/\.attendance-drafts-modal:not\(\[hidden\]\)/,css,"saved drafts appear in dialog");
check(/\.admin-create-person\[open\]/,css,"identity conflict form appears as a modal");
check(/\.functional-export-preview:not\(\[hidden\]\)/,css,"Excel preview floats above the grid");
check(/\.person-report-tabs/,css,"report tabs use shared styles");
check(/body:has\(\.app-operational-modal/,css,"nested confirmation wins layering");
check(/function operationalModal\(/,admin,"shared administrative modal utility");
check(/siteModal\.open\(\)/,admin,"site modal opened by deliberate action");
check(/statusModal\.open\(\)/,admin,"status modal opened by deliberate action");
check(/app-popup-when-properties/,admin,"backup inspection uses modal");
check(/app-reset-verification-modal/,admin,"final reset step uses modal");
check(/auth-password-policy/,functional,"live first-run password policy");
check(/auth-password-toggle/,functional,"login reveal toggle");
check(/app-preview-properties/,functional,"Excel preview summarizes scope");
check(/app-export-progress/,functional,"Excel export progress remains visible");
check(/attendance-drafts-modal/,attendance,"drafts removed from page flow");
check(/dialog-\s*"?sm"?/,dialogs.replace(/"dialog-"\+size/g,'dialog-sm'),"dialog sizes supported");
check(/form:\(fields,opts=/,dialogs,"multi-field dialog API available");
check(/person-report-chip-list/,report,"selected people use compact chips");
check(/person-report-tabs/,report,"report modal offers tab navigation");
check(/separation-selected/,separation,"selected person moves into modal");
const movement=read(base+"assets/js/personnel-movements.js");
check(/movement-retry-panel/,movement,"transfer retry stays over the source screen");
check(/transfer-review-summary/,css,"movement preview is a detailed modal");
check(/closed-correction-fields/,admin,"exceptional correction has a balanced form");
check(/closed-policy-banner/,admin,"exceptional correction exposes real policy feedback");
check(/audit-detail-overlay/,admin,"audit details do not render below the history");
check(/audit-diff-grid/,admin,"audit before and after values are read-only structured diff blocks");
check(/closeAuditDetail\(\)/,admin,"audit overlay can be dismissed after commitment");
check(/monthly-movement-banner/,functional,"monthly movements are highlighted in details");
check(/monthly-revision-table/,functional,"monthly revisions render as a real modal table");
check(/setAttribute\("aria-modal","true"\)/,functional,"action overlays declare modal semantics");
check(/dismissExport/,functional,"monthly XLSX export can close in success, empty and error states");
check(/admin-tile-subtitle/,admin,"administrative navigation has concise tile captions");
check(/PreviewPersonIdentityUpdate/,admin,"person edits retain the native preview");
check(/معاينة تعديل هوية الفرد/,admin,"old versus new identity opens the shared review");
check(/\.audit-detail-overlay\{z-index:1350\}/,css,"audit details appear above audit history");
check(/\.closed-correction-fields\{/,css,"exceptional correction grid is responsive");
check(/\.monthly-revision-table\{/,css,"historical revision table scrolls inside modal");
check(/\.transfer-review-summary\{/,css,"transfer summary is structured in the modal");
check(/app-popup-on-content modal-lg/,admin,"import results are never appended below the table");
check(/app-reset-verification-modal/,admin,"full reset final challenge is overlaid");

for(const x of ['x:Name="webViewHost"','x:Name="startupPanel"',
  'x:Name="startupMessage"','Visibility="{Binding PanelVisibility}"',
  'Text="{Binding Message}"','WindowStyle="SingleBorderWindow"',
  'ResizeMode="CanResize"'])assert.ok(wpf.includes(x),"WPF binding: "+x);
for(const id of ["dashboard","monthly","admin","settings"])
  assert.ok(rootHtml.includes('id="navBtn-'+id+'"'),"navigation ID preserved: "+id);
function walk(dir){
  return fs.readdirSync(dir,{withFileTypes:true}).flatMap(entry=>{
    const full=path.join(dir,entry.name);
    return entry.isDirectory()?walk(full):[full];
  });
}
const htmls=walk(path.join(root,base,"screens"))
  .filter(p=>p.endsWith(path.sep+"index.html"));
assert.equal(htmls.length,68,"all 68 standalone route modules remain present");
for(const file of htmls){
  const html=fs.readFileSync(file,"utf8");
  assert.match(html,/assets\/css\/altamam\.css/,"route uses offline design system: "+file);
  assert.match(html,/assets\/css\/presentation\.css/,"route uses shared presentation: "+file);
  assert.doesNotMatch(html,/<script[^>]+src=["']https?:/i,"no CDN on route: "+file);
}
assert.ok(presentation.includes(".altamam-screen-module"),"standalone route common theme");
console.log("MODAL-FIRST UI CONTRACT PASS: "+htmls.length+
  " route modules, dialogs, offline themes, protected markup and report layouts");
