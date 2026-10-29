"use strict";
/* Offline WebView2 user flow. C# remains the only authorization and data authority.
   No demo data, fake success, network calls, or editable month cells. */
(function () {
  const sections = new Set(["dashboard", "monthly", "admin", "settings"]);
  const STATE = { session: null, siteId: "", statusId: "", dayTerm: "", date: "", dayPage: 1,
    monthPage: 1, monthTerm: "", chosenYear: null, chosenMonth: null, archivePeriods: [] };
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
    return { wrapper, input: inp };
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
  function pager(root, page, total, pageSize, onPage) {
    const bar = el("div", "functional-pager");
    append(bar, el("span", "", "عدد الأفراد: " + total + " — صفحة " + page));
    const prev = button("السابق", ()=>onPage(page-1)); prev.disabled=page<=1;
    const next = button("التالي", ()=>onPage(page+1)); next.disabled=page*pageSize>=total;
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
    const container=view(); if(!container)return;
    container.classList.toggle("daily-dashboard",false);
    container.classList.toggle("monthly-dashboard",false);
    document.querySelector(".app-sidebar").hidden=true;
    document.getElementById("headerRoleBadge").textContent="لم يتم تسجيل الدخول";
    document.getElementById("logoutAction").hidden=true;
    container.replaceChildren(header("تسجيل الدخول إلى التمام اليومي"));
    const form=el("form","card auth-form");
    const name=field("text","اسم المستخدم",true);
    name.input.autocomplete="username";
    const pass=field("password","كلمة المرور",true);
    // Existing users may have historical credentials; only NEW founders must
    // satisfy the current strong-password policy during initialization.
    const confirmation=field("password","تأكيد كلمة المرور",true);
    const foundingName=field("text","الاسم الكامل لصاحب الحساب (اختياري)",false);
    let firstUse=false;
    const note=el("p","section-description","يعمل البرنامج على هذا الجهاز دون اتصال بالإنترنت.");
    const status=el("div","auth-status");
    const submit=el("button","btn btn-primary","دخول"); submit.type="submit";
    append(form,name.wrapper,pass.wrapper,note,status,submit);
    container.appendChild(form);
    try {
      const provisioning=parseData(await bridge().invoke("GetProvisioningState",{}));
      if (provisioning && provisioning.provisioned===false) {
        firstUse=true;
        container.replaceChildren(header("التهيئة الأولى"),form);
        note.textContent="أنشئ اسم المستخدم وكلمة مرور قوية مع تأكيدها. الاسم الكامل اختياري؛ سيظهر الحساب «الإدارة العامة - اسم المستخدم» ويُضاف الاسم إن أدخلته. لا توجد بيانات دخول افتراضية.";
        submit.textContent="إنشاء الإدارة العامة والدخول";
        pass.input.minLength=10;
        pass.input.autocomplete="new-password";
        confirmation.input.minLength=10;
        confirmation.input.autocomplete="new-password";
        foundingName.input.maxLength=200;
        form.replaceChildren(name.wrapper,foundingName.wrapper,pass.wrapper,
          confirmation.wrapper,note,status,submit);
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
        const openAutomaticRegister=location.hash.slice(1)==="auto-prepared";
        render("dashboard");
        if(openAutomaticRegister)openAutoPreparedList();
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
  let autoPreparedOverlay=null,autoPreparedRevisionOverlay=null;
  function dismissAutoPrepared(){
    if(autoPreparedRevisionOverlay){autoPreparedRevisionOverlay.remove?.();autoPreparedRevisionOverlay=null;}
    if(autoPreparedOverlay){autoPreparedOverlay.remove?.();autoPreparedOverlay=null;}
  }
  // Automatic register supports the same audited preview/commit pipeline as daily attendance.
  function openAutoPreparedList(){
    dismissAutoPrepared();
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
      "يمكن تصحيح حالة الفرد المعتمد تلقائياً من خلال معاينة واعتماد مسجلين في سجل العمليات. تخضع التعديلات لصلاحيات المستخدم ووقت قفل اليوم.");
    const search=field("search","بحث بالاسم أو الرقم (ع)");
    search.input.maxLength=100;
    const searchButton=button("بحث",()=>{term=search.input.value.trim();page=1;void load();});
    const actions=el("div","functional-toolbar");
    append(actions,search.wrapper,searchButton);
    const content=el("div","auto-attendance-results");
    const footer=el("div","functional-pager");
    append(panel,heading,description,actions,content,footer);
    overlay.appendChild(panel);
    (document.body?.appendChild?document.body:view()).appendChild(overlay);
    autoPreparedOverlay=overlay;
    let disposed=false,page=1,term="",generation=0,choices=[];
    function closeRevision(){
      const current=autoPreparedRevisionOverlay;
      autoPreparedRevisionOverlay=null;
      if(current)current.remove?.();
    }
    function openRevision(person){
      if(disposed)return;
      closeRevision();
      const overlay=el("div","auto-revision-overlay");
      overlay.setAttribute("role","dialog");
      overlay.setAttribute("aria-modal","true");
      overlay.setAttribute("aria-label","مراجعة حالة محضّر تلقائياً");
      const panel=el("section","auto-attendance-dialog card");
      const title=el("h2","page-title","مراجعة حالة الفرد — "+person.name);
      const details=el("p","section-description",
        "الرقم (ع): "+person.serviceNumberA+" — الموقع: "+person.siteName+
        " — التاريخ: "+(person.savedBusinessDate||date));
      const current=el("p","section-description","الحالة المحفوظة: "+person.statusName+
        " — المصدر: "+(person.autoSource||"مصدر غير موثق"));
      const selector=el("select","form-select auto-attendance-status-select");
      selector.setAttribute("aria-label","الحالة الجديدة لـ "+person.name);
      const empty=el("option","","اختر حالة جديدة");empty.value="";
      selector.appendChild(empty);
      for(const item of choices){
        if(item.statusId===person.statusId)continue;
        const option=el("option","",item.displayName);option.value=item.statusId;
        selector.appendChild(option);
      }
      const tools=el("div","functional-toolbar");
      const cancel=button("إلغاء المراجعة",closeRevision);
      const preview=button("معاينة تغيير الحالة",async()=>{
        preview.disabled=true;
        try{
          if(await editAutoStatus(person,selector.value))closeRevision();
        }finally{preview.disabled=false;}
      });
      append(tools,preview,cancel);
      append(panel,title,details,current,selector,tools);
      overlay.appendChild(panel);
      (document.body?.appendChild?document.body:view()).appendChild(overlay);
      autoPreparedRevisionOverlay=overlay;
      overlay.addEventListener("click",event=>{
        if(event.target===overlay)closeRevision();
      });
      overlay.addEventListener("keydown",event=>{
        if(event.key==="Escape"){event.preventDefault();closeRevision();}
      });
    }
    async function editAutoStatus(person,statusId){
      const chosen=choices.find(item=>item.statusId===statusId);
      if(!chosen){showError(content,new Error("اختر الحالة الجديدة من نافذة المراجعة."));return false;}
      if(chosen.statusId===person.statusId){
        showError(content,new Error("اختر حالة مختلفة عن الحالة المعتمدة حالياً."));return false;
      }
      try{
        const payload={businessDate:date,personIds:[person.personId],
          statusId:chosen.statusId,mode:"revise"};
        const response=await bridge().invoke("PreviewAttendanceChange",payload);
        const preview=parseData(response);
        if(disposed||!preview?.previewToken||!Array.isArray(preview.persons))
          throw Error("لم تصل معاينة موثّقة لتغيير الحالة.");
        const review=window.AltamamDialog?.attendanceReview;
        if(typeof review!=="function")throw Error("نافذة المعاينة غير متاحة؛ لم يُحفظ أي تعديل.");
        const accepted=await review(preview,{title:"معاينة تغيير حالة المحضّر تلقائياً",
          message:"راجع الاسم والرقم (ع) والحالة السابقة والجديدة قبل الاعتماد.",
          confirmLabel:"تأكيد تغيير الحالة"});
        if(!accepted||disposed)return false;
        const saved=parseData(await bridge().invoke("ReviseTodayAttendance",{
          previewToken:preview.previewToken,
          operationId:window.crypto?.randomUUID?.()||
            "AUTO-REV-"+Date.now()+"-"+Math.random().toString(36).slice(2)
        }));
        if(!saved||!Number.isInteger(saved.count)||saved.count<1)
          throw Error("لم يؤكد الخادم نجاح تعديل الحالة.");
        if(!disposed)await load(); // Keep the read-only register open.
        return true;
      }catch(error){if(!disposed)showError(content,error);return false;}
    }
    function close(){
      closeRevision();
      disposed=true;generation++;
      overlay.remove?.();
      if(!overlay.remove&&overlay.parentNode)overlay.parentNode.removeChild(overlay);
      if(autoPreparedOverlay===overlay)autoPreparedOverlay=null;
    }
    overlay.addEventListener("click",event=>{if(event.target===overlay)close();});
    search.input.addEventListener("keydown",event=>{
      if(event.key==="Enter"){event.preventDefault();term=search.input.value.trim();page=1;void load();}
      if(event.key==="Escape"){event.preventDefault();close();}
    });
    closeButton.addEventListener("keydown",event=>{if(event.key==="Escape")close();});
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
        const tb=createTable([{label:"الرقم"},{label:"الرقم (ع)"},
          {label:"الاسم"},{label:"الموقع"},{label:"تاريخ التحضير"},
          {label:"مصدر التحضير"},{label:"الحالة المحفوظة"},
          {label:"المراجعة المنفصلة"}]);
        content.replaceChildren(tb.wrap);
        result.items.forEach((person,i)=>{
          const row=el("tr");
          [(page-1)*DAILY_PAGE_SIZE+i+1,person.serviceNumberA,
            person.name,person.siteName,person.savedBusinessDate||date,
            person.autoSource||"مصدر غير موثق",person.statusName].forEach(value=>
            row.appendChild(el("td","",value==null?"—":value)));
          const action=el("td");
          action.appendChild(button("فتح مراجعة الحالة",()=>openRevision(person)));
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
      }catch(error){
        if(!disposed&&current===generation)showError(content,error);
      }
    }
    void bridge().invoke("GetActiveAttendanceStatuses",{}).then(reply=>{
      if(disposed)return;
      const data=parseData(reply);
      choices=Array.isArray(data)?data:(data?.items||[]);
      if(!choices.length)throw Error("لا توجد حالات تحضير فعّالة للاختيار.");
      void load();
    }).catch(error=>{if(!disposed)showError(content,error);});
  }

  async function renderDashboard() {
    stopDashboardClock();
    STATE.date=startOfToday();
    const container=view();
    container.classList.toggle("daily-dashboard",true);
    container.replaceChildren(header("التمام اليومي"));
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
          const td=el("td","",v);
          if(j===4 && /^#[0-9a-fA-F]{6}$/.test(person.statusColor||""))
            td.style.borderRight="4px solid "+person.statusColor;
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
    append(container,tool,monthlyTotals,feedback,exportPanel,tableHost,bottom,detailOverlay);
    const epoch=Symbol("monthly-request");STATE.monthEpoch=epoch;
    let detailsRequest=0;
    const item=(label,value)=>{
      const row=el("div","monthly-detail-row");
      append(row,el("strong","",label+"："),el("span","",value==null||value===""?"—":value));
      return row;
    };
    async function showCellDetails(personId,date){
      const request=++detailsRequest;
      detailOverlay.hidden=false;
      detailsBox.replaceChildren(message("جارٍ قراءة الواقعة المحفوظة..."));
      try{
        const response=await bridge().invoke("GetMonthlyCellDetails",{
          personId,businessDate:date,siteId:STATE.siteId
        });
        if(STATE.monthEpoch!==epoch||request!==detailsRequest)return;
        const data=parseData(response);
        if(!data||data.personId!==personId||data.businessDate!==date)
          throw new Error("لم تتطابق البيانات التاريخية مع اليوم المختار.");
        const top=el("div","monthly-detail-heading");
        const close=button("إغلاق التفاصيل",()=>{detailsRequest++;detailOverlay.hidden=true;
          detailsBox.replaceChildren();});
        append(top,el("h3","section-title","تفاصيل التمام — "+date),close);
        const content=el("div","monthly-detail-content");
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
          append(content,item("الواقعة الإدارية",kind),
            item("من الموقع",m.sourceSiteName),
            item("إلى",m.destinationSiteName||m.destinationExternalName),
            item("سبب الفصل",m.separationReason),
            item("وقت الاعتماد UTC",m.committedAtUtc));
        }
        if(Array.isArray(data.revisions)&&data.revisions.length){
          const list=el("div","monthly-detail-revisions");
          list.appendChild(el("h4","section-title","تاريخ تعديل التحضير"));
          for(const revision of data.revisions){
            const entry=el("div","monthly-detail-revision");
            append(entry,item("التغيير",revision.beforeStatus+" ← "+revision.afterStatus),
              item("السبب",revision.reason),item("المنفذ",revision.actorUserId),
              item("الوقت UTC",revision.executedAtUtc));
            list.appendChild(entry);
          }
          content.appendChild(list);
        }
        detailsBox.replaceChildren(top,content);
      }catch(e){
        if(STATE.monthEpoch===epoch&&request===detailsRequest)showError(detailsBox,e);
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
        exportPanel.hidden=false;
        exportPanel.replaceChildren(message("جارٍ التحقق من سجلات الشهر قبل التصدير..."));
        try{
          const preview=parseData(await bridge().invoke("PreviewMonthlyExport",chosen));
          if(STATE.monthEpoch!==epoch)return;
          if(!preview||!Number.isSafeInteger(preview.personCount)||preview.personCount<0)
            throw new Error("لم يرجع الخادم معاينة تصدير صالحة.");
          if(!preview.personCount){
            exportPanel.replaceChildren(message("لا توجد أسماء في هذا الشهر والموقع للتصدير."));
            return;
          }
          const description=el("p","section-description",
            "سيتم حفظ "+preview.personCount+" فرد في ملف Excel لشهر "+
            year+"-"+String(month).padStart(2,"0")+
            (STATE.monthTerm?" وفق البحث «"+STATE.monthTerm+"»":"")+
            ". يتم حفظ نفس كشف الموقع والبحث الظاهر في الصفحة في مجلد الإعدادات، دون تعديل التحضير.");
          const confirmSave=button("حفظ ملف Excel",async()=>{
            confirmSave.disabled=true;
            const progressText=message("جارٍ إنشاء ملف Excel...");
            let monitoring=true, pollBusy=false;
            const stopExport=button("إيقاف التصدير الجاري",async()=>{
              stopExport.disabled=true;
              try{
                await bridge().invoke("CancelExport",{});
                progressText.textContent="طُلب إلغاء التصدير؛ جارٍ التأكد من توقف الكتابة...";
              }catch(error){showError(exportPanel,error);}
            });
            exportPanel.replaceChildren(description,progressText,stopExport);
            const poll=(typeof setInterval === "function")?setInterval(async()=>{
              if(!monitoring||pollBusy||STATE.monthEpoch!==epoch)return;
              pollBusy=true;
              try{
                const info=parseData(await bridge().invoke("GetExportProgress",{},
                  {timeoutMs:10000}));
                if(!monitoring||STATE.monthEpoch!==epoch||!info)return;
                const ratio=Number.isInteger(info.percentage)?
                  " — "+info.percentage+"%":"";
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
              if(STATE.monthEpoch!==epoch)return;
              if(!result||result.format!=="xlsx"||!result.filePath)
                throw new Error("لم يؤكد الخادم إنشاء ملف Excel.");
              exportPanel.replaceChildren(message("تم حفظ الكشف بنجاح: "+result.filePath));
            }catch(error){
              if(STATE.monthEpoch===epoch){
                if(error?.errorCode==="StorageBusy")
                  exportPanel.replaceChildren(message("أُلغيت عملية التصدير قبل حفظ ملف Excel."));
                else showError(exportPanel,error);
              }
            }finally{
              monitoring=false;if(poll!==null && typeof clearInterval === "function")clearInterval(poll);confirmSave.disabled=false;
            }
          },"btn btn-primary");
          const cancel=button("إلغاء التصدير",()=>{
            exportPanel.replaceChildren();exportPanel.hidden=true;
          });
          exportPanel.replaceChildren(description,confirmSave,cancel);
        }catch(error){
          if(STATE.monthEpoch===epoch)showError(exportPanel,error);
        }finally{previewExport.disabled=false;}
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
      const [gridReply,forceReply]=await Promise.all([
        bridge().invoke("QueryMonthlyGrid",{
          year,month,siteId:STATE.siteId,term:STATE.monthTerm,
          page:STATE.monthPage,pageSize:100,archiveMode:isArchive
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
      const dayCount=monthDays(year,month);
      const cols=[{label:"الرقم",cls:"pinned fixed-one"},
        {label:"الرقم (ع)",cls:"pinned fixed-two"},
        {label:"الاسم",cls:"pinned fixed-three"},
        {label:"الموقع",cls:"pinned fixed-four"}];
      const currentDay=period===current?now.getDate():null;
      // Always keep real calendar order. A highlighted current day is NOT sticky:
      // it scrolls naturally between earlier and later days.
      const days=Array.from({length:dayCount},(_,i)=>i+1);
      for(const day of days)cols.push({
        label:day+"\n"+weekdayLabel(year,month,day)+(day===currentDay?"\nاليوم":""),
        cls:day===currentDay?"day-column current-day-column":"day-column"
      });
      const tb=createTable(cols);
      tb.wrap.classList.add("monthly-scroll");
      tableHost.replaceChildren(tb.wrap);
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
        [(STATE.monthPage-1)*100+index+1,person.serviceNumberA,person.name,person.siteName]
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
            cell.style.color=brightness>0.179?"#17212B":"#FFFFFF";
          }
          tr.appendChild(cell);
        }
        tb.body.appendChild(tr);
      }
      if(!(grid.items||[]).length)tableHost.replaceChildren(message(
        STATE.monthTerm?"لا يوجد فرد مطابق للبحث في هذا الشهر والموقع":
          period!==current?"لا يوجد أرشيف لهذا الشهر":"لا توجد سجلات لهذا الشهر"));
      else pager(tableHost,STATE.monthPage,grid.total||0,100,p=>{STATE.monthPage=p;renderMonthly();});
    } catch(e) { if(STATE.monthEpoch===epoch)showError(feedback,e); }
  }

  function render(section) {
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
      window.AltamamAdminSettings.renderSettings(STATE.session, logout);
    else nav.navigateTop(section);
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
        const openAutomaticRegister=location.hash.slice(1)==="auto-prepared";
        const initialSection=["dashboard","monthly","admin","settings"].includes(location.hash.slice(1))
          ?location.hash.slice(1):"dashboard";
        render(initialSection);
        if(openAutomaticRegister)openAutoPreparedList();
        // A restored monthly/report page is read-only; check the output-drive
        // on fresh dashboard login and whenever Settings is opened instead.
        if(initialSection==="dashboard")void checkOutputAfterLogin();
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
    if (route.path.startsWith("screens/Administration/Users/") ||
        route.key==="Users" ||
        route.key==="ArchivePersons" ||
        route.key==="DeleteAuditEntry" || route.key==="EditAuditEntry")
      return role==="GeneralAdministration" || role==="OrdinaryAdministration";
    if(route.key==="ResetApplicationData")
      return role==="GeneralAdministration";
    return true;
  }
  document.addEventListener("click",e=>{
    const control=e.target.closest("[data-action='navigateTop']");
    if(!control)return;
    e.stopImmediatePropagation();
    render(control.dataset.section);
  },true);
  document.addEventListener("DOMContentLoaded",boot);
  window.AltamamFunctional=Object.freeze({render, canSeeRoute});
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
