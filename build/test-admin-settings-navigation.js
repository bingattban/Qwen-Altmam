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
      RunDatabaseIntegrityCheck: { integrity:"ok" },
      GetAllowedRoles: [{ role:"Supervisor",name:"مشرف" }],
      ListUserAccounts: { items:[],total:0,hasNext:false },
      QueryAuditLog: { items:[],total:0,hasNext:false },
      GetActiveAttendanceStatuses: [{statusId:"STS-ABSENT",displayName:"غياب"}],
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
const context = { window:{ AltamamBridge:bridge }, document, console,
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
    ["attendance","persons","transfers","reports"]);
  byDataset("adminScreen")[0].handlers.click();
  assert.equal(byDataset("adminScreen").length,0,"only selected detail page is visible");
  assert.ok(buttonNamed("العودة إلى إدارة القوة"),"back from detail to parent");
  buttonNamed("العودة إلى إدارة القوة").handlers.click();
  assert.equal(byDataset("adminScreen").length,4,"re-open proper subgroup");
  buttonNamed("العودة إلى الإدارة").handlers.click();
  assert.equal(byDataset("adminSection").length,5,"return to clean admin landing");

  // Nested administration pages use the same isolated card pattern.
  byDataset("adminSection").find(x=>x.dataset.adminSection==="force").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="transfers").handlers.click();
  assert.deepEqual(byDataset("adminNested").map(x=>x.dataset.adminNested),
    ["transferInternal","transferExternal","transferArchive"]);
  byDataset("adminNested")[0].handlers.click();
  assert.equal(byDataset("adminNested").length,0,"nested cards hidden after opening a leaf");
  buttonNamed("العودة إلى التنقلات").handlers.click();
  assert.equal(byDataset("adminNested").length,3,"back restores transfer choices");
  buttonNamed("العودة إلى إدارة القوة").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="reports").handlers.click();
  assert.deepEqual(byDataset("adminNested").map(x=>x.dataset.adminNested),
    ["personReport","separation","closedCorrection"]);
  byDataset("adminNested").find(x=>x.dataset.adminNested==="separation").handlers.click();
  assert.deepEqual(byDataset("adminNested").map(x=>x.dataset.adminNested),
    ["separatePerson","separatedArchive"]);
  buttonNamed("العودة إلى التقارير والفصل").handlers.click();
  assert.equal(byDataset("adminNested").length,3,"back restores reports choices");

  const correction=byDataset("adminNested").find(x=>x.dataset.adminNested==="closedCorrection");
  assert.ok(correction,"GA sees restricted historical correction screen");
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
    "preview must not commit the closed-day correction");
  await buttonNamed("اعتماد التصحيح الاستثنائي").handlers.click();
  assert.equal(calls.filter(x=>x.name==="CommitClosedDayCorrection").length,1);
  assert.ok(visible().some(x=>x.textContent.includes("اعتمد التصحيح بنجاح")));
  controller.renderAdmin(admin);
  byDataset("adminSection").find(x=>x.dataset.adminSection==="force").handlers.click();
  byDataset("adminScreen").find(x=>x.dataset.adminScreen==="reports").handlers.click();

  controller.renderAdmin({ role:"Supervisor" });
  assert.equal(byDataset("adminSection").length,4,"supervisor cannot see users card");
  assert.ok(!byDataset("adminSection").some(x=>x.dataset.adminSection==="users"));

  controller.renderAdmin(admin);
  byDataset("adminSection").find(x=>x.dataset.adminSection==="audit").handlers.click();
  assert.equal(byDataset("adminScreen").length,0,
    "audit must open the actual operations screen without a redundant submenu");
  assert.ok(buttonNamed("بحث"),"audit filters visible directly");
  assert.ok(buttonNamed("العودة إلى الإدارة"),"direct audit back action");
  buttonNamed("العودة إلى الإدارة").handlers.click();
  byDataset("adminSection").find(x=>x.dataset.adminSection==="users").handlers.click();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(byDataset("adminScreen").length,0,
    "users must open directly without an intermediate screen");
  assert.ok(buttonNamed("إضافة حساب"),"real add-account action visible immediately");
  assert.ok(calls.some(x=>x.name==="ListUserAccounts"),
    "opening users must request server-side account list");
  assert.ok(calls.some(x=>x.name==="QueryAuditLog"),
    "opening audit must request actual operation records");
  buttonNamed("العودة إلى الإدارة").handlers.click();

  await controller.renderSettings(admin,()=>{});
  const titles=visible().filter(x=>x.className.includes("settings-panel-title"))
    .map(x=>x.textContent);
  assert.deepEqual(titles,["الحساب والبرنامج","النسخ الاحتياطي",
    "سلامة قاعدة البيانات","الاستعادة والإعدادات المتقدمة"]);
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
  console.log("ADMIN SETTINGS UI PASS: 5 standalone cards, drill-down/back, supervisor RBAC, clear settings, backup safely disabled");
}
run().catch(error => { console.error(error); process.exitCode=1; });
