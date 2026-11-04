"use strict";
/* Personal local reminders. All persistence and authorization are owned by WPF.
   No SQLCipher, audit records, AppSettings, WebView storage, or external network. */
(function () {
  const CHANNEL = "altamam.reminders.v1";
  const pending = new Map();
  let active = false, pollTimer = null, polling = false;
  let listRefresh = null;
  const api = () => window.AltamamBridge;

  function element(tag, className, value) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (value !== undefined) node.textContent = String(value);
    return node;
  }
  function button(label, click, className) {
    const node = element("button", className || "btn btn-outline", label);
    node.type = "button";
    node.addEventListener("click", click);
    return node;
  }
  function localStamp(value) {
    const date = value instanceof Date ? value : new Date(value);
    return [date.getFullYear(), String(date.getMonth() + 1).padStart(2, "0"),
      String(date.getDate()).padStart(2, "0")].join("-") + "T" +
      String(date.getHours()).padStart(2, "0") + ":" +
      String(date.getMinutes()).padStart(2, "0");
  }
  function requestId() {
    const values = crypto.getRandomValues(new Uint8Array(16));
    return Array.from(values, v => v.toString(16).padStart(2, "0")).join("");
  }
  function send(command, payload) {
    return new Promise((resolve, reject) => {
      if (!api()?.hasBridge?.()) {
        reject(new Error("لا يوجد اتصال بخدمة التنبيهات المحلية."));
        return;
      }
      const id = requestId();
      const timer = setTimeout(() => {
        pending.delete(id);
        reject(new Error("انتهت مهلة التواصل مع خدمة التنبيهات المحلية."));
      }, 15000);
      pending.set(id, { resolve, reject, timer });
      try {
        window.chrome.webview.postMessage(JSON.stringify({
          channel: CHANNEL,
          requestId: id,
          sessionNonce: api().getSessionNonce(),
          command,
          payload: payload || {}
        }));
      } catch (error) {
        pending.delete(id);
        clearTimeout(timer);
        reject(error);
      }
    });
  }
  window.chrome?.webview?.addEventListener?.("message", event => {
    let message;
    try {
      message = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
    } catch { return; }
    if (message?.channel !== CHANNEL) return;
    const waiter = pending.get(message.requestId);
    if (!waiter) return;
    pending.delete(message.requestId);
    clearTimeout(waiter.timer);
    if (message.result?.ok) waiter.resolve(message.result.data);
    else waiter.reject(new Error(message.result?.error || "تعذر تنفيذ طلب التنبيه."));
  });

  function toastHost() {
    let root = document.getElementById("reminderToasts");
    if (!root) {
      root = element("aside", "reminder-toast-host");
      root.id = "reminderToasts";
      root.setAttribute("aria-label", "التنبيهات المستحقة");
      document.body.appendChild(root);
    }
    return root;
  }
  function showNotifications(items) {
    if (!active || !Array.isArray(items)) return;
    const root = toastHost();
    for (const item of items) {
      if (!item?.id || root.querySelector('[data-reminder-id="' + item.id + '"]')) continue;
      const toast = element("section", "reminder-toast");
      toast.dataset.reminderId = item.id;
      toast.setAttribute("role", "status");
      const title = element("strong", "reminder-toast-title", "🔔 " + item.name);
      const subject = element("p", "reminder-toast-subject", item.subject);
      const time = element("small", "reminder-toast-time",
        item.silent ? "فات موعده أثناء إغلاق البرنامج — إشعار صامت" : "حان موعد التنبيه");
      const status = element("div", "reminder-toast-status");
      const actions = element("div", "reminder-actions");
      async function complete(command) {
        stopButton.disabled = true;
        snoozeButton.disabled = true;
        try {
          await send(command, { id: item.id });
          toast.remove();
          if (listRefresh) void listRefresh();
        } catch (error) {
          status.textContent = error.message;
          stopButton.disabled = false;
          snoozeButton.disabled = false;
        }
      }
      const stopButton = button("إيقاف", () => void complete("dismiss"), "btn btn-outline");
      const snoozeButton = button("تأجيل 5 دقائق", () => void complete("snooze"), "btn btn-primary");
      actions.append(stopButton, snoozeButton);
      toast.append(title, subject, time, status, actions);
      root.appendChild(toast);
    }
  }

  async function poll() {
    if (!active || polling) return;
    polling = true;
    try {
      const result = await send("poll");
      if (active) {
        showNotifications(result?.notifications);
        if (listRefresh && (result?.notifications?.length || 0)) void listRefresh();
      }
    } catch { /* A transient reminder error must not interrupt force operations. */ }
    finally { polling = false; }
  }
  function start() {
    if (active) return;
    active = true;
    void poll();
    pollTimer = setInterval(() => void poll(), 10000);
  }
  function stop() {
    active = false;
    if (pollTimer !== null) clearInterval(pollTimer);
    pollTimer = null;
    listRefresh = null;
    document.getElementById("reminderToasts")?.replaceChildren();
    for (const [id, waiter] of pending) {
      pending.delete(id);
      clearTimeout(waiter.timer);
      waiter.reject(new Error("انتهت جلسة المستخدم."));
    }
  }

  function render(container) {
    listRefresh = null;
    const shell = element("section", "card reminders-screen");
    shell.appendChild(element("h2", "section-title", "التنبيهات"));
    shell.appendChild(element("p", "section-description",
      "تنبيهات محلية مستقلة عن قاعدة بيانات التمام. تظهر للجميع أثناء تشغيل البرنامج؛ وما فات أثناء إغلاقه يظهر بصمت عند الدخول."));
    const tabs = element("div", "reminder-tabs");
    const body = element("div", "reminder-screen-body");
    const status = element("p", "reminder-feedback");
    status.setAttribute("role", "status");
    const add = button("＋ إضافة تنبيه", () => showEditor(), "btn btn-outline");
    const list = button("☷ قائمة التنبيهات", () => void showList(), "btn btn-outline");
    tabs.append(add, list);
    shell.append(tabs, status, body);
    container.appendChild(shell);

    function feedback(message, failed) {
      status.textContent = message || "";
      status.classList.toggle("reminder-feedback-error", !!failed);
    }
    function toggle(activeTab) {
      add.classList.toggle("reminder-selected", activeTab === "add");
      list.classList.toggle("reminder-selected", activeTab === "list");
    }
    function showEditor(item = null) {
      listRefresh = null;
      toggle("add");
      feedback("");
      body.replaceChildren();
      const form = element("form", "reminder-form");
      const grid = element("div", "reminder-form-grid");
      function labeled(label, input) {
        const wrap = element("label", "admin-input");
        wrap.append(element("span", "", label), input);
        return wrap;
      }
      const name = element("input", "form-control");
      name.type = "text"; name.maxLength = 120; name.required = true;
      name.value = item?.name || "";
      const subject = element("textarea", "form-control");
      subject.maxLength = 2000; subject.required = true; subject.rows = 4;
      subject.value = item?.subject || "";
      const due = element("input", "form-control");
      due.type = "datetime-local"; due.required = true;
      due.step = "60";
      due.min = localStamp(new Date(Date.now() + 60000));
      due.value = item?.dueLocal || localStamp(new Date(Date.now() + 10 * 60000));
      grid.append(labeled("اسم التنبيه", name), labeled("موضوع التنبيه", subject),
        labeled("وقت التنبيه (اليوم / الشهر / الساعة / الدقيقة)", due));
      const save = element("button", "btn btn-primary",
        item ? "حفظ التعديل" : "حفظ التنبيه");
      save.type = "submit";
      form.append(grid, save);
      form.addEventListener("submit", async event => {
        event.preventDefault();
        feedback("");
        save.disabled = true;
        try {
          await send("upsert", {
            id: item?.id || "", name: name.value.trim(),
            subject: subject.value.trim(), dueLocal: due.value
          });
          await showList("تم حفظ التنبيه بنجاح.");
        } catch (error) { feedback(error.message, true); }
        finally { save.disabled = false; }
      });
      body.appendChild(form);
    }
    async function showList(message = "") {
      listRefresh = () => showList();
      toggle("list");
      feedback(message);
      body.replaceChildren(element("p", "section-description", "جارٍ تحميل التنبيهات…"));
      try {
        const result = await send("list");
        if (!body.isConnected || listRefresh === null) return;
        body.replaceChildren();
        const records = Array.isArray(result?.items) ? result.items : [];
        if (!records.length) {
          body.appendChild(element("p", "section-description", "لا توجد تنبيهات محفوظة."));
          return;
        }
        const ordered = records.slice().sort((a, b) =>
          (a.snoozedUntilLocal || a.dueLocal).localeCompare(b.snoozedUntilLocal || b.dueLocal));
        for (const item of ordered) {
          const expires = new Date(item.snoozedUntilLocal || item.dueLocal).getTime() <= Date.now();
          const expired = item.completed || expires;
          const row = element("article", "reminder-list-item " +
            (expired ? "reminder-expired" : "reminder-active"));
          row.append(
            element("strong", "reminder-list-title", item.name),
            element("span", "reminder-state", expired ? "منتهٍ" : "فعال"),
            element("p", "reminder-list-subject", item.subject),
            element("p", "reminder-list-date", "الموعد: " +
              item.dueLocal.replace("T", " — ") +
              (item.snoozedUntilLocal ? " | مؤجل إلى " +
                item.snoozedUntilLocal.replace("T", " — ") : ""))
          );
          const actions = element("div", "reminder-list-actions");
          actions.appendChild(button("تعديل", () => showEditor(item), "btn btn-outline"));
          actions.appendChild(button("حذف", async () => {
            const accepted = window.AltamamDialog?.confirm
              ? await window.AltamamDialog.confirm("هل تريد حذف التنبيه «" + item.name + "»؟")
              : window.confirm("هل تريد حذف هذا التنبيه؟");
            if (!accepted) return;
            try {
              await send("delete", { id: item.id });
              await showList("تم حذف التنبيه.");
            } catch (error) { feedback(error.message, true); }
          }, "btn btn-outline"));
          row.appendChild(actions);
          body.appendChild(row);
        }
      } catch (error) {
        body.replaceChildren(element("p", "reminder-feedback-error", error.message));
      }
    }
    showEditor();
  }

  window.AltamamReminders = Object.freeze({ start, stop, render });
})();
