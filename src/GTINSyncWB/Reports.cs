using System.IO.Compression;
using System.Text;
using System.Xml;

namespace GTINSyncWB;

public static class ReportWriter
{
    private const string Main="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static void Xlsx(string path,IReadOnlyList<string> headers,IEnumerable<IReadOnlyList<string>> rows)
    {
        using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
        Add(zip,"[Content_Types].xml","""<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
        Add(zip,"_rels/.rels","""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Add(zip,"xl/workbook.xml","""<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="GTIN Sync WB" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        Add(zip,"xl/_rels/workbook.xml.rels","""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
        var entry=zip.CreateEntry("xl/worksheets/sheet1.xml",CompressionLevel.Optimal);
        using var stream=entry.Open();using var writer=XmlWriter.Create(stream,new XmlWriterSettings{Encoding=new UTF8Encoding(false),CloseOutput=false});
        writer.WriteStartDocument();writer.WriteStartElement("worksheet",Main);
        writer.WriteStartElement("sheetData",Main);
        var index=0;
        foreach(var row in new[]{headers}.Concat(rows))
        {
            index++;writer.WriteStartElement("row",Main);writer.WriteAttributeString("r",index.ToString());
            for(var i=0;i<row.Count;i++)
            {
                writer.WriteStartElement("c",Main);writer.WriteAttributeString("r",Column(i)+index);writer.WriteAttributeString("t","inlineStr");
                writer.WriteStartElement("is",Main);writer.WriteElementString("t",Main,Clean(row[i]));writer.WriteEndElement();writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();writer.WriteEndElement();writer.WriteEndDocument();
    }
    private static string Clean(string? value)=>new((value??"").Where(XmlConvert.IsXmlChar).ToArray());
    private static string Column(int number)
    {
        var result="";for(var n=number+1;n>0;n=(n-1)/26)result=(char)('A'+(n-1)%26)+result;
        return result;
    }
    private static void Add(ZipArchive zip,string name,string xml)
    {
        using var stream=new StreamWriter(zip.CreateEntry(name,CompressionLevel.Optimal).Open(),new UTF8Encoding(false));stream.Write(xml);
    }
}
