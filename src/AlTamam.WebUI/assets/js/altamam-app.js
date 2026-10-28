"use strict";
/* Single RTL navigation owner. The backend remains the sole authority for permissions and data. */
const SCREEN_CATALOG = [{"key":"Dashboard","title":"الرئيسية","path":"screens/Dashboard/index.html","phase":"06"},{"key":"UnpreparedPersonnel","title":"المحضّرون تلقائياً","path":"screens/Dashboard/UnpreparedPersonnel/index.html","phase":"06"},{"key":"SelectStatusAndConfirm","title":"اختيار الحالة والتأكيد","path":"screens/Dashboard/UnpreparedPersonnel/SelectStatusAndConfirm/index.html","phase":"06"},{"key":"MonthlyAttendance","title":"التمام الشهري","path":"screens/MonthlyAttendance/index.html","phase":"08"},{"key":"SelectArchive","title":"اختيار الأرشيف","path":"screens/MonthlyAttendance/SelectArchive/index.html","phase":"08"},{"key":"Administration","title":"الإدارة","path":"screens/Administration/index.html","phase":"04"},{"key":"PersonnelManagement","title":"إدارة القوة","path":"screens/Administration/PersonnelManagement/index.html","phase":"04"},{"key":"Attendance","title":"التمام","path":"screens/Administration/PersonnelManagement/Attendance/index.html","phase":"06"},{"key":"ConfirmAttendance","title":"تأكيد التحضير","path":"screens/Administration/PersonnelManagement/Attendance/ConfirmAttendance/index.html","phase":"06"},{"key":"EditPersonnelNames","title":"تعديل الأسماء","path":"screens/Administration/PersonnelManagement/EditPersonnelNames/index.html","phase":"05"},{"key":"EditPersonDetails","title":"تعديل بيانات الفرد","path":"screens/Administration/PersonnelManagement/EditPersonnelNames/EditPersonDetails/index.html","phase":"05"},{"key":"RemovePersonnel","title":"حذف أسماء من القوة","path":"screens/Administration/PersonnelManagement/EditPersonnelNames/RemovePersonnel/index.html","phase":"05"},{"key":"Transfers","title":"التنقلات","path":"screens/Administration/PersonnelManagement/Transfers/index.html","phase":"08"},{"key":"InternalTransfer","title":"النقل الداخلي","path":"screens/Administration/PersonnelManagement/Transfers/InternalTransfer/index.html","phase":"08"},{"key":"ConfirmTransfer","title":"تأكيد النقل الداخلي","path":"screens/Administration/PersonnelManagement/Transfers/InternalTransfer/ConfirmTransfer/index.html","phase":"08"},{"key":"ExternalTransfer","title":"النقل الخارجي","path":"screens/Administration/PersonnelManagement/Transfers/ExternalTransfer/index.html","phase":"08"},{"key":"ConfirmExternalTransfer","title":"تأكيد النقل الخارجي","path":"screens/Administration/PersonnelManagement/Transfers/ExternalTransfer/ConfirmExternalTransfer/index.html","phase":"08"},{"key":"TransferArchive","title":"أرشيف التنقلات","path":"screens/Administration/PersonnelManagement/Transfers/TransferArchive/index.html","phase":"08"},{"key":"ReportsAndDismissal","title":"الفصل والأرشيف","path":"screens/Administration/PersonnelManagement/ReportsAndDismissal/index.html","phase":"09"},{"key":"Dismissal","title":"الفصل","path":"screens/Administration/PersonnelManagement/ReportsAndDismissal/Dismissal/index.html","phase":"08"},{"key":"DismissPerson","title":"فصل","path":"screens/Administration/PersonnelManagement/ReportsAndDismissal/Dismissal/DismissPerson/index.html","phase":"08"},{"key":"ConfirmDismissal","title":"تأكيد الفصل","path":"screens/Administration/PersonnelManagement/ReportsAndDismissal/Dismissal/DismissPerson/ConfirmDismissal/index.html","phase":"08"},{"key":"DismissedPersonnelArchive","title":"أرشيف المفصولين","path":"screens/Administration/PersonnelManagement/ReportsAndDismissal/Dismissal/DismissedPersonnelArchive/index.html","phase":"08"},{"key":"PersonnelReport","title":"التقرير","path":"screens/Administration/PersonnelManagement/PersonnelReport/index.html","phase":"09"},{"key":"ExportPersonReport","title":"تصدير تقرير الفرد","path":"screens/Administration/PersonnelManagement/PersonnelReport/ExportPersonReport/index.html","phase":"09"},{"key":"StatusesAndSites","title":"الحالات والمواقع","path":"screens/Administration/StatusesAndSites/index.html","phase":"05"},{"key":"Statuses","title":"الحالات","path":"screens/Administration/StatusesAndSites/Statuses/index.html","phase":"05"},{"key":"AddStatus","title":"إضافة حالة","path":"screens/Administration/StatusesAndSites/Statuses/AddStatus/index.html","phase":"05"},{"key":"EditStatus","title":"تعديل حالة","path":"screens/Administration/StatusesAndSites/Statuses/EditStatus/index.html","phase":"05"},{"key":"DeleteStatus","title":"حذف حالة","path":"screens/Administration/StatusesAndSites/Statuses/DeleteStatus/index.html","phase":"05"},{"key":"Sites","title":"المواقع","path":"screens/Administration/StatusesAndSites/Sites/index.html","phase":"05"},{"key":"AddSite","title":"إضافة موقع","path":"screens/Administration/StatusesAndSites/Sites/AddSite/index.html","phase":"05"},{"key":"EditSite","title":"تعديل موقع","path":"screens/Administration/StatusesAndSites/Sites/EditSite/index.html","phase":"05"},{"key":"DeleteSite","title":"حذف موقع","path":"screens/Administration/StatusesAndSites/Sites/DeleteSite/index.html","phase":"05"},{"key":"Rosters","title":"الكشوفات","path":"screens/Administration/Rosters/index.html","phase":"10"},{"key":"Import","title":"استيراد","path":"screens/Administration/Rosters/Import/index.html","phase":"10"},{"key":"SelectFile","title":"اختيار الملف","path":"screens/Administration/Rosters/Import/SelectFile/index.html","phase":"10"},{"key":"PreviewAndValidate","title":"المعاينة والتحقق","path":"screens/Administration/Rosters/Import/PreviewAndValidate/index.html","phase":"10"},{"key":"ImportResults","title":"نتيجة الاستيراد","path":"screens/Administration/Rosters/Import/ImportResults/index.html","phase":"10"},{"key":"Export","title":"تصدير","path":"screens/Administration/Rosters/Export/index.html","phase":"10"},{"key":"PreviewAndConfirmExport","title":"معاينة وتأكيد التصدير","path":"screens/Administration/Rosters/Export/PreviewAndConfirmExport/index.html","phase":"10"},{"key":"ExportProgressAndResult","title":"تقدم ونتيجة التصدير","path":"screens/Administration/Rosters/Export/ExportProgressAndResult/index.html","phase":"10"},{"key":"RosterLayout","title":"تعديل الهيكل للكشف","path":"screens/Administration/Rosters/RosterLayout/index.html","phase":"10"},{"key":"AddOrEditColumn","title":"إضافة أو تعديل عمود","path":"screens/Administration/Rosters/RosterLayout/AddOrEditColumn/index.html","phase":"10"},{"key":"Users","title":"المستخدمين","path":"screens/Administration/Users/index.html","phase":"03"},{"key":"AddUser","title":"إضافة مستخدم","path":"screens/Administration/Users/AddUser/index.html","phase":"03"},{"key":"ChangePassword","title":"تعديل كلمة المرور","path":"screens/Administration/Users/ChangePassword/index.html","phase":"03"},{"key":"DeleteAccount","title":"حذف الحساب","path":"screens/Administration/Users/DeleteAccount/index.html","phase":"03"},{"key":"AuditLog","title":"سجل العمليات","path":"screens/Administration/AuditLog/index.html","phase":"11"},{"key":"EditAuditEntry","title":"تعديل قيد سجل العمليات","path":"screens/Administration/AuditLog/EditAuditEntry/index.html","phase":"11"},{"key":"DeleteAuditEntry","title":"حذف قيد سجل العمليات","path":"screens/Administration/AuditLog/DeleteAuditEntry/index.html","phase":"11"},{"key":"ModifiedDeletedAuditEntries","title":"سجل القيود المعدلة والمحذوفة","path":"screens/Administration/AuditLog/ModifiedDeletedAuditEntries/index.html","phase":"11"},{"key":"Settings","title":"الإعدادات","path":"screens/Settings/index.html","phase":"12"},{"key":"CreateBackup","title":"نسخة احتياطية","path":"screens/Settings/CreateBackup/index.html","phase":"12"},{"key":"RestoreBackup","title":"استيراد نسخة احتياطية","path":"screens/Settings/RestoreBackup/index.html","phase":"12"},{"key":"ScheduledBackups","title":"النسخ الاحتياطي التلقائي","path":"screens/Settings/ScheduledBackups/index.html","phase":"12"},{"key":"ResetApplicationData","title":"حذف جميع بيانات البرنامج","path":"screens/Settings/ResetApplicationData/index.html","phase":"12"},{"key":"AboutApplication","title":"بيانات البرنامج","path":"screens/Settings/AboutApplication/index.html","phase":"13"},{"key":"Logout","title":"تسجيل الخروج","path":"screens/Settings/Logout/index.html","phase":"12"},{"key":"AppearanceAndDiagnostics","title":"المظهر وفحص السلامة","path":"screens/Settings/AppearanceAndDiagnostics/index.html","phase":"13"},{"key":"Login","title":"تسجيل الدخول","path":"screens/SharedScreens/Login/index.html","phase":"03"},{"key":"FirstRunSetup","title":"التهيئة الأولى","path":"screens/SharedScreens/FirstRunSetup/index.html","phase":"03"},{"key":"PersonPickerAndSearch","title":"اختيار الفرد والبحث الموحد","path":"screens/SharedScreens/PersonPickerAndSearch/index.html","phase":"04"},{"key":"ConfirmationAndWarning","title":"نمط التأكيد والتحذير","path":"screens/SharedScreens/ConfirmationAndWarning/index.html","phase":"04"},{"key":"DraftsAndTemporarySave","title":"الحفظ المؤقت والمسودات","path":"screens/SharedScreens/DraftsAndTemporarySave/index.html","phase":"04"},{"key":"ConflictsAndNotifications","title":"تعارض العمليات والإشعارات","path":"screens/SharedScreens/ConflictsAndNotifications/index.html","phase":"04"},{"key":"ClosedDayExceptionalCorrection","title":"التصحيح الاستثنائي لليوم المغلق","path":"screens/SharedScreens/ClosedDayExceptionalCorrection/index.html","phase":"11"},{"key":"EightPmScheduler","title":"جدولة التحضير التلقائي","path":"screens/SharedScreens/EightPmScheduler/index.html","phase":"07"},{"key":"AddPerson","title":"إضافة فرد","path":"screens/Administration/PersonnelManagement/EditPersonnelNames/index.html","phase":"05","embedded":true}];

