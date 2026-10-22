using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace DteChile;

public enum SiiAmbiente
{
    Certificacion,
    Produccion,
}

/// <summary>Campo de un formulario multipart: un texto, o un archivo si trae <see cref="FileName"/>.</summary>
public sealed record SiiFormField(string Name, string Content, string? FileName = null, string? ContentType = null);

/// <summary>Solicitud HTTP que el cliente entrega al transporte.</summary>
public sealed record SiiRequest(string Method, string Url, IReadOnlyList<string> Headers, string? Body = null, IReadOnlyList<SiiFormField>? Form = null);

public sealed record SiiResponse(int Status, string Body);

/// <summary>Resultado de consultar un envío o un documento.</summary>
/// <param name="Estado">Código del SII (por ejemplo EPR, RSC, DOK).</param>
/// <param name="Descripcion">Explicación en español; si el código es nuevo, la glosa que informa el SII.</param>
public sealed record SiiEstado(string Estado, string Descripcion, string Glosa, string TrackId, int? Aceptados, int? Rechazados, int? Reparos, string Respuesta);

/// <summary>
/// Cliente de los servicios en línea del SII: autenticación (semilla → token firmado), envío de DTE y de boletas, y consulta del estado de los envíos y de los
/// documentos.
/// <para>Los servicios SOAP del SII (RPC/encoded, de otra época) no se consumen con generadores de clientes, así que el sobre SOAP se arma a mano sobre HTTP.
/// Las boletas usan, en cambio, la API REST propia del SII. Los puntos de conexión, los parámetros y el formato de las respuestas están tomados de los WSDL y de
/// los manuales publicados por el SII; el comportamiento real lo define el SII y puede cambiar: esta biblioteca no puede garantizar la aceptación de un documento,
/// solo entregar el XML conforme al esquema y firmado según el formato publicado.</para>
/// <para>Para pruebas sin red se puede entregar un transporte propio.</para>
/// </summary>
public sealed class SiiClient
{
    private static readonly Dictionary<SiiAmbiente, (string Dte, string Api, string Boleta)> Hosts = new()
    {
        [SiiAmbiente.Certificacion] = ("https://maullin.sii.cl", "https://apicert.sii.cl", "https://pangal.sii.cl"),
        [SiiAmbiente.Produccion] = ("https://palena.sii.cl", "https://api.sii.cl", "https://rahue.sii.cl"),
    };

    /// <summary>Códigos de STATUS que devuelve DTEUpload — manual OI2003_UPDTE_MDE, punto 2.1.</summary>
    private static readonly Dictionary<string, string> UploadStatus = new()
    {
        ["0"] = "Upload OK.",
        ["1"] = "El remitente no tiene permiso para enviar.",
        ["2"] = "Error en el tamaño del archivo (muy grande o muy chico).",
        ["3"] = "Archivo cortado (tamaño distinto al informado).",
        ["5"] = "No está autenticado (token inválido o vencido).",
        ["6"] = "La empresa no está autorizada a enviar archivos.",
        ["7"] = "Esquema XML inválido.",
        ["8"] = "Firma del documento inválida.",
        ["9"] = "Sistema bloqueado.",
    };

    /// <summary>Estados que informa el SII al consultar un envío (QueryEstUp). El texto que viaja en la respuesta del SII (GLOSA) manda sobre este.</summary>
    public static readonly IReadOnlyDictionary<string, string> EstadosEnvio = new Dictionary<string, string>
    {
        ["EPR"] = "Envío procesado.",
        ["RSC"] = "Rechazado por error en el esquema.",
        ["RFR"] = "Rechazado por error en la firma.",
        ["RCT"] = "Rechazado por error en la carátula.",
        ["RCH"] = "Rechazado.",
        ["REC"] = "Recibido.",
        ["DOK"] = "Documento recibido por el SII, datos coinciden.",
    };

