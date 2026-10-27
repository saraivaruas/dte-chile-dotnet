using System.Xml;
using DteChile;

namespace DteChile.Tests;

public class FacturadorTests
{
    private static readonly Dictionary<string, object?> Fecha = new() { ["fechaEmision"] = "2026-09-08" };

    private static (Facturador F, Dictionary<int, (string Xml, Caf Caf, string PublicPem)> Cafs) Crear(int[] tipos, SiiClient? sii = null, int hasta = 100)
    {
        var f = new Facturador(
            new Emisor("76.123.456-0", "Inge Innova SpA", "Servicios de Ingeniería", 711001, "Av. Siempre Viva 123", "Providencia", "Santiago"),
            Fixtures.Cert(), new MemoriaFolios(), SiiAmbiente.Certificacion, "2026-01-01", 0, sii);
        var cafs = new Dictionary<int, (string, Caf, string)>();
        foreach (var t in tipos)
        {
            cafs[t] = Fixtures.NewCaf(t, 1, hasta);
            f.AgregarCaf(cafs[t].Item2);
        }

        return (f, cafs);
    }

    private static Receptor Cliente() => new("77.111.222-6", "Cliente de Prueba Ltda", "Comercio", "Calle Falsa 456", "Las Condes");

    private static byte[] Bytes(XmlDocument d) => XmlIo.ToBytes(d);

    [Fact]
    public void Factura_sale_timbrada_firmada_con_folio_automatico_y_valida_contra_el_XSD()
    {
        var (f, cafs) = Crear([33]);
        var d1 = f.Factura(Cliente(), [new Linea("Consultoría técnica", 10, 25000)], Fecha);
        var d2 = f.Factura(Cliente(), [new Linea("Consultoría técnica", 1, 1000)], Fecha);
        Assert.Equal([1, 2], new[] { d1.Folio, d2.Folio });
        Assert.Equal(297500, d1.MontoTotal);
        Assert.Equal("76123456-0", d1.RutEmisor);
        Verify.Xsd(Bytes(d1.Xml), "dte/DTE_v10.xsd");
        Verify.Ted(Bytes(d1.Xml), cafs[33].PublicPem);
        Verify.Xsd(Bytes(f.Sobre([d1, d2])), "dte/EnvioDTE_v10.xsd");
        Assert.Contains("<svg", d1.TimbreSvg());
    }

    [Fact]
    public void La_fecha_de_emision_por_defecto_es_hoy_en_Chile()
    {
        var (f, _) = Crear([33]);
        var hoy = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Santiago")).ToString("yyyy-MM-dd");
        Assert.Equal(hoy, f.Factura(Cliente(), [new Linea("Servicio", 1, 1000)]).FechaEmision);
    }

    [Fact]
    public void Exenta_nota_de_credito_nota_de_debito_y_guia_cumplen_el_esquema()
    {
        var (f, _) = Crear([34, 61, 56, 52]);
        Referencia[] refs = [new(33, 1, "2026-09-08", Referencia.Anula, "Anula factura")];
        var docs = new[]
        {
            f.FacturaExenta(Cliente(), [new Linea("Capacitación exenta", 1, 50000, true)], Fecha),
            f.NotaCredito(Cliente(), [new Linea("Consultoría técnica", 10, 25000)], refs, Fecha),
            f.NotaDebito(Cliente(), [new Linea("Intereses", 1, 5000)], refs, Fecha),
            f.GuiaDespacho(Cliente(), [new Linea("Equipo", 2, 10000)], 1, Fecha),
        };
        foreach (var d in docs)
        {
            Verify.Xsd(Bytes(d.Xml), "dte/DTE_v10.xsd");
        }

        Assert.Equal([34, 61, 56, 52], docs.Select(d => d.Tipo));
        Verify.Xsd(Bytes(f.Sobre(docs)), "dte/EnvioDTE_v10.xsd");
    }

    [Fact]
    public void Boleta_con_IVA_incluido_receptor_generico_y_su_propio_sobre()
    {
        var (f, _) = Crear([39, 41, 33]);
        var b = f.Boleta([new Linea("Café", 2, 2500), new Linea("Pan", 1, 1190)], null, Fecha);
        Assert.Equal(6190, b.MontoTotal);
        Assert.Equal("66666666-6", b.RutReceptor);
        Verify.Xsd(Bytes(f.Sobre([b])), "envio_bol/EnvioBOLETA_v11.xsd");
        Assert.Equal(DocumentType.BoletaNoAfectaExenta, f.BoletaExenta([new Linea("Libro", 1, 9990, true)], null, Fecha).Tipo);
        var factura = f.Factura(Cliente(), [new Linea("X", 1, 1000)], Fecha);
        Assert.Contains("aparte", Assert.Throws<ArgumentException>(() => f.Sobre([b, factura])).Message);
    }

