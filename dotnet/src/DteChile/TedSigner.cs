using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;

namespace DteChile;

/// <summary>
/// Arma y firma el Timbre Electrónico (TED) de un DTE con la llave RSA propia del CAF (<c>RSASK</c>), validando antes que el folio esté dentro del rango
/// autorizado y que el tipo de documento coincida con el del CAF. Esta firma es independiente y anterior a la firma XMLDSig del certificado de la empresa:
/// son dos pares de llaves completamente distintos.
/// </summary>
public sealed partial class TedSigner
{
    /// <param name="data">Mismos datos que recibe <see cref="DteBuilder.Build"/> (folio, fechaEmision, emisor.rut, receptor.rut y razonSocial, items), más
    /// <c>montoTotal</c>: el monto final ya calculado, que se obtiene con <see cref="DteBuilder.MontoTotal"/>.</param>
    /// <exception cref="ArgumentException">Si el folio no está autorizado por este CAF, o el CAF es de otro tipo de documento.</exception>
    public XmlDocument Sign(Caf caf, int documentType, IDictionary<string, object?> data)
    {
        var folio = Convert.ToInt32(data["folio"], CultureInfo.InvariantCulture);
        if (documentType != caf.DocumentType)
        {
            throw new ArgumentException($"El CAF cargado es para el tipo de documento {caf.DocumentType}, no {documentType}.");
        }

        if (!caf.ContainsFolio(folio))
        {
            throw new ArgumentException($"El folio {folio} no está autorizado por este CAF (rango {caf.FolioFrom}-{caf.FolioTo}).");
        }

        // El TED se arma SIN espacio de nombres: así se firma el texto exacto de <DD> que después viaja dentro del DTE.
        var doc = XmlIo.NewDocument();
        var ted = doc.CreateElement("TED");
        ted.SetAttribute("version", "1.0");
        doc.AppendChild(ted);

        var dd = BuildDd(doc, documentType, folio, caf, data);
        ted.AppendChild(dd);
        ted.AppendChild(BuildFrmt(doc, dd, caf));

        return doc;
    }

    private static XmlElement BuildDd(XmlDocument doc, int documentType, int folio, Caf caf, IDictionary<string, object?> data)
    {
        var emisor = SpecXml.AsMap(data["emisor"])!;
        var receptor = data.TryGetValue("receptor", out var recep) && SpecXml.AsMap(recep) is { } rm ? rm : new Dictionary<string, object?>();
        var dd = doc.CreateElement("DD");

        dd.AppendChild(El(doc, "RE", Rut.ToDteFormat(Convert.ToString(Pick(emisor, "rut", "RUTEmisor"), CultureInfo.InvariantCulture)!)));
        dd.AppendChild(El(doc, "TD", documentType.ToString(CultureInfo.InvariantCulture)));
        dd.AppendChild(El(doc, "F", folio.ToString(CultureInfo.InvariantCulture)));
        dd.AppendChild(El(doc, "FE", Convert.ToString(data["fechaEmision"], CultureInfo.InvariantCulture)!));
        // En las boletas el receptor es opcional: sin RUT va el genérico 66666666-6 y sin nombre «Cliente».
        dd.AppendChild(El(doc, "RR", Rut.ToDteFormat(Pick(receptor, "rut", "RUTRecep") is { } rutRecep && Convert.ToString(rutRecep, CultureInfo.InvariantCulture) is { Length: > 0 } rr ? rr : BoletaBuilder.RutGenerico)));
        var razon = (Convert.ToString(Pick(receptor, "razonSocial", "RznSocRecep"), CultureInfo.InvariantCulture) ?? string.Empty).Trim();
        dd.AppendChild(El(doc, "RSR", Cut(razon.Length > 0 ? razon : "Cliente", 40)));
        dd.AppendChild(El(doc, "MNT", Convert.ToString(data["montoTotal"], CultureInfo.InvariantCulture)!));

        var items = SpecXml.AsList(data["items"]);
        var primero = items is { Count: > 0 } ? SpecXml.AsMap(items[0]) : null;
        dd.AppendChild(El(doc, "IT1", Cut(Convert.ToString(primero is null ? null : Pick(primero, "nombre", "NmbItem"), CultureInfo.InvariantCulture) ?? string.Empty, 40)));

        // El nodo <CAF>…</CAF> completo (con <DA> y <FRMA>) tal como lo exige el TED.
        var frag = doc.CreateDocumentFragment();
        frag.InnerXml = caf.CafXmlNode();
        dd.AppendChild(frag);

        // Hora de Chile explícita (no la del servidor): es la que el SII espera ver impresa junto con el resto del documento.
        dd.AppendChild(El(doc, "TSTED", ChileNow().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)));

        return dd;
    }

    private static XmlElement BuildFrmt(XmlDocument doc, XmlElement dd, Caf caf)
    {
        // El timbre NO se firma con C14N/XMLDSig estándar: el instructivo del SII (Anexo A.2.4) exige eliminar todo el espacio entre etiquetas antes de firmar,
        // sin tocar el contenido de los elementos terminales. Se firman los bytes ISO-8859-1 que viajan (con «í» como un solo byte), que es lo que el SII verifica.
        var texto = EspacioEntreEtiquetas().Replace(dd.OuterXml, "><");
        using var rsa = caf.PrivateKey();
        var firma = rsa.SignData(SiiText.Iso88591.GetBytes(texto), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);

        var frmt = El(doc, "FRMT", Convert.ToBase64String(firma));
        frmt.SetAttribute("algoritmo", "SHA1withRSA");

        return frmt;
    }

    internal static DateTime ChileNow() => TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Santiago"));

    private static object? Pick(IDictionary<string, object?> d, string amigable, string sii) => d.TryGetValue(amigable, out var v) ? v : (d.TryGetValue(sii, out var w) ? w : null);

    private static string Cut(string s, int max) => SiiText.Truncate(s.Trim(), max);

    private static XmlElement El(XmlDocument doc, string name, string value)
    {
        var e = doc.CreateElement(name);
        var texto = SiiText.Latin1(value, name);
        if (texto.Length > 0)
        {
            e.AppendChild(doc.CreateTextNode(texto));
        }

        return e;
    }

    [GeneratedRegex(@">\s+<")]
    private static partial Regex EspacioEntreEtiquetas();
}