const MAIN_SECTIONS = Object.freeze({
  dashboard: { title: "الرئيسية", folder: "Dashboard", entry: "Dashboard" },
  monthly: { title: "التمام الشهري", folder: "MonthlyAttendance", entry: "MonthlyAttendance" },
  admin: { title: "الإدارة", folder: "Administration", entry: "Administration" },
  settings: { title: "الإعدادات", folder: "Settings", entry: "Settings" }
});
const SCREEN_BY_KEY = new Map(SCREEN_CATALOG.map(route => [route.key, route]));
const ROUTE_BY_PATH = new Map(SCREEN_CATALOG.filter(route => !route.embedded).map(route => [route.path, route]));
let activeSection = "dashboard";

function sectionForRoute(route) {
  if (route.path.startsWith("screens/Dashboard/")) return "dashboard";
  if (route.path.startsWith("screens/MonthlyAttendance/")) return "monthly";
  if (route.path.startsWith("screens/Administration/")) return "admin";
  return "settings";
}

function parentRoute(route) {
  let folder = route.path.slice(0, -"/index.html".length);
  while (folder.includes("/")) {
    folder = folder.slice(0, folder.lastIndexOf("/"));
    const parent = ROUTE_BY_PATH.get(folder + "/index.html");
    if (parent) return parent;
  }
  return null;
}

