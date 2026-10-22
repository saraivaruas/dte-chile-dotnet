using System.Globalization;
using System.Text;
using System.Xml;

namespace DteChile;

/// <summary>
/// Documentos firmados del SII distintos del DTE: libros de compras y ventas, de guías y de boletas, consumo de folios (RCOF), respuestas de intercambio,
/// recibos de mercaderías y archivo electrónico de cesión (AEC).
/// <para>Todos se arman igual que el DTE: los datos se entregan con los nombres de elemento del SII (ver los esquemas oficiales en <c>schemas/</c>),
/// <see cref="SpecXml"/> los ordena y valida contra la especificación generada desde esos esquemas, y <see cref="DteSigner"/> los firma con el certificado digital.
/// El identificador (<c>ID</c>) y las marcas de tiempo (hora de Chile) se completan solos cuando no se entregan.</para>
/// </summary>
public sealed class SiiDocuments
{
    private const string Ns = DteBuilder.NamespaceUri;
    private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private readonly DteSigner _signer;
    private readonly bool _truncar;

    public SiiDocuments(DteSigner? signer = null, bool truncar = false)
    {
        _signer = signer ?? new DteSigner();
        _truncar = truncar;
    }

    /// <summary>Libro de compras y ventas (IECV). Datos: <c>{"EnvioLibro": {"Caratula": {...}, "ResumenPeriodo": {...}, "Detalle": [{...}]}}</c>.</summary>
    public XmlDocument LibroCompraVenta(IDictionary<string, object?> data, Certificate cert)
        => Simple("libro_compra_venta", "EnvioLibro", "LibroCV_v10.xsd", "LibroCV", data, cert, ["EnvioLibro", "TmstFirma"]);

    /// <summary>Libro de guías de despacho.</summary>
    public XmlDocument LibroGuia(IDictionary<string, object?> data, Certificate cert)
        => Simple("libro_guia", "EnvioLibro", "LibroGuia_v10.xsd", "LibroGuia", data, cert, ["EnvioLibro", "TmstFirma"]);

    /// <summary>Libro de boletas electrónicas.</summary>
    public XmlDocument LibroBoleta(IDictionary<string, object?> data, Certificate cert)
        => Simple("libro_boleta", "EnvioLibro", "LibroBOLETA_v10.xsd", "LibroBoleta", data, cert, ["EnvioLibro", "TmstFirma"]);

    /// <summary>Reporte de consumo de folios (RCOF) de las boletas. Datos: <c>{"DocumentoConsumoFolios": {"Caratula": {...}, "Resumen": [{...}]}}</c>.</summary>
    public XmlDocument ConsumoFolios(IDictionary<string, object?> data, Certificate cert)
        => Simple("consumo_folios", "DocumentoConsumoFolios", "ConsumoFolio_v10.xsd", "ConsumoFolios", data, cert, ["DocumentoConsumoFolios", "Caratula", "TmstFirmaEnv"]);

    /// <summary>Respuesta de intercambio (recepción de envío, resultado comercial). Datos: <c>{"Resultado": {"Caratula": {...}, "RecepcionEnvio" | "ResultadoDTE": ...}}</c>.</summary>
    public XmlDocument RespuestaDte(IDictionary<string, object?> data, Certificate cert)
        => Simple("respuesta_dte", "Resultado", "RespuestaEnvioDTE_v10.xsd", "Respuesta", data, cert, ["Resultado", "Caratula", "TmstFirmaResp"]);

    /// <summary>Un recibo de mercaderías o servicios (Ley 19.983), firmado. Datos: <c>{"DocumentoRecibo": {TipoDoc, Folio, FchEmis, RUTEmisor, RUTRecep, MntTotal, Recinto, RutFirma, Declaracion}}</c>.</summary>
    public XmlDocument Recibo(IDictionary<string, object?> data, Certificate cert)
        => Simple("recibo", "DocumentoRecibo", "Recibos_v10.xsd", "Recibo", data, cert, ["DocumentoRecibo", "TmstFirmaRecibo"]);

