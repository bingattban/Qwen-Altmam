// Dependency-free DOM smoke test of the real admin/settings controller.
// Runs on Node in both Windows CI workflows. No network, no synthetic writes.
"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");

class Element {
  constructor(tag) {
    this.tag = tag;
    this.children = [];
    this.className = "";
    this.textContent = "";
    this.dataset = {};
    this.attrs = {};
    this.handlers = {};
    this.disabled = false;
    this.isConnected = true;
    this.value = "";
    this.style = {};
  }
  get firstChild() { return this.children[0]||null; }
  get classList() {
    return {
      add: (...tokens) => { this.className += " " + tokens.join(" "); },
      contains: token => this.className.split(/\s+/).includes(token),
      toggle: (token, force) => {
        const tokens=this.className.split(/\s+/).filter(Boolean);
        const has=tokens.includes(token);
        const next=typeof force==="boolean"?force:!has;
        if(next&&!has)tokens.push(token);
        if(!next&&has)tokens.splice(tokens.indexOf(token),1);
        this.className=tokens.join(" ");
        return next;
      }
    };
  }
  append(...items) { this.children.push(...items); }
  appendChild(item) { this.children.push(item); return item; }
  prepend(item) { this.children.unshift(item); }
  replaceChildren(...items) { this.children = [...items]; }
  after(item) {
    const parent=viewport.all().find(x=>x.children.includes(this));
    if(parent)parent.children.splice(parent.children.indexOf(this)+1,0,item);
  }
  remove(){
    const parent=viewport.all().find(x=>x.children.includes(this));
    if(parent)parent.children.splice(parent.children.indexOf(this),1);
  }
  setAttribute(key, val) { this.attrs[key] = val; }
  getAttribute(key) {return this.attrs[key]||null;}
  focus() {document.activeElement=this;}
  querySelectorAll() {return this.all().filter(x=>["button","input","select","textarea"].includes(x.tag)&&!x.disabled);}
  addEventListener(name, fn) { this.handlers[name] = fn; }
  querySelector(selector) {
    const cls = selector.slice(1);
    return this.all().find(n => n.className.split(/\s+/).includes(cls)) || null;
  }
  all() { return [this, ...this.children.flatMap(x => x instanceof Element ? x.all() : [])]; }
}
const viewport = new Element("main");
const calls = [];
let showRosterPerson = false;
let deferAuditHistory = false, completeDeferredAuditHistory = null;
let showAuditEntry = false, deferAuditDetail = false, completeDeferredAuditDetail = null;
const auditTestEntry = {
  auditEventId:"AUDIT-TEST-1",sequence:1,operationArabicName:"تعديل",
  targetDisplay:"فرد تجريبي",actorUsername:"admin",actorDisplay:"مسؤول",
  executedAtUtc:"2026-10-30T18:00:00Z",rawDescription:"قيد أصلي",
  description:"قيد أصلي",reason:"تجربة",outcome:"تم الاعتماد",
  oldValue:{status:"قديم"},newValue:{status:"جديد"},
  overlays:[],chainValid:true
};
const document = {
  activeElement:null,
  getElementById: key => key === "appViewport" ? viewport : null,
  createElement: tag => new Element(tag)
};
const bridge = {
  invoke: async (name, body) => {
    calls.push({ name, body });
    if(name==="GetAuditEntryDetails"){
      const reply={data:auditTestEntry};
      if(deferAuditDetail)return new Promise(resolve=>{
        completeDeferredAuditDetail=()=>resolve(reply);
      });
      return reply;
    }
    if(name==="QueryAuditPresentationChanges" && deferAuditHistory){
      return new Promise(resolve=>{
        completeDeferredAuditHistory=()=>resolve({data:{items:[],total:0,hasNext:false}});
      });
    }
    const responses = {
      GetSettingsOverview: { username:"test-user", roleLabelAr:"الإدارة العامة",
        backupDirectory:"E:\\AlTamamAlYawmi\\Backups", backupTime:"16:00",
        automaticBackupNote:"الجدولة غير نشطة", defaultDriveAvailable:false },
      GetApplicationVersion: { productNameAr:"التمام اليومي", version:"0.4.0" },
      GetAutoAttendancePolicy: {configured:false,defaultStatusId:null,dueTimeLocal:"08:00",dailyLockTimeLocal:"20:00"},
      SetAttendanceSchedule: {dueTimeLocal:"07:15",dailyLockTimeLocal:"19:30",
        defaultStatusId:"STS-ABSENT"},
      SetAutoAttendanceDefaultStatus: {defaultStatusId:"STS-ABSENT",defaultStatusName:"غياب",configured:true},
      RunDatabaseIntegrityCheck: { integrity:"ok" },
      InspectBackup: {verified:true,fileName:"verified.dbbackup",
        path:"C:\\Temp\\verified.dbbackup",fileSizeBytes:4096,
        schemaVersion:9,schemaCompatible:true,people:5,attendance:8,
        auditEvents:9,users:1,sha256:"a".repeat(64),
        message:"النسخة المشفرة سليمة؛ الاستعادة ليست مفعلة بعد."},
      PreviewRestore: {verified:true,previewOnly:true,restoreEnabled:false,
        fileName:"verified.dbbackup",sha256:"a".repeat(64),
        incoming:{people:5,attendance:8,auditEvents:9,users:1},
        current:{people:7,attendance:12,auditEvents:13,users:2}},
      GetAllowedRoles: [{ role:"Supervisor",name:"مشرف" }],
      ListUserAccounts: { items:[],total:0,hasNext:false },
      QueryAuditLog: { items:showAuditEntry?[auditTestEntry]:[],
        total:showAuditEntry?1:0,hasNext:false },
      QueryAuditPresentationChanges: { items:[],total:0,hasNext:false },
      GetActiveAttendanceStatuses: [{statusId:"STS-ABSENT",displayName:"غياب",
        shortSymbol:"غ",color:"#D97706",version:3}],
      GetCustomFieldDefinitions:{items:[
        {columnId:"FLD-SYS-NAME",technicalKey:"sys_person_name",caption:"الاسم",
          fieldType:1,displayOrder:3,isSystemProtected:true,
          active:true,visible:true,storedValueCount:0,snapshot:"A".repeat(64)},
        {columnId:"FLD-TST-ONE",technicalKey:"custom_one",caption:"جهة العمل",
          fieldType:1,displayOrder:5,isSystemProtected:false,
          active:true,visible:true,storedValueCount:2,snapshot:"B".repeat(64)},
        {columnId:"FLD-TST-TWO",technicalKey:"custom_two",caption:"ملاحظة",
          fieldType:1,displayOrder:6,isSystemProtected:false,
          active:true,visible:true,storedValueCount:0,snapshot:"C".repeat(64)}
      ],total:3},
      CreateCustomFieldDefinition:{columnId:"FLD-TST-NEW"},
      UpdateCustomFieldMetadata:{columnId:"FLD-TST-ONE"},
      DeactivateCustomField:{columnId:"FLD-TST-ONE"},
      QuerySites:[{siteId:"SITE-RETURN",name:"الموقع الجديد",active:true,rowVersion:4},
        {siteId:"SITE-REMOVABLE",name:"موقع تجريبي قابل للإزالة",active:true,rowVersion:3},
        {siteId:"SITE-UNSPECIFIED",name:"غير محدد",active:true,rowVersion:1}],
      QueryPersons: showRosterPerson && name === "QueryPersons" &&
        body?.organizationalStatus === 1
        ? {items:[{personId:"TST-ACTIVE-1",displayName:"فرد في القوة",
            serviceNumberA:"91001",siteName:"غير محدد",revision:1}],
            total:1,hasNext:false}
        : {items:[],total:body?.siteId==="SITE-REMOVABLE"?2:0,hasNext:false},
      QuerySeparationArchive:{items:[{movementId:"SEP-TEST",serviceNumberA:"91001",
        name:"مفصول تجريبي",siteName:"غير محدد",date:"2026-10-30"}],total:1,hasNext:false},
      PreviewArchivePersons:{candidates:[{personId:"TST-ACTIVE-1",
        displayName:"فرد في القوة",serviceNumberA:"91001",
        currentSiteName:"غير محدد",attendanceDays:3,blocker:null}],
        effectiveLocalDate:"2026-10-29",baseRevision:1,
        previewToken:"P".repeat(64),reversible:true},
      ArchivePersons:{archivedPersonIds:["TST-ACTIVE-1"],
        effectiveLocalDate:"2026-10-29"},
      RemoveSiteAndReassignCurrentPersons:{siteId:"SITE-REMOVABLE",reassignedCount:2,active:false},
      RenameSite:{siteId:"SITE-RETURN"},
      UpdateAttendanceStatus:{statusId:"STS-ABSENT"},
      PreviewArchivedIdentity:body.serviceNumberA==="4141"
        ?{requiresResolution:true,previewToken:"T".repeat(64),
          candidates:[{personId:"TST-ARCHIVED-P1",name:"شخص تجريبي قديم",
            serviceNumberA:"4141",previousSiteName:"الموقع التاريخي",exitDate:"2026-10-12",exitType:"نقل خارجي",numberMatch:true,nameMatch:false}]}
        :{requiresResolution:true,previewToken:"U".repeat(64),
          candidates:[{personId:"TST-ARCHIVED-P2",name:"شخص جديد مكرر",
            serviceNumberA:"5151",exitDate:"2026-10-12",exitType:"فصل",numberMatch:false,nameMatch:true}]},
      CommitArchivedReturn:{returned:true,personId:"TST-ARCHIVED-P1"},
      CreatePerson:{personId:"PRN-NEW-DIFFERENT"},
      PreviewPersonIdentityUpdate:{
        oldName:"فرد في القوة",newName:"الاسم المحدّث",
        oldServiceNumberA:"91001",newServiceNumberA:"91002"
      },
      UpdatePersonIdentity:{personId:"TST-ACTIVE-1"},
      SearchPersons: {items:[{personId:"TST-CLOSED-P1",displayName:"اختبار",serviceNumberA:"C-1"}]},
      SetClosedDayCorrectionPolicy: {enabled:true},
      PreviewClosedDayCorrection: {previewToken:"T".repeat(64),requiresExplicitConfirmation:true,
        personName:"اختبار",serviceNumberA:"C-1",siteName:"غير محدد",
        businessDate:"2026-10-10",beforeStatusName:"موجود",afterStatusName:"غياب",reason:"تصحيح مستند"},
      PreviewAdministrativeAttendanceCatchUp: {previewToken:"B".repeat(64),
        requiresExplicitConfirmation:true,businessDate:"2026-10-29",
        reason:"استدراك موثق لحالة تحضير ناقصة",pendingCount:3,eligibleCount:4},
      CommitAdministrativeAttendanceCatchUp: {businessDate:"2026-10-29",
        appliedCount:3,summary:"تم الاستدراك"},
      CommitClosedDayCorrection: {revisionNumber:2}
    };
    if (!(name in responses)) throw Error("Unexpected bridge action " + name);
    return { data: responses[name] };
  }
};
const modalReviews=[];
const editDialogRequests=[];
let nextIdentityForm=null,nextConfigForm=null;
let approveDetailPreview=false;
const context = { window:{ AltamamBridge:bridge,AltamamDialog:{
  detailsReview:async(details,opts)=>{modalReviews.push({details,opts});return approveDetailPreview;},
  form:async(fields,opts)=>{
    editDialogRequests.push({fields,opts});
    return ["إعادة تسمية الموقع","تعديل حالة التحضير"].includes(opts.title)?
      nextConfigForm:nextIdentityForm;
  },
  confirm:async()=>true, prompt:async(message,value="")=>value, alert:async()=>true
 } }, document, console,
  alert: () => { throw Error("Unexpected alert"); },
  prompt: () => { throw Error("Unexpected prompt"); },
  confirm: () => true };
