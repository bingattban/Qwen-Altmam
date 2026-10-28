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
    // A search belongs to this table only when it is attached to the same
    // table card or the page-level toolbar of a single-table screen.
    // Never let a search in a sibling card hide another table's search.
    const screen=table.closest(".card,.attendance-review,.person-report-dialog,.person-report-page");
    if(screen){
      for(const input of screen.querySelectorAll("input")){
        if(input.closest(".table-local-search"))continue;
        if(input.type==="search" || (input.placeholder||"").includes("بحث") ||
           (input.closest("label")?.textContent||"").includes("بحث"))return true;
      }
    }
    const viewport=table.closest("#appViewport");
    if(viewport && (
      viewport.classList.contains("daily-dashboard") ||
      viewport.classList.contains("monthly-dashboard") ||
      table.closest(".monthly-scroll")
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
    input.classList.add("app-unified-search");
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
    enhanceInputs();
    for(const table of document.querySelectorAll(tableSelector)){
      enhance(table);
      if(visited.has(table))updateAvailableHeight(table);
    }
  }

  // Shared person-name completion for dynamically created search fields.
  // Suggestions are hints only; existing per-screen server-side search remains authoritative.
  const enhancedInputs=new WeakSet();
  let suggestionSerial=0;
  function containingLabel(input){
    const closest=input.closest?.("label");
    if(closest)return closest;
    const parent=input.parentElement;
    return parent?.tagName?.toLowerCase?.()==="label"||
      parent?.tag==="label"?parent:null;
  }
  function personSearchInput(input){
    if(input.closest?.(".table-local-search"))return false;
    const description=[input.placeholder||"",input.getAttribute?.("aria-label")||"",
      containingLabel(input)?.textContent||""].join(" ");
    return /بحث/.test(description) &&
      /(اسم|فرد|الرقم|كشف القوة)/.test(description);
  }
  function attachNameSuggestions(input){
    const parent=input.parentElement;
    if(!parent||!window.AltamamBridge?.invoke)return;
    const suggestions=document.createElement("datalist");
    suggestions.id="person-suggestions-"+(++suggestionSerial);
    input.setAttribute("list",suggestions.id);
    input.classList.add("app-smart-search");
    input.autocomplete="off";
    parent.appendChild(suggestions);
    let request=0,delay=null;
    input.addEventListener("input",()=>{
      const term=input.value.trim().slice(0,100),seq=++request;
      if(delay!==null&&window.clearTimeout)window.clearTimeout(delay);
      suggestions.replaceChildren();
      if(term.length<2)return;
      const run=async()=>{
        try{
          const context=input.closest?.(".card,.person-report-page")?.textContent||"";
          const includeArchived=/أرشيف|التقرير/.test(context);
          const response=await window.AltamamBridge.invoke("QueryPersons",{
            term,page:1,pageSize:16,includeArchived
          });
          if(seq!==request||!input.isConnected||input.value.trim()!==term)return;
          const seen=new Set();
          for(const person of response?.data?.items||[]){
            const name=String(person.displayName||person.name||"").trim();
            const number=String(person.serviceNumberA||"").trim();
            for(const value of [name,number]){
              if(!value||seen.has(value))continue;
              seen.add(value);
              const option=document.createElement("option");
              option.value=value;
              option.label=name&&number?name+" — ع: "+number:value;
              suggestions.appendChild(option);
            }
          }
        }catch{
          // QueryPersons permissions and connectivity never block normal typing.
          if(seq===request)suggestions.replaceChildren();
        }
      };
      delay=typeof window.setTimeout==="function"?window.setTimeout(run,180):null;
      if(delay===null)void run();
    });
  }
  // One visual contract for EVERY search box, including inputs created after
  // opening an admin sub-screen or modal. Never modify the search parameters.
  const personSearchPlaceholder="بحث بالاسم أو الرقم (ع)";
  function normalizeSearchField(input,isPerson){
    const label=containingLabel(input);
    const caption=label?.children?.[0];
    const captionText=caption?.textContent?.trim?.()||"";
    const existingHint=(input.placeholder||"").trim();
    const genericSearch=input.type==="search"||
      /بحث/.test([existingHint,input.getAttribute?.("aria-label")||"",captionText].join(" "));
    if(!genericSearch||!["text","search"].includes(input.type))return;
    input.classList.add("app-unified-search");
    if(isPerson)input.placeholder=personSearchPlaceholder;
    else if(!existingHint)input.placeholder=captionText||"بحث";
    if(label && label.classList && !label.classList.contains("table-local-search")){
      label.classList.add("app-unified-search-wrap");
      // Keep the accessible label, but put its visible prompt INSIDE the field.
      if(caption?.tagName?.toLowerCase?.()==="span"||caption?.tag==="span"){
        if(/بحث/.test(captionText))caption.classList.add("app-unified-search-caption");
      }
    }
  }
  function enhanceInputs(){
    for(const input of document.querySelectorAll("input")){
      if(enhancedInputs.has(input))continue;
      enhancedInputs.add(input);
      if(input.disabled||input.readOnly||input.type==="password"||
         input.type==="hidden"||input.type==="file")continue;
      const isPerson=personSearchInput(input);
      normalizeSearchField(input,isPerson);
      if(isPerson)attachNameSuggestions(input);
      else if(["text","search","email"].includes(input.type)&&
        !input.autocomplete)
        input.autocomplete="on";
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
      if(document.body)observer.observe(document.body,{childList:true,subtree:true});
      else for(const id of ["appViewport","appModalBody"]){
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
