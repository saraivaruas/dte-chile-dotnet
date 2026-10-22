using System.Text;
using System.Xml;
using DteChile;

namespace DteChile.Tests;

public class TimbreTests
{
    [Fact]
    public void La_firma_se_verifica_con_la_llave_publica_del_CAF()
    {
        var (firmado, caf, _) = Pipeline.Dte(Fixtures.Factura());
        Verify.Ted(Pipeline.Bytes(firmado), caf.PublicPem);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void La_firma_se_verifica_aunque_el_CAF_venga_con_indentacion_propia_o_sin_ella(bool indentado)
    {
        var caf = Fixtures.NewCaf(33, 1, 100, indentado);
        var (firmado, _, _) = Pipeline.Dte(Fixtures.Factura(), 33, caf);
        Verify.Ted(Pipeline.Bytes(firmado), caf.PublicPem);
    }

    [Fact]
    public void La_firma_se_verifica_con_tildes_y_enie_en_el_primer_item_y_en_el_receptor()
    {
        var datos = Fixtures.Factura(items: [Fixtures.Item("Consultoría técnica año", 1, 1000)], cambios: d => ((Dictionary<string, object?>)d["receptor"]!)["razonSocial"] = "Ñuñoa Ñandú Ltda");
        var (firmado, caf, _) = Pipeline.Dte(datos);
        Verify.Ted(Pipeline.Bytes(firmado), caf.PublicPem);
    }

    [Fact]
    public void La_firma_se_verifica_con_ampersand_en_la_razon_social_del_receptor()
    {
        var (firmado, caf, _) = Pipeline.Dte(Fixtures.Factura(cambios: d => ((Dictionary<string, object?>)d["receptor"]!)["razonSocial"] = "Muebles & Cía"));
        Verify.Ted(Pipeline.Bytes(firmado), caf.PublicPem);
    }

    [Fact]
    public void Folio_fuera_del_rango_o_CAF_de_otro_tipo_se_rechazan_antes_de_firmar()
    {
        Assert.Contains("folio", Assert.Throws<ArgumentException>(() => Pipeline.Dte(Fixtures.Factura(101), 33, Fixtures.NewCaf(33, 1, 100))).Message);
        Assert.Contains("tipo de documento", Assert.Throws<ArgumentException>(() => Pipeline.Dte(Fixtures.Factura(1), 33, Fixtures.NewCaf(34, 1, 100))).Message);
    }
}

public class FirmaXmlDsigTests
{
    [Fact]
    public void Resumen_y_firma_del_DTE_se_verifican_con_la_implementacion_de_Microsoft()
    {
        var (firmado, _, cert) = Pipeline.Dte(Fixtures.Factura());
        Assert.Equal(1, Verify.XmlDsig(Pipeline.Bytes(firmado), cert.CertificatePem));
    }

    [Fact]
    public void La_firma_sigue_valida_con_tildes_enie_y_ampersand()
    {
        var datos = Fixtures.Factura(items: [Fixtures.Item("Diseño año – café", 2, 1500)], cambios: d => ((Dictionary<string, object?>)d["receptor"]!)["razonSocial"] = "Peña & Cía Ñandú");
        var (firmado, _, cert) = Pipeline.Dte(datos);
        Verify.XmlDsig(Pipeline.Bytes(firmado), cert.CertificatePem);
    }

    [Fact]
    public void Alterar_un_monto_despues_de_firmar_invalida_la_firma()
    {
        var (firmado, _, cert) = Pipeline.Dte(Fixtures.Factura());
        var original = Encoding.Latin1.GetString(Pipeline.Bytes(firmado));
        var alterado = original.Replace("<MntTotal>297500</MntTotal>", "<MntTotal>100</MntTotal>", StringComparison.Ordinal);
        Assert.NotEqual(original, alterado);
        Assert.Throws<Xunit.Sdk.TrueException>(() => Verify.XmlDsig(Encoding.Latin1.GetBytes(alterado), cert.CertificatePem));
    }

    [Fact]
    public void Otro_certificado_no_verifica_la_firma()
    {
        var (firmado, _, _) = Pipeline.Dte(Fixtures.Factura());
        Assert.Throws<Xunit.Sdk.TrueException>(() => Verify.XmlDsig(Pipeline.Bytes(firmado), Fixtures.NewCertificate().CertPem));
    }

