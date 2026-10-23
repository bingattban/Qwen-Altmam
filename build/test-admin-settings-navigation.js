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
      RunDatabaseIntegrityCheck: { integrity:"ok" }
    };
    if (!(name in responses)) throw Error("Unexpected bridge action " + name);
    return { data: responses[name] };
  }
};
const context = { window:{ AltamamBridge:bridge }, document, console,
  alert: () => { throw Error("Unexpected alert"); },
  prompt: () => { throw Error("Unexpected prompt"); },
  confirm: () => { throw Error("Unexpected confirm"); } };
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

  controller.renderAdmin({ role:"Supervisor" });
  assert.equal(byDataset("adminSection").length,4,"supervisor cannot see users card");
  assert.ok(!byDataset("adminSection").some(x=>x.dataset.adminSection==="users"));

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