function getPrimaryRoutes(section) {
  return SCREEN_CATALOG.filter(route => !route.embedded && route.key!=="SelectStatusAndConfirm" && sectionForRoute(route) === section &&
    (!window.AltamamFunctional || window.AltamamFunctional.canSeeRoute(route)));
}

function openScreen(key) {
  const route = SCREEN_BY_KEY.get(key);
  if (!route) return false;
  // Navigate the real main WebView document, never an iframe falsely treated as
  // the main frame by the authenticated C# dispatcher.
  window.location.assign(encodeURI(route.path));
  return true;
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function createRouteLink(route, className) {
  const link = element("a", className || "route-link", route.title);
  link.href = encodeURI(route.path);
  link.setAttribute("aria-label", "فتح شاشة " + route.title);
  return link;
}

function buildRouteTree(section, container) {
  const routes = getPrimaryRoutes(section);
  const children = new Map();
  for (const route of routes) children.set(route.key, []);
  const roots = [];
  for (const route of routes) {
    const parent = parentRoute(route);
    if (parent && children.has(parent.key)) children.get(parent.key).push(route);
    else roots.push(route);
  }
  function renderRoute(route, depth) {
    const wrap = element("div", "route-item" + (depth === 0 ? " route-item-root" : ""));
    const nested = children.get(route.key) || [];
    const heading = element("div", "route-item-heading");
    heading.appendChild(createRouteLink(route, "route-link"));
    wrap.appendChild(heading);
    if (nested.length) {
      const details = element("details", "route-children");
      const summary = element("summary", "route-children-toggle", "الشاشات التابعة (" + nested.length + ")");
      details.appendChild(summary);
      const group = element("div", "route-children-list");
      nested.forEach(child => group.appendChild(renderRoute(child, depth + 1)));
      details.appendChild(group);
      wrap.appendChild(details);
    }
    return wrap;
  }
  roots.forEach(route => container.appendChild(renderRoute(route, 0)));
}

function navigateTop(section, updateUrl) {
  if (!Object.hasOwn(MAIN_SECTIONS, section)) section = "dashboard";
  activeSection = section;
  for (const key of Object.keys(MAIN_SECTIONS)) {
    const nav = document.getElementById("navBtn-" + key);
    if (nav) {
      nav.classList.toggle("active", key === section);
      if (key === section) nav.setAttribute("aria-current", "page");
      else nav.removeAttribute("aria-current");
    }
  }
  const viewport = document.getElementById("appViewport");
  if (!viewport) return;
  viewport.replaceChildren();
  const header = element("section", "card main-section-heading");
  header.appendChild(element("h1", "page-title", MAIN_SECTIONS[section].title));
  const summary = element("p", "section-description",
    "اختر القسم المطلوب ثم افتح شاشاته الفرعية من القائمة المنظمة أدناه.");
  header.appendChild(summary);
  viewport.appendChild(header);

  const sectionCard = element("section", "card route-directory");
  sectionCard.appendChild(element("h2", "section-title", "شاشات " + MAIN_SECTIONS[section].title));
  const tree = element("nav", "route-tree");
  tree.setAttribute("aria-label", "شاشات " + MAIN_SECTIONS[section].title);
  buildRouteTree(section, tree);
  sectionCard.appendChild(tree);
  viewport.appendChild(sectionCard);

  // Shared authentication and initial setup belong under Settings, not in
  // extra first-level navigation items.
  if (section === "settings") {
    const access = element("section", "card route-directory");
    access.appendChild(element("h2", "section-title", "تسجيل الدخول والتهيئة والخدمات المشتركة"));
    const links = element("div", "shared-route-grid");
    SCREEN_CATALOG.filter(route => route.path.startsWith("screens/SharedScreens/"))
      .forEach(route => links.appendChild(createRouteLink(route, "route-link")));
    access.appendChild(links);
    viewport.appendChild(access);
  }
  if (updateUrl !== false) history.replaceState(null, "", "#" + encodeURIComponent(section));
}

// AppSettings.Theme is the only persisted theme. Before authentication the
// existing button remains a local preview; after login it reads/writes C#.
let themeAuthorized=false,themeWriteInFlight=false,themeEpoch=0;
async function syncThemeFromSettings() {
  themeAuthorized=true;
  const epoch=++themeEpoch;
  const bridge=window.AltamamBridge;
  if(!bridge?.hasBridge?.())return;
  try {
    const reply=await bridge.invoke("GetSettingsOverview",{});
    if(epoch!==themeEpoch||!themeAuthorized)return;
    const theme=reply?.data?.theme;
    if(theme==="dark"||theme==="light")
      document.body.classList.toggle("dark-theme",theme==="dark");
  } catch {
    // An unavailable settings read may not silently write over the saved theme.
  }
}
function clearThemeAuthorization(){themeAuthorized=false;themeEpoch++;}
async function toggleTheme() {
  if(themeWriteInFlight)return;
  const dark=!document.body.classList.contains("dark-theme");
  themeEpoch++;
  document.body.classList.toggle("dark-theme",dark);
  if(!themeAuthorized)return;
  const bridge=window.AltamamBridge;
  if(!bridge?.hasBridge?.())return;
  themeWriteInFlight=true;
  try {
    await bridge.invoke("SetTheme",{theme:dark?"dark":"light"});
  } catch {
    document.body.classList.toggle("dark-theme",!dark);
    if(window.AltamamDialog?.alert)
      void window.AltamamDialog.alert("تعذر حفظ المظهر. تأكد من صلاحية الجلسة.");
  } finally { themeWriteInFlight=false; }
}

function closeModal() {
  document.getElementById("appModalBackdrop")?.classList.remove("open");
}

document.addEventListener("click", event => {
  const control = event.target.closest("[data-action]");
  if (!control) return;
  switch (control.dataset.action) {
    case "navigateTop": navigateTop(control.dataset.section); break;
    case "toggleTheme": toggleTheme(); break;
    case "closeModal": closeModal(); break;
  }
});
window.addEventListener("DOMContentLoaded", () => {
  if (window.AltamamFunctional) return;
  const requested = decodeURIComponent(location.hash.slice(1));
  const route = SCREEN_BY_KEY.get(requested);
  // Module return links point to #RouteKey: show its containing section instead
  // of immediately reopening the same module and trapping the user in a loop.
  if (route) {
    navigateTop(sectionForRoute(route), false);
    return;
  }
  navigateTop(Object.hasOwn(MAIN_SECTIONS, requested) ? requested : "dashboard", false);
});
window.AltamamNavigation = Object.freeze({ navigateTop, openScreen, sectionForRoute });
window.navigateTop = navigateTop;
window.toggleTheme = toggleTheme;
window.AltamamTheme=Object.freeze({
  sync:syncThemeFromSettings,
  clearAuthorization:clearThemeAuthorization
});
window.closeModal = closeModal;
