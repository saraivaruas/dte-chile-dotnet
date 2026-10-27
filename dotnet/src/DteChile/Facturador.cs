using System.Globalization;
using System.Net.Mail;
using System.Xml;

namespace DteChile;

/// <summary>
/// Fachada de alto nivel: emite y envía documentos tributarios con objetos tipados, en una o dos llamadas.
/// <code>
/// var f = new Facturador(emisor, certificado, new ArchivoFolios("folios.json"), SiiAmbiente.Certificacion);
/// f.AgregarCaf(Caf.FromXml(File.ReadAllText("CAF33.xml")));
/// var doc = f.Factura(receptor, [new Linea("Consultoría", 10, 25000)]);
/// var envio = await f.EnviarAsync([doc]);        // ResultadoEnvio (TrackId)
/// var estado = await f.EstadoEnvioAsync(envio.TrackId);
/// </code>
/// Por debajo usa las clases de la capa baja (<see cref="DteBuilder"/>, <see cref="TedSigner"/>, <see cref="DteSigner"/>, <see cref="EnvioDteBuilder"/>, <see cref="SiiClient"/>), que siguen disponibles.
/// </summary>
public sealed class Facturador
{
    private readonly Emisor _emisor;
    private readonly Certificate _cert;
    private readonly IFolioStore _folios;
    private readonly SiiClient _sii;
    private readonly SiiAmbiente _ambiente;
    private readonly string _fechaResolucion;
    private readonly int _numeroResolucion;
    private readonly Dictionary<int, Caf> _cafs = [];
    private string? _tokenDte;
    private string? _tokenBoleta;
    private int _reintentos = 2;

    /// <summary>Cómo se entrega el correo (por defecto SMTP con System.Net.Mail). Se puede reemplazar, por ejemplo en pruebas.</summary>
    public Func<MailMessage, ConfigSmtp, CancellationToken, Task>? EnviadorCorreo { get; set; }
    private int _esperaMs = 500;

    /// <param name="fechaResolucion">Fecha (AAAA-MM-DD) de la resolución del SII que autoriza al emisor.</param>
    /// <param name="numeroResolucion">Número de esa resolución (0 si la autorización fue por internet).</param>
    public Facturador(Emisor emisor, Certificate certificado, IFolioStore folios, SiiAmbiente ambiente = SiiAmbiente.Certificacion,
        string fechaResolucion = "2026-01-01", int numeroResolucion = 0, SiiClient? sii = null)
    {
        _emisor = emisor;
        _cert = certificado;
        _folios = folios;
        _ambiente = ambiente;
        _sii = sii ?? new SiiClient(ambiente);
        _fechaResolucion = fechaResolucion;
        _numeroResolucion = numeroResolucion;
    }

    /// <summary>
    /// Envía el documento por correo al receptor (o a quien indique): adjunta el XML firmado (el EnvioDTE que se usa para el intercambio entre contribuyentes) y,
    /// salvo que se pida lo contrario, el PDF de la representación impresa.
    /// </summary>
    public async Task EnviarPorCorreoAsync(DocumentoEmitido documento, IReadOnlyList<string> para, ConfigSmtp smtp, OpcionesCorreo? opciones = null, CancellationToken ct = default)
    {
        opciones ??= new OpcionesCorreo();
        if (para.Count == 0)
        {
            throw new ArgumentException("Falta al menos un destinatario.");
        }

        var etiqueta = $"{DocumentType.Label(documento.Tipo)} N° {documento.Folio}";
        using var mensaje = new MailMessage { From = new MailAddress(smtp.Remitente, smtp.NombreRemitente), Subject = opciones.Asunto ?? $"{etiqueta} de {_emisor.RazonSocial}" };
        foreach (var p in para)
        {
            mensaje.To.Add(new MailAddress(p));
        }

        mensaje.Body = opciones.Texto ?? $"Estimados,\n\nAdjuntamos la {etiqueta} emitida por {_emisor.RazonSocial} (RUT {_emisor.Rut}).\n\nSaludos cordiales.";
        var @base = $"{documento.Tipo}-{documento.Folio}";
        mensaje.Attachments.Add(new Attachment(new MemoryStream(System.Text.Encoding.Latin1.GetBytes(XmlIo.ToXml(Sobre([documento])))), $"{@base}.xml", "application/xml"));
        if (opciones.ConPdf)
        {
            mensaje.Attachments.Add(new Attachment(new MemoryStream(Pdf(documento, opciones.Pdf)), $"{@base}.pdf", "application/pdf"));
        }

        await (EnviadorCorreo ?? ((m, c, t) => Smtp.EnviarAsync(m, c, t)))(mensaje, smtp, ct);
    }

