using System.IO.Compression;

namespace AlTamam.Infrastructure.Excel.Reports;

/// <summary>
/// Phase 09: verify a complete LOCAL OpenXML ZIP while holding an exclusive
/// file handle, flush its bytes to disk, then atomically rename in the SAME
/// directory. A missing/broken workbook is never published as success.
/// </summary>
public static class ReportWorkbookCommit
{
    public static void VerifyFlushAndMove(string temporary,string destination,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using(var file=new FileStream(temporary,FileMode.Open,FileAccess.ReadWrite,
            FileShare.None))
        {
            ValidateWorkbook(file);
            cancellation.ThrowIfCancellationRequested();
            file.Flush(flushToDisk:true);
        }
        cancellation.ThrowIfCancellationRequested();
        File.Move(temporary,destination,overwrite:false);
        try
        {
            using var published=new FileStream(destination,FileMode.Open,
                FileAccess.Read,FileShare.Read);
            ValidateWorkbook(published);
        }
        catch
        {
            try{File.Delete(destination);}catch(IOException){}
            catch(UnauthorizedAccessException){}
            throw;
        }
    }

    private static void ValidateWorkbook(Stream source)
    {
        if(source.Length==0)throw new InvalidDataException("ملف Excel فارغ.");
        source.Position=0;
        using var archive=new ZipArchive(source,ZipArchiveMode.Read,leaveOpen:true);
        if(archive.GetEntry("xl/workbook.xml") is null
            || archive.GetEntry("[Content_Types].xml") is null
            || !archive.Entries.Any(entry=>entry.FullName.StartsWith(
                "xl/worksheets/sheet",StringComparison.Ordinal)
                && entry.FullName.EndsWith(".xml",StringComparison.Ordinal)))
            throw new InvalidDataException("ملف Excel غير مكتمل، ولن يتم نشره.");
    }
}
