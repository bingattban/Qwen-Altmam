using System.IO.Compression;
using System.Xml.Linq;
using AlTamam.Infrastructure.Excel.Reports;
using Xunit;

namespace AlTamam.Tests.Bridge;

internal static class ExcelLayoutAssertions
{
    internal static void Verify(ZipArchive archive,int headerRow)
    {
        XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XDocument Read(string path)
        {
            using var stream=archive.GetEntry(path)!.Open();
            return XDocument.Load(stream);
        }
        var styles=Read("xl/styles.xml");
        var formats=styles.Root!.Element(ns+"cellXfs")!.Elements().ToArray();
        Assert.Equal(8,formats.Length);
        Assert.All(formats,format=>Assert.Equal("1",(string?)format.Element(ns+"alignment")!.Attribute("wrapText")));
        Assert.Contains(styles.Descendants(ns+"name"),font=>(string?)font.Attribute("val")=="Arial");
        var sheets=archive.Entries.Where(e=>e.FullName.StartsWith("xl/worksheets/sheet",StringComparison.Ordinal)&&e.FullName.EndsWith(".xml",StringComparison.Ordinal)).ToArray();
        foreach(var sheet in sheets)
        {
            var xml=Read(sheet.FullName);
            var rows=xml.Descendants(ns+"row").ToArray();
            var heading=rows.Single(r=>(string?)r.Attribute("r")==headerRow.ToString());
            int count=heading.Elements(ns+"c").Count();
            Assert.Equal(count,xml.Descendants(ns+"col").Count());
            string last=rows.Last().Attribute("r")!.Value;
            Assert.Equal($"A{headerRow}:{ExcelReportLayout.Column(count)}{last}",(string?)xml.Descendants(ns+"autoFilter").Single().Attribute("ref"));
            Assert.All(heading.Elements(ns+"c"),cell=>Assert.Equal("3",(string?)cell.Attribute("s")));
            Assert.All(xml.Descendants(ns+"c"),cell=>Assert.InRange(int.Parse(cell.Attribute("s")!.Value),0,formats.Length-1));
            Assert.Equal("landscape",(string?)xml.Descendants(ns+"pageSetup").Single().Attribute("orientation"));
            Assert.Equal("1",(string?)xml.Descendants(ns+"sheetView").Single().Attribute("rightToLeft"));
            Assert.NotEmpty(xml.Descendants(ns+"pane"));
        }
        Assert.Equal(sheets.Length,Read("xl/workbook.xml").Descendants(ns+"definedName").Count());
    }
}

public sealed class ExcelColumnBoundaryTests
{
    [Theory]
    [InlineData(1,"A")]
    [InlineData(26,"Z")]
    [InlineData(27,"AA")]
    [InlineData(52,"AZ")]
    [InlineData(16384,"XFD")]
    public void DynamicColumnLabels_CoverEntireExcelRange(int number,string label)
        => Assert.Equal(label,ExcelReportLayout.Column(number));

    [Theory]
    [InlineData(0)]
    [InlineData(16385)]
    public void InvalidColumnCount_IsRejectedWithoutTruncation(int number)
        => Assert.Throws<InvalidDataException>(()=>ExcelReportLayout.Column(number));
}
