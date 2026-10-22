using System.IO.Compression;

namespace DumpTriage.Common.Tracing;

/// <summary>
/// The upload: a zip whose first member is the .etl (the service recognises it by that, website#2002), then the
/// <c>.etl.NGENPDB</c> folder WPR writes beside it for precompiled .NET code, if there is one. An ETL compresses about
/// 8x (133 MB to 16 MB on the lab trace, poc/repros/1999-trace-lab).
/// </summary>
public static class TraceArchive
{
    public static void Create(string etlPath, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(etlPath, "trace.etl", CompressionLevel.Optimal);
        var ngen = etlPath + ".NGENPDB";
        if (Directory.Exists(ngen))
        {
            foreach (var file in Directory.EnumerateFiles(ngen))
            {
                zip.CreateEntryFromFile(file, "trace.etl.NGENPDB/" + Path.GetFileName(file), CompressionLevel.Optimal);
            }
        }
    }
}
