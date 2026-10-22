using System.Text;
using System.Xml;
using DteChile;

namespace DteChile.Tests;

using Map = Dictionary<string, object?>;

internal static class B
{
    public static Map M(params (string, object?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    public static List<object?> L(params object?[] items) => [.. items];

    public static Caratula Carat => new("76123456-0", "11111111-1", "2026-01-01", 0);

    public static Map Boleta(int folio = 1, Action<Map>? cambios = null)
    {
        var d = M(
            ("folio", folio), ("fechaEmision", "2026-09-08"),
            ("emisor", M(("rut", "76.123.456-0"), ("razonSocial", "Inge Innova SpA"), ("giro", "Servicios"), ("direccion", "Av. Siempre Viva 123"), ("comuna", "Providencia"), ("ciudad", "Santiago"))),
            ("items", L(M(("nombre", "Café con leche"), ("cantidad", 2), ("precioUnitario", 2500)), M(("nombre", "Pan de pascua"), ("cantidad", 1), ("precioUnitario", 1190)))));
        cambios?.Invoke(d);

        return d;
    }

    public static (XmlDocument Firmado, (string Xml, Caf Caf, string PublicPem) Caf, Certificate Cert) BoletaFirmada(Map datos, int tipo = 39)
    {
        var caf = Fixtures.NewCaf(tipo, 1, 100);
        var cert = Fixtures.Cert();
        var doc = new BoletaBuilder().Build(tipo, datos);
        var monto = new DteBuilder().MontoTotal(doc);
        new DteBuilder().AttachTed(doc, new TedSigner().Sign(caf.Caf, tipo, new Map(datos) { ["montoTotal"] = monto }));

        return (new DteSigner().Sign(doc, cert), caf, cert);
    }

    public static int V(XmlDocument d, string tag) => int.Parse(d.GetElementsByTagName(tag)[0]!.InnerText);
}

public class BoletaTests
{
    [Fact]
    public void Boleta_39_el_IVA_se_descompone_del_total_y_cumple_el_esquema_del_SII()
    {
        var (doc, caf, cert) = B.BoletaFirmada(B.Boleta());
        Assert.Equal([6190, 5202, 988], [B.V(doc, "MntTotal"), B.V(doc, "MntNeto"), B.V(doc, "IVA")]);
        Assert.Equal("66666666-6", doc.GetElementsByTagName("RUTRecep")[0]!.InnerText);
        Verify.Ted(Pipeline.Bytes(doc), caf.PublicPem);
        var env = new EnvioDteBuilder().BuildBoleta([doc], B.Carat, cert);
        Verify.Xsd(Pipeline.Bytes(env), "envio_bol/EnvioBOLETA_v11.xsd");
        Assert.Equal(2, Verify.XmlDsig(Pipeline.Bytes(env), cert.CertificatePem));
    }

    [Fact]
    public void Boleta_39_con_preciosNetos_el_IVA_se_suma()
    {
        var (doc, _, _) = B.BoletaFirmada(B.Boleta(3, d => { d["preciosNetos"] = true; d["items"] = B.L(B.M(("nombre", "Servicio"), ("monto", 10000))); }));
        Assert.Equal([10000, 1900, 11900], [B.V(doc, "MntNeto"), B.V(doc, "IVA"), B.V(doc, "MntTotal")]);
        Assert.Equal("2", doc.GetElementsByTagName("IndMntNeto")[0]!.InnerText);
    }

    [Fact]
    public void Boleta_41_exenta_todo_es_exento_y_sin_IVA()
    {
        var (doc, _, cert) = B.BoletaFirmada(B.Boleta(1, d => d["items"] = B.L(B.M(("nombre", "Libro"), ("cantidad", 1), ("precioUnitario", 15000)))), 41);
        Assert.Equal([15000, 15000], [B.V(doc, "MntExe"), B.V(doc, "MntTotal")]);
        Assert.Equal(0, doc.GetElementsByTagName("IVA").Count + doc.GetElementsByTagName("MntNeto").Count);
        Verify.Xsd(Pipeline.Bytes(new EnvioDteBuilder().BuildBoleta([doc], B.Carat, cert)), "envio_bol/EnvioBOLETA_v11.xsd");
    }

    [Fact]
    public void Boleta_mezcla_de_items_afectos_y_exentos_y_descuento_global()
    {
        var (doc, _, _) = B.BoletaFirmada(B.Boleta(1, d =>
        {
            d["items"] = B.L(B.M(("nombre", "Afecto"), ("monto", 11900)), B.M(("nombre", "Exento"), ("monto", 5000), ("exento", true)));
            d["descuentosRecargos"] = B.L(B.M(("tipo", "D"), ("valorTipo", "%"), ("valor", 10)));
        }));
        // El descuento global sin IndExeDR solo afecta a lo afecto: 10.710 / 1,19 = 9.000.
        Assert.Equal([9000, 1710, 5000, 15710], [B.V(doc, "MntNeto"), B.V(doc, "IVA"), B.V(doc, "MntExe"), B.V(doc, "MntTotal")]);
    }

    [Fact]
    public void Boleta_el_EnvioBOLETA_rechaza_documentos_que_no_son_boletas_y_BoletaBuilder_otros_tipos()
    {
        var (factura, _, cert) = Pipeline.Dte(Fixtures.Factura());
        Assert.Contains("solo admite boletas", Assert.Throws<ArgumentException>(() => new EnvioDteBuilder().BuildBoleta([factura], B.Carat, cert)).Message);
        Assert.Contains("DteBuilder", Assert.Throws<ArgumentException>(() => new BoletaBuilder().Build(33, B.Boleta())).Message);
        Assert.Contains("no es un valor permitido", Assert.Throws<ArgumentException>(() => new BoletaBuilder().Build(39, B.Boleta(1, d => d["indicadorServicio"] = 9))).Message);
    }
}

public class DocumentosTests
{
    private const string Declaracion = "El acuse de recibo que se declara en este acto, de acuerdo a lo dispuesto en la letra b) del Art. 4, y la letra c) del Art. 5 de la Ley 19.983, acredita que la entrega de mercaderias o servicio(s) prestado(s) ha(n) sido recibido(s).";

    private static Map CaratulaLibro(string operacion = "VENTA") => B.M(
        ("RutEmisorLibro", "76123456-0"), ("RutEnvia", "11111111-1"), ("PeriodoTributario", "2026-09"), ("FchResol", "2026-01-01"), ("NroResol", 0),
        ("TipoOperacion", operacion), ("TipoLibro", "MENSUAL"), ("TipoEnvio", "TOTAL"));

    [Fact]
    public void Libro_de_ventas_se_arma_se_firma_y_cumple_el_esquema_oficial()
    {
        var cert = Fixtures.Cert();
        var doc = new SiiDocuments().LibroCompraVenta(B.M(("EnvioLibro", B.M(
            ("Caratula", CaratulaLibro()),
            ("ResumenPeriodo", B.M(("TotalesPeriodo", B.L(B.M(("TpoDoc", 33), ("TotDoc", 1), ("TotMntExe", 0), ("TotMntNeto", 250000), ("TotMntIVA", 47500), ("TotMntTotal", 297500)))))),
            ("Detalle", B.L(B.M(("TpoDoc", 33), ("NroDoc", 1), ("FchDoc", "2026-09-08"), ("RUTDoc", "77111222-6"), ("RznSoc", "Cliente de Prueba Ltda"), ("MntNeto", 250000), ("MntIVA", 47500), ("MntTotal", 297500))))))), cert);
        Verify.Xsd(Pipeline.Bytes(doc), "iecv/LibroCV_v10.xsd");
        Assert.Equal(1, Verify.XmlDsig(Pipeline.Bytes(doc), cert.CertificatePem));
    }

    [Fact]
    public void Libro_de_compras_tipo_de_operacion_invalido_se_rechaza_con_el_campo()
    {
        var ex = Assert.Throws<ArgumentException>(() => new SiiDocuments().LibroCompraVenta(B.M(("EnvioLibro", B.M(("Caratula", CaratulaLibro("ARRIENDO"))))), Fixtures.Cert()));
        Assert.Contains("TipoOperacion", ex.Message);
    }

    [Fact]
    public void Libro_de_guias_se_arma_se_firma_y_cumple_el_esquema_oficial()
    {
        var doc = new SiiDocuments().LibroGuia(B.M(("EnvioLibro", B.M(
            ("Caratula", B.M(("RutEmisorLibro", "76123456-0"), ("RutEnvia", "11111111-1"), ("PeriodoTributario", "2026-09"), ("FchResol", "2026-01-01"), ("NroResol", 0), ("TipoLibro", "ESPECIAL"), ("TipoEnvio", "TOTAL"), ("FolioNotificacion", 1))),
            ("ResumenPeriodo", B.M(("TotFolAnulado", 0), ("TotGuiaAnulada", 0), ("TotGuiaVenta", 1), ("TotMntGuiaVta", 1000), ("TotTraslado", B.L(B.M(("TpoTraslado", 2), ("CantGuia", 1), ("MntGuia", 0)))))),
            ("Detalle", B.L(B.M(("Folio", 1), ("TpoOper", 1), ("FchDoc", "2026-09-08"), ("RUTDoc", "77111222-6"), ("RznSoc", "Cliente"), ("MntNeto", 840), ("TasaImp", 19), ("IVA", 160), ("MntTotal", 1000))))))), Fixtures.Cert());
        Verify.Xsd(Pipeline.Bytes(doc), "lgd/LibroGuia_v10.xsd");
    }

    [Fact]
    public void Consumo_de_folios_RCOF_se_arma_se_firma_y_cumple_el_esquema_oficial()
    {
        var cert = Fixtures.Cert();
        var doc = new SiiDocuments().ConsumoFolios(B.M(("DocumentoConsumoFolios", B.M(
            ("Caratula", B.M(("RutEmisor", "76123456-0"), ("RutEnvia", "11111111-1"), ("FchResol", "2026-01-01"), ("NroResol", 0), ("FchInicio", "2026-09-08"), ("FchFinal", "2026-09-08"), ("SecEnvio", 1))),
            ("Resumen", B.L(B.M(("TipoDocumento", 39), ("MntNeto", 5202), ("MntIva", 988), ("TasaIVA", 19), ("MntExento", 0), ("MntTotal", 6190), ("FoliosEmitidos", 3), ("FoliosAnulados", 1), ("FoliosUtilizados", 2),
                ("RangoUtilizados", B.L(B.M(("Inicial", 1), ("Final", 2)))), ("RangoAnulados", B.L(B.M(("Inicial", 3), ("Final", 3)))))))))), cert);
        Verify.Xsd(Pipeline.Bytes(doc), "consumo_folios/ConsumoFolio_v10.xsd");
        Assert.Equal(1, Verify.XmlDsig(Pipeline.Bytes(doc), cert.CertificatePem));
    }

    [Fact]
    public void Libro_de_boletas_se_arma_se_firma_y_cumple_el_esquema_oficial()
    {
        var cert = Fixtures.Cert();
        var doc = new SiiDocuments().LibroBoleta(B.M(("EnvioLibro", B.M(
            ("Caratula", B.M(("RutEmisorLibro", "76123456-0"), ("RutEnvia", "11111111-1"), ("PeriodoTributario", "2026-09"), ("FchResol", "2026-01-01"), ("NroResol", 0), ("TipoLibro", "ESPECIAL"), ("TipoEnvio", "TOTAL"), ("FolioNotificacion", 1))),
            ("ResumenPeriodo", B.M(("TotalesPeriodo", B.L(B.M(("TpoDoc", 39), ("TotalesServicio", B.L(B.M(("TpoServ", 3), ("PeriodoDevengado", "2026-09"), ("TotDoc", 1), ("TotMntExe", 0), ("TotMntNeto", 5202), ("TotMntIVA", 988), ("TasaIVA", 19), ("TotMntTotal", 6190)))))))))))), cert);
        // El esquema oficial LibroBOLETA_v10.xsd declara su <Signature> en el espacio de nombres del SII y no en el de XMLDSig (distinto de todos los demás
        // esquemas): el documento firmado no puede validar completo contra él. Se valida la misma firma con ese espacio de nombres, y se verifica aparte la real.
        var xml = Encoding.Latin1.GetString(Pipeline.Bytes(doc)).Replace("xmlns=\"http://www.w3.org/2000/09/xmldsig#\"", "xmlns=\"http://www.sii.cl/SiiDte\"", StringComparison.Ordinal);
        Verify.Xsd(Encoding.Latin1.GetBytes(xml), "libro_bol/LibroBOLETA_v10.xsd");
        Assert.Equal(1, Verify.XmlDsig(Pipeline.Bytes(doc), cert.CertificatePem));
    }

    [Fact]
    public void Envio_de_recibos_cada_recibo_y_el_conjunto_quedan_firmados_y_validos()
    {
        var cert = Fixtures.Cert();
        Map Recibo(int folio) => B.M(("DocumentoRecibo", B.M(("TipoDoc", 33), ("Folio", folio), ("FchEmis", "2026-09-08"), ("RUTEmisor", "76123456-0"), ("RUTRecep", "77111222-6"), ("MntTotal", 297500),
            ("Recinto", "Av. Calle Falsa 456, Las Condes"), ("RutFirma", "11111111-1"), ("Declaracion", Declaracion))));
        var doc = new SiiDocuments().EnvioRecibos(B.M(("RutResponde", "77111222-6"), ("RutRecibe", "76123456-0"), ("NmbContacto", "Ana Pérez")), [Recibo(1), Recibo(2)], cert);
        Verify.Xsd(Pipeline.Bytes(doc), "19983/EnvioRecibos_v10.xsd");
        Assert.Equal(3, Verify.XmlDsig(Pipeline.Bytes(doc), cert.CertificatePem));
    }

    [Fact]
    public void Respuesta_de_intercambio_recepcion_de_envio_firmada_y_valida()
    {
        var cert = Fixtures.Cert();
        var doc = new SiiDocuments().RespuestaDte(B.M(("Resultado", B.M(
            ("Caratula", B.M(("RutResponde", "77111222-6"), ("RutRecibe", "76123456-0"), ("IdRespuesta", 1), ("NroDetalles", 1))),
            ("RecepcionEnvio", B.L(B.M(
                ("NmbEnvio", "envio.xml"), ("FchRecep", "2026-09-08T10:00:00"), ("CodEnvio", 1), ("EnvioDTEID", "SetDoc"), ("Digest", "YWJjZGVm"),
                ("RutEmisor", "76123456-0"), ("RutReceptor", "77111222-6"), ("EstadoRecepEnv", 0), ("RecepEnvGlosa", "Envio Recibido Conforme"), ("NroDTE", 1),
                ("RecepcionDTE", B.L(B.M(("TipoDTE", 33), ("Folio", 1), ("FchEmis", "2026-09-08"), ("RUTEmisor", "76123456-0"), ("RUTRecep", "77111222-6"), ("MntTotal", 297500), ("EstadoRecepDTE", 0), ("RecepDTEGlosa", "DTE Recibido OK")))))))))), cert);
        Verify.Xsd(Pipeline.Bytes(doc), "ic/RespuestaEnvioDTE_v10.xsd");
        Assert.Equal(1, Verify.XmlDsig(Pipeline.Bytes(doc), cert.CertificatePem));
    }

    [Fact]
    public void Cesion_de_factura_AEC_el_DTE_la_cesion_y_el_archivo_quedan_firmados_y_validos()
    {
        var (dte, _, cert) = Pipeline.Dte(Fixtures.Factura(7));
        var doc = new SiiDocuments().Aec(
            B.M(("RutCedente", "76123456-0"), ("RutCesionario", "77111222-6")),
            dte,
            [B.M(
                ("SeqCesion", 1),
                ("IdDTE", B.M(("TipoDTE", 33), ("RUTEmisor", "76123456-0"), ("RUTReceptor", "77111222-6"), ("Folio", 7), ("FchEmis", "2026-09-08"), ("MntTotal", 297500))),
                ("Cedente", B.M(("RUT", "76123456-0"), ("RazonSocial", "Inge Innova SpA"), ("Direccion", "Av. Siempre Viva 123"), ("eMail", "facturas@ejemplo.cl"), ("RUTAutorizado", B.L(B.M(("RUT", "11111111-1"), ("Nombre", "Persona de Prueba")))))),
                ("Cesionario", B.M(("RUT", "77111222-6"), ("RazonSocial", "Cliente de Prueba Ltda"), ("Direccion", "Calle Falsa 456"), ("eMail", "cesion@ejemplo.cl"))),
                ("MontoCesion", 297500), ("UltimoVencimiento", "2026-10-08"))],
            cert);
        Verify.Xsd(Pipeline.Bytes(doc), "cesion/AEC_v10.xsd");
        Assert.Equal(4, Verify.XmlDsig(Pipeline.Bytes(doc), cert.CertificatePem)); // Documento del DTE, DTE cedido, cesión y AEC
    }
}

public class ReglasPorTipoTests
{
    [Fact]
    public void La_nota_de_credito_exige_referencia_y_la_guia_de_despacho_el_tipo_de_traslado()
    {
        Assert.Contains("referencia", Assert.Throws<ArgumentException>(() => new DteBuilder().Build(61, Fixtures.Factura())).Message);
        Assert.Contains("tipo de traslado", Assert.Throws<ArgumentException>(() => new DteBuilder().Build(52, Fixtures.Factura())).Message);
        new DteBuilder().Build(61, Fixtures.Factura(cambios: d => d["referencias"] = B.L(B.M(("tipoDocumento", 33), ("folio", 7), ("fecha", "2026-09-08"), ("codigo", 1), ("razon", "Anula factura")))));
        new DteBuilder().Build(52, Fixtures.Factura(cambios: d => d["indicadorTraslado"] = 1));
    }
}
