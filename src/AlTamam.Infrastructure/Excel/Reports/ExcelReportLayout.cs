using System.Globalization;
using System.Xml;

namespace AlTamam.Infrastructure.Excel.Reports;

/// <summary>Shared presentation only. Column counts always come from the export schema,
/// including current user fields; this class never reads, filters or changes data.</summary>
public static class ExcelReportLayout
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static string Column(int number)
    {
        if(number < 1 || number > 16384)throw new InvalidDataException("عدد أعمدة Excel خارج الحد المسموح.");
        string label="";
        while(number>0){number--;label=(char)('A'+number%26)+label;number/=26;}
        return label;
    }
    private static void Element(XmlWriter w,string name,params (string Key,string Value)[] attributes)
    {
        w.WriteStartElement(name,Ns);
        foreach(var (key,value) in attributes)w.WriteAttributeString(key,value);
        w.WriteEndElement();
    }
    public static void Styles(XmlWriter w)
    {
        w.WriteStartElement("styleSheet",Ns);
        w.WriteStartElement("numFmts",Ns);w.WriteAttributeString("count","2");
        Element(w,"numFmt",("numFmtId","164"),("formatCode","yyyy-mm-dd"));
        Element(w,"numFmt",("numFmtId","165"),("formatCode","yyyy-mm-dd hh:mm"));w.WriteEndElement();
        w.WriteStartElement("fonts",Ns);w.WriteAttributeString("count","2");
        for(int i=0;i<2;i++)
        {
            w.WriteStartElement("font",Ns);
            Element(w,"sz",("val",i==0?"12":"13"));Element(w,"name",("val","Arial"));
            Element(w,"color",("rgb",i==0?"FF172B4D":"FFFFFFFF"));
            if(i==1)Element(w,"b");
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteStartElement("fills",Ns);w.WriteAttributeString("count","4");
        foreach(string color in new[]{"none","gray125","FF17365D","FFF0F6FA"})
        {
            w.WriteStartElement("fill",Ns);w.WriteStartElement("patternFill",Ns);
            w.WriteAttributeString("patternType",color.StartsWith("FF",StringComparison.Ordinal)?"solid":color);
            if(color.StartsWith("FF",StringComparison.Ordinal))
            {Element(w,"fgColor",("rgb",color));Element(w,"bgColor",("indexed","64"));}
            w.WriteEndElement();w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteStartElement("borders",Ns);w.WriteAttributeString("count","2");
        Element(w,"border");w.WriteStartElement("border",Ns);
        foreach(string side in new[]{"left","right","top","bottom"})
        {w.WriteStartElement(side,Ns);w.WriteAttributeString("style","thin");Element(w,"color",("rgb","FFD5E0EA"));w.WriteEndElement();}
        Element(w,"diagonal");w.WriteEndElement();w.WriteEndElement();
        w.WriteStartElement("cellStyleXfs",Ns);w.WriteAttributeString("count","1");
        Element(w,"xf",("numFmtId","0"),("fontId","0"),("fillId","0"),("borderId","0"));w.WriteEndElement();
        w.WriteStartElement("cellXfs",Ns);w.WriteAttributeString("count","8");
        // Existing numeric style indexes 1/2 are kept for date serialization.
        for(int i=0;i<8;i++)
        {
            bool header=i is 3 or 7;
            string format=i is 1 or 5?"164":i is 2 or 6?"165":"0";
            w.WriteStartElement("xf",Ns);w.WriteAttributeString("numFmtId",format);
            w.WriteAttributeString("fontId",header?"1":"0");
            w.WriteAttributeString("fillId",header?"2":i is >=4 and <=6?"3":"0");
            w.WriteAttributeString("borderId","1");w.WriteAttributeString("xfId","0");
            w.WriteAttributeString("applyAlignment","1");w.WriteAttributeString("applyFont","1");
            w.WriteAttributeString("applyFill","1");w.WriteAttributeString("applyBorder","1");
            if(format!="0")w.WriteAttributeString("applyNumberFormat","1");
            Element(w,"alignment",("horizontal",header?"center":"right"),("vertical","center"),("wrapText","1"),("readingOrder","2"));
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteStartElement("cellStyles",Ns);w.WriteAttributeString("count","1");
        Element(w,"cellStyle",("name","Normal"),("xfId","0"),("builtinId","0"));w.WriteEndElement();w.WriteEndElement();
    }
    public static string Style(int row,int headerRow,string? numericStyle=null)
    {
        if(row<headerRow)return "7";
        if(row==headerRow)return "3";
        int baseStyle=numericStyle=="1"?1:numericStyle=="2"?2:0;
        return (baseStyle+((row-headerRow)%2==0?4:0)).ToString(CultureInfo.InvariantCulture);
    }
    public static void Columns(XmlWriter w,IReadOnlyList<string> headings)
    {
        Column(headings.Count); // Reject Excel overflow before writing a partial schema.
        w.WriteStartElement("cols",Ns);
        for(int i=0;i<headings.Count;i++)
        {
            string h=headings[i];
            bool day=int.TryParse(h,out int d)&&d is >=1 and <=31;
            int width=day?12:h is "م" or "الرقم" or "رقم الصف"?9:
                h.Contains("الاسم",StringComparison.Ordinal)?32:
                h.Contains("سبب",StringComparison.Ordinal)||h.Contains("وصف",StringComparison.Ordinal)||h.Contains("تفاصيل",StringComparison.Ordinal)?45:
                Math.Clamp(h.Length+8,20,36);
            Element(w,"col",("min",(i+1).ToString(CultureInfo.InvariantCulture)),("max",(i+1).ToString(CultureInfo.InvariantCulture)),
                ("width",width.ToString(CultureInfo.InvariantCulture)),("customWidth","1"));
        }
        w.WriteEndElement();
    }
    public static void SheetProperties(XmlWriter w)
    {
        w.WriteStartElement("sheetPr",Ns);Element(w,"pageSetUpPr",("fitToPage","1"));w.WriteEndElement();
    }
    public static void FinishSheet(XmlWriter w,int columns,int lastRow,int headerRow)
    {
        Element(w,"autoFilter",("ref",$"A{headerRow}:{Column(columns)}{Math.Max(lastRow,headerRow)}"));
        if(headerRow>1)
        {
            w.WriteStartElement("mergeCells",Ns);w.WriteAttributeString("count","1");
            Element(w,"mergeCell",("ref",$"A1:{Column(columns)}1"));w.WriteEndElement();
        }
        Element(w,"printOptions",("horizontalCentered","1"));
        Element(w,"pageMargins",("left","0.25"),("right","0.25"),("top","0.4"),("bottom","0.4"),("header","0.2"),("footer","0.2"));
        // Wide monthly grids use multiple pages rather than unreadably shrinking every column.
        Element(w,"pageSetup",("paperSize",columns>18?"8":"9"),("orientation","landscape"),
            ("fitToWidth",Math.Max(1,(columns+17)/18).ToString(CultureInfo.InvariantCulture)),("fitToHeight","0"));
        w.WriteStartElement("headerFooter",Ns);
        w.WriteElementString("oddFooter",Ns,"&Cصفحة &P من &N");w.WriteEndElement();
    }
    public static void PrintTitles(XmlWriter w,IReadOnlyList<(string Name,int HeaderRow)> sheets)
    {
        w.WriteStartElement("definedNames",Ns);
        for(int i=0;i<sheets.Count;i++)
        {
            w.WriteStartElement("definedName",Ns);w.WriteAttributeString("name","_xlnm.Print_Titles");
            w.WriteAttributeString("localSheetId",i.ToString(CultureInfo.InvariantCulture));
            w.WriteString("'"+sheets[i].Name.Replace("'","''",StringComparison.Ordinal)+"'!$1:$"+sheets[i].HeaderRow.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }
}