    /// <summary>Estados que informa el SII al consultar un documento (QueryEstDte).</summary>
    public static readonly IReadOnlyDictionary<string, string> EstadosDte = new Dictionary<string, string>
    {
        ["DOK"] = "Documento recibido por el SII. Datos coinciden con los registrados.",
        ["DNK"] = "Documento recibido por el SII pero los datos no coinciden con los registrados.",
        ["FAU"] = "Documento no recibido por el SII.",
        ["FNA"] = "Documento no autorizado.",
        ["FAN"] = "Documento anulado.",
        ["EMP"] = "Empresa no autorizada a enviar documentos.",
        ["TMD"] = "Existe una nota de débito que modifica el texto del documento.",
        ["TMC"] = "Existe una nota de crédito que modifica el texto del documento.",
        ["MMD"] = "Existe una nota de débito que modifica los montos del documento.",
        ["MMC"] = "Existe una nota de crédito que modifica los montos del documento.",
        ["AND"] = "Existe una nota de débito que anula el documento.",
        ["ANC"] = "Existe una nota de crédito que anula el documento.",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly SiiAmbiente _ambiente;
    private readonly TokenRequestSigner _tokenSigner;
    private readonly Func<SiiRequest, CancellationToken, Task<SiiResponse>> _transporte;

    public SiiClient(SiiAmbiente ambiente = SiiAmbiente.Certificacion, TokenRequestSigner? tokenSigner = null, Func<SiiRequest, CancellationToken, Task<SiiResponse>>? transporte = null)
    {
        _ambiente = ambiente;
        _tokenSigner = tokenSigner ?? new TokenRequestSigner();
        _transporte = transporte ?? Enviar;
    }

    // ------------------------------------------------------------------ autenticación (servicios SOAP)

    /// <summary>Semilla (número de una sola vez) que hay que firmar dentro de los próximos ~2 minutos para canjearla por un token.</summary>
    public async Task<string> GetSeedAsync(CancellationToken ct = default)
        => LeerValor(ExtraerRetorno(await Soap(Dte("/DTEWS/CrSeed.jws"), "getSeed", [], ct), "getSeedReturn"), "SEMILLA");

    /// <summary>Canjea una semilla ya firmada por el token de sesión que autentica el resto de las llamadas al SII.</summary>
    public async Task<string> GetTokenAsync(string semilla, Certificate certificate, CancellationToken ct = default)
        => LeerValor(ExtraerRetorno(await Soap(Dte("/DTEWS/GetTokenFromSeed.jws"), "getToken", [("pszXml", SolicitudToken(semilla, certificate))], ct), "getTokenReturn"), "TOKEN");

    /// <summary>Atajo: obtiene la semilla y la canjea por token en un solo paso.</summary>
    public async Task<string> AuthenticateAsync(Certificate certificate, CancellationToken ct = default)
        => await GetTokenAsync(await GetSeedAsync(ct), certificate, ct);

    // ------------------------------------------------------------------ autenticación (API REST de boletas)

    /// <summary>Token para la API de boletas: es otro servicio del SII y su token no sirve para los servicios de DTE (ni al revés).</summary>
    public async Task<string> AuthenticateBoletaAsync(Certificate certificate, CancellationToken ct = default)
    {
        var semilla = LeerValor(await Llamar("GET", Api("/recursos/v1/boleta.electronica.semilla"), ["Accept: application/xml"], null, null, ct), "SEMILLA");
        var respuesta = await Llamar("POST", Api("/recursos/v1/boleta.electronica.token"), ["Content-Type: application/xml", "Accept: application/xml"], SolicitudToken(semilla, certificate), null, ct);

        return LeerValor(respuesta, "TOKEN");
    }

    // ------------------------------------------------------------------ envío

    /// <summary>
    /// Sube un <c>EnvioDTE</c> ya armado y firmado (ver <see cref="EnvioDteBuilder.Build"/>). No es un servicio SOAP sino un POST HTTP multipart/form-data, autenticado con
    /// el token vía cookie (manual OI2003_UPDTE_MDE y ejemplo oficial del SII). El <c>trackId</c> sirve para consultar el estado con <see cref="GetUploadStatusAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Si el SII rechaza el envío (status distinto de «0») o la respuesta no se puede interpretar.</exception>
    public async Task<(string TrackId, string Status)> UploadEnvioDteAsync(XmlDocument signedEnvioDte, string token, string rutSender, string rutCompany, CancellationToken ct = default)
    {
        var body = await Subir(Dte("/cgi_dte/UPL/DTEUpload"), signedEnvioDte, token, rutSender, rutCompany, "EnvioDTE.xml", true, ct);

        return ParsearSubida(body);
    }

    /// <summary>Sube un <c>EnvioBOLETA</c> (ver <see cref="EnvioDteBuilder.BuildBoleta"/>) a la API de boletas. Requiere el token de <see cref="AuthenticateBoletaAsync"/>.</summary>
    /// <returns>Respuesta del SII (JSON): <c>trackid</c>, <c>rut_emisor</c>, <c>rut_envia</c>, <c>fecha_recepcion</c>, <c>estado</c>, <c>file</c>.</returns>
    public async Task<JsonElement> UploadBoletaAsync(XmlDocument signedEnvioBoleta, string token, string rutSender, string rutCompany, CancellationToken ct = default)
    {
        var body = await Subir(Boleta("/recursos/v1/boleta.electronica.envio"), signedEnvioBoleta, token, rutSender, rutCompany, "EnvioBOLETA.xml", false, ct);
        var json = LeerJson(body, "El SII no aceptó el envío de boletas o respondió algo inesperado");
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("trackid", out _))
        {
            throw new InvalidOperationException("El SII no aceptó el envío de boletas o respondió algo inesperado: " + Recortar(body));
        }

        return json;
    }

