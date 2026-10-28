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
    this.style = {};
  }
  get firstChild() { return this.children[0]||null; }
  get classList() {
    return {
      add: (...tokens) => { this.className += " " + tokens.join(" "); },
      contains: token => this.className.split(/\s+/).includes(token)
    };
  }
  append(...items) { this.children.push(...items); }
  appendChild(item) { this.children.push(item); return item; }
  prepend(item) { this.children.unshift(item); }
  replaceChildren(...items) { this.children = [...items]; }
  setAttribute(key, val) { this.attrs[key] = val; }
  addEventListener(name, fn) { this.handlers[name] = fn; }
  querySelector(selector) {
    const cls = selector.slice(1);
    return this.all().find(n => n.className.split(/\s+/).includes(cls)) || null;
  }
  all() { return [this, ...this.children.flatMap(x => x instanceof Element ? x.all() : [])]; }
}
const viewport = new Element("main");
const calls = [];
const document = {
  getElementById: key => key === "appViewport" ? viewport : null,
  createElement: tag => new Element(tag)
};
const bridge = {
  invoke: async (name, body) => {
    calls.push({ name, body });
    const responses = {
      GetSettingsOverview: { username:"test-user", roleLabelAr:"الإدارة العامة",
        backupDirectory:"E:\\AlTamamAlYawmi\\Backups", backupTime:"16:00",
        automaticBackupNote:"الجدولة غير نشطة", defaultDriveAvailable:false },
      GetApplicationVersion: { productNameAr:"التمام اليومي", version:"0.4.0" },
      GetAutoAttendancePolicy: {configured:false,defaultStatusId:null,dueTimeLocal:"08:00",dailyLockTimeLocal:"20:00"},
      SetAttendanceSchedule: {dueTimeLocal:"07:15",dailyLockTimeLocal:"19:30"},
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
      QueryAuditLog: { items:[],total:0,hasNext:false },
      GetActiveAttendanceStatuses: [{statusId:"STS-ABSENT",displayName:"غياب"}],
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
      QuerySites:[{siteId:"SITE-RETURN",name:"الموقع الجديد",active:true},
        {siteId:"SITE-REMOVABLE",name:"موقع تجريبي قابل للإزالة",active:true,rowVersion:3},
        {siteId:"SITE-UNSPECIFIED",name:"غير محدد",active:true,rowVersion:1}],
      QueryPersons:{items:[],total:body?.siteId==="SITE-REMOVABLE"?2:0,hasNext:false},
      RemoveSiteAndReassignCurrentPersons:{siteId:"SITE-REMOVABLE",reassignedCount:2,active:false},
      PreviewArchivedIdentity:body.serviceNumberA==="4141"
        ?{requiresResolution:true,previewToken:"T".repeat(64),
          candidates:[{personId:"TST-ARCHIVED-P1",name:"شخص تجريبي قديم",
            serviceNumberA:"4141",exitDate:"2026-10-12",exitType:"نقل خارجي",numberMatch:true,nameMatch:false}]}
        :{requiresResolution:true,previewToken:"U".repeat(64),
          candidates:[{personId:"TST-ARCHIVED-P2",name:"شخص جديد مكرر",
            serviceNumberA:"5151",exitDate:"2026-10-12",exitType:"فصل",numberMatch:false,nameMatch:true}]},
      CommitArchivedReturn:{returned:true,personId:"TST-ARCHIVED-P1"},
      CreatePerson:{personId:"PRN-NEW-DIFFERENT"},
      SearchPersons: {items:[{personId:"TST-CLOSED-P1",displayName:"اختبار",serviceNumberA:"C-1"}]},
      SetClosedDayCorrectionPolicy: {enabled:true},
      PreviewClosedDayCorrection: {previewToken:"T".repeat(64),requiresExplicitConfirmation:true,
        personName:"اختبار",serviceNumberA:"C-1",siteName:"غير محدد",
        businessDate:"2026-10-10",beforeStatusName:"موجود",afterStatusName:"غياب",reason:"تصحيح مستند"},
      CommitClosedDayCorrection: {revisionNumber:2}
    };
    if (!(name in responses)) throw Error("Unexpected bridge action " + name);
    return { data: responses[name] };
  }
};
const modalReviews=[];
let approveDetailPreview=false;
const context = { window:{ AltamamBridge:bridge,AltamamDialog:{
  detailsReview:async(details,opts)=>{modalReviews.push({details,opts});return approveDetailPreview;},
  confirm:async()=>true, prompt:async(message,value="")=>value, alert:async()=>true
 } }, document, console,
  alert: () => { throw Error("Unexpected alert"); },
  prompt: () => { throw Error("Unexpected prompt"); },
  confirm: () => true };
const script = fs.readFileSync(
  path.join(__dirname,"../src/AlTamam.WebUI/assets/js/admin-settings.js"),"utf8");
vm.runInNewContext(script,context,{filename:"admin-settings.js"});
const controller = context.window.AltamamAdminSettings;
const visible = () => viewport.all();
const byDataset = (key) => visible().filter(x => x.dataset[key]);
const buttonNamed = name => visible().find(x => x.tag==="button" && x.textContent===name);

async function run() {
  const admin = { role:"GeneralAdministration", username:"test-user" };
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
  assert.equal(byDataset("adminScreen").length,6,"re-open all force management screens");
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
  assert.deepEqual(byDataset("adminNested").map(x=>x.dataset.adminNested),
    ["separatePerson","separatedArchive"],"dismissal and its archive share the dismissal screen");
  buttonNamed("العودة إلى إدارة القوة").handlers.click();
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
  usernameField.children.find(x=>x.tag==="input").value="tester-user";
  secrets[0].value="Abc123!secure";secrets[1].value="Different123!";
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

  controller.renderAdmin(admin);
  byDataset("adminSection").find(x=>x.dataset.adminSection==="config").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="sites").handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(buttonNamed("إزالة الموقع")!==undefined,true,
    "active non-system site is removable only by explicit operation");
  assert.equal(visible().filter(x=>x.textContent==="موقع محمي").length,1,
    "the Unspecified site can never display a remove action");
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
  assert.ok(calls.some(x=>x.name==="SetAttendanceSchedule"&&x.body.dueTimeLocal==="07:15"&&
    x.body.dailyLockTimeLocal==="19:30"&&x.body.operationId.length>=16),
    "times must persist on explicit unified save");
  assert.ok(calls.some(x=>x.name==="SetAutoAttendanceDefaultStatus"&&x.body.statusId==="STS-ABSENT"),
    "unified save also writes the chosen active fallback");

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
  console.log("ADMIN SETTINGS UI PASS: admin fallback status, supervisor policy isolation, backup safety");
}
run().catch(error => { console.error(error); process.exitCode=1; });
