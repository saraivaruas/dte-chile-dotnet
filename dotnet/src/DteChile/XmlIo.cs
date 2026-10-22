using System.Text;
using System.Xml;

namespace DteChile;

/// <summary>Lectura y escritura de documentos XML tal como viajan al SII: ISO-8859-1, sin sangrías ni espacios que alteren la canonicalización.</summary>
public static class XmlIo
{
    /// <summary>Documento nuevo que conserva los espacios (los documentos del SII no llevan espacios entre etiquetas, y los que sí, no deben alterarse).</summary>
    public static XmlDocument NewDocument() => new() { PreserveWhitespace = true };

    /// <summary>Bytes del documento en ISO-8859-1, con la declaración <c>&lt;?xml version="1.0" encoding="ISO-8859-1"?&gt;</c> que acostumbra el SII.</summary>
    public static byte[] ToBytes(XmlDocument doc, bool declaracion = true)
    {
        using var ms = new MemoryStream();
        if (declaracion)
        {
            ms.Write(Encoding.Latin1.GetBytes("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>\n"));
        }

        var settings = new XmlWriterSettings { Encoding = Encoding.Latin1, OmitXmlDeclaration = true, Indent = false, NewLineHandling = NewLineHandling.None };
        using (var writer = XmlWriter.Create(ms, settings))
        {
            doc.DocumentElement!.WriteTo(writer);
        }

        return ms.ToArray();
    }

    /// <summary>El documento serializado como texto (los bytes ISO-8859-1 leídos como texto: cada byte es un carácter).</summary>
    public static string ToXml(XmlDocument doc, bool declaracion = true) => Encoding.Latin1.GetString(ToBytes(doc, declaracion));

    public static XmlDocument Parse(byte[] bytes)
    {
        var doc = NewDocument();
        using var ms = new MemoryStream(bytes);
        using var reader = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        doc.Load(reader);

        return doc;
    }

    /// <summary>Serializa y vuelve a leer: así lo que se firma es exactamente lo que recalculará quien reciba los bytes finales.</summary>
    public static XmlDocument Reparse(XmlDocument doc) => Parse(ToBytes(doc));

    /// <summary>Texto XML (UTF-16 de .NET) a documento.</summary>
    public static XmlDocument ParseText(string xml)
    {
        var doc = NewDocument();
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        doc.Load(reader);

        return doc;
    }
}
