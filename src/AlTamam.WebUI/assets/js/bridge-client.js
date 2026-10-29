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

  // ---------------------------------------------------------------- REFRESH wiring (04.07)
  const ROUTE_ACTIONS = {
    Administration: ['QuerySites'],
    PersonnelManagement: ['SearchPersons'],
    PersonPickerAndSearch: ['SearchPersons'],
    ConfirmationAndWarning: [],
    DraftsAndTemporarySave: ['ListAttendanceDrafts'],
    ConflictsAndNotifications: []
  };

  function setStatus(text, kind) {
    const el = document.getElementById('bridgeStatusText');
    if (!el) return;
    el.textContent = text;
    const box = document.getElementById('bridgeStatusBox');
    if (box && kind === 'error') box.classList.add('error');
  }

  async function refreshCurrentScreen() {
    const bodyEl = document.body;
    const routeKey = bodyEl && bodyEl.dataset ? bodyEl.dataset.routeKey : null;
    if (!routeKey) return;
    setStatus('جارٍ تحميل البيانات…', 'loading');
    const actions = ROUTE_ACTIONS[routeKey] || [];
    try {
      if (actions.length === 0) {
        const version = await invoke('GetApplicationVersion', {});
        setStatus('الجسر متصل — الإصدار ' + ((version.data && version.data.version) || '?') + ' (يعمل دون اتصال خارجي).', 'ok');
        return;
      }
      let lastData = null;
      for (const action of actions) {
        const payload = action === 'SearchPersons' ? { term: ' ', take: 10 } : {};
        const res = await invoke(action, payload);
        lastData = res.data;
      }
      renderScreenData(routeKey, lastData);
      setStatus('تم تحديث البيانات من خدمات البرنامج المحلية.', 'ok');
    } catch (err) {
      if (err.errorCode === 'SessionExpired' || err.errorCode === 'Unauthorized') {
        setSessionNonce(null);
        setStatus('الجلسة غير سارية — سجّل الدخول أولاً لعرض البيانات.', 'error');
      } else if (err.errorCode === 'MissingData') {
        setStatus('الاتصال بالجسر سليم؛ بيانات هذه الشاشة تُفعَّل في مراحلها المخصصة.', 'warn');
      } else {
        setStatus('تعذر التحديث: ' + (err.message || err.errorCode || 'خطأ غير معروف'), 'error');
      }
    }
  }

  function renderScreenData(routeKey, data) {
    const mount = document.querySelector('[data-bridge-mount="' + routeKey + '"] .present65c9a6077');
    if (!mount || !data) return;
    if (Array.isArray(data) && data.length) {
      mount.textContent = '';
      const list = document.createElement('ul');
      data.slice(0, 10).forEach(function (row) {
        const li = document.createElement('li');
        li.textContent = row.name || row.displayName || row.visibleName || JSON.stringify(row);
        list.appendChild(li);
      });
      mount.appendChild(list);
    } else if (data && Array.isArray(data.items)) {
      mount.textContent = 'نتائج البحث: ' + data.count;
    }
  }

  document.addEventListener('click', function (event) {
    const button = event.target.closest('[data-bridge-action]');
    if (!button) return;
    if (button.dataset.bridgeAction === 'REFRESH') { event.preventDefault(); refreshCurrentScreen(); }
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