    /// <summary>Envío de recibos: agrupa varios recibos (cada uno firmado por separado) y firma el conjunto.</summary>
    /// <param name="caratula">RutResponde, RutRecibe, NmbContacto?, FonoContacto?, MailContacto?, TmstFirmaEnv? (por defecto, ahora).</param>
    /// <param name="recibos">Datos de cada recibo (ver <see cref="Recibo"/>).</param>
    public XmlDocument EnvioRecibos(IDictionary<string, object?> caratula, IReadOnlyList<IDictionary<string, object?>> recibos, Certificate cert)
    {
        if (recibos.Count == 0)
        {
            throw new ArgumentException("El envío de recibos debe contener al menos un recibo.");
        }

        var firmados = new List<XmlDocument>();
        for (var i = 0; i < recibos.Count; i++)
        {
            var r = new Dictionary<string, object?>(recibos[i]);
            var doc = new Dictionary<string, object?>(SpecXml.AsMap(r["DocumentoRecibo"])!);
            doc.TryAdd("@ID", "Recibo" + (i + 1));
            r["DocumentoRecibo"] = doc;
            firmados.Add(Recibo(r, cert));
        }

        var car = new Dictionary<string, object?>(caratula);
        car.TryAdd("TmstFirmaEnv", Ahora());
        var esqueleto = Esqueleto("envio_recibos", new Dictionary<string, object?> { ["SetRecibos"] = new Dictionary<string, object?> { ["@ID"] = "SetRecibos", ["Caratula"] = car } }, "EnvioRecibos_v10.xsd", ["Recibo"]);
        var conRecibos = Insertar(esqueleto, "</SetRecibos>", firmados.Select(Fragmento), despues: false);

        return _signer.SignElement(conRecibos, "SetRecibos", cert);
    }

    /// <summary>Archivo electrónico de cesión (AEC) de una factura.</summary>
    /// <param name="caratula">RutCedente, RutCesionario, NmbContacto?, FonoContacto?, MailContacto?, TmstFirmaEnvio? (por defecto, ahora).</param>
    /// <param name="dte">El DTE firmado que se cede (el mismo que se envió al SII).</param>
    /// <param name="cesiones">Datos de cada cesión (<c>DocumentoCesion</c>: SeqCesion, IdDTE, Cedente, Cesionario, MontoCesion, UltimoVencimiento...). Hasta 40.</param>
    /// <param name="dteCedido">Extras opcionales de DocumentoDTECedido: ImagenDTE (base64), ImagenAR (base64).</param>
    public XmlDocument Aec(IDictionary<string, object?> caratula, XmlDocument dte, IReadOnlyList<IDictionary<string, object?>> cesiones, Certificate cert, IDictionary<string, object?>? dteCedido = null)
    {
        if (cesiones.Count == 0)
        {
            throw new ArgumentException("El AEC debe contener al menos una cesión.");
        }

        if (dte.DocumentElement is not { Name: "DTE" })
        {
            throw new ArgumentException("El documento a ceder debe ser un DTE firmado.");
        }

        var cedidoDatos = new Dictionary<string, object?>(dteCedido ?? new Dictionary<string, object?>()) { ["@ID"] = "DTECedido" };
        cedidoDatos.TryAdd("TmstFirma", Ahora());
        var cedido = Esqueleto("dte_cedido", new Dictionary<string, object?> { ["DocumentoDTECedido"] = cedidoDatos }, null, ["DTE"]);
        cedido = Insertar(cedido, "<DocumentoDTECedido ID=\"DTECedido\">", [Fragmento(dte)], despues: true);
        cedido = _signer.SignElement(cedido, "DocumentoDTECedido", cert);

        var firmadas = new List<XmlDocument>();
        for (var i = 0; i < cesiones.Count; i++)
        {
            var c = new Dictionary<string, object?>(cesiones[i]);
            c.TryAdd("@ID", "Cesion" + (i + 1));
            firmadas.Add(Simple("cesion", "DocumentoCesion", null, "Cesion", new Dictionary<string, object?> { ["DocumentoCesion"] = c }, cert, ["DocumentoCesion", "TmstCesion"]));
        }

        var car = new Dictionary<string, object?>(caratula);
        car.TryAdd("TmstFirmaEnvio", Ahora());
        var aec = Esqueleto("aec", new Dictionary<string, object?> { ["DocumentoAEC"] = new Dictionary<string, object?> { ["@ID"] = "AEC", ["Caratula"] = car } }, "AEC_v10.xsd", ["Cesiones"]);
        var contenido = "<Cesiones>" + Fragmento(cedido) + string.Concat(firmadas.Select(Fragmento)) + "</Cesiones>";
        var doc = Insertar(aec, "</DocumentoAEC>", [contenido], despues: false);

        return _signer.SignElement(doc, "DocumentoAEC", cert);
    }

