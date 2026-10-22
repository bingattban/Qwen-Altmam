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
      const envelope = {
        requestId: requestId,
        protocolVersion: PROTOCOL_VERSION,
        action: action,
        sessionNonce: opts.sessionNonce || getSessionNonce(),
        payload: payload || {}
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
  const SCREEN_ROUTES = {"AboutApplication": "screens/Settings/AboutApplication/index.html", "AddOrEditColumn": "screens/Administration/Rosters/RosterLayout/AddOrEditColumn/index.html", "AddSite": "screens/Administration/StatusesAndSites/Sites/AddSite/index.html", "AddStatus": "screens/Administration/StatusesAndSites/Statuses/AddStatus/index.html", "AddUser": "screens/Administration/Users/AddUser/index.html", "Administration": "screens/Administration/index.html", "AppearanceAndDiagnostics": "screens/Settings/AppearanceAndDiagnostics/index.html", "Attendance": "screens/Administration/PersonnelManagement/Attendance/index.html", "AuditLog": "screens/Administration/AuditLog/index.html", "ChangePassword": "screens/Administration/Users/ChangePassword/index.html", "ClosedDayExceptionalCorrection": "screens/SharedScreens/ClosedDayExceptionalCorrection/index.html", "ConfirmAttendance": "screens/Administration/PersonnelManagement/Attendance/ConfirmAttendance/index.html", "ConfirmDismissal": "screens/Administration/PersonnelManagement/ReportsAndDismissal/Dismissal/DismissPerson/ConfirmDismissal/index.html", "ConfirmExternalTransfer": "screens/Administration/PersonnelManagement/Transfers/ExternalTransfer/ConfirmExternalTransfer/index.html", "ConfirmTransfer": "screens/Administration/PersonnelManagement/Transfers/InternalTransfer/ConfirmTransfer/index.html", "ConfirmationAndWarning": "screens/SharedScreens/ConfirmationAndWarning/index.html", "ConflictsAndNotifications": "screens/SharedScreens/ConflictsAndNotifications/index.html", "CreateBackup": "screens/Settings/CreateBackup/index.html", "Dashboard": "screens/Dashboard/index.html", "DeleteAccount": "screens/Administration/Users/DeleteAccount/index.html", "DeleteAuditEntry": "screens/Administration/AuditLog/DeleteAuditEntry/index.html", "DeleteSite": "screens/Administration/StatusesAndSites/Sites/DeleteSite/index.html", "DeleteStatus": "screens/Administration/StatusesAndSites/Statuses/DeleteStatus/index.html", "DismissPerson": "screens/Administration/PersonnelManagement/ReportsAndDismissal/Dismissal/DismissPerson/index.html", "Dismissal": "screens/Administration/PersonnelManagement/ReportsAndDismissal/Dismissal/index.html", "DismissedPersonnelArchive": "screens/Administration/PersonnelManagement/ReportsAndDismissal/Dismissal/DismissedPersonnelArchive/index.html", "DraftsAndTemporarySave": "screens/SharedScreens/DraftsAndTemporarySave/index.html", "EditAuditEntry": "screens/Administration/AuditLog/EditAuditEntry/index.html", "EditPersonDetails": "screens/Administration/PersonnelManagement/EditPersonnelNames/EditPersonDetails/index.html", "EditPersonnelNames": "screens/Administration/PersonnelManagement/EditPersonnelNames/index.html", "EditSite": "screens/Administration/StatusesAndSites/Sites/EditSite/index.html", "EditStatus": "screens/Administration/StatusesAndSites/Statuses/EditStatus/index.html", "EightPmScheduler": "screens/SharedScreens/EightPmScheduler/index.html", "Export": "screens/Administration/Rosters/Export/index.html", "ExportPersonReport": "screens/Administration/PersonnelManagement/PersonnelReport/ExportPersonReport/index.html", "ExportProgressAndResult": "screens/Administration/Rosters/Export/ExportProgressAndResult/index.html", "ExternalTransfer": "screens/Administration/PersonnelManagement/Transfers/ExternalTransfer/index.html", "FirstRunSetup": "screens/SharedScreens/FirstRunSetup/index.html", "Import": "screens/Administration/Rosters/Import/index.html", "ImportResults": "screens/Administration/Rosters/Import/ImportResults/index.html", "InternalTransfer": "screens/Administration/PersonnelManagement/Transfers/InternalTransfer/index.html", "Login": "screens/SharedScreens/Login/index.html", "Logout": "screens/Settings/Logout/index.html", "ModifiedDeletedAuditEntries": "screens/Administration/AuditLog/ModifiedDeletedAuditEntries/index.html", "MonthlyAttendance": "screens/MonthlyAttendance/index.html", "PersonPickerAndSearch": "screens/SharedScreens/PersonPickerAndSearch/index.html", "PersonnelManagement": "screens/Administration/PersonnelManagement/index.html", "PersonnelReport": "screens/Administration/PersonnelManagement/PersonnelReport/index.html", "PreviewAndConfirmExport": "screens/Administration/Rosters/Export/PreviewAndConfirmExport/index.html", "PreviewAndValidate": "screens/Administration/Rosters/Import/PreviewAndValidate/index.html", "RemovePersonnel": "screens/Administration/PersonnelManagement/EditPersonnelNames/RemovePersonnel/index.html", "Reports": "screens/Administration/PersonnelManagement/ReportsAndDismissal/Reports/index.html", "ReportsAndDismissal": "screens/Administration/PersonnelManagement/ReportsAndDismissal/index.html", "ResetApplicationData": "screens/Settings/ResetApplicationData/index.html", "RestoreBackup": "screens/Settings/RestoreBackup/index.html", "RosterLayout": "screens/Administration/Rosters/RosterLayout/index.html", "Rosters": "screens/Administration/Rosters/index.html", "ScheduledBackups": "screens/Settings/ScheduledBackups/index.html", "SelectArchive": "screens/MonthlyAttendance/SelectArchive/index.html", "SelectFile": "screens/Administration/Rosters/Import/SelectFile/index.html", "SelectStatusAndConfirm": "screens/Dashboard/UnpreparedPersonnel/SelectStatusAndConfirm/index.html", "Settings": "screens/Settings/index.html", "Sites": "screens/Administration/StatusesAndSites/Sites/index.html", "Statuses": "screens/Administration/StatusesAndSites/Statuses/index.html", "StatusesAndSites": "screens/Administration/StatusesAndSites/index.html", "TransferArchive": "screens/Administration/PersonnelManagement/Transfers/TransferArchive/index.html", "Transfers": "screens/Administration/PersonnelManagement/Transfers/index.html", "UnpreparedPersonnel": "screens/Dashboard/UnpreparedPersonnel/index.html", "Users": "screens/Administration/Users/index.html"};

  const SECTIONS = {
    dashboard: 'Dashboard', monthly: 'MonthlyAttendance', admin: 'Administration',
    settings: 'Settings', auth: 'SharedScreens', routes69: null
  };

  function screenMatchesSection(routeKey, section) {
    const path = SCREEN_ROUTES[routeKey];
    if (!path) return false;
    if (section === 'routes69') return true;
    const folder = SECTIONS[section];
    return folder ? path.indexOf('screens/' + folder + '/') === 0 : false;
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  }

  function renderRouteIndex(container, section) {
    const keys = Object.keys(SCREEN_ROUTES).filter(function (k) { return screenMatchesSection(k, section); });
    const wrap = document.createElement('section');
    wrap.className = 'card';
    const head = document.createElement('div');
    head.className = 'card-header';
    const title = document.createElement('h2');
    title.className = 'page-title';
    title.textContent = 'فهرس الشاشات المتصلة بالجسر';
    head.appendChild(title);
    wrap.appendChild(head);
    const grid = document.createElement('div');
    grid.className = 'presentb7e106bd3';
    keys.forEach(function (k) {
      const btn = document.createElement('button');
      btn.className = 'btn btn-outline';
      btn.dataset.action = 'openScreen';
      btn.dataset.screen = k;
      btn.textContent = k;
      grid.appendChild(btn);
    });
    wrap.appendChild(grid);
    container.insertBefore(wrap, container.firstChild);
  }

  function openScreen(routeKey) {
    const viewport = document.getElementById('appViewport');
    if (!viewport) return;
    const path = SCREEN_ROUTES[routeKey];
    if (!path) return;
    history.replaceState(null, '', '#' + encodeURIComponent(routeKey));
    viewport.textContent = '';
    const bar = document.createElement('div');
    bar.className = 'present8b2e0334c';
    const close = document.createElement('button');
    close.className = 'btn btn-outline';
    close.dataset.action = 'closeScreen';
    close.textContent = '← إغلاق الشاشة';
    bar.appendChild(close);
    const frame = document.createElement('iframe');
    frame.className = 'screen-frame';
    frame.setAttribute('style', 'width:100%;min-height:70vh;border:0;background:#fff;');
    frame.setAttribute('title', routeKey);
    frame.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-forms');
    frame.src = encodeURI(path);
    viewport.appendChild(bar);
    viewport.appendChild(frame);
  }

  document.addEventListener('click', function (event) {
    const control = event.target.closest('[data-action]');
    if (!control) return;
    if (control.dataset.action === 'openScreen') { event.preventDefault(); openScreen(control.dataset.screen); }
    if (control.dataset.action === 'closeScreen') {
      event.preventDefault();
      const v = document.getElementById('appViewport');
      if (v) v.textContent = '';
    }
  });

  // Keep the existing top-level navigation but append the authentic route index per section.
  const originalNavigateTop = window.navigateTop;
  window.navigateTop = function (section) {
    if (typeof originalNavigateTop === 'function') originalNavigateTop(section);
    const viewport = document.getElementById('appViewport');
    if (viewport) renderRouteIndex(viewport, section);
  };

  document.addEventListener('DOMContentLoaded', function () {
    const name = decodeURIComponent(location.hash.slice(1));
    if (SCREEN_ROUTES[name]) openScreen(name);
  });

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
