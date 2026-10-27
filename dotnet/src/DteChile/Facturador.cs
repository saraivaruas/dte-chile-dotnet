using System.Globalization;
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
    private readonly string _fechaResolucion;
    private readonly int _numeroResolucion;
    private readonly Dictionary<int, Caf> _cafs = [];
    private string? _tokenDte;
    private string? _tokenBoleta;

    /// <param name="fechaResolucion">Fecha (AAAA-MM-DD) de la resolución del SII que autoriza al emisor.</param>
    /// <param name="numeroResolucion">Número de esa resolución (0 si la autorización fue por internet).</param>
    public Facturador(Emisor emisor, Certificate certificado, IFolioStore folios, SiiAmbiente ambiente = SiiAmbiente.Certificacion,
        string fechaResolucion = "2026-01-01", int numeroResolucion = 0, SiiClient? sii = null)
    {
        _emisor = emisor;
        _cert = certificado;
        _folios = folios;
        _sii = sii ?? new SiiClient(ambiente);
        _fechaResolucion = fechaResolucion;
        _numeroResolucion = numeroResolucion;
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