    private XmlDocument Simple(string spec, string signTag, string? esquema, string prefijoId, IDictionary<string, object?> data, Certificate cert, string[] tiempo)
    {
        var d = Copiar(data);
        var firmado = SpecXml.AsMap(d.GetValueOrDefault(signTag)) ?? throw new ArgumentException($"Falta el campo requerido «{signTag}».");
        if (!firmado.ContainsKey("@ID") || firmado["@ID"] is null)
        {
            firmado["@ID"] = prefijoId + "_" + TedSigner.ChileNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        var nodo = d;
        foreach (var k in tiempo[..^1])
        {
            nodo = SpecXml.AsMap(nodo.GetValueOrDefault(k)) ?? throw new ArgumentException($"Falta el campo requerido «{k}».");
        }

        if (!nodo.ContainsKey(tiempo[^1]) || nodo[tiempo[^1]] is null)
        {
            nodo[tiempo[^1]] = Ahora();
        }

        var esqueleto = Esqueleto(spec, d, esquema, []);

        return _signer.SignElement(esqueleto, signTag, cert, Convert.ToString(firmado["@ID"], CultureInfo.InvariantCulture));
    }

    /// <summary>Copia profunda de los diccionarios (para completar el ID y las marcas de tiempo sin modificar lo que entregó quien llama).</summary>
    private static Dictionary<string, object?> Copiar(IDictionary<string, object?> src)
    {
        var r = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in src)
        {
            r[k] = v switch
            {
                IDictionary<string, object?> m => Copiar(m),
                _ => v,
            };
        }

        return r;
    }

    private XmlDocument Esqueleto(string spec, IDictionary<string, object?> data, string? esquema, string[] deferred)
    {
        var raiz = Spec.Load(spec);
        var doc = XmlIo.NewDocument();
        var el = new SpecXml(Ns, deferred, _truncar).Element(doc, raiz, data, raiz.Name);
        // Todos declaran xmlns:xsi: así la forma canónica (C14N inclusiva) de lo que se firma es la misma suelto y dentro de otro documento.
        var xmlnsXsi = doc.CreateAttribute("xmlns", "xsi", "http://www.w3.org/2000/xmlns/");
        xmlnsXsi.Value = Xsi;
        el.SetAttributeNode(xmlnsXsi);
        if (esquema is not null)
        {
            var loc = doc.CreateAttribute("xsi", "schemaLocation", Xsi);
            loc.Value = Ns + " " + esquema;
            el.SetAttributeNode(loc);
        }

        doc.AppendChild(el);

        return doc;
    }

    /// <summary>Inserta fragmentos XML ya firmados, como texto y sin reconciliar espacios de nombres (que cambiaría su forma canónica y rompería sus firmas).</summary>
    private static XmlDocument Insertar(XmlDocument doc, string ancla, IEnumerable<string> fragmentos, bool despues)
    {
        var xml = XmlIo.ToXml(doc);
        var partes = xml.Split(ancla);
        if (partes.Length != 2)
        {
            throw new InvalidOperationException($"No se encontró el punto de inserción {ancla}.");
        }

        var texto = string.Concat(fragmentos);

        return XmlIo.Parse(Encoding.Latin1.GetBytes(despues ? partes[0] + ancla + texto + partes[1] : partes[0] + texto + ancla + partes[1]));
    }

    /// <summary>El elemento raíz del documento, serializado (sin declaración XML).</summary>
    private static string Fragmento(XmlDocument d) => XmlIo.ToXml(d, declaracion: false);

    private static string Ahora() => TedSigner.ChileNow().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