    // ------------------------------------------------------------------ consultas de estado

    /// <summary>Estado de un envío de DTE (servicio QueryEstUp).</summary>
    public async Task<SiiEstado> GetUploadStatusAsync(string rutCompany, string trackId, string token, CancellationToken ct = default)
    {
        var (numero, dv) = SepararRut(rutCompany);
        var interior = ExtraerRetorno(
            await Soap(Dte("/DTEWS/QueryEstUp.jws"), "getEstUp", [("RutCompania", numero), ("DvCompania", dv), ("TrackId", trackId), ("Token", token)], ct),
            "getEstUpReturn");

        return ParsearEstado(interior, EstadosEnvio, trackId);
    }

    /// <summary>Estado de un documento ya enviado (servicio QueryEstDte): si el SII lo recibió y si los datos coinciden.</summary>
    /// <param name="fechaEmision">AAAA-MM-DD (se envía al SII como DDMMAAAA).</param>
    public async Task<SiiEstado> GetDteStatusAsync(string rutConsultante, string rutCompany, string rutReceptor, int tipoDte, int folio, string fechaEmision, long monto, string token, CancellationToken ct = default)
    {
        if (!DateTime.TryParseExact(fechaEmision, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f))
        {
            throw new ArgumentException("La fecha de emisión debe tener el formato AAAA-MM-DD.");
        }

        var (rc, dvc) = SepararRut(rutConsultante);
        var (re, dve) = SepararRut(rutCompany);
        var (rr, dvr) = SepararRut(rutReceptor);
        var interior = ExtraerRetorno(
            await Soap(Dte("/DTEWS/QueryEstDte.jws"), "getEstDte",
            [
                ("RutConsultante", rc), ("DvConsultante", dvc), ("RutCompania", re), ("DvCompania", dve), ("RutReceptor", rr), ("DvReceptor", dvr),
                ("TipoDte", tipoDte.ToString(CultureInfo.InvariantCulture)), ("FolioDte", folio.ToString(CultureInfo.InvariantCulture)),
                ("FechaEmisionDte", f.ToString("ddMMyyyy", CultureInfo.InvariantCulture)), ("MontoDte", monto.ToString(CultureInfo.InvariantCulture)), ("Token", token),
            ], ct),
            "getEstDteReturn");

        return ParsearEstado(interior, EstadosDte, string.Empty);
    }

    /// <summary>Estado de un envío de boletas (API REST). Requiere el token de <see cref="AuthenticateBoletaAsync"/>. Respuesta del SII (JSON): <c>estado</c>, <c>estadistica</c>, <c>detalle_rep_rech</c>...</summary>
    public async Task<JsonElement> GetBoletaUploadStatusAsync(string rutEmisor, string trackId, string token, CancellationToken ct = default)
    {
        var (numero, dv) = SepararRut(rutEmisor);
        var body = await Llamar("GET", Api($"/recursos/v1/boleta.electronica.envio/{numero}-{dv}-{Uri.EscapeDataString(trackId)}"), ["Accept: application/json", "Cookie: TOKEN=" + token], null, null, ct);

        return LeerJson(body, "El SII respondió algo inesperado al consultar el envío de boletas");
    }

    // ------------------------------------------------------------------ detalle interno

    private string Dte(string ruta) => Hosts[_ambiente].Dte + ruta;

    private string Api(string ruta) => Hosts[_ambiente].Api + ruta;

    private string Boleta(string ruta) => Hosts[_ambiente].Boleta + ruta;

    private string SolicitudToken(string semilla, Certificate certificate) => XmlIo.ToXml(_tokenSigner.Sign(semilla, certificate), declaracion: false);

