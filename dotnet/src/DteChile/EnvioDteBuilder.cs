using System.Globalization;
using System.Text;
using System.Xml;

namespace DteChile;

/// <summary>Datos de la carátula del envío.</summary>
/// <param name="RutEmisor">RUT de la empresa emisora.</param>
/// <param name="RutEnvia">RUT del titular del certificado que firma el envío (el «firmante autorizado»), no necesariamente el del emisor si quien envía es un mandatario.</param>
/// <param name="FechaResolucion">Fecha de la resolución del SII que autorizó al emisor como facturador electrónico (AAAA-MM-DD).</param>
/// <param name="NumeroResolucion">Número de esa resolución (0 si la autorización fue por internet, según el instructivo del SII).</param>
public sealed record Caratula(string RutEmisor, string RutEnvia, string FechaResolucion, int NumeroResolucion);

/// <summary>
/// Envuelve uno o más <c>&lt;DTE&gt;</c> ya firmados (ver <see cref="DteSigner.Sign"/>) en un <c>&lt;EnvioDTE&gt;</c>, con su <c>&lt;Caratula&gt;</c> y su propia firma
/// XMLDSig sobre el <c>&lt;SetDTE&gt;</c> completo: el archivo que efectivamente se sube al SII.
/// </summary>
public sealed class EnvioDteBuilder
{
    private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>RUT genérico del SII: receptor de todo envío hecho directamente al SII (no a otro contribuyente).</summary>
    private const string SiiRut = "60803000-K";

    private readonly DteSigner _signer;

    public EnvioDteBuilder(DteSigner? signer = null) => _signer = signer ?? new DteSigner();

    /// <exception cref="ArgumentException">Si no se recibe ningún DTE, o alguno no tiene la forma esperada.</exception>
    public XmlDocument Build(IReadOnlyList<XmlDocument> signedDtes, Caratula caratula, Certificate certificate)
        => Armar("EnvioDTE", "envio_dte", "EnvioDTE_v10.xsd", signedDtes, caratula, certificate);

    /// <summary>
    /// Igual que <see cref="Build"/>, pero para boletas (39 y 41): el SII las recibe en un <c>&lt;EnvioBOLETA&gt;</c> con su propio esquema (hasta 500 por envío).
    /// </summary>
    /// <exception cref="ArgumentException">Si algún documento no es una boleta.</exception>
    public XmlDocument BuildBoleta(IReadOnlyList<XmlDocument> signedBoletas, Caratula caratula, Certificate certificate)
    {
        for (var i = 0; i < signedBoletas.Count; i++)
        {
            var tipo = signedBoletas[i].GetElementsByTagName("TipoDTE") is { Count: > 0 } n ? int.Parse(n[0]!.InnerText, CultureInfo.InvariantCulture) : 0;
            if (!DocumentType.IsBoleta(tipo))
            {
                throw new ArgumentException($"El documento en la posición {i} es de tipo {tipo}: el EnvioBOLETA solo admite boletas (39 y 41).");
            }
        }

        return Armar("EnvioBOLETA", "envio_boleta", "EnvioBOLETA_v11.xsd", signedBoletas, caratula, certificate);
    }

    private XmlDocument Armar(string raizNombre, string spec, string esquema, IReadOnlyList<XmlDocument> signedDtes, Caratula caratula, Certificate certificate)
    {
        if (signedDtes.Count == 0)
        {
            throw new ArgumentException("El envío debe contener al menos un DTE.");
        }

        var subTotales = new SortedDictionary<int, int>();
        var dtesXml = new StringBuilder();
        for (var i = 0; i < signedDtes.Count; i++)
        {
            var dte = signedDtes[i];
            if (dte.DocumentElement is not { Name: "DTE" })
            {
                throw new ArgumentException($"El DTE en la posición {i} no es un documento válido (¿se firmó con DteSigner.Sign?).");
            }

            var tipoNodo = dte.GetElementsByTagName("TipoDTE");
            if (tipoNodo.Count == 0)
            {
                throw new ArgumentException("Uno de los DTE recibidos no tiene <TipoDTE> — ¿se generó con DteBuilder?");
            }

            var tipo = int.Parse(tipoNodo[0]!.InnerText, CultureInfo.InvariantCulture);
            subTotales[tipo] = subTotales.GetValueOrDefault(tipo) + 1;
            // Los DTE ya firmados se insertan como TEXTO, tal como fueron firmados (los bytes ISO-8859-1 leídos como texto): reconstruir el árbol podría alterar
            // los espacios de nombres en alcance y, con ellos, la forma canónica de <Documento> y la firma de cada DTE.
            dtesXml.Append(XmlIo.ToXml(dte, declaracion: false));
        }

        var carat = new Dictionary<string, object?>
        {
            ["@version"] = "1.0",
            ["RutEmisor"] = Rut.ToDteFormat(caratula.RutEmisor),
            ["RutEnvia"] = Rut.ToDteFormat(caratula.RutEnvia),
            ["RutReceptor"] = SiiRut,
            ["FchResol"] = caratula.FechaResolucion,
            ["NroResol"] = caratula.NumeroResolucion,
            // Hora de Chile explícita: es la que el SII espera ver como momento en que se firmó el archivo de envío.
            ["TmstFirmaEnv"] = TedSigner.ChileNow().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ["SubTotDTE"] = subTotales.Select(kv => (object?)new Dictionary<string, object?> { ["TpoDTE"] = kv.Key, ["NroDTE"] = kv.Value }).ToList(),
        };

        var doc = XmlIo.NewDocument();
        var xml = new SpecXml(DteBuilder.NamespaceUri, ["DTE", "Signature"]);
        var raiz = xml.Element(
            doc,
            Spec.Load(spec),
            new Dictionary<string, object?> { ["@version"] = "1.0", ["SetDTE"] = new Dictionary<string, object?> { ["@ID"] = "SetDoc", ["Caratula"] = carat } },
            raizNombre);
        var schemaLocation = doc.CreateAttribute("xsi", "schemaLocation", Xsi);
        schemaLocation.Value = "http://www.sii.cl/SiiDte " + esquema;
        raiz.SetAttributeNode(schemaLocation);
        doc.AppendChild(raiz);

        var texto = XmlIo.ToXml(doc).Replace("</SetDTE>", dtesXml + "</SetDTE>", StringComparison.Ordinal);
        var sobre = XmlIo.Parse(Encoding.Latin1.GetBytes(texto));

        return _signer.SignElement(sobre, "SetDTE", certificate);
    }
}
