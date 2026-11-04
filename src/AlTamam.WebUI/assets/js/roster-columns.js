"use strict";
/* One read-only PERSON-category column renderer. ColumnId is the immutable
 * saved key; captions are NEVER used to look up a person's custom value.
 * All output must be inserted through textContent, never HTML. */
(function () {
  function columns(data) {
    return Array.isArray(data?.customColumns) ?
      data.customColumns.filter(c=>c&&typeof c.columnId==="string"&&
        typeof c.caption==="string" && c.columnId.length>0) : [];
  }
  function value(person,column) {
    const field=person?.customFields?.[column.columnId];
    return field==null ? "" : String(field);
  }
  function values(person,visibleColumns) {
    return visibleColumns.map(column=>value(person,column));
  }
  function headings(visibleColumns) {
    return visibleColumns.map(column=>column.caption);
  }
  window.AltamamRosterColumns=Object.freeze({columns,value,values,headings});
})();
