"use strict";
/* Offline WebView2 user flow. C# remains the only authorization and data authority.
   No demo data, fake success, network calls, or editable month cells. */
(function () {
  const sections = new Set(["dashboard", "monthly", "admin", "settings"]);
  const STATE = { session: null, siteId: "", statusId: "", date: "", dayPage: 1,
    monthPage: 1, chosenYear: null, chosenMonth: null, archivePeriods: [] };
  const ar = new Intl.DateTimeFormat("ar", { month: "long" });
  const weekday = new Intl.DateTimeFormat("ar", { weekday: "short" });
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
    inp.autocomplete = type === "password" ? "current-password" : "username";
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
    append(bar, prev, next); root.appendChild(bar);
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

  async function loginGate() {
    const container=view(); if(!container)return;
    document.querySelector(".app-sidebar").hidden=true;
    document.getElementById("headerRoleBadge").textContent="لم يتم تسجيل الدخول";
    document.getElementById("logoutAction").hidden=true;
    container.replaceChildren(header("تسجيل الدخول إلى التمام اليومي"));
    const form=el("form","card auth-form");
    const name=field("text","اسم المستخدم",true);
    const pass=field("password","كلمة المرور",true);
    pass.input.minLength=8;
    const note=el("p","section-description","يعمل البرنامج على هذا الجهاز دون اتصال بالإنترنت.");
    const status=el("div","auth-status");
    const submit=el("button","btn btn-primary","دخول"); submit.type="submit";
    append(form,name.wrapper,pass.wrapper,note,status,submit);
    container.appendChild(form);
    try {
      const provisioning=parseData(await bridge().invoke("GetProvisioningState",{}));
      if (provisioning && provisioning.provisioned===false) {
        note.textContent="التهيئة الأولى: سيُنشأ حساب الإدارة العامة بكلمة مرور لا تقل عن ثمانية أحرف. احتفظ بها في مكان آمن.";
        submit.textContent="إنشاء الإدارة العامة والدخول";
        pass.input.autocomplete="new-password";
      }
    } catch(e) { showError(status,e); }
    form.addEventListener("submit",async event=>{
      event.preventDefault(); submit.disabled=true;
      status.replaceChildren(message("جارٍ التحقق من الحساب…"));
      try {
        const result=parseData(await bridge().invoke("AuthenticateLocalUser",{
          username:name.input.value.trim(), password:pass.input.value
        }));
        if (!result || !result.sessionNonce)
          throw new Error("لم يرجع خادم المصادقة جلسة صالحة.");
        bridge().setSessionNonce(result.sessionNonce);
        STATE.session=result;
        pass.input.value="";
        document.querySelector(".app-sidebar").hidden=false;
        document.getElementById("headerRoleBadge").textContent=
          result.username+" — "+(result.roleLabelAr||result.role);
        document.getElementById("logoutAction").hidden=false;
        render("dashboard");
      } catch(e) { showError(status,e); }
      finally { submit.disabled=false; }
    });
  }

  async function loadSites() {
    const data=parseData(await bridge().invoke("QuerySites",{}));
    return Array.isArray(data)?data:[];
  }

  async function renderDashboard() {
    STATE.date=startOfToday();
    const container=view();
    container.replaceChildren(header("التمام اليومي"));
    const controls=el("div","card functional-toolbar");
    const feedback=el("div","functional-feedback");
    const counters=el("div","functional-counters");
    const chips=el("div","functional-status-filters");
    const tableHost=el("section","card");
    append(container,controls,feedback,counters,chips,tableHost);
    const epoch=Symbol("dashboard-request"); STATE.dashboardEpoch=epoch;
    try {
      const sites=await loadSites();
      if(STATE.dashboardEpoch!==epoch)return;
      append(controls,siteSelector(sites,renderDashboard),button("تحديث البيانات",renderDashboard,"btn btn-primary"));
      const [countsResponse,rosterResponse]=await Promise.all([
        bridge().invoke("GetDashboardSummary",{businessDate:STATE.date,siteId:STATE.siteId}),
        bridge().invoke("QueryDailyRoster",{businessDate:STATE.date,siteId:STATE.siteId,
          statusId:STATE.statusId,page:STATE.dayPage,pageSize:50})
      ]);
      if(STATE.dashboardEpoch!==epoch)return;
      const counts=parseData(countsResponse), rows=parseData(rosterResponse);
      feedback.replaceChildren(el("p","section-description","التحضير ليوم "+STATE.date+
        " — البيانات من قاعدة التطبيق المحلية."));
      const stats=[{name:"إجمالي القوة",count:counts.totalForce},
        {name:"غير المحضرين",count:counts.unprepared}].concat(
          (counts.statusCounts||[]).map(s=>({name:s.name,count:s.count,color:s.color})));
      for(const item of stats) {
        const card=el("div","counter-card");
        card.appendChild(el("div","counter-label",item.name));
        card.appendChild(el("div","counter-value",item.count));
        if(/^#[0-9a-fA-F]{6}$/.test(item.color||""))
          card.style.borderRight="4px solid "+item.color;
        counters.appendChild(card);
      }
      const all=button("الكل",()=>{STATE.statusId="";STATE.dayPage=1;renderDashboard();},
        "btn filter-chip"+(!STATE.statusId?" active":""));
      chips.appendChild(all);
      for(const st of counts.statusCounts||[]) {
        const b=button(st.name,()=>{STATE.statusId=st.statusId;STATE.dayPage=1;renderDashboard();},
          "btn filter-chip"+(st.statusId===STATE.statusId?" active":""));
        if(/^#[0-9a-fA-F]{6}$/.test(st.color||""))b.style.borderBottom="4px solid "+st.color;
        chips.appendChild(b);
      }
      const tb=createTable([{label:"الرقم"},{label:"الرقم (ع)"},{label:"الاسم"},
        {label:"الموقع"},{label:"الحالة"}]);
      tableHost.replaceChildren(tb.wrap);
      (rows.items||[]).forEach((person,i)=>{
        const tr=el("tr");
        const data=[(STATE.dayPage-1)*50+i+1, person.serviceNumberA,
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
      pager(tableHost,STATE.dayPage,rows.total||0,50,p=>{STATE.dayPage=p;renderDashboard();});
    } catch(e) { if(STATE.dashboardEpoch===epoch)showError(feedback,e); }
  }

  function monthLabel(year,month) { return ar.format(new Date(year,month-1,1)); }
  function monthDays(year,month) { return new Date(year,month,0).getDate(); }
  function weekdayLabel(y,m,d) { return weekday.format(new Date(y,m-1,d)); }

  async function renderMonthly() {
    const container=view();
    const now=new Date();
    const year=STATE.chosenYear||now.getFullYear();
    const month=STATE.chosenMonth||now.getMonth()+1;
    container.replaceChildren(header("التمام الشهري لشهر "+monthLabel(year,month)+" "+year));
    const tool=el("div","card functional-toolbar");
    const feedback=el("div","functional-feedback");
    const exportPanel=el("section","card functional-export-preview");
    exportPanel.hidden=true;
    const tableHost=el("section","card");
    const detailsBox=el("section","card monthly-cell-details");
    detailsBox.hidden=true;
    const bottom=el("form","card functional-archive");
    append(container,tool,feedback,exportPanel,detailsBox,tableHost,bottom);
    const epoch=Symbol("monthly-request");STATE.monthEpoch=epoch;
    let detailsRequest=0;
    const item=(label,value)=>{
      const row=el("div","monthly-detail-row");
      append(row,el("strong","",label+"："),el("span","",value==null||value===""?"—":value));
      return row;
    };
    async function showCellDetails(personId,date){
      const request=++detailsRequest;
      detailsBox.hidden=false;
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
        const close=button("إغلاق التفاصيل",()=>{detailsRequest++;detailsBox.hidden=true;
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
          const kind=m.type===3?"فصل":m.type===2?"نقل خارجي":"نقل داخلي";
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
      tool.appendChild(button("تحديث",renderMonthly,"btn btn-primary"));
      const previewExport=button("معاينة تصدير Excel",async()=>{
        const chosen={year,month,siteId:STATE.siteId};
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
            ". الحفظ في المستندات / AlTamam / Exports، دون تعديل سجلات التحضير.");
          const confirmSave=button("حفظ ملف Excel",async()=>{
            confirmSave.disabled=true;
            exportPanel.replaceChildren(description,message("جارٍ إنشاء ملف Excel..."));
            try{
              const result=parseData(await bridge().invoke("ExportMonthlyWorkbookSet",chosen));
              if(STATE.monthEpoch!==epoch)return;
              if(!result||result.format!=="xlsx"||!result.filePath)
                throw new Error("لم يؤكد الخادم إنشاء ملف Excel.");
              exportPanel.replaceChildren(message("تم حفظ الكشف بنجاح: "+result.filePath));
            }catch(error){
              if(STATE.monthEpoch===epoch)showError(exportPanel,error);
            }finally{confirmSave.disabled=false;}
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
      const grid=parseData(await bridge().invoke("QueryMonthlyGrid",{
        year,month,siteId:STATE.siteId,page:STATE.monthPage,pageSize:50
      }));
      if(STATE.monthEpoch!==epoch)return;
      const dayCount=monthDays(year,month);
      const cols=[{label:"الرقم",cls:"pinned fixed-one"},
        {label:"الرقم (ع)",cls:"pinned fixed-two"},
        {label:"الاسم",cls:"pinned fixed-three"},
        {label:"الموقع",cls:"pinned fixed-four"}];
      for(let day=1;day<=dayCount;day++)cols.push({label:day+"\n"+weekdayLabel(year,month,day)});
      const tb=createTable(cols);
      tb.wrap.classList.add("monthly-scroll");
      tableHost.replaceChildren(tb.wrap);
      for(const [index,person] of (grid.items||[]).entries()){
        const tr=el("tr");
        [(STATE.monthPage-1)*50+index+1,person.serviceNumberA,person.name,person.siteName]
          .forEach((v,j)=>tr.appendChild(el("td","pinned fixed-"+["one","two","three","four"][j],v)));
        for(let day=1;day<=dayCount;day++){
          const date=period+"-"+String(day).padStart(2,"0");
          const info=(person.days||{})[date];
          const cell=el("td","day-cell");
          cell.title=info?info.name:"لا يوجد تحضير مسجل";
          if(info){
            const open=button(info.symbol||"تفاصيل",()=>showCellDetails(person.personId,date),
              "monthly-day-detail");
            open.setAttribute("aria-label","عرض تفاصيل "+person.name+" في "+date);
            cell.appendChild(open);
          }
          if(info && /^#[0-9a-fA-F]{6}$/.test(info.color||""))
            cell.style.backgroundColor=info.color;
          tr.appendChild(cell);
        }
        tb.body.appendChild(tr);
      }
      if(!(grid.items||[]).length)tableHost.replaceChildren(message(
        period!==current?"لا يوجد أرشيف لهذا الشهر":"لا توجد سجلات لهذا الشهر"));
      else pager(tableHost,STATE.monthPage,grid.total||0,50,p=>{STATE.monthPage=p;renderMonthly();});
    } catch(e) { if(STATE.monthEpoch===epoch)showError(feedback,e); }
  }

  function render(section) {
    if (!STATE.session) { loginGate();return; }
    if (!sections.has(section)) section="dashboard";
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
      const state=parseData(await bridge().invoke("GetCurrentSession",{}));
      if(state && state.sessionNonce) {
        STATE.session=state;
        document.querySelector(".app-sidebar").hidden=false;
        document.getElementById("headerRoleBadge").textContent=
          state.username+" — "+(state.roleLabelAr||state.role);
        document.getElementById("logoutAction").hidden=false;
        render(["dashboard","monthly","admin","settings"].includes(location.hash.slice(1))
          ?location.hash.slice(1):"dashboard");
      } else await loginGate();
    } catch { await loginGate(); }
  }

  async function logout(){
    try { await bridge().invoke("Logout",{}); }
    catch(e) { /* Do not continue using a possibly expired session. */ }
    finally {
      bridge().setSessionNonce(null);
      STATE.session=null;
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
    if(name==="SessionEnded"){STATE.session=null;bridge().setSessionNonce(null);loginGate();}
  });
})();
