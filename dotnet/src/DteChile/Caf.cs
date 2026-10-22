using System.Security.Cryptography;
using System.Xml.Linq;

namespace DteChile;

/// <summary>
/// Código de Autorización de Folios (CAF) del SII, ya en memoria: tipo de documento, rango de folios autorizado, llave RSA propia del CAF (para firmar el timbre)
/// y el nodo <c>&lt;CAF&gt;</c> crudo que exige el TED. No tiene relación con el certificado digital de la empresa: son dos pares de llaves distintos.
/// </summary>
public sealed class Caf
{
    private readonly XDocument _xml;
    private readonly XElement _da;

    private Caf(XDocument xml, XElement da)
    {
        _xml = xml;
        _da = da;
    }

    /// <exception cref="ArgumentException">Si el XML no tiene la forma de un CAF real del SII.</exception>
    public static Caf FromXml(string rawXml)
    {
        XDocument doc;
        try
        {
            using var reader = System.Xml.XmlReader.Create(new StringReader(rawXml), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            throw new ArgumentException("El archivo no es un XML válido.");
        }

        var da = doc.Descendants("DA").FirstOrDefault();
        // <RSASK> (llave privada del CAF) es hermano de <CAF>, no hijo de <DA>: vive en <AUTORIZACION><CAF>…</CAF><RSASK>…</RSASK></AUTORIZACION>.
        if (da is null || da.Element("TD") is null || da.Element("RNG")?.Element("D") is null || da.Element("RNG")?.Element("H") is null || !doc.Descendants("RSASK").Any())
        {
            throw new ArgumentException("El archivo no tiene el formato de un CAF válido del SII.");
        }

        return new Caf(doc, da);
    }

    public int DocumentType => int.Parse(_da.Element("TD")!.Value.Trim());

    public int FolioFrom => int.Parse(_da.Element("RNG")!.Element("D")!.Value.Trim());

    public int FolioTo => int.Parse(_da.Element("RNG")!.Element("H")!.Value.Trim());

    public bool ContainsFolio(int folio) => folio >= FolioFrom && folio <= FolioTo;

    /// <summary>RUT de la empresa emisora a la que el SII le autorizó este rango de folios.</summary>
    public string IssuerRut => Rut.ToDteFormat(_da.Element("RE")?.Value ?? string.Empty);

    public string IssuerName => _da.Element("RS")?.Value ?? string.Empty;

    public int Idk => int.TryParse(_da.Element("IDK")?.Value.Trim(), out var i) ? i : 0;

    /// <summary>El nodo <c>&lt;CAF&gt;…&lt;/CAF&gt;</c> completo (con <c>DA</c> y <c>FRMA</c>) tal como lo pide el TED, sin la declaración XML ni el <c>AUTORIZACION</c> que lo envuelve.</summary>
    public string CafXmlNode()
    {
        var caf = _xml.Descendants("CAF").FirstOrDefault() ?? throw new InvalidOperationException("No fue posible ubicar el nodo <CAF> dentro del archivo.");

        return caf.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Llave privada propia del CAF (RSASK): firma el TED.</summary>
    public RSA PrivateKey()
    {
        var pem = _xml.Descendants("RSASK").First().Value.Trim();
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            throw new InvalidOperationException("No fue posible leer la llave privada del CAF (RSASK).");
        }

        return rsa;
    }
}