    /// <summary>
    /// Representación impresa en PDF (Carta) con el timbre PDF417. Usa la resolución del Facturador para la leyenda del timbre.
    /// </summary>
    /// <returns>Bytes del archivo PDF.</returns>
    public byte[] Pdf(DocumentoEmitido documento, OpcionesPdf? opciones = null)
        => new RepresentacionImpresa().Generar(documento, (opciones ?? new OpcionesPdf()) with { ResolucionNumero = opciones?.ResolucionNumero ?? _numeroResolucion, ResolucionFecha = opciones?.ResolucionFecha ?? _fechaResolucion });

    /// <summary>
    /// Comprueba la conexión con el SII SIN enviar ningún documento ni necesitar CAF: autentica (semilla firmada → token) en el servicio de facturas
    /// y en el de boletas, y dice si el SII aceptó el certificado. No lanza por un fallo del SII: lo informa en el resultado.
    /// </summary>
    public async Task<ResultadoConexion> ProbarConexionAsync(CancellationToken ct = default)
    {
        var errores = new Dictionary<string, string>();
        try
        {
            _tokenDte = await _sii.AuthenticateAsync(_cert, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            errores["facturas"] = e.Message;
        }

        try
        {
            _tokenBoleta = await _sii.AuthenticateBoletaAsync(_cert, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            errores["boletas"] = e.Message;
        }

        return new ResultadoConexion(_ambiente.ToString(), !errores.ContainsKey("facturas"), !errores.ContainsKey("boletas"), _cert.SubjectRut() ?? string.Empty, _cert.NotAfter, errores);
    }

    public Facturador AgregarCaf(Caf caf)
    {
        if (Rut.ToDteFormat(caf.IssuerRut) != Rut.ToDteFormat(_emisor.Rut))
        {
            throw new ArgumentException($"El CAF es de otro contribuyente ({caf.IssuerRut}), no del emisor {_emisor.Rut}.");
        }

        _cafs[caf.DocumentType] = caf;

        return this;
    }

    public DocumentoEmitido Factura(Receptor receptor, IReadOnlyList<Linea> lineas, IDictionary<string, object?>? opciones = null)
        => Emitir(DocumentType.FacturaElectronica, receptor, lineas, null, opciones);

    /// <summary>Factura no afecta o exenta (34).</summary>
    public DocumentoEmitido FacturaExenta(Receptor receptor, IReadOnlyList<Linea> lineas, IDictionary<string, object?>? opciones = null)
        => Emitir(DocumentType.FacturaNoAfectaExenta, receptor, lineas, null, opciones);

    /// <param name="referencias">Al menos una: el documento que se corrige o anula.</param>
    public DocumentoEmitido NotaCredito(Receptor receptor, IReadOnlyList<Linea> lineas, IReadOnlyList<Referencia> referencias, IDictionary<string, object?>? opciones = null)
        => Emitir(DocumentType.NotaCredito, receptor, lineas, referencias, opciones);

    public DocumentoEmitido NotaDebito(Receptor receptor, IReadOnlyList<Linea> lineas, IReadOnlyList<Referencia> referencias, IDictionary<string, object?>? opciones = null)
        => Emitir(DocumentType.NotaDebito, receptor, lineas, referencias, opciones);

    /// <param name="indicadorTraslado">1 operación constituye venta, 2 venta por efectuar, 3 consignación, 4 entrega gratuita, 5 traslado interno, 6 otros traslados no venta, 7 guía de devolución, 8 traslado para exportación, 9 venta para exportación.</param>
    public DocumentoEmitido GuiaDespacho(Receptor receptor, IReadOnlyList<Linea> lineas, int indicadorTraslado, IDictionary<string, object?>? opciones = null)
    {
        var o = opciones is null ? [] : new Dictionary<string, object?>(opciones);
        o["indicadorTraslado"] = indicadorTraslado;

        return Emitir(DocumentType.GuiaDespacho, receptor, lineas, null, o);
    }

    /// <summary>Boleta electrónica (39): precios con IVA incluido; el receptor es opcional.</summary>
    public DocumentoEmitido Boleta(IReadOnlyList<Linea> lineas, Receptor? receptor = null, IDictionary<string, object?>? opciones = null)
        => Emitir(DocumentType.BoletaElectronica, receptor, lineas, null, opciones);

    /// <summary>Boleta no afecta o exenta (41).</summary>
    public DocumentoEmitido BoletaExenta(IReadOnlyList<Linea> lineas, Receptor? receptor = null, IDictionary<string, object?>? opciones = null)
        => Emitir(DocumentType.BoletaNoAfectaExenta, receptor, lineas, null, opciones);

    /// <summary>Construye, timbra y firma un documento de cualquier tipo.</summary>
    /// <param name="opciones">Campos adicionales de la capa baja (fechaEmision, formaPago, descuentosRecargos, transporte, ...). <c>fechaEmision</c> por defecto es hoy en Chile.</param>
    public DocumentoEmitido Emitir(int tipo, Receptor? receptor, IReadOnlyList<Linea> lineas, IReadOnlyList<Referencia>? referencias = null, IDictionary<string, object?>? opciones = null)
    {
        if (lineas.Count == 0)
        {
            throw new ArgumentException("El documento necesita al menos una línea.");
        }

        if (!_cafs.TryGetValue(tipo, out var caf))
        {
            throw new InvalidOperationException($"No hay un CAF cargado para {DocumentType.Label(tipo)} (tipo {tipo}). Use AgregarCaf().");
        }

        var boleta = DocumentType.IsBoleta(tipo);
        if (receptor is null && !boleta)
        {
            throw new ArgumentException("Este tipo de documento exige receptor.");
        }

        var folio = _folios.Siguiente(tipo, caf.FolioFrom, caf.FolioTo);
        var datos = new Dictionary<string, object?>
        {
            ["folio"] = folio,
            ["fechaEmision"] = HoyEnChile(),
            ["emisor"] = _emisor.ToMap(boleta),
            ["items"] = lineas.Select(l => (object?)l.ToMap()).ToList(),
        };
        if (opciones is not null)
        {
            foreach (var (k, v) in opciones)
            {
                datos[k] = v;
            }
        }

        if (receptor is not null)
        {
            datos["receptor"] = receptor.ToMap();
        }

        if (referencias is { Count: > 0 })
        {
            datos["referencias"] = referencias.Select(r => (object?)r.ToMap()).ToList();
        }

        var builder = new DteBuilder();
        var dte = boleta ? new BoletaBuilder().Build(tipo, datos) : builder.Build(tipo, datos);
        var monto = builder.MontoTotal(dte);
        var ted = new TedSigner().Sign(caf, tipo, new Dictionary<string, object?>(datos) { ["montoTotal"] = monto });
        builder.AttachTed(dte, ted);
        var firmado = new DteSigner().Sign(dte, _cert);

        return new DocumentoEmitido(
            tipo, folio, monto, Convert.ToString(datos["fechaEmision"], CultureInfo.InvariantCulture)!, Rut.ToDteFormat(_emisor.Rut),
            receptor is not null ? Rut.ToDteFormat(receptor.Rut) : BoletaBuilder.RutGenerico, firmado, Pdf417Encoder.TedToLatin1(ted));
    }

    /// <summary>Cuántas veces reintentar un envío ante fallos temporales (red, SII caído) y cuánto esperar entre intentos (se duplica cada vez).</summary>
    public Facturador Reintentos(int veces, int esperaMs = 500)
    {
        _reintentos = Math.Max(0, veces);
        _esperaMs = Math.Max(0, esperaMs);

        return this;
    }

    /// <summary>
    /// Emite (construye, timbra y firma) y envía al SII en una sola llamada. No lanza por un fallo del SII o de la red: el resultado trae el estado y qué hacer.
    /// Si el envío no llegó, el documento queda emitido (el folio ya se usó) y se reenvía con <see cref="ReenviarAsync"/>.
    /// </summary>
    public async Task<ResultadoEmision> EmitirYEnviarAsync(int tipo, Receptor? receptor, IReadOnlyList<Linea> lineas, IReadOnlyList<Referencia>? referencias = null,
        IDictionary<string, object?>? opciones = null, CancellationToken ct = default)
        => await ReenviarAsync(Emitir(tipo, receptor, lineas, referencias, opciones), ct);

    /// <summary>Envía un documento ya emitido (por ejemplo, uno que quedó en estado Emitido por una caída del SII), con reintentos ante fallos temporales.</summary>
    public async Task<ResultadoEmision> ReenviarAsync(DocumentoEmitido documento, CancellationToken ct = default)
    {
        var intentos = 0;
        var espera = _esperaMs;
        var renovoToken = false;
        while (true)
        {
            intentos++;
            try
            {
                var envio = await EnviarAsync([documento], ct);

                return new ResultadoEmision(documento, EstadoDocumento.Enviado, envio.TrackId, string.Empty, intentos);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var original = e.Message;
                if (original.Contains("STATUS=5", StringComparison.Ordinal) && !renovoToken)
                {
                    renovoToken = true; // sesión vencida: renovar una vez
                    _tokenDte = _tokenBoleta = null;
                    continue;
                }

                var temporal = MensajesSii.EsTemporal(original);
                if (temporal && intentos <= _reintentos)
                {
                    await Task.Delay(espera, ct);
                    espera *= 2;
                    continue;
                }

                return new ResultadoEmision(documento, temporal ? EstadoDocumento.Emitido : EstadoDocumento.Rechazado, null, MensajesSii.Explicar(original), intentos, original);
            }
        }
    }

    /// <summary>Consulta al SII y devuelve el resultado actualizado (aceptado, con reparos, rechazado o aún en proceso).</summary>
    public async Task<ResultadoEmision> ConsultarAsync(ResultadoEmision resultado, CancellationToken ct = default)
    {
        if (resultado.TrackId is null)
        {
            return resultado;
        }

        string estado;
        int aceptados, rechazados, reparos;
        try
        {
            if (resultado.Documento.EsBoleta)
            {
                var j = await EstadoEnvioBoletaAsync(resultado.TrackId, ct);
                estado = j.TryGetProperty("estado", out var e) ? e.GetString() ?? string.Empty : string.Empty;
                aceptados = rechazados = reparos = 0;
                if (j.TryGetProperty("estadistica", out var est) && est.ValueKind == System.Text.Json.JsonValueKind.Array && est.GetArrayLength() > 0)
                {
                    var x = est[0];
                    aceptados = x.TryGetProperty("aceptados", out var a) ? a.GetInt32() : 0;
                    rechazados = x.TryGetProperty("rechazados", out var r) ? r.GetInt32() : 0;
                    reparos = x.TryGetProperty("reparos", out var p) ? p.GetInt32() : 0;
                }
            }
            else
            {
                var r = await EstadoEnvioAsync(resultado.TrackId, ct);
                estado = r.Estado;
                aceptados = r.Aceptados ?? 0;
                rechazados = r.Rechazados ?? 0;
                reparos = r.Reparos ?? 0;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return resultado.ConEstado(resultado.Estado, MensajesSii.Explicar(e.Message), e.Message);
        }

        if (estado is "RSC" or "RFR" or "RCT" or "RCH")
        {
            return resultado.ConEstado(EstadoDocumento.Rechazado, MensajesSii.Explicar(estado), estado);
        }

        if (estado == "EPR" && rechazados > 0)
        {
            return resultado.ConEstado(EstadoDocumento.Rechazado, $"El SII procesó el envío y rechazó el documento. Revise el detalle en el sitio del SII con el seguimiento {resultado.TrackId}.", estado);
        }

        if (estado == "EPR" && reparos > 0)
        {
            return resultado.ConEstado(EstadoDocumento.ConReparos, "El SII aceptó el documento con reparos (observaciones que no lo invalidan).", estado);
        }

        if (estado == "EPR" && aceptados > 0)
        {
            return resultado.ConEstado(EstadoDocumento.Aceptado, string.Empty, estado);
        }

        return resultado.ConEstado(EstadoDocumento.Enviado, "El SII aún está procesando el envío. Consulte de nuevo en unos minutos.", estado);
    }

    /// <summary>Arma el sobre firmado y lo sube al SII. Todos los documentos deben ser boletas, o ninguno.</summary>
    public async Task<ResultadoEnvio> EnviarAsync(IReadOnlyList<DocumentoEmitido> documentos, CancellationToken ct = default)
    {
        var sobre = Sobre(documentos);
        var boletas = documentos[0].EsBoleta;
        var enviaRut = _cert.SubjectRut() ?? throw new InvalidOperationException("El certificado no trae el RUT del titular (serialNumber).");
        var rutEmisor = Rut.ToDteFormat(_emisor.Rut);
        if (boletas)
        {
            _tokenBoleta ??= await _sii.AuthenticateBoletaAsync(_cert, ct);
            var r = await _sii.UploadBoletaAsync(sobre, _tokenBoleta, enviaRut, rutEmisor, ct);

            return new ResultadoEnvio(r.GetProperty("trackid").ToString(), documentos.Count);
        }

        _tokenDte ??= await _sii.AuthenticateAsync(_cert, ct);
        var (trackId, _) = await _sii.UploadEnvioDteAsync(sobre, _tokenDte, enviaRut, rutEmisor, ct);

        return new ResultadoEnvio(trackId, documentos.Count);
    }

    /// <summary>Sobre firmado (EnvioDTE o EnvioBOLETA) sin subirlo: útil para guardarlo o enviarlo por otro medio.</summary>
    public XmlDocument Sobre(IReadOnlyList<DocumentoEmitido> documentos)
    {
        if (documentos.Count == 0)
        {
            throw new ArgumentException("No hay documentos para enviar.");
        }

        var boletas = documentos[0].EsBoleta;
        if (documentos.Any(d => d.EsBoleta != boletas))
        {
            throw new ArgumentException("Las boletas (39 y 41) van en un envío aparte del resto de los documentos.");
        }

        var caratula = new Caratula(
            Rut.ToDteFormat(_emisor.Rut), _cert.SubjectRut() ?? throw new InvalidOperationException("El certificado no trae el RUT del titular (serialNumber)."),
            _fechaResolucion, _numeroResolucion);
        var xmls = documentos.Select(d => d.Xml).ToList();
        var builder = new EnvioDteBuilder();

        return boletas ? builder.BuildBoleta(xmls, caratula, _cert) : builder.Build(xmls, caratula, _cert);
    }

    /// <summary>Estado de un envío de facturas y demás (las boletas usan <see cref="EstadoEnvioBoletaAsync"/>).</summary>
    public async Task<SiiEstado> EstadoEnvioAsync(string trackId, CancellationToken ct = default)
    {
        _tokenDte ??= await _sii.AuthenticateAsync(_cert, ct);

        return await _sii.GetUploadStatusAsync(Rut.ToDteFormat(_emisor.Rut), trackId, _tokenDte, ct);
    }

    public async Task<System.Text.Json.JsonElement> EstadoEnvioBoletaAsync(string trackId, CancellationToken ct = default)
    {
        _tokenBoleta ??= await _sii.AuthenticateBoletaAsync(_cert, ct);

        return await _sii.GetBoletaUploadStatusAsync(Rut.ToDteFormat(_emisor.Rut), trackId, _tokenBoleta, ct);
    }

    private static string HoyEnChile()
    {
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById("Pacific SA Standard Time"); // nombre de Windows
        }

        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
