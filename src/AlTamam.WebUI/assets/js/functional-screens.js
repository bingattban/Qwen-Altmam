"use strict";
/* Offline WebView2 user flow. C# remains the only authorization and data authority.
   No demo data, fake success, network calls, or editable month cells. */
(function () {
  const sections = new Set(["dashboard", "monthly", "admin", "settings"]);
  const STATE = { session: null, siteId: "", statusId: "", dayTerm: "", date: "", dayPage: 1,
    monthPage: 1, monthPageSize: 100, monthTerm: "", chosenYear: null, chosenMonth: null, archivePeriods: [],
    monthDayPeriod: "", monthDayStart: 1 };
  const ar = new Intl.DateTimeFormat("ar", { month: "long" });
  const weekday = new Intl.DateTimeFormat("ar", { weekday: "short" });
  const DAILY_PAGE_SIZE=100;
  const nav = window.AltamamNavigation;
  const view = () => document.getElementById("appViewport");
  const bridge = () => window.AltamamBridge;

  function el(tag, cls, value) {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (value !== undefined) node.textContent = String(value);
    return node;
  }
  function append(parent, ...children) { children.forEach(child => parent.appendChild(child)); return parent; }
  function button(label, handler, cls) {
    const b = el("button", cls || "btn btn-outline", label);
    b.type = "button";
    b.addEventListener("click", handler);
    return b;
  }
  function field(type, label, required) {
    const wrapper = el("label", "form-field");
    const text = el("span", "", label);
    const inp = el("input", "form-input");
    inp.type = type;
    inp.required = !!required;
    inp.autocomplete = type === "password" ? "current-password" : type === "search" ? "off" : "on";
    append(wrapper, text, inp);
    return { wrapper, input: inp, label: text };
  }
  // Names remain explicitly associated with their controls even while the
  // visual caption moves. No password/username value is mirrored into the DOM.
  function decorateAuthField(control, id, glyph) {
    const wrapper=el("div","form-field auth-floating-field");
    const caption=el("label","auth-floating-caption",control.label.textContent);
    control.input.id=id;caption.htmlFor=id;control.input.placeholder=" ";
    const icon=el("span","auth-field-icon",glyph);
    icon.setAttribute("aria-hidden","true");
    append(wrapper,icon,control.input,caption);
    control.wrapper=wrapper;
    return control;
  }
  function message(text, error) {
    const p = el("div", error ? "ui-alert ui-error" : "ui-alert", text);
    p.setAttribute("role", error ? "alert" : "status");
    return p;
  }
  function startOfToday() {
    const d = new Date();
    return [d.getFullYear(), String(d.getMonth()+1).padStart(2,"0"),
      String(d.getDate()).padStart(2,"0")].join("-");
  }
  function parseData(reply) { return reply && reply.data !== undefined ? reply.data : null; }
  function showError(container, error) {
    container.replaceChildren(message(error && error.message || "تعذر قراءة البيانات من التطبيق.", true));
  }
  function siteSelector(sites, onChange) {
    const select = el("select", "form-select");
    select.setAttribute("aria-label", "فلتر المواقع");
    const any = el("option", "", "جميع المواقع"); any.value = "";
    append(select, any);
    for (const site of sites) {
      if (site.active === false) continue;
      const opt = el("option", "", site.name); opt.value = site.siteId;
      append(select, opt);
    }
    select.value = STATE.siteId;
    if (select.value !== STATE.siteId) STATE.siteId = "";
    select.addEventListener("change", () => { STATE.siteId=select.value; STATE.dayPage=1;
      STATE.monthPage=1; onChange(); });
    return select;
  }
  function header(title) {
    const card = el("section", "card functional-header");
    card.appendChild(el("h1", "page-title", title));
    return card;
  }
  function createTable(columns) {
    const wrap = el("div", "functional-table-wrap");
    const table = el("table", "data-table functional-table");
    const thead = el("thead"), tr = el("tr");
    columns.forEach(col => tr.appendChild(el("th", col.cls || "", col.label)));
    thead.appendChild(tr);
    append(table, thead, el("tbody"));
    wrap.appendChild(table);
    return { wrap, body: table.tBodies[0] };
  }
  function pager(root, page, total, pageSize, onPage, onPageSize) {
    const bar = el("div", "functional-pager");
    const pageCount=Math.max(1,Math.ceil(total/pageSize));
    append(bar, el("span", "", "عدد الأفراد: " + total + " — صفحة " + page + " من " + pageCount));
    if(typeof onPageSize==="function"){
      const label=el("label", "monthly-page-size");
      label.appendChild(el("span", "", "حجم الصفحة"));
      const sizeSelect=el("select", "form-select");
      sizeSelect.setAttribute("aria-label", "عدد الأفراد في الصفحة");
      for(const value of [25,50,100]){
        const opt=el("option", "", String(value));opt.value=String(value);
        sizeSelect.appendChild(opt);
      }
      sizeSelect.value=String(pageSize);
      sizeSelect.addEventListener("change",()=>{
        const size=Number(sizeSelect.value);
        if([25,50,100].includes(size)&&size!==pageSize)onPageSize(size);
      });
      label.appendChild(sizeSelect);bar.appendChild(label);
    }
    const prev = button("السابق", ()=>onPage(page-1)); prev.disabled=page<=1;
    const next = button("التالي", ()=>onPage(page+1)); next.disabled=page>=pageCount;
    append(bar, prev, next);
    root.classList.add("functional-paged-area");
    root.appendChild(bar);
  }
  function applyMainNav(section) {
    for(const key of sections) {
      const b=document.getElementById("navBtn-"+key);
      if(b) {
        b.classList.toggle("active", key===section);
        if(key===section) b.setAttribute("aria-current","page");
        else b.removeAttribute("aria-current");
      }
    }
  }

  async function checkOutputAfterLogin() {
    try {
      const reply=parseData(await bridge().invoke("GetSettingsOverview",{}));
      // Show the settings screen (and its native-styled modal) immediately after
      // login if E: or the user's previously selected folder is unavailable.
      if(STATE.session && reply?.defaultDriveAvailable===false)
        render("settings");
    } catch { /* A storage-status check never invalidates a valid login. */ }
  }

  async function loginGate() {
    dismissAutoPrepared();
    const container=view(); if(!container)return;
    container.classList.toggle("daily-dashboard",false);
    container.classList.toggle("monthly-dashboard",false);
    document.querySelector(".app-sidebar").hidden=true;
    document.getElementById("headerRoleBadge").textContent="لم يتم تسجيل الدخول";
    document.getElementById("logoutAction").hidden=true;
    container.replaceChildren(header("تسجيل الدخول إلى التمام اليومي"));
    const form=el("form","card auth-form");
    const brand=el("div","auth-brand");
    append(brand,el("div","auth-brand-mark","ت"),
      el("h2","section-title","التمام اليومي"),
      el("p","section-description","نظام إدارة القوة والتحضير — تسجيل محلي آمن"));
    const name=decorateAuthField(field("text","اسم المستخدم",true),"auth-username","👤");
    name.input.autocomplete="username";
    const pass=decorateAuthField(field("password","كلمة المرور",true),"auth-password","🔒");
    // Both password controls have an accessible, keyboard-operable icon toggle.
    function addPasswordReveal(control) {
      const reveal=button("👁",()=>{
        const visible=control.input.type==="password";
        control.input.type=visible?"text":"password";
        reveal.textContent=visible?"🙈":"👁";
        const description=visible?"إخفاء كلمة المرور":"إظهار كلمة المرور";
        reveal.title=description;
        reveal.setAttribute("aria-label",description);
        reveal.setAttribute("aria-pressed",String(visible));
        control.input.focus();
      },"auth-password-toggle");
      reveal.setAttribute("aria-label","إظهار كلمة المرور");
      reveal.setAttribute("aria-pressed","false");
      reveal.title="إظهار كلمة المرور";
      control.wrapper.classList.add("auth-password-field");
      control.wrapper.appendChild(reveal);
    }
    addPasswordReveal(pass);
    // Existing users may have historical credentials; only NEW founders must
    // satisfy the current strong-password policy during initialization.
    const confirmation=decorateAuthField(field("password","تأكيد كلمة المرور",true),
      "auth-confirm-password","🔒");
    addPasswordReveal(confirmation);
    const foundingName=decorateAuthField(
      field("text","الاسم الكامل (اختياري)",false),"auth-full-name","👤");
    let firstUse=false;
    const note=el("p","section-description","يعمل البرنامج على هذا الجهاز دون اتصال بالإنترنت.");
    const status=el("div","auth-status");
    const submit=el("button","btn btn-primary","دخول"); submit.type="submit";
    append(form,brand,name.wrapper,pass.wrapper,note,status,submit);
    container.appendChild(form);
    try {
      const provisioning=parseData(await bridge().invoke("GetProvisioningState",{}));
      if (provisioning && provisioning.provisioned===false) {
        firstUse=true;
        const heading=el("h1","auth-setup-heading","التهيئة الأولى");
        container.replaceChildren(heading,form);
        note.classList.add("auth-info-box");
        note.setAttribute("role","note");
        note.textContent="اختر كلمة مرور تضم 3 أحرف إنجليزية و3 أرقام على الأقل (6 خانات كحد أدنى)، دون اشتراط رمز خاص، وأعد كتابتها للتأكيد. الاسم الكامل اختياري؛ سيظهر الحساب «الإدارة العامة - اسم المستخدم» ويُضاف الاسم إن أدخلته. لا توجد بيانات دخول افتراضية.";
        submit.textContent="إنشاء الإدارة العامة والدخول";
        pass.input.minLength=6;
        pass.input.autocomplete="new-password";
        confirmation.input.minLength=6;
        confirmation.input.autocomplete="new-password";
        foundingName.input.maxLength=200;
        form.classList.add("auth-first-run");
        const policy=el("div","auth-password-policy");
        policy.setAttribute("role","list");
        policy.setAttribute("aria-label","متطلبات كلمة المرور");
        const checks=[
          ["6 خانات فأكثر",v=>v.length>=6],
          ["3 أحرف إنجليزية",v=>(v.match(/[A-Za-z]/g)||[]).length>=3],
          ["3 أرقام",v=>(v.match(/[0-9]/g)||[]).length>=3],
          ["دون مسافات أو محارف تحكم",v=>v.length>0&&!/[\s\x00-\x1f\x7f-\x9f]/.test(v)],
          ["تطابق كلمتي المرور",v=>v.length>0&&v===confirmation.input.value]
        ];
        const chips=checks.map(([label])=>{
          const chip=el("div","auth-policy-chip");
          chip.setAttribute("role","listitem");
          const indicator=el("span","auth-policy-icon","○");
          indicator.setAttribute("aria-hidden","true");
          append(chip,indicator,el("span","",label));
          policy.appendChild(chip);
          return {chip,indicator};
        });
        const refreshPolicy=()=>{
          const value=pass.input.value;
          checks.forEach(([label,passes],i)=>{
            const valid=passes(value);
            chips[i].chip.classList.toggle("valid",valid);
            chips[i].indicator.textContent=valid?"✓":"○";
            chips[i].chip.setAttribute("aria-label",label+(valid?" — مستوفى":" — غير مستوفى"));
          });
        };
        pass.input.addEventListener("input",refreshPolicy);
        confirmation.input.addEventListener("input",refreshPolicy);
        refreshPolicy();
        const passwordArea=el("div","auth-password-area");
        append(passwordArea,pass.wrapper,policy);
        // Only the credential fields scroll; the primary action remains on screen.
        const setupFields=el("div","auth-setup-fields");
        append(setupFields,name.wrapper,foundingName.wrapper,passwordArea,
          confirmation.wrapper,note);
        const submitFooter=el("div","auth-submit-footer");
        append(submitFooter,status,submit);
        form.replaceChildren(brand,setupFields,submitFooter);
      }
    } catch(e) { showError(status,e); }
    form.addEventListener("submit",async event=>{
      event.preventDefault(); submit.disabled=true;
      status.replaceChildren(message("جارٍ التحقق من الحساب…"));
      try {
        const payload={username:name.input.value.trim(),password:pass.input.value};
        if(firstUse){
          if(pass.input.value!==confirmation.input.value)
            throw new Error("تأكيد كلمة المرور غير مطابق لكلمة المرور.");
          payload.setupIntent="firstTimeSetup";
          payload.passwordConfirmation=confirmation.input.value;
          const optionalFullName=foundingName.input.value.trim();
          if(optionalFullName) payload.fullName=optionalFullName;
        }
        const result=parseData(await bridge().invoke("AuthenticateLocalUser",payload));
        if (!result || !result.sessionNonce)
          throw new Error("لم يرجع خادم المصادقة جلسة صالحة.");
        bridge().setSessionNonce(result.sessionNonce);
        STATE.session=result;
        void window.AltamamTheme?.sync();
        window.AltamamIdleGuard?.start(true,logout);
        pass.input.value="";
        confirmation.input.value="";
        foundingName.input.value="";
        document.querySelector(".app-sidebar").hidden=false;
        document.getElementById("headerRoleBadge").textContent=
          result.username+" — "+(result.roleLabelAr||result.role);
        document.getElementById("logoutAction").hidden=false;
        const requested=decodeURIComponent(location.hash.slice(1));
        if(!openRoute(requested)){
          const automatic=requested==="auto-prepared";
          render("dashboard");
          if(automatic)openAutoPreparedList();
        }
        void checkOutputAfterLogin();
      } catch(e) { showError(status,e); }
      finally { submit.disabled=false; }
    });
  }

  async function loadSites() {
    const data=parseData(await bridge().invoke("QuerySites",{}));
    return Array.isArray(data)?data:[];
  }

  let dashboardClockTimer=null;
  let lastMorningRefreshAt=0;
  let configuredDueTime="08:00";
  function stopDashboardClock(){
    if(dashboardClockTimer!==null&&typeof window.clearInterval==="function")
      window.clearInterval(dashboardClockTimer);
    dashboardClockTimer=null;
  }
  function beginEightAmCountdown(host,businessDate){
    stopDashboardClock();
    const line=el("div","dashboard-eight-clock");
    line.setAttribute("role","timer");
    line.setAttribute("aria-label","الوقت المتبقي للاعتماد التلقائي حسب الإعدادات");
    host.appendChild(line);
    const tick=()=>{
      const now=new Date();
      if(startOfToday()!==businessDate){
        stopDashboardClock();
        if(STATE.session&&view().contains?.(line))void renderDashboard();
        return;
      }
      const [dueHour,dueMinute]=configuredDueTime.split(":").map(Number);
      const cutoff=new Date(now.getFullYear(),now.getMonth(),now.getDate(),dueHour,dueMinute,0,0);
      const remaining=Math.max(0,Math.ceil((cutoff.getTime()-now.getTime())/1000));
      // The native scheduler performs the write; refresh the read-only counters
      // as it completes, without initiating or repeating the write in JavaScript.
      if(remaining===0&&now.getTime()-cutoff.getTime()<600000&&
         now.getTime()-lastMorningRefreshAt>=30000){
        lastMorningRefreshAt=now.getTime();
        if(STATE.session&&view().contains?.(line))void renderDashboard();
      }
      const h=String(Math.floor(remaining/3600)).padStart(2,"0");
      const m=String(Math.floor(remaining%3600/60)).padStart(2,"0");
      const sec=String(remaining%60).padStart(2,"0");
      line.textContent=remaining>0
        ?"الوقت المتبقي للاعتماد التلقائي عند "+configuredDueTime+" : "+h+":"+m+":"+sec
        :"حان موعد الاعتماد التلقائي عند "+configuredDueTime+"؛ سيعرض الكشف الأفراد المعتمدين بعد التنفيذ.";
    };
    tick();
    // The native scheduler is authoritative; this is DISPLAY ONLY and never
    // renews an idle session or commits attendance from JavaScript.
    if(typeof window.setInterval==="function")
      dashboardClockTimer=window.setInterval(tick,1000);
  }

  // Keep this dialog mounted across dashboard invalidations and redraws.
  let autoPreparedOverlay=null,disposeAutoPrepared=null;
  function dismissAutoPrepared(){
    if(!autoPreparedOverlay)return;
    // A route change, logout or a new register invalidates pending previews
    // and human approvals before detaching the UI. Already submitted writes
    // are not cancelled; the authoritative C# transaction still decides.
    disposeAutoPrepared?.();
    const overlay=autoPreparedOverlay;
    if(overlay.remove)overlay.remove();
    else overlay.parentNode?.removeChild(overlay);
    autoPreparedOverlay=null;
    disposeAutoPrepared=null;
  }
  // Automatic register supports the same audited preview/commit pipeline as daily attendance.
  function openAutoPreparedList(){
    dismissAutoPrepared();
    const returnFocus=document.activeElement;
    const date=STATE.date,site=STATE.siteId;
    const overlay=el("div","auto-attendance-overlay");
    overlay.setAttribute("role","dialog");
    overlay.setAttribute("aria-modal","true");
    overlay.setAttribute("aria-label","المحضّرون تلقائياً");
    const panel=el("section","auto-attendance-dialog card");
    const heading=el("div","auto-attendance-heading");
    const title=el("h2","page-title","المحضّرون تلقائياً — "+date);
    const closeButton=button("إغلاق",close);
    append(heading,title,closeButton);
    const description=el("p","section-description",
      "يمكن اختيار فرد واحد أو مجموعة وتغيير حالتهم بعد معاينة الأسماء والحالات السابقة والجديدة ثم التأكيد. لا يحدث أي تعديل بمجرد التحديد أو فتح السجل.");
    const search=field("search","بحث بالاسم أو الرقم (ع)");
    search.input.maxLength=100;
    const searchButton=button("بحث",()=>{term=search.input.value.trim();page=1;void load();});
    const actions=el("div","functional-toolbar");
    const batchStatus=el("select","form-select auto-attendance-status-select");
    batchStatus.setAttribute("aria-label","الحالة الجديدة للمحددين");
    const batchPlaceholder=el("option","","الحالة الجديدة للمحددين");
    batchPlaceholder.value="";batchStatus.appendChild(batchPlaceholder);
    const selectPageButton=button("تحديد الصفحة",()=>selectVisiblePage());
    const clearButton=button("إلغاء التحديد",()=>clearSelections());
    const previewSelected=button("معاينة تغيير المحددين",()=>void previewSelectedChanges());
    previewSelected.disabled=true;
    const selectedInfo=el("span","auto-attendance-selection-info","المحددون: 0 من 100");
    append(actions,search.wrapper,searchButton,batchStatus,
      selectPageButton,clearButton,previewSelected,selectedInfo);
    const notice=el("div","auto-attendance-feedback");
    const content=el("div","auto-attendance-results");
    const footer=el("div","functional-pager");
    append(panel,heading,description,actions,notice,content,footer);
    overlay.appendChild(panel);
    // Keep the dialog in #appViewport for both WebView2 accessibility
    // and the same DOM ownership model used by the Windows UI smoke tests.
    view().appendChild(overlay);
    autoPreparedOverlay=overlay;
    closeButton.focus?.();
    let disposed=false,page=1,term="",generation=0,choices=[];
    let visiblePeople=[],busy=false,selectionVersion=0;
    const selected=new Map();
    disposeAutoPrepared=()=>{
      disposed=true;generation++;selectionVersion++;
      selected.clear();
    };
    batchStatus.addEventListener("change",()=>{
      selectionVersion++;
      notice.replaceChildren();
    });
    function updateSelected(){
      selectedInfo.textContent="المحددون: "+selected.size+" من 100";
      previewSelected.disabled=busy||selected.size===0;
      clearButton.disabled=busy||selected.size===0;
      closeButton.disabled=busy;
      selectPageButton.disabled=busy||!visiblePeople.length;
    }
    // A pending human selection is never discarded silently. Keep this
    // separate from the one-time server preview: refusing cancellation is read-only.
    async function confirmDiscard(){
      if(disposed||busy)return false;
      if(selected.size===0)return true;
      const expectedVersion=selectionVersion;
      const confirm=window.AltamamDialog?.confirm;
      if(typeof confirm!=="function"){
        showError(notice,new Error("نافذة تأكيد الإلغاء غير متاحة؛ احتُفظ بالتحديد."));
        return false;
      }
      const approved=await confirm("لديك "+selected.size+
        " فرداً محدداً دون اعتماد. هل تريد إلغاء الاختيار دون تغيير حالاتهم؟",
        {title:"تأكيد إلغاء التحديد"});
      return approved===true&&!disposed&&!busy&&expectedVersion===selectionVersion;
    }
    async function clearSelections(){
      if(!await confirmDiscard())return;
      selected.clear();selectionVersion++;notice.replaceChildren();
      updateSelected();void load();
    }
    function selectVisiblePage(){
      if(busy)return;
      const missing=visiblePeople.filter(p=>!selected.has(p.personId));
      if(selected.size+missing.length>100){
        showError(notice,new Error("الحد الأقصى 100 فرد لكل عملية؛ اختر عدداً أقل."));
        return;
      }
      for(const p of missing)selected.set(p.personId,p);
      selectionVersion++;notice.replaceChildren();updateSelected();void load();
    }
    async function previewSelectedChanges(){
      if(!batchStatus.value){
        showError(notice,new Error("اختر الحالة الجديدة للمحددين قبل المعاينة."));return;
      }
      await editAutoStatuses(Array.from(selected.values()),batchStatus.value,true);
    }
    async function editAutoStatuses(persons,statusId,bulk=false){
      if(busy||disposed)return;
      const chosen=choices.find(item=>item.statusId===statusId);
      if(!chosen){
        showError(notice,new Error("اختر الحالة الجديدة من القائمة المنسدلة."));return;
      }
      if(!persons.length||persons.length>100){
        showError(notice,new Error("حدد فرداً واحداً على الأقل، وبحد أقصى 100 فرد."));return;
      }
      const unchanged=persons.find(person=>person.statusId===statusId);
      if(unchanged){
        showError(notice,new Error("لا يمكن تغيير حالة "+unchanged.name+
          " ("+unchanged.serviceNumberA+"): الحالة الجديدة هي نفسها الحالة المحفوظة."));return;
      }
      const expectedVersion=selectionVersion;
      const ids=persons.map(person=>person.personId).sort();
      busy=true;updateSelected();notice.replaceChildren();
      try{
        // ONE status and ONE C# transaction for the entire selection: never
        // commit separate per-person changes with partial success.
        const response=await bridge().invoke("PreviewAttendanceChange",{
          businessDate:date,personIds:ids,statusId:chosen.statusId,
          siteId:site,mode:"revise"
        });
        const preview=parseData(response);
        if(disposed||!preview?.previewToken||!Array.isArray(preview.persons)
          ||preview.persons.length!==ids.length)
          throw Error("لم تصل معاينة موثّقة تشمل جميع الأفراد المحددين.");
        const verified=preview.persons.map(p=>p.personId).sort();
        if(verified.some((id,i)=>id!==ids[i]))
          throw Error("لا تتطابق المعاينة مع الأفراد المحددين؛ لم يُحفظ شيء.");
        const review=window.AltamamDialog?.attendanceReview;
        if(typeof review!=="function")
          throw Error("نافذة المعاينة غير متاحة؛ لم يُحفظ أي تعديل.");
        const accepted=await review(preview,{
          title:bulk?"معاينة تغيير حالات المحضّرين تلقائياً":
            "معاينة تغيير حالة المحضّر تلقائياً",
          message:"راجع أسماء جميع الأفراد وأرقامهم ومواقعهم والحالة السابقة والجديدة؛ الإلغاء لا يحفظ شيئاً.",
          confirmLabel:"تأكيد واعتماد التغييرات"
        });
        if(!accepted||disposed)return;
        if(expectedVersion!==selectionVersion){
          showError(notice,new Error("تغيّر التحديد أثناء المعاينة؛ راجع الأسماء والحالات من جديد."));
          return;
        }
        const saved=parseData(await bridge().invoke("ReviseTodayAttendance",{
          previewToken:preview.previewToken,
          operationId:window.crypto?.randomUUID?.()||
            "AUTO-REV-"+Date.now()+"-"+Math.random().toString(36).slice(2)
        }));
        if(!saved||saved.count!==ids.length)
          throw Error("لم يؤكد الخادم اعتماد جميع الأفراد المحددين.");
        for(const id of ids)selected.delete(id);
        selectionVersion++;
        notice.replaceChildren(message("تم اعتماد تغيير حالة "+saved.count+
          " فرداً، مع حفظ التعديل في سجل العمليات."));
        if(!disposed)await load(); // Keep register visible after confirmation.
      }catch(error){if(!disposed)showError(notice,error);}
      finally{busy=false;updateSelected();}
    }
    async function close(){
      if(!await confirmDiscard()||disposed)return;
      dismissAutoPrepared();
      // Dashboard refresh replaces its counters while the dialog stays open.
      // Do not return keyboard focus to an element no longer in the document.
      const focusTarget=returnFocus?.isConnected===false
        ? view()?.querySelector?.('[data-status-filter="__AUTO_PREPARED__"]')
        : returnFocus;
      focusTarget?.focus?.();
    }
    overlay.addEventListener("click",event=>{if(event.target===overlay)void close();});
    overlay.addEventListener("keydown",event=>{
      if(event.key==="Escape"){
        event.preventDefault?.();
        void close(); // pending selections still require explicit discard consent
        return;
      }
      if(event.key!=="Tab"||disposed)return;
      // Keep keyboard focus within the active register and exclude disabled
      // controls from wrap targets as selections and requests change.
      const controls=Array.from(overlay.querySelectorAll?.(
        'button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled])')||[])
        .filter(control=>!control.hidden&&control.getAttribute?.("aria-hidden")!=="true");
      if(!controls.length){event.preventDefault?.();return;}
      const first=controls[0],last=controls[controls.length-1];
      const current=document.activeElement;
      if(!controls.includes(current)){
        event.preventDefault?.();
        (event.shiftKey?last:first).focus?.();
      }else if(event.shiftKey&&current===first){
        event.preventDefault?.();last.focus?.();
      }else if(!event.shiftKey&&current===last){
        event.preventDefault?.();first.focus?.();
      }
    });
    search.input.addEventListener("keydown",event=>{
      if(event.key==="Enter"){event.preventDefault();term=search.input.value.trim();page=1;void load();}
    });
    async function load(){
      const current=++generation;
      content.replaceChildren(message("جارٍ قراءة سجل التحضير التلقائي..."));
      footer.replaceChildren();
      try{
        const result=parseData(await bridge().invoke("QueryDailyRoster",{
          businessDate:date,siteId:site,statusId:"__AUTO_PREPARED__",
          term,page,pageSize:DAILY_PAGE_SIZE
        }));
        if(disposed||current!==generation)return;
        if(!result||!Array.isArray(result.items))
          throw Error("تعذر قراءة قائمة المحضّرين تلقائياً.");
        visiblePeople=result.items;
        const tb=createTable([{label:"تحديد"},{label:"الرقم"},{label:"الرقم (ع)"},
          {label:"الاسم"},{label:"الموقع"},{label:"تاريخ التحضير"},
          {label:"مصدر التحضير"},{label:"الحالة المحفوظة"},
          {label:"الحالة الجديدة"},{label:"الإجراء"}]);
        content.replaceChildren(tb.wrap);
        result.items.forEach((person,i)=>{
          const row=el("tr");
          const checkCell=el("td"),checkbox=el("input");
          checkbox.type="checkbox";checkbox.checked=selected.has(person.personId);
          checkbox.setAttribute("aria-label","تحديد "+person.name);
          checkbox.addEventListener("change",()=>{
            if(busy){checkbox.checked=selected.has(person.personId);return;}
            if(checkbox.checked){
              if(selected.size>=100){
                checkbox.checked=false;
                showError(notice,new Error("الحد الأقصى 100 فرد لكل عملية."));
                return;
              }
              selected.set(person.personId,person);
            }else selected.delete(person.personId);
            selectionVersion++;notice.replaceChildren();updateSelected();
          });
          checkCell.appendChild(checkbox);row.appendChild(checkCell);
          [(page-1)*DAILY_PAGE_SIZE+i+1,person.serviceNumberA,
            person.name,person.siteName,person.savedBusinessDate||date,
            person.autoSource||"مصدر غير موثق",person.statusName].forEach(value=>
            row.appendChild(el("td","",value==null?"—":value)));
          const selectCell=el("td"),statusSelect=el("select","form-select auto-attendance-status-select");
          statusSelect.setAttribute("aria-label","الحالة الجديدة لـ "+person.name);
          const placeholder=el("option","","اختر حالة");placeholder.value="";
          statusSelect.appendChild(placeholder);
          for(const item of choices){
            if(item.statusId===person.statusId)continue;
            const opt=el("option","",item.displayName);opt.value=item.statusId;
            statusSelect.appendChild(opt);
          }
          selectCell.appendChild(statusSelect);row.appendChild(selectCell);
          const action=el("td");action.appendChild(button("معاينة التعديل",
            ()=>void editAutoStatuses([person],statusSelect.value)));
          row.appendChild(action);
          tb.body.appendChild(row);
        });
        if(!result.items.length)content.replaceChildren(message(
          "لا يوجد أفراد اعتُمدت حالاتهم تلقائياً لهذا اليوم وفق البحث والموقع."));
        const total=Number(result.total)||0;
        footer.appendChild(el("span","", "المحضّرون تلقائياً: "+total+" — صفحة "+page));
        const previous=button("السابق",()=>{page--;void load();});
        const next=button("التالي",()=>{page++;void load();});
        previous.disabled=page<=1;next.disabled=page*DAILY_PAGE_SIZE>=total;
        append(footer,previous,next);
        updateSelected();
      }catch(error){
        if(!disposed&&current===generation)showError(content,error);
      }
    }
    // Reading saved automatic attendance must work even when no new status
    // choices can be loaded; only the edit controls then become unavailable.
    void load();
    void bridge().invoke("GetActiveAttendanceStatuses",{}).then(reply=>{
      if(disposed)return;
      const data=parseData(reply);
      choices=Array.isArray(data)?data:(data?.items||[]);
      for(const item of choices){
        const option=el("option","",item.displayName);option.value=item.statusId;
        batchStatus.appendChild(option);
      }
      if(!choices.length)
        notice.replaceChildren(message("لا توجد حالات نشطة للتعديل؛ عرض السجل متاح."));
      void load();
    }).catch(error=>{
      if(!disposed)showError(notice,error);
    });
  }

  async function renderDashboard() {
    stopDashboardClock();
    STATE.date=startOfToday();
    const container=view();
    container.classList.toggle("daily-dashboard",true);
    container.replaceChildren(header("التمام اليومي"));
    // Refreshing the background counters must not silently discard an open
    // auto-prepared register or the user's pending multi-selection.
    if(autoPreparedOverlay)container.appendChild(autoPreparedOverlay);
    const controls=el("div","card functional-toolbar");
    const feedback=el("div","functional-feedback");
    const counters=el("div","functional-counters");
    const tableHost=el("section","card daily-roster-panel");
    append(container,controls,feedback,counters,tableHost);
    const epoch=Symbol("dashboard-request"); STATE.dashboardEpoch=epoch;
    try {
      const sites=await loadSites();
      if(STATE.dashboardEpoch!==epoch)return;
      const search=field("search","بحث بالاسم أو الرقم (ع)");
      search.input.value=STATE.dayTerm;
      search.input.autocomplete="off";
      const submitSearch=()=>{
        const next=search.input.value.trim();
        if(next!==STATE.dayTerm){STATE.dayTerm=next;STATE.dayPage=1;}
        void renderDashboard();
      };
      search.input.addEventListener("keydown",event=>{
        if(event.key==="Enter"){event.preventDefault();submitSearch();}
      });
      append(controls,siteSelector(sites,renderDashboard),search.wrapper,
        button("بحث",submitSearch),button("مسح البحث",()=>{
          STATE.dayTerm="";STATE.dayPage=1;void renderDashboard();
        }),button("تحديث البيانات",renderDashboard,"btn btn-primary"));
      const [countsResponse,rosterResponse]=await Promise.all([
        bridge().invoke("GetDashboardSummary",{businessDate:STATE.date,siteId:STATE.siteId}),
        bridge().invoke("QueryDailyRoster",{businessDate:STATE.date,siteId:STATE.siteId,
          statusId:STATE.statusId,term:STATE.dayTerm,page:STATE.dayPage,pageSize:DAILY_PAGE_SIZE})
      ]);
      if(STATE.dashboardEpoch!==epoch)return;
      const counts=parseData(countsResponse), rows=parseData(rosterResponse);
      feedback.replaceChildren(el("p","section-description","التحضير ليوم "+STATE.date+
        " — البيانات من قاعدة التطبيق المحلية."));
      beginEightAmCountdown(feedback,STATE.date);
      // Read the saved clock from the C# policy; this timer never writes.
      void bridge().invoke("GetAutoAttendancePolicy",{}).then(reply=>{
        const due=parseData(reply)?.dueTimeLocal;
        if(typeof due==="string"&&/^([01][0-9]|2[0-3]):[0-5][0-9]$/.test(due))
          configuredDueTime=due;
      }).catch(()=>{});
      // Order is operational: total force, present, then every configured status.
      // Do not show completion/unprepared counters in the primary dashboard.
      const statusCounts=Array.isArray(counts.statusCounts)?counts.statusCounts:[];
      const present=statusCounts.find(st=>/^(موجود|الموجود|حاضر|الحاضر)$/.test(
        String(st.name||"").trim()));
      const stats=[{name:"إجمالي القوة",count:counts.totalForce,statusId:""}];
      if(present)stats.push({name:"الموجود",count:present.count,
        statusId:present.statusId,color:present.color});
      stats.push(...statusCounts.filter(st=>st.statusId!==present?.statusId)
        .map(st=>({name:st.name,count:st.count,statusId:st.statusId,color:st.color})));
      // The automatic-attendance register is a workflow rather than a saved
      // attendance status and remains accessible after the configured statuses.
      stats.push({name:"المحضّرون تلقائياً",count:counts.automaticallyPrepared||0,
        statusId:"__AUTO_PREPARED__"});
      if(Number(counts.internalTransferDay)>0)
        stats.push({name:"نُقلوا اليوم",count:counts.internalTransferDay,
          statusId:"__TRANSFER_DAY__",color:"#D8B4FE"});
      for(const item of stats) {
        const isActive=STATE.statusId===item.statusId;
        const card=button("",()=>{
          if(item.statusId==="__AUTO_PREPARED__"){openAutoPreparedList();return;}
          if(STATE.statusId!==item.statusId){
            STATE.statusId=item.statusId;
            STATE.dayPage=1;
            renderDashboard();
          }
        },"counter-card dashboard-filter-counter"+(isActive?" active":""));
        card.dataset.statusFilter=item.statusId||"all";
        card.setAttribute("aria-label","عرض كشف "+item.name+" ("+item.count+")");
        card.setAttribute("aria-pressed",String(isActive));
        card.appendChild(el("span","counter-label",item.name));
        card.appendChild(el("span","counter-value",item.count));
        if(item.statusId && !["__AUTO_PREPARED__","__TRANSFER_DAY__"].includes(item.statusId)
          && Number(counts.totalForce)>0){
          const ratio=Math.round(100*Number(item.count||0)/Number(counts.totalForce));
          const percentage=el("span","counter-percentage",ratio+"%");
          percentage.setAttribute("aria-label","النسبة من إجمالي القوة");
          card.appendChild(percentage);
        }
        if(/^#[0-9a-fA-F]{6}$/.test(item.color||""))
          card.style.borderRight="4px solid "+item.color;
        counters.appendChild(card);
      }
      const tb=createTable([{label:"الرقم"},{label:"الرقم (ع)"},{label:"الاسم"},
        {label:"الموقع"},{label:"الحالة"}]);
      tableHost.replaceChildren(tb.wrap);
      (rows.items||[]).forEach((person,i)=>{
        const tr=el("tr");
        const data=[(STATE.dayPage-1)*DAILY_PAGE_SIZE+i+1, person.serviceNumberA,
          person.name,person.siteName,person.statusName];
        data.forEach((v,j)=> {
          const td=el("td","",j===4?"":v);
          if(j===4){
            const status=el("span","status-pill",v||"—");
            const color=String(person.statusColor||"");
            if(/^#[0-9a-fA-F]{6}$/.test(color)){
              const channels=[1,3,5].map(pos=>parseInt(color.slice(pos,pos+2),16));
              status.style.borderColor=color;
              status.style.backgroundColor="rgba("+channels.join(",")+",0.13)";
              status.style.setProperty?.("--status-dot",color);
            }
            td.appendChild(status);
          }
          tr.appendChild(td);
        });
        tb.body.appendChild(tr);
      });
      if(!(rows.items||[]).length) {
        const tr=el("tr"),td=el("td","","لا توجد بيانات مطابقة للفلاتر المحددة.");
        td.colSpan=5;tr.appendChild(td);tb.body.appendChild(tr);
      }
      pager(tableHost,STATE.dayPage,rows.total||0,DAILY_PAGE_SIZE,p=>{STATE.dayPage=p;renderDashboard();});
    } catch(e) { if(STATE.dashboardEpoch===epoch)showError(feedback,e); }
  }

  function monthLabel(year,month) { return ar.format(new Date(year,month-1,1)); }
  function monthDays(year,month) { return new Date(year,month,0).getDate(); }
  function weekdayLabel(y,m,d) { return weekday.format(new Date(y,m-1,d)); }

  async function renderMonthly() {
    const container=view();
    // Month roster owns the available desktop height, like the daily roster.
    // All calendar days and the fixed identity columns retain their order.
    container.classList.toggle("daily-dashboard",false);
    container.classList.toggle("monthly-dashboard",true);
    const now=new Date();
    const year=STATE.chosenYear||now.getFullYear();
    const month=STATE.chosenMonth||now.getMonth()+1;
    container.replaceChildren(header("التمام الشهري لشهر "+monthLabel(year,month)+" "+year));
    const tool=el("div","card functional-toolbar");
    const feedback=el("div","functional-feedback");
    const monthlyTotals=el("div","monthly-force-totals");
    monthlyTotals.setAttribute("role","status");
    monthlyTotals.setAttribute("aria-live","polite");
    const exportPanel=el("section","card functional-export-preview");
    exportPanel.hidden=true;
    exportPanel.setAttribute("role","dialog");
    exportPanel.setAttribute("aria-modal","true");
    exportPanel.setAttribute("aria-label","معاينة تصدير Excel الشهري");
    let exportPreviewSerial=0,exportReturnFocus=null,exportInProgress=false,activePreviewTrigger=null;
    const closeExportPreview=()=>{
      // Closing a view never cancels a submitted native export operation.
      if(exportInProgress)return;
      exportPreviewSerial++;
      exportPanel.replaceChildren();exportPanel.hidden=true;
      if(activePreviewTrigger)activePreviewTrigger.disabled=false;
      const opener=exportReturnFocus;exportReturnFocus=null;
      opener?.focus?.();
    };
    const dismissExport=button("إغلاق المعاينة",closeExportPreview,"btn btn-outline");
    exportPanel.addEventListener("keydown",event=>{
      if(exportPanel.hidden)return;
      if(event.key==="Escape"){
        event.preventDefault?.();closeExportPreview();return;
      }
      if(event.key!=="Tab")return;
      const controls=Array.from(exportPanel.querySelectorAll?.(
        'button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled])')||[])
        .filter(control=>!control.hidden&&control.getAttribute?.("aria-hidden")!=="true");
      if(!controls.length){event.preventDefault?.();return;}
      const first=controls[0],last=controls[controls.length-1],active=document.activeElement;
      if(!controls.includes(active)){
        event.preventDefault?.();(event.shiftKey?last:first).focus?.();
      }else if(event.shiftKey&&active===first){
        event.preventDefault?.();last.focus?.();
      }else if(!event.shiftKey&&active===last){
        event.preventDefault?.();first.focus?.();
      }
    });
    const tableHost=el("section","card monthly-grid-panel");
    const detailsBox=el("section","card monthly-cell-details auto-attendance-dialog");
    const detailOverlay=el("div","auto-attendance-overlay");
    detailOverlay.setAttribute("role","dialog");
    detailOverlay.setAttribute("aria-modal","true");
    detailOverlay.setAttribute("aria-label","تفاصيل التمام الشهري");
    detailOverlay.hidden=true;
    detailOverlay.appendChild(detailsBox);
    detailsBox.hidden=false;
    const bottom=el("form","card functional-archive monthly-archive-controls");
    append(container,tool,bottom,monthlyTotals,feedback,exportPanel,tableHost,detailOverlay);
    const epoch=Symbol("monthly-request");STATE.monthEpoch=epoch;
    let detailsRequest=0,detailsReturnFocus=null;
    const closeCellDetails=()=>{
      detailsRequest++;
      detailOverlay.hidden=true;
      detailsBox.replaceChildren();
      const previous=detailsReturnFocus;detailsReturnFocus=null;
      previous?.focus?.();
    };
    // History is read-only, but its dialog must be keyboard-dismissible
    // even while its native GetMonthlyCellDetails request is still pending.
    const detailsClose=button("إغلاق التفاصيل",closeCellDetails,
      "btn btn-outline app-modal-close");
    detailOverlay.addEventListener("keydown",event=>{
      if(detailOverlay.hidden)return;
      if(event.key==="Escape"){
        event.preventDefault?.();closeCellDetails();return;
      }
      if(event.key!=="Tab")return;
      const controls=Array.from(detailOverlay.querySelectorAll?.(
        'button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled])')||[])
        .filter(control=>!control.hidden&&control.getAttribute?.("aria-hidden")!=="true");
      if(!controls.length){event.preventDefault?.();return;}
      const first=controls[0],last=controls[controls.length-1];
      const active=document.activeElement;
      if(!controls.includes(active)){
        event.preventDefault?.();(event.shiftKey?last:first).focus?.();
      }else if(event.shiftKey&&active===first){
        event.preventDefault?.();last.focus?.();
      }else if(!event.shiftKey&&active===last){
        event.preventDefault?.();first.focus?.();
      }
    });
    const item=(label,value)=>{
      const row=el("div","monthly-detail-row");
      append(row,el("strong","",label+"："),el("span","",value==null||value===""?"—":value));
      return row;
    };
    async function showCellDetails(personId,date){
      const request=++detailsRequest;
      detailsReturnFocus=document.activeElement||null;
      detailOverlay.hidden=false;
      detailsBox.replaceChildren(detailsClose,message("جارٍ قراءة الواقعة المحفوظة..."));
      detailsClose.focus?.();
      try{
        const response=await bridge().invoke("GetMonthlyCellDetails",{
          personId,businessDate:date,siteId:STATE.siteId
        });
        if(STATE.monthEpoch!==epoch||request!==detailsRequest||detailOverlay.hidden)return;
        const data=parseData(response);
        if(!data||data.personId!==personId||data.businessDate!==date)
          throw new Error("لم تتطابق البيانات التاريخية مع اليوم المختار.");
        const top=el("div","monthly-detail-heading");
        append(top,el("h3","section-title","تفاصيل التمام — "+date),detailsClose);
        const content=el("div","monthly-detail-content app-preview-properties");
        append(content,item("الاسم",data.personName),item("الرقم (ع)",data.serviceNumberA),
          item("الموقع في هذا اليوم",data.attendance?.siteName||data.siteName));
        if(data.attendance){
          append(content,item("التحضير المعتمد",data.attendance.statusName),
            item("رمز التحضير",data.attendance.symbol),
            item("تاريخ ووقت الاعتماد UTC",data.attendance.committedAtUtc),
            item("منفّذ التحضير",data.attendance.actorUserId),
            item("عدد نسخ التحضير",data.attendance.revisionNumber));
        }else content.appendChild(item("التحضير","لا يوجد تحضير معتمد لهذا اليوم"));
        if(data.movement){
          const m=data.movement;
          const kind=m.type===4?"عودة للخدمة":m.type===3?"فصل":m.type===2?"نقل خارجي":"نقل داخلي";
          const banner=el("p","monthly-movement-banner",
            "واقعة إدارية مسجلة في هذا اليوم: "+kind);
          banner.setAttribute("role","status");
          content.appendChild(banner);
          append(content,item("الواقعة الإدارية",kind),
            item("من الموقع",m.sourceSiteName),
            item("إلى",m.destinationSiteName||m.destinationExternalName),
            item("سبب الفصل",m.separationReason),
            item("وقت الاعتماد UTC",m.committedAtUtc));
        }
        if(Array.isArray(data.revisions)&&data.revisions.length){
          const list=el("div","monthly-detail-revisions");
          list.appendChild(el("h4","section-title","تاريخ تعديل التحضير"));
          const history=createTable([
            {label:"الحالة السابقة"},{label:"الحالة الجديدة"},
            {label:"السبب"},{label:"المنفذ"},{label:"وقت التعديل UTC"}
          ]);
          history.wrap.classList.add("monthly-revision-table");
          for(const revision of data.revisions){
            const tr=el("tr");
            for(const value of [revision.beforeStatus,revision.afterStatus,
              revision.reason,revision.actorUserId,revision.executedAtUtc])
              tr.appendChild(el("td","",value==null?"—":value));
            history.body.appendChild(tr);
          }
          list.appendChild(history.wrap);
          content.appendChild(list);
        }
        detailsBox.replaceChildren(top,content);
      }catch(e){
        if(STATE.monthEpoch===epoch&&request===detailsRequest&&!detailOverlay.hidden)
          detailsBox.replaceChildren(detailsClose,
            message(e?.message||"تعذر قراءة تفاصيل اليوم المحفوظة.",true));
      }
    }
    const showMonth=async()=>{
      STATE.monthPage=1;await renderMonthly();
    };
    try {
      const [sites,periodReply]=await Promise.all([loadSites(),bridge().invoke("GetAvailableArchivePeriods",{})]);
      if(STATE.monthEpoch!==epoch)return;
      STATE.archivePeriods=parseData(periodReply)||[];
      tool.appendChild(siteSelector(sites,showMonth));
      const search=field("search","بحث بالاسم أو الرقم (ع) في الشهر");
      search.input.value=STATE.monthTerm;
      search.input.maxLength=100;
      search.input.autocomplete="off";
      const applySearch=()=>{
        const term=search.input.value.trim();
        if(term!==STATE.monthTerm){STATE.monthTerm=term;STATE.monthPage=1;}
        void renderMonthly();
      };
      search.input.addEventListener("keydown",event=>{
        if(event.key==="Enter"){event.preventDefault();applySearch();}
      });
      append(tool,search.wrapper,button("بحث في الكشف",applySearch),
        button("مسح بحث الشهر",()=>{
          STATE.monthTerm="";STATE.monthPage=1;void renderMonthly();
        }));
      // Navigate the real calendar, not an inferred count of prepared days.
      // Empty-but-enrolled months must remain accessible from this screen.
      const navigateMonth=offset=>{
        const target=new Date(year,month-1+offset,1);
        STATE.chosenYear=target.getFullYear();
        STATE.chosenMonth=target.getMonth()+1;
        STATE.monthPage=1;
        void renderMonthly();
      };
      const previousMonth=button("الشهر السابق",()=>navigateMonth(-1),"btn btn-outline monthly-period-nav");
      previousMonth.disabled=year===1900&&month===1;
      const followingMonth=button("الشهر التالي",()=>navigateMonth(1),"btn btn-outline monthly-period-nav");
      followingMonth.disabled=year>now.getFullYear()||
        (year===now.getFullYear()&&month>=now.getMonth()+1);
      append(tool,previousMonth,followingMonth);
      tool.appendChild(button("تحديث",renderMonthly,"btn btn-primary"));
      const previewExport=button("معاينة تصدير Excel",async()=>{
        const chosen={year,month,siteId:STATE.siteId,term:STATE.monthTerm};
        previewExport.disabled=true;
        activePreviewTrigger=previewExport;
        exportReturnFocus=document.activeElement||null;
        const request=++exportPreviewSerial;
        const activePreview=()=>STATE.monthEpoch===epoch&&request===exportPreviewSerial
          &&!exportPanel.hidden&&exportPanel.isConnected!==false;
        exportPanel.hidden=false;
        exportPanel.replaceChildren(dismissExport,
          message("جارٍ التحقق من سجلات الشهر قبل التصدير..."));
        dismissExport.focus?.();
        try{
          const preview=parseData(await bridge().invoke("PreviewMonthlyExport",chosen));
          if(!activePreview())return;
          if(!preview||!Number.isSafeInteger(preview.personCount)||preview.personCount<0)
            throw new Error("لم يرجع الخادم معاينة تصدير صالحة.");
          if(!preview.personCount){
            exportPanel.replaceChildren(message("لا توجد أسماء في هذا الشهر والموقع للتصدير."),
              dismissExport);
            return;
          }
          const properties=el("dl","app-preview-properties");
          [
            ["الشهر",year+"-"+String(month).padStart(2,"0")],
            ["النطاق",STATE.siteId?"الموقع المحدد":"جميع المواقع"],
            ["عدد الأفراد",String(preview.personCount)],
            ["عدد ملفات Excel",Number.isSafeInteger(preview.fileCount)&&preview.fileCount>0?
              String(preview.fileCount):"يُحدد عند إنشاء الملفات"]
          ].forEach(([label,value])=>{
            const property=el("div","app-preview-property");
            append(property,el("dt","",label),el("dd","",value));
            properties.appendChild(property);
          });
          const description=el("p","section-description",
            "سيتم حفظ "+preview.personCount+" فرد في ملف Excel لشهر "+
            year+"-"+String(month).padStart(2,"0")+
            (STATE.monthTerm?" وفق البحث «"+STATE.monthTerm+"»":"")+
            (STATE.siteId?" في ملف واحد.":". لجميع المواقع: ملف موحد وملف منفصل لكل موقع لديه أفراد في هذا الشهر.")+
            " يتم الحفظ في المجلد المختار من الإعدادات دون تغيير التحضير.");
          const fileLabel=el("label","section-description","اسم ملف Excel المقترح");
          const fileInput=el("input","form-control");
          fileInput.type="text";
          fileInput.value="التمام الشهري_"+year+"-"+String(month).padStart(2,"0")+".xlsx";
          fileInput.setAttribute("aria-label","اسم ملف التصدير الشهري");
          const destination=message("جارٍ قراءة مجلد الحفظ الحالي...");
          let directory="";
          const chooseFolder=button("اختيار مجلد الحفظ من Windows",async()=>{
            chooseFolder.disabled=true;
            try{
              const answer=parseData(await bridge().invoke("SetOutputDirectory",{choose:true}));
              if(!activePreview())return;
              if(!answer?.directory)throw new Error("لم يتم تحديد مجلد صالح.");
              directory=answer.directory;
              destination.textContent="مجلد الحفظ: "+directory+
                " — سيُستخدم أيضاً في عمليات النسخ والتصدير التالية.";
            }catch(error){
               if(activePreview())
                 destination.textContent=error?.message||"تعذر اختيار مجلد الحفظ.";
             }finally{chooseFolder.disabled=false;}
          },"btn btn-outline");
          try{
            const info=parseData(await bridge().invoke("GetSettingsOverview",{}));
            if(!activePreview())return;
            directory=info?.backupDirectory||"";
            destination.textContent=directory?"مجلد الحفظ الحالي: "+directory:
              "يجب اختيار مجلد حفظ محلي من Windows قبل التصدير.";
          }catch(error){
             if(!activePreview())return;
             destination.textContent="تعذر قراءة مجلد الحفظ؛ اختر مجلداً من Windows.";
           }
          const confirmSave=button("حفظ ملف Excel",async()=>{
             if(!activePreview()||exportInProgress)return;
            const fileName=fileInput.value.trim();
            if(!/^[\p{L}\p{N} _()\-]+\.xlsx$/iu.test(fileName) ||
               fileName.length>124 || fileName.length<6 || !directory){
              destination.textContent="حدد مجلداً محلياً واسم ملف آمن بصيغة xlsx.";
              return;
            }
            chosen.fileName=fileName;chosen.directory=directory;
            confirmSave.disabled=true;
             exportInProgress=true;
            const progressText=message("جارٍ إنشاء ملف Excel...");
            let monitoring=true, pollBusy=false;
            const stopExport=button("إيقاف التصدير الجاري",async()=>{
              stopExport.disabled=true;
              try{
                await bridge().invoke("CancelExport",{});
                progressText.textContent="طُلب إلغاء التصدير؛ جارٍ التأكد من توقف الكتابة...";
              }catch(error){showError(exportPanel,error);}
            });
            const progressBar=el("progress","app-export-progress");
            progressBar.max=100;progressBar.removeAttribute("value");
            progressBar.setAttribute("aria-label","تقدم إنشاء ملف Excel");
            exportPanel.replaceChildren(properties,description,progressText,progressBar,stopExport);
            const poll=(typeof setInterval === "function")?setInterval(async()=>{
              if(!monitoring||pollBusy||!activePreview())return;
              pollBusy=true;
              try{
                const info=parseData(await bridge().invoke("GetExportProgress",{},
                  {timeoutMs:10000}));
                if(!monitoring||!activePreview()||!info)return;
                const ratio=Number.isInteger(info.percentage)?
                  " — "+info.percentage+"%":"";
                if(Number.isInteger(info.percentage))
                  progressBar.value=Math.max(0,Math.min(100,info.percentage));
                const written=info.total?
                  " ("+info.processed+" من "+info.total+" صف)":"";
                progressText.textContent=info.cancelRequested?
                  "جارٍ إيقاف التصدير والتحقق من حذف الملف المؤقت...":
                  "جارٍ كتابة Excel"+ratio+written;
              }catch(error){
                if(error?.errorCode!=="MissingData"&&monitoring)
                  progressText.textContent="جارٍ تنفيذ التصدير والتحقق من حالته...";
              }finally{pollBusy=false;}
            },1250):null;
            try{
              const result=parseData(await bridge().invoke("ExportMonthlyWorkbookSet",chosen,
                {timeoutMs:600000}));
              if(!activePreview())return;
              if(!result||result.format!=="xlsx"||!result.filePath)
                throw new Error("لم يؤكد الخادم إنشاء ملف Excel.");
              const files=Array.isArray(result.files)?result.files:[];
              if(!STATE.siteId && (!files.length || files.length!==result.fileCount))
                throw new Error("لم يرجع الخادم قائمة ملفات جميع المواقع مكتملة.");
              const lines=files.length?
                "تم حفظ "+files.length+" ملفات Excel: "+files.map((f,i)=>
                  (i===0?"الكشف الموحد":("الموقع "+i))+" — "+f.filePath).join(" | "):
                "تم حفظ الكشف بنجاح: "+result.filePath;
              exportPanel.replaceChildren(message(lines),dismissExport);
            }catch(error){
              if(activePreview()){
                if(error?.errorCode==="StorageBusy")
                  exportPanel.replaceChildren(message("أُلغيت عملية التصدير قبل حفظ ملف Excel."),
                    dismissExport);
                else{
                  showError(exportPanel,error);
                  exportPanel.appendChild(dismissExport);
                }
              }
            }finally{
              monitoring=false;
               if(poll!==null&&typeof clearInterval==="function")clearInterval(poll);
               confirmSave.disabled=false;exportInProgress=false;
            }
          },"btn btn-primary");
          const cancel=button("إلغاء التصدير",closeExportPreview);
          if(!activePreview())return;
          exportPanel.replaceChildren(properties,description,fileLabel,fileInput,destination,
            chooseFolder,confirmSave,cancel);
          fileInput.focus?.();
        }catch(error){
          if(activePreview()){
            showError(exportPanel,error);
            exportPanel.appendChild(dismissExport);
          }
         }finally{
          if(request===exportPreviewSerial&&!exportInProgress)previewExport.disabled=false;
        }
      });
      tool.appendChild(previewExport);
      const yearPick=el("select","form-select"),monthPick=el("select","form-select");
      const noneYear=el("option","","السنة (الحالية عند الفراغ)");
      noneYear.value="";yearPick.appendChild(noneYear);
      const noneMonth=el("option","","الشهر (الحالي عند الفراغ)");
      noneMonth.value="";monthPick.appendChild(noneMonth);
      const years=new Set([now.getFullYear()]);
      for(let y=now.getFullYear();y>=Math.max(1900,now.getFullYear()-40);y--)years.add(y);
      for(const period of STATE.archivePeriods)years.add(Number(String(period).slice(0,4)));
      Array.from(years).sort((a,b)=>b-a).forEach(value=>{
        const option=el("option","",value);option.value=value;yearPick.appendChild(option);
      });
      for(let m=1;m<=12;m++){
        const option=el("option","",monthLabel(now.getFullYear(),m));
        option.value=m;monthPick.appendChild(option);
      }
      yearPick.value=STATE.chosenYear||"";monthPick.value=STATE.chosenMonth||"";
      const reveal=el("button","btn btn-primary","إظهار");reveal.type="submit";
      append(bottom,el("span","","أرشيف التمام الشهري"),monthPick,yearPick,reveal);
      bottom.addEventListener("submit",e=>{
        e.preventDefault();
        STATE.chosenYear=yearPick.value?Number(yearPick.value):null;
        STATE.chosenMonth=monthPick.value?Number(monthPick.value):null;
        STATE.monthPage=1;renderMonthly();
      });
      const period=year+"-"+String(month).padStart(2,"0");
      const current=now.getFullYear()+"-"+String(now.getMonth()+1).padStart(2,"0");
      if(period!==current && !STATE.archivePeriods.includes(period)){
        tableHost.replaceChildren(message("لا يوجد أرشيف لهذا الشهر"));
        return;
      }
      const isArchive=STATE.chosenYear!==null||STATE.chosenMonth!==null;
      const liveCurrent=period===current&&!isArchive;
      // Bound both data transfer and DOM nodes to the visible twelve-day window.
      // Row paging remains server-side; earlier/later dates stay reachable.
      const dayCount=monthDays(year,month),dayWindowSize=12;
      if(STATE.monthDayPeriod!==period){
        STATE.monthDayPeriod=period;
        // Keep today as the first day beside the frozen identity columns.
        // Previous days remain available through the independent day navigator.
        STATE.monthDayStart=period===current?now.getDate():1;
      }
      const visibleStart=Math.min(Math.max(1,STATE.monthDayStart),dayCount);
      const visibleEnd=Math.min(dayCount,visibleStart+dayWindowSize-1);
      const [gridReply,forceReply]=await Promise.all([
        bridge().invoke("QueryMonthlyGrid",{
          year,month,siteId:STATE.siteId,term:STATE.monthTerm,
          page:STATE.monthPage,pageSize:STATE.monthPageSize,archiveMode:isArchive,
          dayFrom:visibleStart,dayTo:visibleEnd
        }),
        liveCurrent
          ? bridge().invoke("GetDashboardSummary",{
              businessDate:startOfToday(),siteId:STATE.siteId
            }).catch(()=>null)
          : Promise.resolve(null)
      ]);
      if(STATE.monthEpoch!==epoch)return;
      const grid=parseData(gridReply);
      const force=parseData(forceReply);
      if(!grid)throw Error("تعذر قراءة كشف التمام الشهري.");
      if(liveCurrent){
        monthlyTotals.replaceChildren(
          el("strong","","إجمالي القوة النشطة: "+
            (typeof force?.totalForce==="number"?force.totalForce:"غير متاح")),
          el("span","","أفراد كشف الشهر الظاهرون: "+(grid.total||0)),
          el("small","","يبقى المنقول داخلياً ضمن القوة النشطة وكشف الشهر في موقعه الجديد، "+
            "ولا تظهر حالات النقل الخارجي والفصل إلا عند استعراض الأرشيف.")
        );
      }else{
        monthlyTotals.replaceChildren(
          el("strong","","إجمالي الأفراد في أرشيف الشهر: "+(grid.total||0)),
          el("span","","هذا سجل تاريخي للشهر المحدد، وليس عداد القوة النشطة اليوم.")
        );
      }
      const cols=[{label:"الرقم",cls:"pinned fixed-one"},
        {label:"الرقم (ع)",cls:"pinned fixed-two"},
        {label:"الاسم",cls:"pinned fixed-three"},
        {label:"الموقع",cls:"pinned fixed-four"}];
      const currentDay=period===current?now.getDate():null;
      // Always keep real calendar order. A highlighted current day is NOT sticky:
      // it scrolls naturally between earlier and later days.
      const days=Array.from({length:visibleEnd-visibleStart+1},(_,i)=>visibleStart+i);
      for(const day of days)cols.push({
        label:day+"\n"+weekdayLabel(year,month,day)+(day===currentDay?"\nاليوم":""),
        cls:day===currentDay?"day-column current-day-column":"day-column"
      });
      const tb=createTable(cols);
      tb.wrap.classList.add("monthly-scroll");
      tableHost.replaceChildren(tb.wrap);
      const dayNav=el("div","monthly-day-window-nav");
      dayNav.setAttribute("role","group");
      dayNav.setAttribute("aria-label","التنقل بين أيام الشهر");
      const earlierDays=button("الأيام السابقة",()=>{
        STATE.monthDayStart=Math.max(1,visibleStart-dayWindowSize);
        void renderMonthly();
      },"btn btn-outline");
      const laterDays=button("الأيام التالية",()=>{
        STATE.monthDayStart=Math.min(dayCount,visibleEnd+1);
        void renderMonthly();
      },"btn btn-outline");
      earlierDays.disabled=visibleStart===1;
      laterDays.disabled=visibleEnd===dayCount;
      append(dayNav,earlierDays,el("span","","الأيام "+visibleStart+"–"+visibleEnd+
        " من "+dayCount),laterDays);
      tableHost.appendChild(dayNav);
      // The first four identity/site columns are sticky at the RIGHT edge.
      // Initial position: today's date immediately LEFT of the last pinned
      // identity/site column, without reordering the chronological dates.
      const revealCurrentDay=()=>{
        if(STATE.monthEpoch!==epoch || !currentDay || !tb.wrap.isConnected)return;
        const header=tb.wrap.querySelector(".current-day-column");
        if(!header || typeof header.getBoundingClientRect!=="function")return;
        const viewport=tb.wrap.getBoundingClientRect();
        const target=header.getBoundingClientRect();
        const stickyColumns=tb.wrap.querySelectorAll("thead .pinned");
        const fixedWidth=Array.from(stickyColumns)
          .reduce((sum,col)=>sum+col.getBoundingClientRect().width,0);
        const available=Math.max(0,viewport.width-fixedWidth);
        if(available<=0)return;
        // RTL: pinned columns cover the RIGHT of the viewport.
        const desiredCenter=viewport.right-fixedWidth-target.width/2;
        const currentCenter=(target.left+target.right)/2;
        const delta=currentCenter-desiredCenter;
        if(Math.abs(delta)>1)tb.wrap.scrollBy({left:delta,behavior:"instant"});
      };
      if(currentDay && typeof window.requestAnimationFrame==="function")
        window.requestAnimationFrame(revealCurrentDay);
      for(const [index,person] of (grid.items||[]).entries()){
        const tr=el("tr");
        [(STATE.monthPage-1)*STATE.monthPageSize+index+1,person.serviceNumberA,person.name,person.siteName]
          .forEach((v,j)=>{
            const td=el("td","pinned fixed-"+["one","two","three","four"][j],v);
            td.title=String(v??"");
            tr.appendChild(td);
          });
        for(const day of days){
          const date=period+"-"+String(day).padStart(2,"0");
          const info=(person.days||{})[date];
          const cell=el("td",day===currentDay?
            "day-cell current-day-column":"day-cell");
          cell.title=info?(info.name+(info.movementNote?" — "+info.movementNote:"")):"لا يوجد تحضير مسجل";
          if(info){
            const movementMark=info.movementKind==="return"?" ↩":
              info.movementKind==="internal-transfer"?" ↔":
              info.movementKind==="external-transfer"?" ↗":
              info.movementKind==="separation"?" ⊗":"";
            const open=button((info.symbol||"تفاصيل")+movementMark,
              ()=>showCellDetails(person.personId,date),"monthly-day-detail");
            open.setAttribute("aria-label","عرض تفاصيل "+person.name+" في "+date);
            cell.appendChild(open);
          }
          if(info && /^#[0-9a-fA-F]{6}$/.test(info.color||"")){
            cell.style.backgroundColor=info.color;
            // Choose contrasting ink over colored status backgrounds.
            const rgb=[1,3,5].map(i=>parseInt(info.color.slice(i,i+2),16)/255)
              .map(v=>v<=0.04045?v/12.92:((v+0.055)/1.055)**2.4);
            const brightness=rgb[0]*0.2126+rgb[1]*0.7152+rgb[2]*0.0722;
            cell.style.color=brightness>0.179?"#000000":"#FFFFFF";
          }
          tr.appendChild(cell);
        }
        tb.body.appendChild(tr);
      }
      if(!(grid.items||[]).length)tableHost.replaceChildren(message(
        STATE.monthTerm?"لا يوجد فرد مطابق للبحث في هذا الشهر والموقع":
          period!==current?"لا يوجد أرشيف لهذا الشهر":"لا توجد سجلات لهذا الشهر"));
      else pager(tableHost,STATE.monthPage,grid.total||0,STATE.monthPageSize,
        p=>{STATE.monthPage=p;renderMonthly();},
        size=>{STATE.monthPageSize=size;STATE.monthPage=1;renderMonthly();});
    } catch(e) { if(STATE.monthEpoch===epoch)showError(feedback,e); }
  }

  function render(section, requestedRoute=null) {
    if(section!=="dashboard")dismissAutoPrepared();
    if(section!=="dashboard")stopDashboardClock();
    if (!STATE.session) { loginGate();return; }
    if (!sections.has(section)) section="dashboard";
    if(section!=="dashboard")view().classList.toggle("daily-dashboard",false);
    if(section!=="monthly")view().classList.toggle("monthly-dashboard",false);
    applyMainNav(section);
    history.replaceState(null,"","#"+section);
    if(section==="dashboard")renderDashboard();
    else if(section==="monthly")renderMonthly();
    else if (section==="admin" && window.AltamamAdminSettings)
      window.AltamamAdminSettings.renderAdmin(STATE.session);
    else if (section==="settings" && window.AltamamAdminSettings)
      window.AltamamAdminSettings.renderSettings(STATE.session, logout, requestedRoute);
    else nav.navigateTop(section);
  }

  // Navigate legacy named HTML routes into their real authenticated owning
  // screens. This never creates/commits a draft, transfer or other mutation.
  function openRoute(routeKey){
    if(!STATE.session)return false;
    const route=nav?.getRoute?.(routeKey);
    if(!route)return false;
    if(!canSeeRoute(route)){
      render("admin");
      view().appendChild(message("لا تملك صلاحية الدخول إلى الشاشة المطلوبة.",true));
      return true;
    }
    if(routeKey==="UnpreparedPersonnel"||routeKey==="SelectStatusAndConfirm"){
      render("dashboard");openAutoPreparedList();return true;
    }
    // Provisioning and authentication remain database-authoritative.
    if(routeKey==="Login"||routeKey==="FirstRunSetup"){
      render("dashboard");return true;
    }
    const section=routeKey==="ClosedDayExceptionalCorrection"
      ?"admin":nav.sectionForRoute(route);
    render(section,section==="settings"?routeKey:null);
    if(section==="admin" && routeKey!=="Administration" &&
       !window.AltamamAdminSettings?.openOwnedRoute?.(routeKey)){
      view().appendChild(message("لم تتوفر وجهة عملية معتمدة لهذا المسار.",true));
    }
    return true;
  }

  async function boot() {
    STATE.date=startOfToday();
    if (!bridge() || !bridge().hasBridge()) {
      view().replaceChildren(message("تعذر الاتصال بخدمات سطح المكتب الآمنة.",true));
      return;
    }
    try {
      if(window.AltamamIdleGuard?.shouldLogout()){
        try{await bridge().invoke("Logout",{});}catch{ /* already expired */ }
        bridge().setSessionNonce(null);
        return await loginGate();
      }
      const state=parseData(await bridge().invoke("GetCurrentSession",{}));
      if(state && state.sessionNonce) {
        STATE.session=state;
        void window.AltamamTheme?.sync();
        window.AltamamIdleGuard?.start(false,logout);
        document.querySelector(".app-sidebar").hidden=false;
        document.getElementById("headerRoleBadge").textContent=
          state.username+" — "+(state.roleLabelAr||state.role);
        document.getElementById("logoutAction").hidden=false;
        const requested=decodeURIComponent(location.hash.slice(1));
        if(!openRoute(requested)){
          const automatic=requested==="auto-prepared";
          const initialSection=sections.has(requested)?requested:"dashboard";
          render(initialSection);
          if(automatic)openAutoPreparedList();
          if(initialSection==="dashboard")void checkOutputAfterLogin();
        }
      } else await loginGate();
    } catch { await loginGate(); }
  }

  async function logout(){
    dismissAutoPrepared();
    try { await bridge().invoke("Logout",{}); }
    catch(e) { /* Do not continue using a possibly expired session. */ }
    finally {
      window.AltamamIdleGuard?.stop();
      bridge().setSessionNonce(null);
      STATE.session=null;
      window.AltamamTheme?.clearAuthorization();
      await loginGate();
    }
  }
  document.getElementById("logoutAction")?.addEventListener("click",logout);
  function canSeeRoute(route) {
    if(!STATE.session) return false;
    const role=STATE.session.role;
    // The authoritative C# action manifest grants both administrations
    // visible-overlay edits; the immutable source evidence is never edited.
    if (route.key==="EditAuditEntry" || route.key==="DeleteAuditEntry")
      return role==="GeneralAdministration"||role==="OrdinaryAdministration";
    if (route.key==="ModifiedDeletedAuditEntries" ||
        route.path.startsWith("screens/Administration/Users/") ||
        route.key==="Users" || route.key==="ArchivePersons")
      return role==="GeneralAdministration" || role==="OrdinaryAdministration";
    if(route.key==="ResetApplicationData")
      return role==="GeneralAdministration";
    // Only administrators may change the daily backup / 08:00 scheduler.
    if(route.key==="ScheduledBackups"||route.key==="EightPmScheduler")
      return role==="GeneralAdministration"||role==="OrdinaryAdministration";
    return true;
  }
  document.addEventListener("click",e=>{
    const control=e.target.closest("[data-action='navigateTop']");
    if(!control)return;
    e.stopImmediatePropagation();
    render(control.dataset.section);
  },true);
  document.addEventListener("DOMContentLoaded",boot);
  window.AltamamFunctional=Object.freeze({render, canSeeRoute, openRoute});
  bridge().onInvalidation(evt=>{
    const name=typeof evt==="string"?evt:(evt&&evt.eventName);
    if(["AttendanceCommitted","AttendanceRevised","PersonnelMoved","PersonnelArchived","SiteCreated",
      "SiteRenamed","StatusDefinitionChanged","BackupRestored"].includes(name)) {
      const section=location.hash.slice(1);
      // Administration refreshes its own nested screen after a write; keep its context.
     if(sections.has(section)&&section!=="admin"&&STATE.session)render(section);
    }
    if(name==="SessionEnded"){dismissAutoPrepared();window.AltamamIdleGuard?.stop();STATE.session=null;bridge().setSessionNonce(null);window.AltamamTheme?.clearAuthorization();loginGate();}
  });
})();