    [Fact]
    public void Solicitud_de_token_firma_enveloped_valida_con_un_solo_Transform()
    {
        var cert = Fixtures.Cert();
        var doc = new TokenRequestSigner().Sign("123456789012", cert);
        var bytes = XmlIo.ToBytes(doc, declaracion: false);
        Verify.XmlDsig(bytes, cert.CertificatePem);
        var xml = Encoding.Latin1.GetString(bytes);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(xml, "<Transform ").Count);
        Assert.Contains("<Semilla>123456789012</Semilla>", xml);
    }
}

public class SobreTests
{
    private static readonly Caratula Carat = new("76.123.456-0", "11.111.111-1", "2026-01-01", 0);

    [Fact]
    public void Caratula_con_subtotales_y_las_tres_firmas_validas_y_cumple_el_esquema()
    {
        var cert = Fixtures.Cert();
        var caf = Fixtures.NewCaf(33, 1, 100);
        var d1 = Pipeline.Dte(Fixtures.Factura(1), 33, caf, cert).Firmado;
        var d2 = Pipeline.Dte(Fixtures.Factura(2), 33, caf, cert).Firmado;
        var envio = new EnvioDteBuilder().Build([d1, d2], Carat, cert);
        var bytes = Pipeline.Bytes(envio);

        Assert.Equal(3, Verify.XmlDsig(bytes, cert.CertificatePem)); // 2 DTE + el SetDTE
        Verify.Xsd(bytes, "dte/EnvioDTE_v10.xsd");
        var doc = new XmlDocument();
        doc.Load(new MemoryStream(bytes));
        Assert.Equal("2", doc.GetElementsByTagName("NroDTE")[0]!.InnerText);
        Assert.Equal("60803000-K", doc.GetElementsByTagName("RutReceptor")[0]!.InnerText);
        Assert.Equal("76123456-0", doc.GetElementsByTagName("RutEmisor")[0]!.InnerText);
    }

    [Fact]
    public void Documentos_de_exportacion_y_facturas_viajan_juntos_en_un_envio_valido()
    {
        var cert = Fixtures.Cert();
        var fa = Pipeline.Dte(Fixtures.Factura(1), 33, null, cert).Firmado;
        var ex = Pipeline.Dte(Fixtures.Factura(1, d => d["moneda"] = "DOLAR USA", items: [Fixtures.Item("Vino", 1, 10)]), 110, null, cert).Firmado;
        var bytes = Pipeline.Bytes(new EnvioDteBuilder().Build([fa, ex], Carat, cert));
        Verify.XmlDsig(bytes, cert.CertificatePem);
        Verify.Xsd(bytes, "dte/EnvioDTE_v10.xsd");
    }

    [Fact]
    public void Un_envio_sin_documentos_se_rechaza()
    {
        Assert.Contains("al menos un DTE", Assert.Throws<ArgumentException>(() => new EnvioDteBuilder().Build([], Carat, Fixtures.Cert())).Message);
    }
}

