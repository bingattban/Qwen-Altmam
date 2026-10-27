"use strict";
/* Shared offline table usability. This is UI-only: backend authorizes all data,
   server-side table searches remain authoritative across paginated results. */
(function(){
  const visited=new WeakSet();
  const searchSelector='input[type="search"],input[placeholder*="بحث"]';
  const tableSelector="#appViewport table, #appModalBody table";
  const normalize=value=>String(value??"").toLocaleLowerCase()
    .replace(/[ًٌٍَُِّْـ]/g,"")
    .replace(/[٠-٩]/g,c=>String(c.charCodeAt(0)-0x0660))
    .replace(/[۰-۹]/g,c=>String(c.charCodeAt(0)-0x06f0))
    .replace(/\s+/g," ").trim();
  const isEmptyRow=row=>row.children.length===1 &&
    (Number(row.children[0].colSpan)||1)>1;
  function existingSearch(table){
    const screen=table.closest(".card,.attendance-review,.person-report-dialog,.person-report-page");
    if(screen){
      // A local search in another table is not a screen-level search.
      for(const input of screen.querySelectorAll("input")){
        if(input.closest(".table-local-search"))continue;
        if(input.type==="search" || (input.placeholder||"").includes("بحث") ||
           (input.closest("label")?.textContent||"").includes("بحث"))return true;
      }
    }
    // Dashboard and monthly searches live in a toolbar alongside their table cards.
    const viewport=table.closest("#appViewport");
    if(viewport && (
      viewport.classList.contains("daily-dashboard") ||
      table.closest(".monthly-scroll") ||
      table.closest(".person-report-page")
    ) && viewport.querySelector(searchSelector))return true;
    return false;
  }
  function addVisibleRowSearch(table,wrapper){
    const bar=document.createElement("label");
    bar.className="table-local-search";
    const prompt=document.createElement("span");
    const input=document.createElement("input");
    const count=document.createElement("span");
    input.type="search";input.maxLength=120;input.autocomplete="off";
    input.placeholder="بحث في الصفوف المعروضة";
    input.setAttribute("aria-label","بحث في صفوف الجدول المعروضة");
    prompt.textContent="بحث في الجدول";
    count.className="table-local-count";
    const page=wrapper.parentElement?.querySelector(".functional-pager");
    const label=page?"الصفحة الحالية":"الجدول";
    function filter(){
      const term=normalize(input.value);
      const rows=table.tBodies.length?[...table.tBodies[0].rows]:[];
      let visible=0,total=0;
      for(const row of rows){
        if(isEmptyRow(row))continue;
        total++;
        const match=!term||normalize(row.textContent).includes(term);
        row.hidden=!match;
        if(match)visible++;
      }
      count.textContent=visible+" من "+total+" — "+label;
    }
    input.addEventListener("input",filter);
    bar.append(prompt,input,count);
    wrapper.insertBefore(bar,table);
    wrapper.classList.add("has-local-table-search");
    filter();
  }
  function updateAvailableHeight(table){
    const parent=table.parentElement;
    if(!parent || !parent.classList.contains("table-expanded-scroll") ||
      typeof window.innerHeight!=="number" ||
      typeof parent.getBoundingClientRect!=="function")return;
    if(table.closest(".daily-dashboard"))return; // already consumes 100% available height
    const rect=parent.getBoundingClientRect();
    const footer=parent.parentElement?.querySelector(".functional-pager")?66:28;
    const usable=Math.max(230,Math.min(window.innerHeight*.86,window.innerHeight-rect.top-footer));
    parent.style.setProperty("--usable-table-height",Math.round(usable)+"px");
  }
  function enhance(table){
    if(!table||visited.has(table))return;
    const wrapper=table.parentElement;
    if(!wrapper)return;
    visited.add(table);
    table.classList.add("unified-app-table");
    wrapper.classList.add("table-expanded-scroll");
    const container=wrapper.parentElement;
    if(container?.querySelector(":scope > .functional-pager"))
      container.classList.add("table-expanded-paged");
    if(!existingSearch(table))addVisibleRowSearch(table,wrapper);
    updateAvailableHeight(table);
  }
  function scan(){
    for(const table of document.querySelectorAll(tableSelector)){
      enhance(table);
      if(visited.has(table))updateAvailableHeight(table);
    }
  }
  let scheduled=false;
  function schedule(){
    if(scheduled)return;
    scheduled=true;
    Promise.resolve().then(()=>{scheduled=false;scan();});
  }
  function initialize(){
    scan();
    if(typeof MutationObserver==="function"){
      const observer=new MutationObserver(schedule);
      for(const id of ["appViewport","appModalBody"]){
        const root=document.getElementById(id);
        if(root)observer.observe(root,{childList:true,subtree:true});
      }
    }
    window.addEventListener?.("resize",schedule);
  }
  window.AltamamTables=Object.freeze({enhance,scan,normalize});
  if(document.readyState==="loading")
    document.addEventListener("DOMContentLoaded",initialize,{once:true});
  else initialize();
})();
