"use strict";
/* ============================================================================
   AlTamam WebView2 bridge client (Phase 04).
   - Asynchronous request/response correlation over window.chrome.webview postMessage.
   - Canonical envelope: {requestId, protocolVersion:1, action, sessionNonce, payload}.
   - Timeout + cancel, loading states, reliable canonical errors, 13 UI invalidation events.
   - No inline scripts (CSP script-src 'self'), zero CDN, offline only.
   ============================================================================ */
(function () {
  const PROTOCOL_VERSION = 1;
  const DEFAULT_TIMEOUT_MS = 15000;
  // Generated from action-manifest.json's mutating actions, excluding Logout.
  // The C# bridge independently REJECTS missing/malformed operationId; this
  // helper supplies an intent ID to every current UI caller, including legacy
  // views that never previously attached one.
  const MUTATING_ACTIONS = new Set([
    "SubmitAttendance",
    "ReviseTodayAttendance",
    "SaveAttendanceDraft",
    "DeleteAttendanceDraft",
    "CommitArchivedReturn",
    "CreatePerson",
    "UpdatePersonIdentity",
    "ArchivePersons",
    "CommitInternalTransfer",
    "CommitExternalTransfer",
    "CommitSeparation",
    "ExportPersonLifetimeExcel",
    "CreateAttendanceStatus",
    "UpdateAttendanceStatus",
    "DeactivateOrDeleteStatus",
    "CreateSite",
    "RenameSite",
    "RemoveSiteAndReassignCurrentPersons",
    "CreateCustomFieldDefinition",
    "UpdateCustomFieldMetadata",
    "DeactivateCustomField",
    "CommitImport",
    "ExportImportIssues",
    "ExportMonthlyWorkbookSet",
    "CancelExport",
    "CreateUserAccount",
    "ChangeUserPassword",
    "DeactivateUserAccount",
    "ReactivateUserAccount",
    "UpdateAuditPresentation",
    "DeleteAuditPresentation",
    "SetAutoAttendanceDefaultStatus",
    "SetAttendanceSchedule",
    "SetOutputDirectory",
    "CreateVerifiedBackup",
    "RestoreDatabaseAtomically",
    "UpdateBackupSchedule",
    "CommitFullReset",
    "SetTheme",
    "SetClosedDayCorrectionPolicy",
    "CommitClosedDayCorrection"
  ]);
  const SESSION_NONCE_STORAGE_KEY = 'altamam.sessionNonce.v1';
  const CANONICAL_ERROR_CODES = ["Unauthorized", "SessionExpired", "MissingData", "ValidationError", "DuplicateServiceNumber", "DuplicateUsername", "ConflictStale", "AlreadyPrepared", "TransferDayAttendanceForbidden", "InvalidTransferSites", "OverlappingAssignments", "HistoricDayLocked", "CustomStatusInactive", "ImportPlanStale", "InvalidWorkbook", "ExcelExportFailed", "BackupIntegrityFailed", "WrongRecoveryKey", "MissingEVolume", "InsufficientDiskSpace", "ScheduleUnavailable", "ResetBackupGateFailed", "StorageBusy", "ClockRollbackSuspected"];
  const UI_INVALIDATION_EVENTS = ["AttendanceCommitted", "AttendanceRevised", "PersonnelMoved", "PersonnelArchived", "SiteCreated", "SiteRenamed", "StatusDefinitionCreated", "StatusDefinitionChanged", "PersonIdentityUpdated", "VisibleAuditOverlayChanged", "UserAccountChanged", "BackupRestored", "SessionEnded"];

  function newRequestId() {
    const bytes = new Uint8Array(16);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
  }

  function getSessionNonce() {
    // The nonce is issued by the C# host on AuthenticateLocalUser; before login a well-formed
    // placeholder is used — the server rejects it for any authenticated action.
    let nonce = null;
    try { nonce = localStorage.getItem(SESSION_NONCE_STORAGE_KEY); } catch (_) { /* storage may be unavailable */ }
    if (nonce && /^[0-9a-fA-F]{32}$/.test(nonce)) return nonce;
    return newRequestId();
  }

  function setSessionNonce(nonce) {
    try {
      if (nonce) localStorage.setItem(SESSION_NONCE_STORAGE_KEY, nonce);
      else localStorage.removeItem(SESSION_NONCE_STORAGE_KEY);
    } catch (_) { /* ignore */ }
    if (!nonce) {
      // Logout/idle expiry discards EVERY unresolved response of the old user.
      // No delayed result is allowed to repaint a new user's screen.
      for (const [requestId, entry] of pending) {
        pending.delete(requestId);
        clearTimeout(entry.timer);
        const error = new Error("أُلغيت العملية لانتهاء جلسة المستخدم.");
        error.errorCode = "SessionExpired";
        entry.reject(error);
      }
    }
  }

  const pending = new Map();          // requestId -> {resolve, reject, timer}
  const listeners = new Set();        // invalidation event subscribers

  function hasBridge() {
    return !!(window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function');
  }

  /** Correlates one response frame back to its originating promise. */
  function onHostMessage(event) {
    let msg;
    try { msg = JSON.parse(event.data); } catch (_) { return; }
    if (!msg || typeof msg.requestId !== 'string') return;
    const entry = pending.get(msg.requestId);
    if (!entry) return;                       // stale or foreign frame — drop silently
    pending.delete(msg.requestId);
    clearTimeout(entry.timer);
    if (msg.success) {
      entry.resolve(msg);
    } else {
      const err = new Error(msg.userMessage || 'فشلت العملية عبر الجسر.');
      err.errorCode = msg.errorCode || 'ValidationError';
      err.canonical = CANONICAL_ERROR_CODES.indexOf(err.errorCode) !== -1;
      entry.reject(err);
    }
    if (Array.isArray(msg.invalidationEvents)) {
      msg.invalidationEvents.forEach(evt => listeners.forEach(fn => { try { fn(evt); } catch (_) { /* listener fault isolation */ } }));
    }
  }

  if (hasBridge()) window.chrome.webview.addEventListener('message', onHostMessage);

  /**
   * Sends one canonical BridgeRequest and resolves with the full BridgeResponse frame.
   * Rejects with Error{errorCode, canonical} on failure, timeout ('BridgeTimeout') or cancel ('BridgeCanceled').
   */
  function invoke(action, payload, options) {
    const opts = options || {};
    const timeoutMs = opts.timeoutMs || DEFAULT_TIMEOUT_MS;
    return new Promise(function (resolve, reject) {
      if (!hasBridge()) {
        const offline = new Error('جسر سطح المكتب غير متاح؛ تعمل الواجهة هنا دون وسيط WebView2.');
        offline.errorCode = 'BridgeOffline';
        reject(offline);
        return;
      }
      const requestId = newRequestId();
      const intent = payload && typeof payload === 'object' && !Array.isArray(payload)
        ? payload : {};
      if (MUTATING_ACTIONS.has(action) && intent.operationId == null) {
        // Reusing the same payload object for an explicit retry preserves the ID.
        // An existing UI-provided ID is never replaced.
        intent.operationId = newRequestId();
      }
      const envelope = {
        requestId: requestId,
        protocolVersion: PROTOCOL_VERSION,
        action: action,
        sessionNonce: opts.sessionNonce || getSessionNonce(),
        payload: intent
      };
      const settleLoading = function (busy) {
        document.dispatchEvent(new CustomEvent('altamam:bridge-loading', { detail: { action: action, requestId: requestId, busy: busy } }));
      };
      settleLoading(true);
      const timer = setTimeout(function () {
        if (pending.delete(requestId)) {
          settleLoading(false);
          const err = new Error('انتهت مهلة الاستجابة من خدمات البرنامج.');
          err.errorCode = 'BridgeTimeout';
          reject(err);
        }
      }, timeoutMs);
      pending.set(requestId, {
        resolve: function (msg) { settleLoading(false); resolve(msg); },
        reject: function (err) { settleLoading(false); reject(err); },
        timer: timer
      });
      if (opts.signal) {
        opts.signal.addEventListener('abort', function () {
          const entry = pending.get(requestId);
          if (entry) {
            pending.delete(requestId);
            clearTimeout(entry.timer);
            settleLoading(false);
            const err = new Error('أُلغي الطلب.');
            err.errorCode = 'BridgeCanceled';
            entry.reject(err);
          }
        });
      }
      window.chrome.webview.postMessage(JSON.stringify(envelope));
    });
  }

  /** Subscribes to the 13 canonical UiInvalidation events pushed after committed mutations. */
  function onInvalidation(listener) {
    listeners.add(listener);
    return function () { listeners.delete(listener); };
  }

  // ---------------------------------------------------------------- unified navigation (04.06)
  // The main window owns navigation, so avoid injecting a second English index,
  // nested iframe, or duplicated event listeners into its Arabic page.
  function openScreen(routeKey) {
    const navigation = window.AltamamNavigation;
    return !!(navigation && navigation.openScreen(routeKey));
  }

  // Phase 04: standalone read-only modules use actual C# contracts.
  // Other legacy HTML routes must NOT represent GetApplicationVersion as a data refresh.
  const ROUTE_ACTIONS = Object.freeze({
    Administration: "QuerySites",
    Sites: "QuerySites",
    Statuses: "QueryAttendanceStatuses",
    PersonnelManagement: "SearchPersons",
    PersonPickerAndSearch: "SearchPersons",
    DraftsAndTemporarySave: "ListAttendanceDrafts"
  });
  const SEARCH_ROUTES = new Set(["PersonnelManagement", "PersonPickerAndSearch"]);
  let refreshing = false;

  function setStatus(text, kind) {
    const label = document.getElementById("bridgeStatusText");
    if (label) label.textContent = text;
    const box = document.getElementById("bridgeStatusBox");
    if (box) {
      box.classList.remove("error", "success", "warn", "loading", "empty");
      if (kind) box.classList.add(kind);
    }
  }

  function appendMessage(parent, text) {
    const message = document.createElement("p");
    message.textContent = text;
    parent.appendChild(message);
  }

  // All user/server values are textContent, never HTML markup.
  function renderScreenData(routeKey, data, mount) {
    const searching = SEARCH_ROUTES.has(routeKey);
    const sites = routeKey === "Administration" || routeKey === "Sites";
    const rows = searching ? data?.items : data;
    if (!Array.isArray(rows))
      throw new Error("صيغة بيانات الشاشة المستلمة من الخدمة غير صالحة.");
    mount.replaceChildren();
    if (rows.length === 0) {
      appendMessage(mount, searching ? "لا توجد نتائج مطابقة للبحث." :
        sites ? "لا توجد مواقع لعرضها." :
        routeKey === "Statuses" ? "لا توجد حالات نشطة لعرضها." :
        "لا توجد مسودات محفوظة لهذا الحساب.");
      return 0;
    }
    const headings = searching
      ? ["الرقم (ع)", "الاسم", "الموقع", "الوضع التنظيمي"]
      : sites
        ? ["الموقع", "الحالة"]
        : routeKey === "Statuses"
          ? ["الحالة", "الرمز", "اللون"]
          : ["تاريخ المسودة", "معرّف الموقع", "معرّف الحالة", "عدد الأفراد"];
    const wrap = document.createElement("div");
    wrap.className = "functional-table-wrap";
    const table = document.createElement("table");
    table.className = "data-table functional-table";
    const head = document.createElement("thead");
    const header = document.createElement("tr");
    for (const heading of headings) {
      const cell = document.createElement("th");
      cell.textContent = heading;
      header.appendChild(cell);
    }
    head.appendChild(header);
    const body = document.createElement("tbody");
    for (const item of rows) {
      if (!item || typeof item !== "object" || Array.isArray(item))
        throw new Error("سجل مستلم غير صالح.");
      const values = searching
        ? [item.serviceNumberA, item.displayName, item.siteName,
            item.organizationalStatus === 1 ? "نشط" : "مؤرشف"]
        : sites
          ? [item.name, item.active ? "نشط" : "غير نشط"]
          : routeKey === "Statuses"
            ? [item.displayName, item.shortSymbol, item.color]
            : [item.businessDate, item.siteId || "غير محدد", item.targetStatusId,
                Array.isArray(item.selectedPersonIds)
                  ? item.selectedPersonIds.length : "غير متاح"];
      const row = document.createElement("tr");
      for (const value of values) {
        const cell = document.createElement("td");
        cell.textContent = value == null ? "" : String(value);
        row.appendChild(cell);
      }
      body.appendChild(row);
    }
    table.append(head, body);
    wrap.appendChild(table);
    mount.appendChild(wrap);
    return rows.length;
  }

  async function refreshCurrentScreen() {
    const routeKey = document.body?.dataset?.routeKey;
    if (!routeKey || refreshing) return;
    const action = ROUTE_ACTIONS[routeKey];
    if (!action) {
      setStatus("هذه الشاشة قيد الاستكمال ضمن مرحلتها المعتمدة؛ لا يوجد تحديث بيانات فعلي هنا.", "warn");
      return;
    }
    const mount = document.getElementById("routeResults");
    if (!mount) {
      setStatus("تعذر عرض نتائج الشاشة: حاوية البيانات غير موجودة.", "error");
      return;
    }
    let payload = {};
    if (SEARCH_ROUTES.has(routeKey)) {
      const term = document.getElementById("routeSearch")?.value?.trim() || "";
      if (!term) {
        mount.replaceChildren();
        setStatus("أدخل اسماً أو رقماً (ع) للبحث.", "error");
        return;
      }
      if (term.length > 100 || /[\x00-\x1f\x7f]/.test(term)) {
        mount.replaceChildren();
        setStatus("خطأ في التحقق: نص البحث غير صالح (الحد 100 حرف).", "error");
        return;
      }
      payload = {term: term, take: 50};
    }
    refreshing = true;
    const button = document.getElementById("routeSubmit");
    if (button) button.disabled = true;
    mount.replaceChildren();
    appendMessage(mount, "جارٍ تحميل البيانات من البرنامج المحلي…");
    setStatus("جارٍ تحميل البيانات…", "loading");
    try {
      const response = await invoke(action, payload);
      const count = renderScreenData(routeKey, response.data, mount);
      setStatus(count ? "تم عرض " + count + " سجل من بيانات البرنامج المحلية." :
        "اكتملت القراءة، ولا توجد بيانات مطابقة.", count ? "success" : "empty");
    } catch (err) {
      mount.replaceChildren(); // Never retain another session's or an obsolete query's rows.
      if (err.errorCode === "SessionExpired") {
        setSessionNonce(null);
        setStatus("انتهت الجلسة؛ سجّل الدخول من الواجهة الرئيسية.", "error");
      } else if (err.errorCode === "Unauthorized") {
        setStatus("غير مصرح بعرض هذه البيانات وفق صلاحيات الحساب الحالي.", "error");
      } else if (err.errorCode === "ValidationError" || err.errorCode === "MissingData") {
        setStatus("خطأ في التحقق: " + (err.message || "بيانات البحث غير صالحة."), "error");
      } else if (err.errorCode === "ConflictStale") {
        setStatus("تعارض: تغيرت البيانات؛ أعد القراءة قبل متابعة العمل.", "error");
      } else {
        setStatus("تعذر تحديث البيانات: " + (err.message || err.errorCode || "خطأ غير معروف"), "error");
      }
    } finally {
      refreshing = false;
      if (button) button.disabled = false;
    }
  }

  document.addEventListener("click", function (event) {
    const button = event.target?.closest?.('[data-bridge-action="REFRESH"]');
    if (button && !button.disabled) {
      event.preventDefault();
      void refreshCurrentScreen();
    }
  });
  document.addEventListener("submit", function (event) {
    if (event.target?.matches?.("form[data-bridge-search]")) {
      event.preventDefault();
      void refreshCurrentScreen();
    }
  });
  document.addEventListener("DOMContentLoaded", function () {
    const routeKey = document.body?.dataset?.routeKey;
    if (routeKey && !ROUTE_ACTIONS[routeKey]) {
      document.querySelectorAll('[data-bridge-action="REFRESH"]').forEach(button => {
        button.disabled = true;
        button.textContent = "قيد التطوير";
        button.title = "لم يكتمل الربط الوظيفي لهذه الشاشة بعد";
      });
      setStatus("هذه الشاشة قيد الاستكمال ضمن مرحلتها؛ افتح الواجهة الرئيسية للوظائف المتاحة.", "warn");
    }
  });

  // Loading-state hook: marks the status area busy while a request is in flight.
  document.addEventListener('altamam:bridge-loading', function (event) {
    const el = document.getElementById('bridgeStatusText');
    if (el && event.detail && event.detail.busy) el.textContent = 'جارٍ تنفيذ: ' + event.detail.action + '…';
  });

  window.AltamamBridge = {
    invoke: invoke,
    onInvalidation: onInvalidation,
    setSessionNonce: setSessionNonce,
    getSessionNonce: getSessionNonce,
    hasBridge: hasBridge,
    refresh: refreshCurrentScreen,
    openScreen: openScreen,
    protocolVersion: PROTOCOL_VERSION,
    canonicalErrorCodes: CANONICAL_ERROR_CODES,
    uiInvalidationEvents: UI_INVALIDATION_EVENTS
  };
})();