    [Fact]
    public void Errores_claros_sin_CAF_nota_sin_referencia_folios_agotados_CAF_ajeno_y_datos_invalidos()
    {
        var (f, _) = Crear([33, 61], hasta: 2);
        Assert.Contains("No hay un CAF cargado", Assert.Throws<InvalidOperationException>(() => f.GuiaDespacho(Cliente(), [new Linea("X", 1, 1)], 1)).Message);
        Assert.Contains("referencia", Assert.Throws<ArgumentException>(() => f.NotaCredito(Cliente(), [new Linea("X", 1, 1000)], [])).Message);
        f.Factura(Cliente(), [new Linea("X", 1, 1000)]);
        f.Factura(Cliente(), [new Linea("X", 1, 1000)]);
        Assert.Contains("Se agotaron los folios", Assert.Throws<InvalidOperationException>(() => f.Factura(Cliente(), [new Linea("X", 1, 1000)])).Message);
        Assert.Contains("al menos una línea", Assert.Throws<ArgumentException>(() => f.Factura(Cliente(), [])).Message);
        Assert.Contains("no es válido", Assert.Throws<ArgumentException>(() => new Receptor("12345678-0", "X")).Message);
        Assert.Contains("cantidad", Assert.Throws<ArgumentException>(() => new Linea("X", 0, 100)).Message);
        var otro = Fixtures.NewCaf(33, 1, 10).Xml.Replace("76123456-0", "99999999-9");
        Assert.Contains("otro contribuyente", Assert.Throws<ArgumentException>(() => f.AgregarCaf(Caf.FromXml(otro))).Message);
    }

    [Fact]
    public void ArchivoFolios_persiste_el_avance_entre_instancias()
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"folios_{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(5, new ArchivoFolios(ruta).Siguiente(33, 5, 10));
            Assert.Equal(6, new ArchivoFolios(ruta).Siguiente(33, 5, 10));
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public async Task Enviar_autentica_una_vez_sube_el_sobre_y_devuelve_el_trackId()
    {
        var log = new List<SiiRequest>();
        var cola = new Queue<string>([
            Soap("getSeed", "<SII:RESPUESTA xmlns:SII=\"http://www.sii.cl/XMLSchema\"><SII:RESP_HDR><ESTADO>00</ESTADO></SII:RESP_HDR><SII:RESP_BODY><SEMILLA>123</SEMILLA></SII:RESP_BODY></SII:RESPUESTA>"),
            Soap("getToken", "<SII:RESPUESTA xmlns:SII=\"http://www.sii.cl/XMLSchema\"><SII:RESP_HDR><ESTADO>00</ESTADO></SII:RESP_HDR><SII:RESP_BODY><TOKEN>TKN</TOKEN></SII:RESP_BODY></SII:RESPUESTA>"),
            "<RECEPCIONDTE><STATUS>0</STATUS><TRACKID>555</TRACKID></RECEPCIONDTE>",
            "<RECEPCIONDTE><STATUS>0</STATUS><TRACKID>556</TRACKID></RECEPCIONDTE>"]);
        var sii = new SiiClient(SiiAmbiente.Certificacion, null, (req, _) =>
        {
            log.Add(req);

            return Task.FromResult(new SiiResponse(200, cola.Count > 0 ? cola.Dequeue() : "sin respuesta"));
        });
        var (f, _) = Crear([33], sii);
        var d = f.Factura(Cliente(), [new Linea("Servicio", 1, 1000)], Fecha);
        Assert.Equal("555", (await f.EnviarAsync([d])).TrackId);
        Assert.Equal("556", (await f.EnviarAsync([d])).TrackId);
        Assert.Equal(4, log.Count);
        Assert.Contains("Cookie: TOKEN=TKN", log[2].Headers);
    }

    private static string Soap(string nombre, string interior)
        => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\"><soapenv:Body><{nombre} xmlns=\"http://DefaultNamespace\"><{nombre}Return>"
           + System.Security.SecurityElement.Escape(interior) + $"</{nombre}Return></{nombre}></soapenv:Body></soapenv:Envelope>";
}
