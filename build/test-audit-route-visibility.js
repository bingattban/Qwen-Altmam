"use strict";
// Phase 11 audit-screen route visibility; exercised by both Phase 01 Windows CI workflows.
// Server-side RBAC remains the independent, authoritative enforcement boundary.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const js = fs.readFileSync(path.join(__dirname,
  "../src/AlTamam.WebUI/assets/js/functional-screens.js"), "utf8");
const begin = js.indexOf("  function canSeeRoute(route) {");
const end = js.indexOf("\n  document.addEventListener(", begin);
assert.ok(begin >= 0 && end > begin, "route permission guard missing");
const guard = js.slice(begin, end);

const routes = [
  ["AuditLog", "screens/Administration/AuditLog/index.html"],
  ["EditAuditEntry", "screens/Administration/AuditLog/EditAuditEntry/index.html"],
  ["DeleteAuditEntry", "screens/Administration/AuditLog/DeleteAuditEntry/index.html"],
  ["ModifiedDeletedAuditEntries",
    "screens/Administration/AuditLog/ModifiedDeletedAuditEntries/index.html"],
  ["Users", "screens/Administration/Users/index.html"],
  ["ResetApplicationData", "screens/Settings/ResetApplicationData/index.html"]
];

const expectations = {
  GeneralAdministration: [true, true, true, true, true, true],
  OrdinaryAdministration: [true, false, false, true, true, false],
  Supervisor: [true, false, false, false, false, false],
  unauthenticated: [false, false, false, false, false, false]
};

for (const [role, expected] of Object.entries(expectations)) {
  const STATE = {session: role === "unauthenticated" ? null : {role}};
  const visible = vm.runInNewContext(guard + "\ncanSeeRoute",
    {STATE}, {timeout: 1000});
  routes.forEach(([key, routePath], index) => {
    assert.strictEqual(visible({key, path: routePath}), expected[index],
      role + " must have correct route visibility for " + key);
  });
}
console.log("AUDIT ROUTE RBAC PASS: 24 route/role checks");
