using System.Text;
using System.Xml;
using DteChile;

namespace DteChile.Tests;

public class BasicosTests
{
    [Theory]
    [InlineData("76.123.456-0")]
    [InlineData("77111222-6")]
    [InlineData("11.111.111-1")]
    [InlineData("12345678-5")]
    [InlineData("1-9")]
    public void Rut_acepta_digitos_verificadores_correctos(string rut) => Assert.True(Rut.IsValid(rut));

    [Theory]
    [InlineData("76.123.456-1")]
    [InlineData("12345678-0")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("123456789-0")]
    [InlineData("12345678")]
    public void Rut_rechaza_digitos_y_formatos_incorrectos(string rut) => Assert.False(Rut.IsValid(rut));

    [Fact]
    public void Rut_normaliza_puntos_espacios_y_K_minuscula()
    {
        Assert.Equal("76123456-0", Rut.ToDteFormat("76.123.456-0"));
        Assert.Equal("12345678-K", Rut.ToDteFormat(" 12.345.678-k "));
    }

    [Fact]
    public void Certificado_abre_un_pfx_y_lee_el_RUT_del_titular()
    {
        var c = Fixtures.Cert();
        Assert.Equal("11111111-1", c.SubjectRut());
        Assert.True(c.NotAfter > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Certificado_con_clave_equivocada_o_archivo_corrupto_da_un_error_claro()
    {
        var (pfx, _) = Fixtures.NewCertificate();
        Assert.Contains("contraseña", Assert.Throws<ArgumentException>(() => Certificate.FromPkcs12(pfx, "otra-clave")).Message);
        Assert.Contains("contraseña", Assert.Throws<ArgumentException>(() => Certificate.FromPkcs12(Encoding.UTF8.GetBytes("no soy un pfx"), "x")).Message);
    }

    [Fact]
    public void Caf_lee_tipo_rango_y_emisor_y_reconoce_los_limites_del_rango()
    {
        var caf = Fixtures.NewCaf(33, 5, 10).Caf;
        Assert.Equal(33, caf.DocumentType);
        Assert.Equal((5, 10), (caf.FolioFrom, caf.FolioTo));
        Assert.True(caf.ContainsFolio(5) && caf.ContainsFolio(10) && !caf.ContainsFolio(4) && !caf.ContainsFolio(11));
        Assert.Equal("76123456-0", caf.IssuerRut);
    }

    [Fact]
    public void Caf_rechaza_archivos_que_no_son_un_CAF()
    {
        Assert.Contains("CAF", Assert.Throws<ArgumentException>(() => Caf.FromXml("<hola/>")).Message);
        Assert.Contains("XML", Assert.Throws<ArgumentException>(() => Caf.FromXml("no es xml")).Message);
    }
}

public class MontosTests
{
    private static int Valor(XmlDocument d, string tag, int i = 0) => int.Parse(d.GetElementsByTagName(tag)[i]!.InnerText);

    [Fact]
    public void Neto_IVA_y_total_de_una_factura_simple()
    {
        var doc = new DteBuilder().Build(33, Fixtures.Factura(items: [Fixtures.Item("A", 10, 25000)]));
        Assert.Equal((250000, 47500, 297500), (Valor(doc, "MntNeto"), Valor(doc, "IVA"), Valor(doc, "MntTotal")));
    }

    [Fact]
    public void Redondeo_del_IVA_y_de_cantidades_decimales()
    {
        var b = new DteBuilder();
        var doc = b.Build(33, Fixtures.Factura(items: [Fixtures.Item("A", 3, 1990)]));
        Assert.Equal((5970, 1134, 7104), (Valor(doc, "MntNeto"), Valor(doc, "IVA"), b.MontoTotal(doc)));
        doc = b.Build(33, Fixtures.Factura(items: [Fixtures.Item("A", 2.5m, 999)]));
        Assert.Equal(2498, Valor(doc, "MntNeto")); // 2.497,5 → 2.498 (mitad hacia arriba)
    }

    [Fact]
    public void Varias_lineas_suman_bien_y_numeran_sus_detalles()
    {
        var doc = new DteBuilder().Build(33, Fixtures.Factura(items: [Fixtures.Item("A", 1, 1000), Fixtures.Item("B", 2, 500), Fixtures.Item("C", 4, 250)]));
        Assert.Equal(3000, Valor(doc, "MntNeto"));
        Assert.Equal(3, doc.GetElementsByTagName("Detalle").Count);
        Assert.Equal("3", doc.GetElementsByTagName("NroLinDet")[2]!.InnerText);
    }

    [Fact]
    public void Descuento_y_recargo_por_linea_se_aplican_y_se_informan()
    {
        var doc = new DteBuilder().Build(33, Fixtures.Factura(items:
        [
            Fixtures.Item("A", 10, 1000, d => d["descuentoPct"] = 10),
            Fixtures.Item("B", 1, 2000, d => d["recargoMonto"] = 300),
        ]));
        Assert.Equal((9000, 2300), (Valor(doc, "MontoItem", 0), Valor(doc, "MontoItem", 1)));
        Assert.Equal("1000", doc.GetElementsByTagName("DescuentoMonto")[0]!.InnerText);
        Assert.Equal((11300, 2147), (Valor(doc, "MntNeto"), Valor(doc, "IVA")));
    }

    private static Dictionary<string, string> Totales(Dictionary<string, object?> datos, int tipo = 33)
    {
        var (firmado, _, _) = Pipeline.Dte(datos, tipo);
        var bytes = Pipeline.Bytes(firmado);
        Verify.Xsd(bytes, "dte/DTE_v10.xsd");
        var d = new XmlDocument();
        d.Load(new MemoryStream(bytes));
        var t = new Dictionary<string, string>();
        foreach (XmlNode n in d.GetElementsByTagName("Totales")[0]!.ChildNodes)
        {
            t[n.Name] = t.TryGetValue(n.Name, out var previo) ? previo + "|" + n.InnerText : n.InnerText;
        }

        return t;
    }

    [Fact]
    public void Afectos_y_exentos_mezclados_con_descuento_global_del_10_por_ciento_a_los_afectos()
    {
        var t = Totales(Fixtures.Factura(items: [Fixtures.Item("A", 10, 1000), Fixtures.Item("B", 1, 500, d => d["exento"] = true)], cambios: d =>
            d["descuentosRecargos"] = new List<object?> { new Dictionary<string, object?> { ["tipo"] = "D", ["valorTipo"] = "%", ["valor"] = 10, ["glosa"] = "Pago contado" } }));
        Assert.Equal(["9000", "500", "19", "1710", "11210"], [t["MntNeto"], t["MntExe"], t["TasaIVA"], t["IVA"], t["MntTotal"]]);
    }

    [Fact]
    public void Descuento_global_en_pesos_al_grupo_exento()
    {
        var t = Totales(Fixtures.Factura(items: [Fixtures.Item("A", 1, 10000), Fixtures.Item("B", 1, 5000, d => d["exento"] = true)], cambios: d =>
            d["descuentosRecargos"] = new List<object?> { new Dictionary<string, object?> { ["tipo"] = "D", ["valorTipo"] = "$", ["valor"] = 1000, ["exento"] = 1 } }));
        Assert.Equal(["10000", "4000", "1900", "15900"], [t["MntNeto"], t["MntExe"], t["IVA"], t["MntTotal"]]);
    }

    [Fact]
    public void Impuesto_adicional_codigo_24_suma_al_total_con_su_tasa_explicita()
    {
        var t = Totales(Fixtures.Factura(items: [Fixtures.Item("Licor", 1, 10000, d => d["impuestoAdicional"] = "24")], cambios: d =>
            d["impuestos"] = new Dictionary<string, object?> { ["24"] = 31.5m }));
        Assert.Equal(["10000", "1900", "15050"], [t["MntNeto"], t["IVA"], t["MntTotal"]]);
        Assert.Contains("3150", t["ImptoReten"]);
    }

    [Fact]
    public void Retencion_de_IVA_codigo_15_resta_del_total_y_exige_indicar_su_tasa()
    {
        var datos = Fixtures.Factura(items: [Fixtures.Item("Producto", 1, 10000, d => d["impuestoAdicional"] = "15")], cambios: d => d["impuestos"] = new Dictionary<string, object?> { ["15"] = 19 });
        var t = Totales(datos, 46);
        Assert.Equal(["10000", "1900", "10000"], [t["MntNeto"], t["IVA"], t["MntTotal"]]);
        datos.Remove("impuestos");
        Assert.Contains("necesita su tasa", Assert.Throws<ArgumentException>(() => new DteBuilder().Build(46, datos)).Message);
    }

    [Fact]
    public void Item_no_facturable_va_aparte_y_no_suma_al_total()
    {
        var t = Totales(Fixtures.Factura(items: [Fixtures.Item("A", 1, 1000), Fixtures.Item("Envase", 1, 200, d => d["exento"] = 2)]));
        Assert.Equal(["1000", "190", "1190", "200"], [t["MntNeto"], t["IVA"], t["MntTotal"], t["MontoNF"]]);
    }

    [Fact]
    public void Un_bloque_totales_explicito_reemplaza_al_calculo_automatico()
    {
        var t = Totales(Fixtures.Factura(cambios: d => d["totales"] = new Dictionary<string, object?> { ["MntNeto"] = 1000, ["TasaIVA"] = 19, ["IVA"] = 190, ["MntTotal"] = 1190 }));
        Assert.Equal(["1000", "190", "1190"], [t["MntNeto"], t["IVA"], t["MntTotal"]]);
    }
}

public class ValidacionTests
{
    [Fact]
    public void Rechaza_RUT_invalido_y_campos_obligatorios_ausentes()
    {
        var b = new DteBuilder();
        Assert.Contains("RUT", Assert.Throws<ArgumentException>(() => b.Build(33, Fixtures.Factura(cambios: d => ((Dictionary<string, object?>)d["receptor"]!)["rut"] = "77.111.222-0"))).Message);
        var sinItems = Fixtures.Factura();
        sinItems["items"] = new List<object?>();
        Assert.Contains("tem", Assert.Throws<ArgumentException>(() => b.Build(33, sinItems)).Message);
        var sinFolio = Fixtures.Factura();
        sinFolio.Remove("folio");
        Assert.Contains("folio", Assert.Throws<ArgumentException>(() => b.Build(33, sinFolio)).Message);
    }

    [Fact]
    public void Folio_no_positivo_y_fecha_invalida_se_rechazan()
    {
        var b = new DteBuilder();
        Assert.Contains("folio", Assert.Throws<ArgumentException>(() => b.Build(33, Fixtures.Factura(0))).Message);
        Assert.Contains("fecha", Assert.Throws<ArgumentException>(() => b.Build(33, Fixtures.Factura(cambios: d => d["fechaEmision"] = "08/09/2026"))).Message);
        Assert.Contains("fecha", Assert.Throws<ArgumentException>(() => b.Build(33, Fixtures.Factura(cambios: d => d["fechaEmision"] = "2026-02-30"))).Message);
    }

    [Fact]
    public void Un_campo_mal_escrito_o_fuera_de_rango_se_rechaza_con_la_ruta_del_campo()
    {
        var b = new DteBuilder();
        ArgumentException Err(Action<Dictionary<string, object?>> cambio) => Assert.Throws<ArgumentException>(() => b.Build(33, Fixtures.Factura(cambios: cambio)));
        Assert.Contains("giroo", Err(d => ((Dictionary<string, object?>)d["emisor"]!)["giroo"] = "x").Message);
        Assert.Contains("máximo es 100", Err(d => ((Dictionary<string, object?>)d["emisor"]!)["razonSocial"] = new string('A', 101)).Message);
        Assert.Contains("no es un valor permitido", Err(d => d["formaPago"] = 9).Message);
        Assert.Contains("Acteco", Err(d => ((Dictionary<string, object?>)d["emisor"]!).Remove("actividadEconomica")).Message);
        Assert.Contains("no existe en el esquema", Assert.Throws<ArgumentException>(() => b.Build(99, Fixtures.Factura())).Message);
    }

    [Theory]
    [InlineData(39)]
    [InlineData(41)]
    public void Boletas_DteBuilder_las_rechaza_y_apunta_a_BoletaBuilder(int tipo) =>
        Assert.Contains("BoletaBuilder", Assert.Throws<ArgumentException>(() => new DteBuilder().Build(tipo, Fixtures.Factura())).Message);

    [Fact]
    public void Caracteres_que_el_SII_no_admite_se_normalizan_o_se_rechazan_nunca_se_cambian_en_silencio()
    {
        var b = new DteBuilder();
        var doc = b.Build(33, Fixtures.Factura(items: [Fixtures.Item("Diseño – “premium” … año", 1, 1000)]));
        Assert.Equal("Diseño - \"premium\" ... año", doc.GetElementsByTagName("NmbItem")[0]!.InnerText);
        Assert.Contains("no admite", Assert.Throws<ArgumentException>(() => b.Build(33, Fixtures.Factura(items: [Fixtures.Item("Torta 🎂", 1, 1000)]))).Message);
        Assert.Contains("control", Assert.Throws<ArgumentException>(() => b.Build(33, Fixtures.Factura(cambios: d => ((Dictionary<string, object?>)d["receptor"]!)["razonSocial"] = "Cliente\u0007"))).Message);
    }

    [Fact]
    public void Razon_social_con_ampersand_y_comillas_produce_un_XML_valido_y_conserva_el_nombre()
    {
        var doc = new DteBuilder().Build(33, Fixtures.Factura(cambios: d => ((Dictionary<string, object?>)d["receptor"]!)["razonSocial"] = "Muebles & Cía \"El Pino\" <Ltda>"));
        var releido = new XmlDocument();
        releido.LoadXml(XmlIo.ToXml(doc, declaracion: false));
        Assert.Equal("Muebles & Cía \"El Pino\" <Ltda>", releido.GetElementsByTagName("RznSocRecep")[0]!.InnerText);
    }

    [Fact]
    public void Tildes_y_enie_en_el_documento_final_se_codifican_en_ISO_8859_1()
    {
        var bytes = XmlIo.ToBytes(new DteBuilder().Build(33, Fixtures.Factura(items: [Fixtures.Item("Ñandú año 2026 – café", 1, 1000)])));
        var texto = Encoding.Latin1.GetString(bytes);
        Assert.Contains("encoding=\"ISO-8859-1\"", texto);
        Assert.Contains("Ñandú año", texto); // Ñ, ú y ñ como bytes ISO-8859-1
    }
}