    private Task<string> Subir(string url, XmlDocument envio, string token, string rutSender, string rutCompany, string nombre, bool comoNavegadorAntiguo, CancellationToken ct)
    {
        var (sn, sdv) = SepararRut(rutSender);
        var (cn, cdv) = SepararRut(rutCompany);

        // Sin este User-Agent, DTEUpload responde una página HTML de error genérica en vez del XML <RECEPCIONDTE>: es un mecanismo antiguo documentado en el
        // propio manual del SII (OI2003_UPDTE_MDE, figura 2.3: el parámetro «PROG 1.0» pide la salida en XML).
        string[] cabeceras = comoNavegadorAntiguo
            ? ["Cookie: TOKEN=" + token, "User-Agent: Mozilla/4.0 (compatible; PROG 1.0; Windows NT 5.0; YComp 5.0.2.4)"]
            : ["Cookie: TOKEN=" + token, "Accept: application/json"];

        return Llamar("POST", url, cabeceras, null,
        [
            new("rutSender", sn), new("dvSender", sdv), new("rutCompany", cn), new("dvCompany", cdv),
            new("archivo", XmlIo.ToXml(envio), nombre, "text/xml"),
        ], ct);
    }

    private static (string TrackId, string Status) ParsearSubida(string respuesta)
    {
        XmlDocument doc;
        try
        {
            doc = XmlIo.ParseText(respuesta);
        }
        catch (XmlException)
        {
            throw new InvalidOperationException("La respuesta del SII al subir el EnvioDTE no es un XML válido: " + Recortar(respuesta));
        }

        var status = doc.GetElementsByTagName("STATUS") is { Count: > 0 } s ? s[0]!.InnerText : null;
        if (status != "0")
        {
            throw new InvalidOperationException($"El SII rechazó el envío del DTE (STATUS={status}): " + (status is not null && UploadStatus.TryGetValue(status, out var g) ? g : "Error desconocido."));
        }

        if (doc.GetElementsByTagName("TRACKID") is not { Count: > 0 } t)
        {
            throw new InvalidOperationException("El SII aceptó el envío pero no devolvió un TRACKID.");
        }

        return (t[0]!.InnerText, status);
    }

    private static SiiEstado ParsearEstado(string interior, IReadOnlyDictionary<string, string> descripciones, string trackId)
    {
        XmlDocument doc;
        try
        {
            doc = XmlIo.ParseText(interior);
        }
        catch (XmlException)
        {
            throw new InvalidOperationException("El SII devolvió una respuesta que no se pudo interpretar: " + Recortar(interior));
        }

        string? Texto(string tag) => doc.GetElementsByTagName(tag) is { Count: > 0 } n ? n[0]!.InnerText.Trim() : null;
        int? Entero(string tag) => Texto(tag) is { Length: > 0 } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;

        var estado = Texto("ESTADO") ?? string.Empty;
        if (estado.Length == 0)
        {
            throw new InvalidOperationException("La respuesta del SII no informa un ESTADO: " + Recortar(interior));
        }

        var glosa = Texto("GLOSA") ?? string.Empty;

        return new SiiEstado(
            estado,
            descripciones.TryGetValue(estado, out var d) ? d : (glosa.Length > 0 ? glosa : "Estado desconocido."),
            glosa,
            Texto("TRACKID") ?? trackId,
            Entero("ACEPTADOS"),
            Entero("RECHAZADOS"),
            Entero("REPAROS"),
            interior);
    }

    private static (string Numero, string Dv) SepararRut(string rut)
    {
        var partes = Rut.ToDteFormat(rut).Split('-');

        return (partes[0], partes[1]);
    }

    private async Task<string> Soap(string url, string operacion, (string Nombre, string Valor)[] parametros, CancellationToken ct)
    {
        const string soapNs = "http://schemas.xmlsoap.org/soap/envelope/";
        var doc = new XmlDocument();
        var envelope = doc.CreateElement("soapenv", "Envelope", soapNs);
        doc.AppendChild(envelope);
        var body = doc.CreateElement("soapenv", "Body", soapNs);
        envelope.AppendChild(body);
        var op = doc.CreateElement(string.Empty, operacion, "http://DefaultNamespace");
        body.AppendChild(op);
        foreach (var (nombre, valor) in parametros)
        {
            var p = doc.CreateElement(string.Empty, nombre, "http://DefaultNamespace");
            p.AppendChild(doc.CreateTextNode(valor));
            op.AppendChild(p);
        }

        return await Llamar("POST", url, ["Content-Type: text/xml; charset=utf-8", "SOAPAction: \"\""], doc.OuterXml, null, ct);
    }

