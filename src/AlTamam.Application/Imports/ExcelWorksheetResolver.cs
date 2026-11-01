using System.IO.Compression;
using System.Xml.Linq;

namespace AlTamam.Application.Imports;

/// <summary>
/// Resolve real workbook ordering through OpenXML relationship IDs, not guessed
/// sheetN.xml filenames. Synthetic legacy fixtures without a relationship part
/// still use the old contiguous fallback to preserve their test contracts.
/// </summary>
internal static class ExcelWorksheetResolver
{
    internal sealed record Worksheet(string Name,ZipArchiveEntry Entry);
    private static readonly XNamespace Spreadsheet=
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace DocumentRelationships=
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships=
        "http://schemas.openxmlformats.org/package/2006/relationships";

    internal static List<Worksheet> Resolve(ZipArchive zip,Func<ZipArchiveEntry,XDocument> parse)
    {
        var workbook=zip.GetEntry("xl/workbook.xml") ??
            throw new InvalidDataException("تعذر قراءة تعريف أوراق Excel.");
        var definitions=parse(workbook).Descendants(Spreadsheet+"sheet").ToArray();
        var relationships=zip.GetEntry("xl/_rels/workbook.xml.rels");
        if(relationships is not null && definitions.Length>0)
        {
            if(definitions.Length>50)
                throw new InvalidDataException("عدد أوراق Excel يزيد على الحد الآمن.");
            var relationMap=parse(relationships).Descendants(PackageRelationships+"Relationship")
                .Where(x=>((string?)x.Attribute("Type")??"").EndsWith("/worksheet",StringComparison.Ordinal)
                    && (string?)x.Attribute("TargetMode")!="External")
                .ToDictionary(x=>(string?)x.Attribute("Id")??"",
                    x=>(string?)x.Attribute("Target")??"",StringComparer.Ordinal);
            var found=new List<Worksheet>();
            foreach(var sheet in definitions)
            {
                string id=(string?)sheet.Attribute(DocumentRelationships+"id")??"";
                if(!relationMap.TryGetValue(id,out string? target) || string.IsNullOrEmpty(target))
                    throw new InvalidDataException("تعريف ورقة Excel غير متطابق مع علاقات الملف.");
                string entryPath=target.StartsWith("/",StringComparison.Ordinal)?
                    target.TrimStart('/'): "xl/"+target;
                if(entryPath.Contains("..",StringComparison.Ordinal) ||
                   entryPath.Contains('\\') || entryPath.Contains(':') ||
                   entryPath.Contains('?',StringComparison.Ordinal) ||
                   entryPath.Contains('#',StringComparison.Ordinal))
                    throw new InvalidDataException("رابط ورقة Excel غير آمن.");
                var entry=zip.GetEntry(entryPath) ??
                    throw new InvalidDataException("ورقة Excel المشار إليها غير موجودة.");
                string name=((string?)sheet.Attribute("name")??"").Trim();
                found.Add(new Worksheet(name.Length>0?name:"الورقة "+(found.Count+1),entry));
            }
            return found;
        }

        // Minimal original fixtures omit workbook relationships entirely.
        var fallback=new List<Worksheet>();
        for(int i=1;i<=50;i++)
        {
            var entry=zip.GetEntry($"xl/worksheets/sheet{i}.xml");
            if(entry is null)continue;
            string name=i<=definitions.Length?
                ((string?)definitions[i-1].Attribute("name")??"").Trim():"";
            fallback.Add(new Worksheet(name.Length>0?name:"الورقة "+i,entry));
        }
        if(fallback.Count==0)
            throw new InvalidDataException("ملف Excel لا يحتوي على أوراق أفراد متاحة.");
        return fallback;
    }
}