public class EsquemaOficialTests
{
    [Theory]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(46)]
    [InlineData(52)]
    public void Documento_se_construye_timbra_firma_y_cumple_el_esquema_oficial(int tipo)
    {
        var datos = Fixtures.Factura(1);
        if (tipo == 34)
        {
            datos["items"] = new List<object?> { Fixtures.Item("Servicio exento", 2, 5000, d => d["exento"] = true) };
        }

        if (tipo == 52)
        {
            datos["indicadorTraslado"] = 1;
        }

        var (firmado, caf, cert) = Pipeline.Dte(datos, tipo);
        var bytes = Pipeline.Bytes(firmado);
        Verify.Ted(bytes, caf.PublicPem);
        Verify.XmlDsig(bytes, cert.CertificatePem);
        Verify.Xsd(bytes, "dte/DTE_v10.xsd");
        Assert.Equal(tipo.ToString(), firmado.GetElementsByTagName("TipoDTE")[0]!.InnerText);
    }

    [Theory]
    [InlineData(56)]
    [InlineData(61)]
    public void Notas_de_debito_y_credito_con_referencia_cumplen_el_esquema_oficial(int tipo)
    {
        var datos = Fixtures.Factura(1, d => d["referencias"] = new List<object?>
        {
            new Dictionary<string, object?> { ["tipoDocumento"] = 33, ["folio"] = 10, ["fecha"] = "2026-09-01", ["codigo"] = 1, ["razon"] = "Anula documento" },
        });
        var (firmado, caf, cert) = Pipeline.Dte(datos, tipo);
        var bytes = Pipeline.Bytes(firmado);
        Verify.Ted(bytes, caf.PublicPem);
        Verify.XmlDsig(bytes, cert.CertificatePem);
        Verify.Xsd(bytes, "dte/DTE_v10.xsd");
        Assert.Contains("<Referencia>", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public void Factura_exenta_todo_va_a_monto_exento_sin_IVA_y_con_IndExe_1()
    {
        var (firmado, caf, cert) = Pipeline.Dte(Fixtures.Factura(1, items: [Fixtures.Item("Asesoría", 3, 1000)]), 34);
        var bytes = Pipeline.Bytes(firmado);
        Verify.Ted(bytes, caf.PublicPem);
        Verify.XmlDsig(bytes, cert.CertificatePem);
        Verify.Xsd(bytes, "dte/DTE_v10.xsd");
        var xml = Encoding.Latin1.GetString(bytes);
        Assert.Contains("<MntExe>3000</MntExe>", xml);
        Assert.DoesNotContain("<IVA>", xml);
        Assert.Contains("<IndExe>1</IndExe>", xml);
    }

    [Theory]
    [InlineData(110)]
    [InlineData(111)]
    [InlineData(112)]
    public void Exportacion_monto_en_moneda_extranjera_con_decimales_cumple_el_esquema_oficial(int tipo)
    {
        var datos = Fixtures.Factura(1, d => d["moneda"] = "DOLAR USA", items: [Fixtures.Item("Vino", 100, 12.5m)]);
        if (tipo != 110)
        {
            datos["referencias"] = new List<object?>
            {
                new Dictionary<string, object?> { ["tipoDocumento"] = 110, ["folio"] = 5, ["fecha"] = "2026-09-01", ["codigo"] = 1, ["razon"] = "Corrige" },
            };
        }

        var (firmado, caf, cert) = Pipeline.Dte(datos, tipo);
        var bytes = Pipeline.Bytes(firmado);
        Verify.Ted(bytes, caf.PublicPem);
        Verify.XmlDsig(bytes, cert.CertificatePem);
        Verify.Xsd(bytes, "dte/DTE_v10.xsd");
        var xml = Encoding.Latin1.GetString(bytes);
        Assert.Contains("<TpoMoneda>DOLAR USA</TpoMoneda>", xml);
        Assert.Contains("<MntTotal>1250</MntTotal>", xml);
        Assert.DoesNotContain("<IVA>", xml);
    }

    [Fact]
    public void Liquidacion_factura_exige_el_bloque_totales_y_con_el_cumple_el_esquema_oficial()
    {
        var items = new[] { Fixtures.Item("Venta por cuenta de terceros", 1, 0, d => { d.Remove("cantidad"); d.Remove("precioUnitario"); d["tipoDocumentoLiquidado"] = "30"; d["monto"] = 10000; }) };
        Assert.Contains("totales", Assert.Throws<ArgumentException>(() => new DteBuilder().Build(43, Fixtures.Factura(1, items: items))).Message);
        var (firmado, caf, cert) = Pipeline.Dte(Fixtures.Factura(1, d => d["totales"] = new Dictionary<string, object?> { ["MntTotal"] = 10000 }, items: items), 43);
        var bytes = Pipeline.Bytes(firmado);
        Verify.Ted(bytes, caf.PublicPem);
        Verify.XmlDsig(bytes, cert.CertificatePem);
        Verify.Xsd(bytes, "dte/DTE_v10.xsd");
    }

    [Fact]
    public void Los_campos_llegan_en_el_orden_que_exige_el_SII_aunque_se_entreguen_desordenados()
    {
        var datos = Fixtures.Factura(1);
        datos["emisor"] = ((Dictionary<string, object?>)datos["emisor"]!).Reverse().ToDictionary(kv => kv.Key, kv => kv.Value);
        datos["receptor"] = ((Dictionary<string, object?>)datos["receptor"]!).Reverse().ToDictionary(kv => kv.Key, kv => kv.Value);
        Verify.Xsd(Pipeline.Bytes(Pipeline.Dte(datos).Firmado), "dte/DTE_v10.xsd");
    }

    [Fact]
    public void La_validacion_contra_el_esquema_detecta_de_verdad_un_documento_invalido()
    {
        var bytes = Pipeline.Bytes(Pipeline.Dte(Fixtures.Factura(1)).Firmado);
        var roto = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace("<TipoDTE>33</TipoDTE>", "<TipoDTE>99</TipoDTE>", StringComparison.Ordinal));
        Assert.Contains("No cumple", Assert.Throws<Xunit.Sdk.TrueException>(() => Verify.Xsd(roto, "dte/DTE_v10.xsd")).Message);
    }
}