    private async Task<string> Llamar(string metodo, string url, IReadOnlyList<string> cabeceras, string? cuerpo, IReadOnlyList<SiiFormField>? formulario, CancellationToken ct)
    {
        var r = await _transporte(new SiiRequest(metodo, url, cabeceras, cuerpo, formulario), ct);
        if (r.Status >= 400)
        {
            var detalle = r.Body.Length > 0 ? ": " + Recortar(System.Text.RegularExpressions.Regex.Replace(r.Body, "<[^>]+>", string.Empty).Trim()) : ".";
            throw new InvalidOperationException($"El SII respondió con un error HTTP {r.Status} ({url}){detalle}");
        }

        return r.Body;
    }

    /// <summary>Transporte por defecto (HttpClient).</summary>
    private static async Task<SiiResponse> Enviar(SiiRequest req, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(new HttpMethod(req.Method), req.Url);
        HttpContent? contenido = null;
        if (req.Form is not null)
        {
            var multipart = new MultipartFormDataContent();
            foreach (var f in req.Form)
            {
                if (f.FileName is null)
                {
                    multipart.Add(new StringContent(f.Content), f.Name);
                }
                else
                {
                    var bytes = new ByteArrayContent(Encoding.Latin1.GetBytes(f.Content)); // los documentos del SII viajan en ISO-8859-1
                    bytes.Headers.ContentType = MediaTypeHeaderValue.Parse(f.ContentType ?? "application/octet-stream");
                    multipart.Add(bytes, f.Name, f.FileName);
                }
            }

            contenido = multipart;
        }
        else if (req.Body is not null)
        {
            contenido = new StringContent(req.Body, Encoding.UTF8);
        }

        msg.Content = contenido;
        foreach (var h in req.Headers)
        {
            var i = h.IndexOf(':', StringComparison.Ordinal);
            var nombre = h[..i];
            var valor = h[(i + 1)..].Trim();
            if (nombre.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (msg.Content is not null && req.Form is null)
                {
                    msg.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(valor);
                }
            }
            else
            {
                msg.Headers.TryAddWithoutValidation(nombre, valor);
            }
        }

        using var resp = await Http.SendAsync(msg, ct);

        return new SiiResponse((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static string ExtraerRetorno(string soap, string nombreRetorno)
    {
        XmlDocument doc;
        try
        {
            doc = XmlIo.ParseText(soap);
        }
        catch (XmlException)
        {
            throw new InvalidOperationException("La respuesta del SII no es un XML válido: " + Recortar(soap));
        }

        var nodos = doc.GetElementsByTagName(nombreRetorno);
        if (nodos.Count == 0)
        {
            if (doc.GetElementsByTagName("faultstring") is { Count: > 0 } fault)
            {
                throw new InvalidOperationException("El SII rechazó la solicitud: " + fault[0]!.InnerText);
            }

            throw new InvalidOperationException($"La respuesta del SII no tiene el elemento esperado <{nombreRetorno}>.");
        }

        return nodos[0]!.InnerText;
    }

    /// <summary>Las respuestas del SII (semilla, token) vienen envueltas en SII:RESPUESTA: ESTADO «00» es éxito y cualquier otro código, un rechazo.</summary>
    private static string LeerValor(string interior, string tag)
    {
        XmlDocument doc;
        try
        {
            doc = XmlIo.ParseText(interior);
        }
        catch (XmlException)
        {
            throw new InvalidOperationException("El SII devolvió una respuesta que no se pudo interpretar: " + Recortar(interior));
        }

        var estado = doc.GetElementsByTagName("ESTADO") is { Count: > 0 } e ? e[0]!.InnerText : null;
        if (doc.GetElementsByTagName(tag) is { Count: > 0 } v && (estado is null || estado == "00"))
        {
            return v[0]!.InnerText;
        }

        var glosa = doc.GetElementsByTagName("GLOSA") is { Count: > 0 } g ? g[0]!.InnerText : "sin detalle";

        throw new InvalidOperationException($"El SII rechazó la solicitud (ESTADO={estado ?? "desconocido"}): {glosa}");
    }

    private static JsonElement LeerJson(string cuerpo, string error)
    {
        try
        {
            return JsonDocument.Parse(cuerpo).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(error + ": " + Recortar(cuerpo));
        }
    }

    private static string Recortar(string s) => s.Length <= 300 ? s : s[..300];
}
