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
const appController=read(base+"assets/js/altamam-app.js");
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

check(/\.admin-create-person\[open\]>\.admin-site-form\{/,css,
  "personnel creation uses a balanced RTL two-column form in the modal");
check(/\.admin-person-actions>\.btn-primary/,css,
  "personnel creation keeps a prominent full-width-friendly save action");
check(/\.admin-user-form \.admin-account-form-grid\{/,css,
  "account creation uses one responsive structured editor");
check(/\.admin-password-rule\.valid\{/,css,
  "live password requirements have a verified success presentation");
check(/function attachSecretReveal\(control\)/,admin,
  "user account secrets expose an accessible eye toggle");
check(/function livePasswordPolicy\(password,confirmation=null\)/,admin,
  "account policy is computed from the real field values");
check(/const changes=await showForm\(\[/,admin,
  "identity correction is edited together in one modal before the real preview");

check(/\.functional-export-preview:not\(\[hidden\]\)/,css,"Excel preview floats above the grid");
check(/\.person-report-tabs/,css,"report tabs use shared styles");
check(/body:has\(\.app-operational-modal/,css,"nested confirmation wins layering");
check(/function operationalModal\(/,admin,"shared administrative modal utility");
check(/panel\.replaceChildren\(header,body,actions\)/,admin,
  "administrative action windows retain a single framed modal");
check(/\.app-operational-modal>\.app-modal-sticky-header\{/,css,
  "administrative modal header remains outside the scrolling body");
check(/\.app-operational-modal>\.app-modal-scroll-body\{/,css,
  "administrative modal body scrolls independently");
check(/\.app-operational-modal>\.app-modal-sticky-footer:not\(:empty\)\{/,css,
  "administrative modal action footer stays outside the scrolling body");
check(/width:min\(96vw,1220px\);max-height:90dvh;min-height:0;overflow:hidden;/,css,
  "auto-prepared register respects modal-xl width and height");
check(/brightness>0\.179\?"#000000":"#FFFFFF"/,functional,
  "monthly contrast uses WCAG-safe black or white ink");
check(/siteModal\.open\(\)/,admin,"site modal opened by deliberate action");

check(/\.admin-site-create-form \.app-modal-scroll-body/,css,
  "site editor keeps accessible fields spaced inside the existing modal");
check(/\.admin-status-create-form \.admin-status-fields\{/,css,
  "status editor arranges the name, symbol and color in a responsive grid");
check(/\.admin-status-create-form \.status-live-preview\{/,css,
  "real-time status preview is readable and emphasized");
check(/livePreview\.setAttribute\("aria-live","polite"\)/,admin,
  "status-color preview is announced accessibly");
check(/title:"إعادة تسمية الموقع",size:"md"/,admin,
  "site rename uses a single input dialog");
check(/title:"تعديل حالة التحضير",size:"md"/,admin,
  "status editing uses one three-field dialog instead of serial prompts");
check(/title:"معاينة تعديل الحالة",confirmLabel:"تأكيد تعديل الحالة"/,admin,
  "status mutation still requires a separate old/new preview");

check(/statusModal\.open\(\)/,admin,"status modal opened by deliberate action");
check(/app-popup-when-properties/,admin,"backup inspection uses modal");
check(/app-reset-verification-modal/,admin,"final reset step uses modal");
check(/returnFocus=document\.activeElement/,functional,
  "auto-prepared modal restores focus on close");
check(/view\(\)\.appendChild\(overlay\)/,functional,
  "auto-prepared modal is owned by the active dashboard viewport");
check(/if\(autoPreparedOverlay\)container\.appendChild\(autoPreparedOverlay\)/,functional,
  "background refresh cannot silently dismiss a pending auto-attendance selection");
check(/overlay\.addEventListener\("keydown",event=>/,functional,
  "automatic attendance modal handles Escape consistently with confirmation");
check(/size:review\.persons\.length>25\?"xl":"lg"/,dialogs,
  "large multi-person transfer previews have sufficient room");
check(/details\.items\?\.length/,dialogs,
  "large import previews use a wider shared modal");

/* All text/select fields retain a persistent caption INSIDE their borders:
   first-run setup, administration, dialogs and historical reports. */
for(const family of ["form-field","admin-input","app-dialog-field",
  "person-report-filter","monthly-page-size"])
  assert.ok(css.includes("label."+family),family+" must use the shared inset caption");
assert.ok(css.includes(">span:first-child{") &&
  css.includes("position:absolute;inset-block-start:7px;inset-inline-start:13px;"),
  "the field title must be positioned inside its border at the RTL leading edge");
assert.ok(css.includes("padding-block:27px 8px"),
  "typed values must have separate space below the persistent inset title");
assert.ok(css.includes('input:not([type="checkbox"]):not([type="radio"]):not([type="file"])'),
  "the three native type exceptions must be on the SAME input selector");
for(const native of ["checkbox","radio","file"])
  assert.ok(css.includes(':not([type="'+native+'"])'),
    native+" must retain its accessible native presentation");
assert.ok(css.includes(".auth-form .auth-password-field>input.form-input{padding-inline-end:140px}"),
  "the password reveal button must not cover entered text");
assert.ok(report.includes('fromLabel.append(el("span","من تاريخ"),dateFrom)'),
  "historical report date fields must have inset labels");
assert.ok(report.includes('siteLabel.append(el("span","الموقع التاريخي"),site)'),
  "historical report selectors must have inset labels");


/* The first-run page is a real form, not an instructional screenshot:
   labels float over RTL fields; all credential semantics remain unchanged. */
check(/function decorateAuthField\(control, id, glyph\)/,functional,
  "authentication uses a single accessible floating-field builder");
check(/caption\.htmlFor=id;control\.input\.placeholder=" "/,functional,
  "caption remains a real label while focus/value move it");
check(/auth-setup-heading","التهيئة الأولى"/,functional,
  "first-run title is a plain heading outside the form card");
check(/auth-password-area/,functional,
  "password rules appear immediately after the password input");
check(/function addPasswordReveal\(control\)/,functional,
  "password and confirmation use icon-only, keyboard-accessible visibility buttons");
check(/aria-pressed",String\(visible\)/,functional,
  "password reveal communicates visible state");
check(/chips\[i\]\.indicator\.textContent=valid\?"✓":"○"/,functional,
  "rule indicators change in real time");
check(/pass\.input\.addEventListener\("input",refreshPolicy\)/,functional,
  "password requirements refresh on input");
check(/confirmation\.input\.addEventListener\("input",refreshPolicy\)/,functional,
  "password matching refreshes when confirmation changes");
check(/v=>v.length>0&&!\/\\s\/\.test\(v\)/,functional,
  "an empty password must not show a green no-spaces check");
check(/if\(optionalFullName\) payload\.fullName=optionalFullName/,functional,
  "founder full name stays optional");
check(/bridge\(\)\.invoke\("AuthenticateLocalUser",payload\)/,functional,
  "real offline C# provisioning endpoint is not replaced");
check(/\.auth-form \.form-input:focus~\.auth-floating-caption/,css,
  "caption floats on focus");
check(/\.auth-form \.form-input:not\(:placeholder-shown\)~\.auth-floating-caption/,css,
  "caption remains raised after typing");
check(/\.auth-form \.auth-floating-field>\.form-input:focus/,css,
  "active field gets an accent-colored focus border");
check(/\.auth-first-run \.auth-password-policy\{\s*display:grid/,css,
  "password rules are displayed as a responsive list");
check(/\.auth-setup-scroll>\.auth-info-box\{/,css,
  "founder instructions are readable in a light information panel");
check(/\.auth-first-run>\.btn\.btn-primary\{/,css,
  "primary action is full-width and touch-friendly");

/* First-run setup: the button must not be scrolled out of a short window. */
check(/const setupFields=el\("div","auth-setup-scroll"\)/,functional,
  "first-run fields are grouped in a bounded scrolling region");
check(/form\.replaceChildren\(brand,setupFields,submit\)/,functional,
  "first-run submit remains outside the fields scroller");
const firstRunLayout=css.slice(css.lastIndexOf("/* First-run small-window CTA visibility"));
assert.ok(firstRunLayout.length>0,
  "first-run layout override exists");
check(/#appViewport:has\(\.auth-first-run\)\{[^}]*min-height:0;[^}]*overflow:hidden/,firstRunLayout,
  "setup viewport fits the actual client area");
check(/\.auth-first-run\{[^}]*display:flex;[^}]*flex-direction:column;[^}]*min-height:0;[^}]*overflow:hidden/,firstRunLayout,
  "setup card has a bounded column layout");
check(/\.auth-setup-scroll\{[^}]*min-height:0;[^}]*overflow-y:auto;/,firstRunLayout,
  "only the founder form fields scroll");
check(/\.auth-first-run>\.btn\.btn-primary\{[^}]*flex:0 0 auto;/,firstRunLayout,
  "create-and-sign-in button remains visible below the scroll area");

assert.ok(css.lastIndexOf(".auth-form .auth-password-field>input.form-input{") >
  css.indexOf("padding-inline-end:140px"),
  "the new eye control must override legacy 140px text-button spacing");
check(/auth-password-policy/,functional,"live first-run password policy");
check(/auth-brand/,functional,"the branded login block appears inside both login and first-run forms");
check(/\.auth-brand-mark\{/,css,"login uses the local Cairo heading and teal symbol");
check(/\.auth-password-toggle\{\s*position:absolute;/,css,
  "password reveal stays inside its own password field");
check(/--status-dot/,functional,"saved status accent is derived from validated HEX");
check(/\.status-pill:before/,css,"status dot is generated by the shared design system");
check(/عدد ملفات Excel/,functional,"export previews disclose verified or pending file counts");
check(/auth-password-toggle/,functional,"login reveal toggle");
check(/app-preview-properties/,functional,"Excel preview summarizes scope");
check(/app-export-progress/,functional,"Excel export progress remains visible");
check(/attendance-drafts-modal/,attendance,"drafts removed from page flow");
check(/dialog-\s*"?sm"?/,dialogs.replace(/"dialog-"\+size/g,'dialog-sm'),"dialog sizes supported");
check(/form:\(fields,opts=/,dialogs,"multi-field dialog API available");
check(/dismiss:\(\)=>/,dialogs,"modal header close resolves a live promise");
check(/AltamamDialog\?\.dismiss\?\.\(\)/,appController,
  "legacy modal close uses the same cancellation and focus restoration path");
check(/person-report-chip-list/,report,"selected people use compact chips");
check(/person-report-tabs/,report,"report modal offers tab navigation");
check(/separation-selected/,separation,"selected person moves into modal");
const movement=read(base+"assets/js/personnel-movements.js");
check(/movement-retry-panel/,movement,"transfer retry stays over the source screen");
check(/transfer-review-summary/,css,"movement preview is a detailed modal");
check(/closed-correction-fields/,admin,"exceptional correction has a balanced form");
check(/closed-policy-banner/,admin,"exceptional correction exposes real policy feedback");
check(/audit-detail-overlay/,admin,"audit details do not render below the history");
check(/detailOpener=document\.activeElement\|\|null/,admin,
  "individual audit details remember the original row action for focus restoration");
check(/request===detailRequest&&openOverlay===overlay/,admin,
  "late audit detail results are ignored after dismissal or a later selection");
check(/overlay\.addEventListener\("keydown",event=>/,admin,
  "individual audit details keep Escape and keyboard focus inside the modal");
check(/!isCurrent\(\)\)return;/,admin,
  "late user confirmations cannot commit a dismissed audit edit");

check(/historyModal=operationalModal\(historyPanel,"modal-xl",true,true/,admin,
  "audit history is a true action-triggered overlay with a stable close control");
check(/request!==historySerial\|\|historyPanel\.hidden/,admin,
  "dismissed audit history ignores stale asynchronous server responses");
check(/historyContent\.replaceChildren\(revised\.wrap,nav\)/,admin,
  "audit paging retains the modal close control and keyboard focus");

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
check(/\.status-live-preview\{/,css,"status creation previews configured badge");
check(/returnFocus=document\.activeElement/,functional,"automatic register restores focus");
check(/let returnFocus=null;/,admin,"admin action dialogs restore their opening control");
check(/if\(event\.key!=="Tab"\)return;/,admin,"admin dialogs trap forward/backward keyboard Tab");
check(/livePreview\.style\.backgroundColor/,admin,"new status shows the selected color");

check(/\.monthly-revision-table\{/,css,"historical revision table scrolls inside modal");
check(/\.transfer-review-summary\{/,css,"transfer summary is structured in the modal");
check(/app-popup-on-content modal-lg/,admin,"import results are never appended below the table");

check(/historyDetails\.setAttribute\("aria-modal","true"\)/,admin,
  "import outcome opens as an accessible modal");
check(/const closeOutcome=\(\)=>\{\s*detailsSerial\+\+/,admin,
  "closing an import outcome invalidates pending reads");
check(/historyDetails\.replaceChildren\(dismiss,warning\("جارٍ قراءة تفاصيل الدفعة/,admin,
  "import batch details can be dismissed during native fetch");
check(/historyDetails\.replaceChildren\(dismiss,panel\)/,admin,
  "import outcome close button remains mounted across replies");

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