const script = fs.readFileSync(
  path.join(__dirname,"../src/AlTamam.WebUI/assets/js/admin-settings.js"),"utf8");
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/js/personnel-separation.js"),"utf8"),
  context,{filename:"personnel-separation.js"});
vm.runInNewContext(script,context,{filename:"admin-settings.js"});
const controller = context.window.AltamamAdminSettings;
const visible = () => viewport.all();
const byDataset = (key) => visible().filter(x => x.dataset[key]);
const buttonNamed = name => visible().find(x => x.tag==="button" && x.textContent===name);

async function run() {
  const admin = { role:"GeneralAdministration", username:"test-user" };
  controller.renderAdmin(admin);
  // Canonical legacy screens open their existing owner; modal-like routes do not
  // execute a mutation without the normal user selection and confirmation.
  assert.equal(controller.openOwnedRoute("PersonnelManagement"),true);
  assert.ok(byDataset("adminScreen").some(x=>x.dataset.adminScreen==="attendance"));
  controller.renderAdmin(admin);
  assert.equal(controller.openOwnedRoute("StatusesAndSites"),true);
  assert.deepEqual(byDataset("adminScreen").map(x=>x.dataset.adminScreen),
    ["sites","statuses"]);
  controller.renderAdmin(admin);
  assert.equal(controller.openOwnedRoute("ConfirmAttendance"),true);
  assert.ok(!calls.some(x=>x.name==="SubmitAttendance"),
    "opening confirmation route may not commit attendance");
  controller.renderAdmin({role:"Supervisor",username:"supervisor"});
  assert.equal(controller.openOwnedRoute("Users"),false,
    "supervisor cannot deep-link to account management");
  assert.equal(controller.openOwnedRoute("ClosedDayExceptionalCorrection"),false,
    "supervisor cannot deep-link to closed-day correction");
  controller.renderAdmin(admin);
  assert.equal(controller.openOwnedRoute("UnknownStandaloneRoute"),false);
  controller.renderAdmin(admin);
  assert.equal(byDataset("adminSection").length,5,"five primary administrative cards");
  assert.equal(byDataset("adminScreen").length,0,"no nested options on main admin page");
  assert.deepEqual(byDataset("adminSection").map(x=>x.dataset.adminSection),
    ["force","config","sheets","users","audit"]);
  byDataset("adminSection")[0].handlers.click();
  assert.equal(byDataset("adminSection").length,0,"top-level cards hidden in subgroup");
  assert.deepEqual(byDataset("adminScreen").map(x=>x.dataset.adminScreen),
    ["attendance","persons","transfers","separation","personReport","closedCorrection"]);
  byDataset("adminScreen")[0].handlers.click();
  assert.equal(byDataset("adminScreen").length,0,"only selected detail page is visible");
  assert.ok(buttonNamed("العودة إلى إدارة القوة"),"back from detail to parent");
  buttonNamed("العودة إلى إدارة القوة").handlers.click();
  assert.equal(byDataset("adminScreen").length,6,"archive must be nested under the separation screen");
  buttonNamed("العودة إلى الإدارة").handlers.click();
  assert.equal(byDataset("adminSection").length,5,"return to clean admin landing");

  // Nested administration pages use the same isolated card pattern.
  byDataset("adminSection").find(x=>x.dataset.adminSection==="force").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="transfers").handlers.click();
  assert.deepEqual(byDataset("adminNested").map(x=>x.dataset.adminNested),
    ["transferInternal","transferExternal","archiveInternal","archiveExternal"]);
  byDataset("adminNested")[0].handlers.click();
  assert.equal(byDataset("adminNested").length,0,"nested cards hidden after opening a leaf");
  buttonNamed("العودة إلى التنقلات").handlers.click();
  assert.equal(byDataset("adminNested").length,4,"back restores transfer and both archive choices");
  buttonNamed("العودة إلى إدارة القوة").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="separation").handlers.click();
  assert.equal(byDataset("adminNested").length,0,
    "separation opens the actual screen rather than a second menu");
  assert.ok(buttonNamed("فصل فرد") && buttonNamed("أرشيف الفصل"),
    "archive tab lives on the separation screen");
  await buttonNamed("أرشيف الفصل").handlers.click();
  assert.ok(calls.some(x=>x.name==="QuerySeparationArchive"),
    "archive tab calls historical archive read");
  assert.equal(controller.openOwnedRoute("DismissedPersonnelArchive"),true,
    "old archive deep link keeps working");
  await new Promise(resolve=>setImmediate(resolve));
  assert.ok(buttonNamed("العودة إلى الفصل"),
    "archive deep link is owned by separation");
  buttonNamed("العودة إلى الفصل").handlers.click();
  assert.ok(buttonNamed("أرشيف الفصل"),"integrated archive tab survives return");
  buttonNamed("العودة إلى إدارة القوة").handlers.click();
  assert.ok(!byDataset("adminScreen").some(x=>x.dataset.adminScreen==="separatedArchive"),
    "the archive is no longer duplicated on the force-management landing");
  assert.ok(byDataset("adminScreen").some(x=>x.dataset.adminScreen==="personReport"),
    "one unified report card is present on roster landing");
  assert.ok(!byDataset("adminScreen").some(x=>x.dataset.adminScreen==="archives"),
    "redundant top-level archive must be removed");
  assert.ok(!byDataset("adminScreen").some(x=>x.dataset.adminScreen==="reports"),
    "retired combined Reports/Dismissal card must not return");
  buttonNamed("العودة إلى الإدارة").handlers.click();
  // 04.09(d): a real server-backed layout leaf, not a placeholder.
  byDataset("adminSection").find(x=>x.dataset.adminSection==="sheets").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="layout").handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.ok(buttonNamed("إضافة العمود"),"layout editor must expose actual custom column create");
  assert.ok(visible().some(x=>x.textContent==="عمود محمي"),
    "core identifier columns remain protected");
  const layoutCaption=visible().find(x=>x.tag==="label"&&
    x.children[0]?.textContent==="عنوان العمود الجديد");
  assert.ok(layoutCaption,"custom column caption field");
  layoutCaption.children.find(x=>x.tag==="input").value="تاريخ الإضافة";
  await buttonNamed("إضافة العمود").handlers.click();
  assert.ok(calls.some(x=>x.name==="CreateCustomFieldDefinition"&&
    x.body.caption==="تاريخ الإضافة"&&x.body.fieldType===1&&
    x.body.operationId.length>=16));
  await buttonNamed("إخفاء").handlers.click();
  assert.ok(calls.some(x=>x.name==="UpdateCustomFieldMetadata"&&
    x.body.columnId==="FLD-TST-ONE"&&x.body.visible===false&&
    x.body.expectedSnapshot==="B".repeat(64)));
  await buttonNamed("أسفل").handlers.click();
  assert.ok(calls.some(x=>x.name==="UpdateCustomFieldMetadata"&&
    x.body.columnId==="FLD-TST-ONE"&&x.body.displayOrder===6));
  await buttonNamed("تعطيل العمود").handlers.click();
  assert.ok(calls.some(x=>x.name==="DeactivateCustomField"&&
    x.body.columnId==="FLD-TST-ONE"&&x.body.confirmed===true));
  controller.renderAdmin(admin);
  byDataset("adminSection").find(x=>x.dataset.adminSection==="config").handlers.click();
  assert.ok(!byDataset("adminScreen").some(x=>x.dataset.adminScreen==="closedCorrection"),
    "closed-day correction must leave statuses and sites");
  buttonNamed("العودة إلى الإدارة").handlers.click();
  byDataset("adminSection").find(x=>x.dataset.adminSection==="force").handlers.click();
  const correction=byDataset("adminScreen").find(x=>x.dataset.adminScreen==="closedCorrection");
  assert.ok(correction,"GA sees restricted historical correction under force management");
  correction.handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.ok(buttonNamed("معاينة التصحيح"));
  assert.ok(visible().some(x=>x.className==="closed-correction-fields"),
    "closed-day correction uses a compact two-column form");
  assert.ok(visible().some(x=>x.className==="closed-policy-banner"),
    "permission banner never assumes the policy is enabled");
  assert.equal(calls.filter(x=>x.name==="CommitClosedDayCorrection").length,0,
    "opening emergency screen must not mutate attendance");
  const controls=visible().filter(x=>x.className==="form-control");
  const date=controls.find(x=>x.type==="date");
  date.value="2026-10-10";
  const searchInput=controls.find(x=>x.type==="text");
  searchInput.value="اختبار";
  await buttonNamed("بحث عن فرد").handlers.click();
  const selects=visible().filter(x=>x.tag==="select");
  assert.equal(selects.length,2);
  selects[0].value="TST-CLOSED-P1";
  selects[1].value="STS-ABSENT";
  const inputReason=controls.filter(x=>x.tag==="input"&&x.type==="text").slice(-1)[0];
  inputReason.value="تصحيح مستند رسمي موثق";
  await buttonNamed("معاينة التصحيح").handlers.click();
  assert.equal(calls.filter(x=>x.name==="PreviewClosedDayCorrection").length,1);
  assert.equal(calls.filter(x=>x.name==="CommitClosedDayCorrection").length,0,
    "closing the modal without approval must not correct a closed day");
  assert.equal(modalReviews.length,1,"closed-day preview renders as a modal");
  assert.ok(modalReviews[0].details.rows.some(x=>x[0]==="الحالة السابقة"));
  approveDetailPreview=true;
  await buttonNamed("معاينة التصحيح").handlers.click();
  assert.equal(calls.filter(x=>x.name==="CommitClosedDayCorrection").length,1);
  assert.ok(visible().some(x=>x.textContent.includes("اعتمد التصحيح بنجاح")));
  assert.ok(buttonNamed("استدراك يوم سابق (خلال 7 أيام)"),
    "backfill is part of the existing authorized administration screen");
  await buttonNamed("استدراك يوم سابق (خلال 7 أيام)").handlers.click();
  const catchUpForm=visible().find(x=>x.className.includes("app-operational-modal")&&
    x.all().some(y=>y.textContent.includes("استدراك التحضير الناقص")));
  assert.ok(catchUpForm,"new action opens a controlled modal without changing route hierarchy");
  const catchUpDate=catchUpForm.all().find(x=>x.type==="date");
  const catchUpReason=catchUpForm.all().find(x=>x.tag==="input"&&x.type==="text");
  catchUpDate.value="2026-10-29";
  catchUpReason.value="استدراك موثق لحالة تحضير ناقصة";
  approveDetailPreview=false;
  await buttonNamed("معاينة الاستدراك").handlers.click();
  assert.equal(calls.filter(x=>x.name==="PreviewAdministrativeAttendanceCatchUp").length,1);
  assert.equal(calls.filter(x=>x.name==="CommitAdministrativeAttendanceCatchUp").length,0,
    "cancel of the preview must not write historical attendance");
  approveDetailPreview=true;
  await buttonNamed("معاينة الاستدراك").handlers.click();
  const priorCommit=calls.filter(x=>x.name==="CommitAdministrativeAttendanceCatchUp");
  assert.equal(priorCommit.length,1);
  assert.ok(priorCommit[0].body.operationId?.length>=16 &&
    priorCommit[0].body.previewToken==="B".repeat(64));
  assert.ok(visible().some(x=>x.textContent.includes("تم اعتماد 3 حالة ناقصة")));
  controller.renderAdmin(admin);
  byDataset("adminSection").find(x=>x.dataset.adminSection==="force").handlers.click();
  assert.ok(byDataset("adminScreen").some(x=>x.dataset.adminScreen==="closedCorrection"));

  controller.renderAdmin({role:"OrdinaryAdministration",username:"test-admin"});
  byDataset("adminSection").find(x=>x.dataset.adminSection==="force").handlers.click();
  assert.ok(byDataset("adminScreen").some(x=>x.dataset.adminScreen==="closedCorrection"),
    "ordinary admin can see closed-day correction");
    controller.renderAdmin({ role:"Supervisor" });
  assert.equal(byDataset("adminSection").length,4,"supervisor cannot see users card");
  assert.ok(!byDataset("adminSection").some(x=>x.dataset.adminSection==="users"));
  byDataset("adminSection").find(x=>x.dataset.adminSection==="force").handlers.click();
  assert.ok(!byDataset("adminScreen").some(x=>x.dataset.adminScreen==="closedCorrection"),
    "supervisor cannot see closed-day correction even inside force management");
  buttonNamed("العودة إلى الإدارة").handlers.click();
  byDataset("adminSection").find(x=>x.dataset.adminSection==="config").handlers.click();
  assert.ok(!byDataset("adminScreen").some(x=>x.dataset.adminScreen==="closedCorrection"),
    "sites and statuses never contain the correction screen");

  controller.renderAdmin(admin);
  byDataset("adminSection").find(x=>x.dataset.adminSection==="audit").handlers.click();
  assert.equal(byDataset("adminScreen").length,0,
    "audit must open the actual operations screen without a redundant submenu");
  assert.ok(buttonNamed("بحث"),"audit filters visible directly");
  assert.ok(buttonNamed("العودة إلى الإدارة"),"direct audit back action");
  const auditSearch=visible().find(x=>x.tag==="input"&&
    x.type==="search"&&x.placeholder?.includes("بحث بالاسم"));
  assert.ok(auditSearch,"audit history must have a comprehensive search box");
  auditSearch.value="اسم تجريبي";
  await buttonNamed("بحث").handlers.click();
  assert.ok(calls.some(x=>x.name==="QueryAuditLog"&&x.body.term==="اسم تجريبي"),
    "audit search must query matching records across all pages");

  // Audit history is an action-triggered modal. Closing it via Escape, its
  // close button, or a late backend response must never revive stale records.
  const historyTrigger=buttonNamed("القيود المعدلة والمحذوفة");
  assert.ok(historyTrigger,"GeneralAdministration can open audit history");
  historyTrigger.focus();historyTrigger.handlers.click();
  const historyModal=visible().find(x=>x.attrs["aria-label"]==="القيود المعدلة والمحذوفة");
  assert.ok(historyModal&&!historyModal.hidden,"audit history opens as a modal");
  assert.equal(historyModal.attrs["aria-modal"],"true");
  assert.ok(historyModal.className.includes("modal-xl"));
  await new Promise(resolve=>setImmediate(resolve));
  assert.ok(historyModal.all().some(x=>x.textContent==="لا توجد قيود معدلة أو مخفية."),
    "empty history renders only after a server response");
  const historyClose=historyModal.all().find(x=>x.tag==="button"&&x.textContent==="✕ إغلاق");
  assert.ok(historyClose,"audit history has a keyboard-focusable close control");
  assert.equal(document.activeElement,historyClose,"opening history focuses its close control");
  historyModal.handlers.keydown({key:"Escape",preventDefault(){}});
  assert.equal(historyModal.hidden,true,"Escape closes history");
  assert.equal(document.activeElement,historyTrigger,"Escape restores focus to the opener");
  assert.ok(!historyModal.all().some(x=>x.textContent==="لا توجد قيود معدلة أو مخفية."),
    "closing clears stale history content");

  deferAuditHistory=true;
  historyTrigger.focus();historyTrigger.handlers.click();
  assert.ok(!historyModal.hidden);
  assert.ok(completeDeferredAuditHistory,"history request remains pending");
  historyClose.handlers.click();
  assert.ok(historyModal.hidden,"close button dismisses history during fetch");
  completeDeferredAuditHistory();
  await new Promise(resolve=>setImmediate(resolve));
  assert.ok(historyModal.hidden,"late audit response cannot reopen history");
  assert.ok(!historyModal.all().some(x=>x.textContent==="لا توجد قيود معدلة أو مخفية."),
    "late audit responses cannot render after dismissal");
  assert.equal(document.activeElement,historyTrigger,
    "close button restores focus even while server request is pending");
  deferAuditHistory=false;
  assert.equal(calls.filter(x=>x.name==="UpdateAuditPresentation"||
    x.name==="DeleteAuditPresentation").length,0,
    "opening and dismissing audit history does not mutate audit data");

  // An entry details dialog remains independently dismissible while the C#
  // read is still pending. Late replies and pending confirmations cannot
  // repaint a closed dialog or submit an audit overlay write.
  showAuditEntry=true;
  buttonNamed("بحث").handlers.click();
  // The click handler intentionally does not return the asynchronous load().
  // Wait for the actual bridge read and DOM render before locating the row.
  await new Promise(resolve=>setImmediate(resolve));
  // The saved audit table is a compact nine-column overview. Historical
  // before/after evidence remains available only in the existing detail view.
  const auditTable=visible().find(x=>x.tag==="table"&&
    x.className.split(/\\s+/).includes("audit-log-table"));
  assert.ok(auditTable,"audit listing must use its own responsive table layout");
  const auditHeaders=auditTable.children[0].children[0].children.map(x=>x.textContent);
  assert.deepEqual(auditHeaders,[
    "م","العملية","الهدف","المنفذ وقت الحدث","التاريخ",
    "السبب","النتيجة","الوصف","التفاصيل"
  ],"previous/new values must not appear as audit listing columns");
  assert.equal(auditTable.children[1].children[0].children.length,auditHeaders.length,
    "every audit listing cell must align with a corresponding header");
  assert.equal(auditTable.children[1].children[0].children[4].textContent,
    "2026-10-30 18:00 UTC","table timestamp is concise and unambiguously UTC");
  const auditWrap=visible().find(x=>x.className.split(/\\s+/).includes("audit-log-table-wrap"));
  assert.ok(auditWrap,"audit scrolling must be scoped to the log table");
  const css=fs.readFileSync(path.join(__dirname,
    "../src/AlTamam.WebUI/assets/css/altamam.css"),"utf8");
  assert.match(css,/\\.audit-log-table\\s+:is\\(th,td\\)/,
    "audit table must have its own readable column layout without global table changes");
  assert.match(css,/\\.audit-log-table-wrap/, "audit table must keep bounded scrolling");
  const detailTrigger=buttonNamed("عرض التفاصيل");
  assert.ok(detailTrigger,"individual audit entry can be reviewed");
  deferAuditDetail=true;
  detailTrigger.focus();detailTrigger.handlers.click();
  const detailsOverlay=visible().find(x=>x.attrs["aria-label"]==="تفاصيل القيد");
  assert.ok(detailsOverlay,"audit entry details is a real overlay");
  assert.equal(detailsOverlay.attrs["aria-modal"],"true");
  const detailsClose=detailsOverlay.all().find(x=>x.tag==="button"&&
    x.textContent==="إغلاق التفاصيل");
  assert.equal(document.activeElement,detailsClose,
    "details close control receives initial keyboard focus");
  const detailContent=detailsOverlay.all().find(x=>x.className==="admin-note");
  assert.ok(detailContent,"details content is mounted below a stable close control");
  detailsOverlay.handlers.keydown({key:"Escape",preventDefault(){}});
  assert.ok(!visible().includes(detailsOverlay),"Escape removes the details popup");
  assert.equal(document.activeElement,detailTrigger,"Escape restores focus to the row action");
  completeDeferredAuditDetail();
  await new Promise(resolve=>setImmediate(resolve));
  assert.ok(!detailContent.all().some(x=>x.textContent==="موثوق ✓ — سلسلة السجل سليمة"),
    "late details response must not paint a dismissed modal");

  deferAuditDetail=false;
  detailTrigger.focus();detailTrigger.handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  const activeOverlay=visible().find(x=>x.attrs["aria-label"]==="تفاصيل القيد");
  assert.ok(activeOverlay,"details may be reopened after dismissal");
  assert.ok(activeOverlay.all().some(x=>x.textContent==="موثوق ✓ — سلسلة السجل سليمة"));
  assert.ok(activeOverlay.all().some(x=>x.className==="audit-diff-grid"),
    "audited before/after differences remain visible");
  const detailTabStops=activeOverlay.all().filter(x=>
    ["button","input","select","textarea"].includes(x.tag)&&!x.disabled);
  assert.ok(detailTabStops.length>=3);
  detailTabStops.at(-1).focus();
  let trappedDetailTab=false;
  activeOverlay.handlers.keydown({key:"Tab",shiftKey:false,
    preventDefault(){trappedDetailTab=true;}});
  assert.ok(trappedDetailTab,"Tab at the last control stays inside audit details");
  assert.equal(document.activeElement,detailTabStops[0]);
  activeOverlay.handlers.keydown({key:"Tab",shiftKey:true,preventDefault(){}});
  assert.equal(document.activeElement,detailTabStops.at(-1),
    "Shift+Tab at first control wraps to the last control");

  const reasonInput=activeOverlay.all().find(x=>x.tag==="label"&&
    x.children[0]?.textContent==="سبب التعديل أو الإخفاء (إلزامي)").children[1];
  const editedInput=activeOverlay.all().find(x=>x.tag==="label"&&
    x.children[0]?.textContent==="الوصف الظاهر الجديد").children[1];
  reasonInput.value="مراجعة سبب موثق";editedInput.value="وصف مصحح";
  let settleApproval=null;
  context.window.AltamamDialog.confirm=()=>new Promise(resolve=>{settleApproval=resolve;});
  const editAudit=activeOverlay.all().find(x=>x.tag==="button"&&
    x.textContent==="تعديل الوصف الظاهر");
  const pendingEdit=editAudit.handlers.click();
  assert.ok(settleApproval,"edit confirmation is pending");
  activeOverlay.all().find(x=>x.tag==="button"&&
    x.textContent==="إغلاق التفاصيل").handlers.click();
  settleApproval(true);
  await pendingEdit;
  assert.equal(calls.filter(x=>x.name==="UpdateAuditPresentation"||
    x.name==="DeleteAuditPresentation").length,0,
    "dismissed audit dialog must not dispatch a write after late approval");
  assert.equal(document.activeElement,detailTrigger,
    "dismissal during confirmation returns to the originating action");
  context.window.AltamamDialog.confirm=async()=>true;
  showAuditEntry=false;
  buttonNamed("العودة إلى الإدارة").handlers.click();
  byDataset("adminSection").find(x=>x.dataset.adminSection==="users").handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(byDataset("adminScreen").length,0,
    "users must open directly without an intermediate screen");
  assert.ok(buttonNamed("إضافة حساب"),"real add-account action visible immediately");
  buttonNamed("إضافة حساب").handlers.click();
  const addUserForm=visible().find(x=>x.className.includes("admin-user-form"));
  assert.ok(addUserForm,"account editor is a real contained form");
  const secrets=addUserForm.all().filter(x=>x.tag==="input"&&x.type==="password");
  assert.equal(secrets.length,2,
    "account form must render both password and matching confirmation");
  const selection=addUserForm.all().find(x=>x.tag==="select"&&
    x.attrs["aria-label"]==="اختر فرداً من الكشف");
  selection.value="TST-ACTIVE-1";
  const usernameField=addUserForm.all().find(x=>x.tag==="label"&&
    x.children[0]?.textContent==="اسم المستخدم");
  assert.ok(usernameField,"a dedicated username field is required");
  const reveals=addUserForm.all().filter(x=>x.className.includes("admin-secret-reveal"));
  assert.equal(reveals.length,2,"each credential field has a keyboard-operable eye toggle");
  assert.equal(reveals[0].attrs["aria-label"],"إظهار كلمة المرور");
  const passwordRules=addUserForm.all().filter(x=>x.className.split(/\s+/).includes("admin-password-rule"));
  assert.equal(passwordRules.length,6,"live account rules include password confirmation");
  assert.ok(passwordRules.every(x=>!x.className.includes("valid")));
  usernameField.children.find(x=>x.tag==="input").value="tester-user";
  secrets[0].value="Abc123!secure";secrets[1].value="Abc123!secure";
  secrets[0].handlers.input();
  assert.ok(passwordRules.every(x=>x.className.includes("valid")),
    "typing valid credentials lights all six live indicators");
  reveals[0].handlers.click();
  assert.equal(secrets[0].type,"text","eye reveals the existing password without changing its value");
  assert.equal(reveals[0].attrs["aria-pressed"],"true");
  reveals[0].handlers.click();
  assert.equal(secrets[0].type,"password","eye restores hidden field");
  secrets[1].value="Different123!";
  secrets[1].handlers.input();
  assert.ok(!passwordRules.at(-1).className.includes("valid"),
    "mismatched confirmation must never show a green match");
  const beforeUserWrites=calls.filter(x=>x.name==="CreateUserAccount").length;
  await buttonNamed("تأكيد إضافة الحساب").handlers.click();
  assert.equal(calls.filter(x=>x.name==="CreateUserAccount").length,beforeUserWrites,
    "mismatched passwords cannot reach C# bridge even when a PersonId is selected");
  assert.ok(calls.some(x=>x.name==="ListUserAccounts"),
    "opening users must request server-side account list");
  assert.ok(calls.some(x=>x.name==="QueryAuditLog"),
    "opening audit must request actual operation records");
  buttonNamed("العودة إلى الإدارة").handlers.click();

  controller.renderAdmin(admin);
  byDataset("adminSection").find(x=>x.dataset.adminSection==="force").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="persons").handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.ok(visible().some(x=>x.className.includes("admin-force-inline-count")&&
    x.textContent==="(0)"),"the active-force number belongs beside the screen title");
  assert.ok(!visible().some(x=>x.className.includes("admin-roster-totals")),
    "keep the original names table without a separate totals panel");
  const form=visible().find(x=>x.className.includes("admin-create-person"));
  assert.ok(form,"single-person form is available");
  const inputs=form.all().filter(x=>x.tag==="input");
  const location=form.all().find(x=>x.tag==="select");
  assert.equal(inputs.length,2);
  inputs[0].value="شخص تجريبي راجع";inputs[1].value="4141";
  location.value="SITE-RETURN";
  await buttonNamed("تأكيد وإضافة الفرد").handlers.click();
  assert.ok(buttonNamed("عاد نفس الفرد"),"archived identity list shows explicit return choice");
  assert.ok(visible().some(x=>x.textContent==="2026-10-12"),
    "candidate exit date is visible before identity decision");
  assert.ok(visible().some(x=>x.textContent==="الموقع التاريخي"),
    "identity decision must show the archived source-site snapshot");
  assert.equal(calls.filter(x=>x.name==="CreatePerson").length,0,
    "matching archived number must not silently create a new identity");
  await buttonNamed("عاد نفس الفرد").handlers.click();
  assert.ok(calls.some(x=>x.name==="CommitArchivedReturn"&&
    x.body.personId==="TST-ARCHIVED-P1"&&x.body.decision==="same"),
    "return binds explicit candidate ID and actor-bound preview");
  assert.equal(calls.filter(x=>x.name==="CreatePerson").length,0);

  inputs[0].value="شخص جديد مكرر";inputs[1].value="6262";
  location.value="SITE-RETURN";
  await buttonNamed("تأكيد وإضافة الفرد").handlers.click();
  assert.ok(buttonNamed("فرد آخر مختلف عن جميع المرشحين"),
    "name-only candidate requires explicit different-human resolution");
  await buttonNamed("فرد آخر مختلف عن جميع المرشحين").handlers.click();
  assert.ok(calls.some(x=>x.name==="CreatePerson"&&x.body.identityDecision==="different"&&
    x.body.identityResolutionToken==="U".repeat(64)),
    "independent identity requires the token-bound archived-name resolution");

  // 04.03(c): real authorized roster removal is a separate verified
  // modal, not a direct delete and never offered to Supervisors.
  showRosterPerson=true;
  controller.renderAdmin(admin);
  byDataset("adminSection").find(x=>x.dataset.adminSection==="force").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="persons").handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  const removal=buttonNamed("إزالة من القوة");
  assert.ok(removal,"GeneralAdmin must have a protected roster-removal action");
  approveDetailPreview=false;
  const priorArchiveCommits=calls.filter(x=>x.name==="ArchivePersons").length;
  await removal.handlers.click();
  assert.equal(calls.filter(x=>x.name==="ArchivePersons").length,priorArchiveCommits,
    "review rejected: no archive transaction may occur");
  assert.ok(calls.some(x=>x.name==="PreviewArchivePersons"),
    "removal must first use the C# actor-bound preview");
  approveDetailPreview=true;
  await removal.handlers.click();
  const savedArchive=calls.filter(x=>x.name==="ArchivePersons").at(-1);
  assert.ok(savedArchive,"approved removal must call the audited C# archive service");
  assert.equal(savedArchive.body.previewToken,"P".repeat(64));
  assert.equal(savedArchive.body.baseRevision,1);
  assert.equal(savedArchive.body.expectedRevision,1);
  assert.ok(savedArchive.body.operationId.length>=16);
  assert.equal(savedArchive.body.personIds[0],"TST-ACTIVE-1");
  // One two-field modal replaces the old two consecutive prompts.
  nextIdentityForm=null;approveDetailPreview=false;
  const beforeEdit=calls.filter(x=>x.name==="UpdatePersonIdentity").length;
  await buttonNamed("تعديل الاسم والرقم").handlers.click();
  assert.equal(editDialogRequests.at(-1).fields.length,2);
  assert.equal(editDialogRequests.at(-1).opts.title,"تعديل بيانات الفرد");
  assert.equal(calls.filter(x=>x.name==="UpdatePersonIdentity").length,beforeEdit,
    "canceling form never changes the stored identity");
  nextIdentityForm={visibleName:"الاسم المحدّث",serviceNumberA:"91002"};
  await buttonNamed("تعديل الاسم والرقم").handlers.click();
  assert.ok(calls.some(x=>x.name==="PreviewPersonIdentityUpdate"),
    "identity edit must use the real server preview");
  assert.equal(calls.filter(x=>x.name==="UpdatePersonIdentity").length,beforeEdit,
    "declined identity preview prevents the write");
  approveDetailPreview=true;
  await buttonNamed("تعديل الاسم والرقم").handlers.click();
  assert.ok(calls.some(x=>x.name==="UpdatePersonIdentity"&&
    x.body.visibleName==="الاسم المحدّث"&&x.body.serviceNumberA==="91002"&&
    typeof x.body.operationId==="string"),
    "approved identity edit retains the backend authorization and operation ID");
  nextIdentityForm=null;approveDetailPreview=false;
  controller.renderAdmin({role:"Supervisor",username:"supervisor"});
  byDataset("adminSection").find(x=>x.dataset.adminSection==="force").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="persons").handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(buttonNamed("إزالة من القوة"),undefined,
    "Supervisor must never see active-roster removal");
  showRosterPerson=false;

  controller.renderAdmin(admin);
  byDataset("adminSection").find(x=>x.dataset.adminSection==="config").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="sites").handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(buttonNamed("إزالة الموقع")!==undefined,true,
    "active non-system site is removable only by explicit operation");
  assert.equal(visible().filter(x=>x.textContent==="موقع محمي").length,1,
    "the Unspecified site can never display a remove action");
  // Modal-first site editor: no data changes from open/close, Escape restores
  // focus, and Tab wraps rather than going back to the underlying table.
  const openSite=buttonNamed("+ إنشاء موقع جديد");
  assert.ok(openSite,"site creation is action-triggered, not inline");
  openSite.focus();openSite.handlers.click();
  const siteForm=visible().find(x=>x.className.includes("app-operational-modal")&&
    x.all().some(y=>y.textContent==="إنشاء موقع جديد"));
  assert.ok(siteForm&&!siteForm.hidden,"site form opens as a real dialog");
  assert.equal(siteForm.attrs["aria-modal"],"true");
  const closeSite=siteForm.all().find(x=>x.tag==="button"&&x.textContent==="✕ إغلاق");
  assert.equal(document.activeElement,closeSite,"opening a modal moves focus inside it");
  let prevented=false;
  siteForm.handlers.keydown({key:"Tab",shiftKey:true,
    preventDefault(){prevented=true}});
  assert.equal(prevented,true,"Shift-Tab at first control wraps to the last");
  assert.equal(document.activeElement,siteForm.querySelectorAll().at(-1));
  siteForm.handlers.keydown({key:"Tab",shiftKey:false,preventDefault(){}});
  assert.equal(document.activeElement,closeSite,"Tab at last wraps to first");
  siteForm.handlers.keydown({key:"Escape",preventDefault(){}});
  assert.equal(siteForm.hidden,true,"Escape hides site modal without a mutation");
  assert.equal(document.activeElement,openSite,
    "the keyboard returns to the original Create Site control");
  assert.equal(calls.filter(x=>x.name==="CreateSite").length,0,
    "opening and closing a site form cannot create a site");
  context.window.AltamamDialog={...context.window.AltamamDialog,confirm:async()=>true,alert:async()=>true};
  const selectedRow=visible().find(x=>x.tag==="tr"&&
    x.children[0]?.textContent==="موقع تجريبي قابل للإزالة");
  assert.ok(selectedRow,"expected removable site row must exist");
  const removeSiteButton=selectedRow.all().find(x=>x.tag==="button"&&x.textContent==="إزالة الموقع");
  assert.ok(removeSiteButton,"remove action belongs to the specific site");
  await removeSiteButton.handlers.click();
  const mutation=calls.find(x=>x.name==="RemoveSiteAndReassignCurrentPersons");
  assert.ok(mutation,"site removal must call authoritative C# service");
  assert.equal(mutation.body.siteId,"SITE-REMOVABLE");
  assert.equal(mutation.body.expectedRevision,3);
  assert.equal(mutation.body.expectedActiveCount,2);
  assert.equal(mutation.body.confirmed,true);
  assert.ok(mutation.body.operationId.length>=16);
  // Rename uses one accessible form and a separate before/after confirmation.
  const renameRow=visible().find(x=>x.tag==="tr"&&
    x.children[0]?.textContent==="الموقع الجديد");
  const rename=renameRow.all().find(x=>x.tag==="button"&&x.textContent==="إعادة تسمية");
  const beforeRename=calls.filter(x=>x.name==="RenameSite").length;
  nextConfigForm=null;approveDetailPreview=false;
  await rename.handlers.click();
  assert.equal(editDialogRequests.at(-1).opts.title,"إعادة تسمية الموقع");
  assert.equal(calls.filter(x=>x.name==="RenameSite").length,beforeRename,
    "canceling the site editor does not rename");
  nextConfigForm={name:"الموقع الحديث"};
  await rename.handlers.click();
  assert.ok(modalReviews.at(-1).details.rows.some(x=>x[0]==="الاسم السابق"));
  assert.equal(calls.filter(x=>x.name==="RenameSite").length,beforeRename,
    "declining preview leaves the site unchanged");
  approveDetailPreview=true;
  await rename.handlers.click();
  assert.ok(calls.some(x=>x.name==="RenameSite"&&
    x.body.siteId==="SITE-RETURN"&&x.body.name==="الموقع الحديث"&&
    x.body.expectedRevision===4&&x.body.operationId?.length>=16),
    "approved site rename uses existing C# optimistic revision");
  nextConfigForm=null;approveDetailPreview=false;
  // Status creation uses the same reusable modal and previews the actual
  // configured badge color while keeping all native action payloads intact.
  controller.renderAdmin(admin);
  byDataset("adminSection").find(x=>x.dataset.adminSection==="config").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="statuses").handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  const statusTrigger=buttonNamed("+ إضافة حالة جديدة");
  assert.ok(statusTrigger);
  statusTrigger.focus();statusTrigger.handlers.click();
  const statusForm=visible().find(x=>x.className.includes("app-operational-modal")&&
    x.all().some(y=>y.textContent==="إضافة حالة جديدة"));
  assert.ok(statusForm&&!statusForm.hidden);
  const statusName=statusForm.all().find(x=>x.tag==="label"&&
    x.children[0]?.textContent==="اسم الحالة").children[1];
  const statusSymbol=statusForm.all().find(x=>x.tag==="label"&&
    x.children[0]?.textContent==="الرمز").children[1];
  const statusColor=statusForm.all().find(x=>x.tag==="label"&&
    x.children[0]?.textContent==="اللون").children[1];
  statusName.value="إجازة";statusName.handlers.input();
  statusSymbol.value="إ";statusSymbol.handlers.input();
  statusColor.value="#15803D";statusColor.handlers.input();
  const badge=statusForm.all().find(x=>x.className.includes("status-live-preview"));
  assert.equal(badge.textContent,"إجازة — إ");
  assert.equal(badge.style.borderColor,"#15803D");
  assert.equal(badge.style.backgroundColor,"rgba(21,128,61,0.12)");
  const statusClose=statusForm.all().find(x=>x.tag==="button"&&x.textContent==="✕ إغلاق");
  statusClose.handlers.click();
  assert.equal(statusForm.hidden,true);
  assert.equal(document.activeElement,statusTrigger,
    "closing status editor restores focus to its originating action");
  assert.equal(calls.filter(x=>x.name==="CreateAttendanceStatus").length,0,
    "preview and close cannot save an unconfirmed status");
  assert.equal(badge.attrs["aria-live"],"polite",
    "live status preview exposes its visual changes to screen readers");
  const changeStatus=buttonNamed("تعديل");
  assert.ok(changeStatus,"active status is editable through its own action");
  const beforeUpdate=calls.filter(x=>x.name==="UpdateAttendanceStatus").length;
  nextConfigForm=null;approveDetailPreview=false;
  await changeStatus.handlers.click();
  assert.equal(editDialogRequests.at(-1).opts.title,"تعديل حالة التحضير");
  assert.equal(calls.filter(x=>x.name==="UpdateAttendanceStatus").length,beforeUpdate,
    "canceling status form never writes");
  nextConfigForm={name:"غياب معدل",symbol:"غ",color:"#15803D"};
  await changeStatus.handlers.click();
  assert.ok(modalReviews.at(-1).details.rows.some(x=>x[0]==="اللون السابق"),
    "status edit preview shows old and proposed colors");
  assert.equal(calls.filter(x=>x.name==="UpdateAttendanceStatus").length,beforeUpdate,
    "declining status preview prevents an edit");
  approveDetailPreview=true;
  await changeStatus.handlers.click();
  assert.ok(calls.some(x=>x.name==="UpdateAttendanceStatus"&&
    x.body.statusId==="STS-ABSENT"&&x.body.name==="غياب معدل"&&
    x.body.color==="#15803D"&&x.body.expectedRevision===3),
    "confirmed edit uses the existing C# status API and revision");
  nextConfigForm=null;approveDetailPreview=false;
  await controller.renderSettings(admin,()=>{});
  const titles=visible().filter(x=>x.className.includes("settings-panel-title"))
    .map(x=>x.textContent);
  assert.deepEqual(titles,["الحساب والبرنامج","أوقات التحضير التلقائي وقفل التمام اليومي","النسخ الاحتياطي",
    "سلامة قاعدة البيانات","التحقق من النسخ والاستعادة"]);
  const fallback=visible().find(x=>x.tag==="select"&&
    x.attrs["aria-label"]==="الحالة الافتراضية عند عدم وجود تحضير أمس");
  assert.ok(fallback,"administrators can choose the 08:00 fallback status");
  assert.ok(!calls.some(x=>x.name==="SetAutoAttendanceDefaultStatus"),
    "loading settings must not mutate policy");
  const due=visible().find(x=>x.tag==="input"&&x.attrs["aria-label"]==="وقت التحضير التلقائي");
  const locked=visible().find(x=>x.tag==="input"&&x.attrs["aria-label"]==="وقت قفل التحضير اليومي");
  assert.ok(due&&locked,"independently configured attendance time fields must exist");
  assert.equal(due.value,"08:00");
  assert.equal(locked.value,"20:00");
  due.value="07:15";locked.value="19:30";
  fallback.value="STS-ABSENT";
  assert.equal(visible().filter(x=>x.tag==="button"&&x.textContent.includes("حفظ")&&
    x.className.includes("settings-action")&&x.textContent.includes("التحضير")).length,1,
    "attendance group must use one unified save control");
  await buttonNamed("حفظ إعدادات التحضير التلقائي").handlers.click();
  const clockSaves=calls.filter(x=>x.name==="SetAttendanceSchedule");
  assert.equal(clockSaves.length,2,"each attendance clock must save independently");
  assert.equal(clockSaves[0].body.dueTimeLocal,"07:15");
  assert.equal(clockSaves[0].body.dailyLockTimeLocal,undefined);
  assert.equal(clockSaves[1].body.dailyLockTimeLocal,"19:30");
  assert.equal(clockSaves[1].body.dueTimeLocal,undefined);
  assert.ok(clockSaves.every(x=>x.body.operationId.length>=16));
  assert.notEqual(clockSaves[0].body.operationId,clockSaves[1].body.operationId);
  const statusSaves=calls.filter(x=>x.name==="SetAutoAttendanceDefaultStatus");
  assert.equal(statusSaves.length,1,
    "default attendance status is a third independent mutation");
  assert.equal(statusSaves[0].body.statusId,"STS-ABSENT");
  assert.ok(statusSaves[0].body.operationId.length>=16);

  assert.ok(buttonNamed("إنشاء نسخة احتياطية الآن").disabled,
    "manual backup disabled on a device without E: drive");
  assert.ok(!calls.some(x=>x.name==="CreateVerifiedBackup"),
    "settings rendering must never create backup automatically");
  assert.ok(visible().some(x=>x.textContent.includes("الجدولة غير نشطة")),
    "pending scheduler must not be marked operational");
  buttonNamed("فحص سلامة البيانات").handlers.click();
  // The callback must be a real, asynchronously running bridge operation.
  await new Promise(resolve => setImmediate(resolve));
  assert.ok(calls.some(x=>x.name==="RunDatabaseIntegrityCheck"));
  assert.ok(buttonNamed("اختيار نسخة مشفرة وفحص سلامتها"),
    "native backup inspection must be exposed as a real settings action");
  assert.ok(buttonNamed("معاينة الاستعادة دون تنفيذ").disabled,
    "restore preview cannot run before a verified candidate is selected");
  await buttonNamed("اختيار نسخة مشفرة وفحص سلامتها").handlers.click();
  assert.ok(calls.some(x=>x.name==="InspectBackup"),
    "backup verification must invoke the C# + native file picker");
  assert.ok(visible().some(x=>x.textContent==="verified.dbbackup"),
    "verified metadata is displayed only after real native inspection");
  assert.equal(buttonNamed("معاينة الاستعادة دون تنفيذ").disabled,false);
  const reviewCount=modalReviews.length;
  await buttonNamed("معاينة الاستعادة دون تنفيذ").handlers.click();
  assert.ok(calls.some(x=>x.name==="PreviewRestore"&&
    x.body.path==="C:\\Temp\\verified.dbbackup"&&
    x.body.expectedSha256==="a".repeat(64)),
    "verified candidate path and SHA-256 are pinned on the C# preview request");
  assert.equal(modalReviews.length,reviewCount+1,"preview must open the shared modal");
  assert.ok(modalReviews.at(-1).details.rows.some(x=>x[0].includes("الأفراد:")),
    "restore comparison shows incoming and current personnel counts");
  assert.equal(calls.filter(x=>x.name==="RestoreDatabaseAtomically").length,0,
    "preview must never perform a destructive restore");
  await controller.renderSettings({role:"Supervisor",username:"test-supervisor"},()=>{});
  assert.equal(buttonNamed("معاينة الاستعادة دون تنفيذ"),undefined,
    "a supervisor can inspect a backup but cannot open a restore preview");
  assert.ok(!buttonNamed("حفظ إعدادات التحضير التلقائي"),
    "supervisors cannot see or invoke schedule and default-status policy changes");
  // A deep link focuses its existing real action; it must never execute writes,
  // inspect another user's backups, restore a database or log out by itself.
  const privileged={role:"GeneralAdministration",username:"test-user"};
  const activeButton=()=>document.activeElement;
  const checkRoute=async(route,label,forbiddenWrites=[])=>{
    const before=calls.length;
    await controller.renderSettings(privileged,()=>{},route);
    assert.equal(activeButton()?.textContent,label,
      route+" must focus the real existing action without changing layouts");
    const newlyCalled=calls.slice(before).map(x=>x.name);
    assert.ok(!newlyCalled.includes("RestoreDatabaseAtomically"),
      route+" must never enable native destructive restore");
    for(const name of forbiddenWrites)
      assert.ok(!newlyCalled.includes(name),route+" must not auto-run "+name);
  };
  await checkRoute("CreateBackup","اختيار مجلد من Windows",["CreateVerifiedBackup"]);
  await checkRoute("ScheduledBackups","حفظ جدول النسخ التلقائي",["UpdateBackupSchedule"]);
  await checkRoute("RestoreBackup","اختيار نسخة مشفرة وفحص سلامتها",["InspectBackup","PreviewRestore"]);
  await checkRoute("AppearanceAndDiagnostics","فحص سلامة البيانات",["RunDatabaseIntegrityCheck"]);
  await checkRoute("EightPmScheduler","حفظ إعدادات التحضير التلقائي",
    ["SetAttendanceSchedule","SetAutoAttendanceDefaultStatus"]);
  await checkRoute("ResetApplicationData","معاينة التصفير الكامل",
    ["PreviewFullReset","CommitFullReset"]);
  await checkRoute("Logout","تسجيل الخروج",["Logout"]);
  await controller.renderSettings(privileged,()=>{},"AboutApplication");
  assert.ok(activeButton()?.className.includes("settings-account-panel"),
    "AboutApplication targets read-only account/version details instead of logout");
  // Supervisor must have no hidden schedule/reset action exposed by a deep link.
  await controller.renderSettings({role:"Supervisor",username:"supervisor"},()=>{},"ScheduledBackups");
  assert.ok(!buttonNamed("حفظ جدول النسخ التلقائي"));
  await controller.renderSettings({role:"Supervisor",username:"supervisor"},()=>{},"ResetApplicationData");
  assert.ok(!buttonNamed("معاينة التصفير الكامل"));
  // The ordinary administration role has READ access to immutable audit
  // presentation-history (according to the C# ActionManifest), but never
  // access to the GA-only edit and hide commands.
  const historyReadsBefore=calls.filter(x=>x.name==="QueryAuditPresentationChanges").length;
  const auditWritesBefore=calls.filter(x=>
    x.name==="UpdateAuditPresentation"||x.name==="DeleteAuditPresentation").length;
  const ordinary={role:"OrdinaryAdministration",username:"test-admin"};
  controller.renderAdmin(ordinary);
  assert.equal(controller.openOwnedRoute("ModifiedDeletedAuditEntries"),true,
    "ordinary administration must open the actual permitted history route");
  await new Promise(resolve=>setImmediate(resolve));
  const archivedHistory=visible().find(x=>
    x.attrs["aria-label"]==="القيود المعدلة والمحذوفة");
  assert.ok(archivedHistory && !archivedHistory.hidden,
    "deep link opens the actual read-only history panel, not generic audit search");
  assert.ok(buttonNamed("القيود المعدلة والمحذوفة"),
    "ordinary administration may reopen permitted read-only history");
  assert.ok(calls.filter(x=>x.name==="QueryAuditPresentationChanges").length>
    historyReadsBefore,"the real server history read must be invoked");
  assert.ok(visible().some(x=>x.tag==="input"&&x.type==="checkbox"),
    "ordinary administration can reveal hidden entries for authorized correction");
  showAuditEntry=true;
  assert.equal(controller.openOwnedRoute("AuditLog"),true);
  await new Promise(resolve=>setImmediate(resolve));
  const oaDetailTrigger=buttonNamed("عرض التفاصيل");
  assert.ok(oaDetailTrigger,"ordinary administration can open a saved audit entry");
  oaDetailTrigger.handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  const oaPopup=visible().find(x=>x.attrs["aria-label"]==="تفاصيل القيد");
  assert.ok(oaPopup,"ordinary administration has actual audit details");
  assert.ok(oaPopup.all().some(x=>x.tag==="button"&&x.textContent==="تعديل الوصف الظاهر"),
    "ordinary administration sees the existing confirmed edit action");
  assert.ok(oaPopup.all().some(x=>x.tag==="button"&&x.textContent==="إخفاء القيد من العرض"),
    "ordinary administration sees the existing confirmed hide action");
  oaPopup.all().find(x=>x.tag==="button"&&x.textContent==="إغلاق التفاصيل").handlers.click();
  showAuditEntry=false;
  assert.equal(calls.filter(x=>
    x.name==="UpdateAuditPresentation"||x.name==="DeleteAuditPresentation").length,
    auditWritesBefore,"history navigation never edits the immutable audit log");
  const supervisor={role:"Supervisor",username:"supervisor"};
  controller.renderAdmin(supervisor);
  assert.equal(controller.openOwnedRoute("ModifiedDeletedAuditEntries"),false,
    "supervisor may not use direct routing to enter restricted history");
  controller.renderAdmin(admin);
  assert.equal(controller.openOwnedRoute("ModifiedDeletedAuditEntries"),true,
    "GA deep link also opens the existing audit history panel");
  await new Promise(resolve=>setImmediate(resolve));
  assert.ok(visible().some(x=>
    x.attrs["aria-label"]==="القيود المعدلة والمحذوفة"&&!x.hidden));
  assert.equal(calls.filter(x=>
    x.name==="UpdateAuditPresentation"||x.name==="DeleteAuditPresentation").length,
    auditWritesBefore,"GA deep link must remain a read until explicitly confirmed");
  console.log("ADMIN SETTINGS UI PASS: admin fallback status, supervisor policy isolation, backup safety");
}
run().catch(error => { console.error(error); process.exitCode=1; });
