using System.Globalization;
using System.Xml;

namespace DteChile;

/// <summary>
/// Fase 1 del flujo de un DTE: arma el XML del documento (encabezado, detalle, descuentos, referencias, totales) SIN timbre (TED) ni firma; eso lo agregan
/// <see cref="TedSigner"/> y <see cref="DteSigner"/> sobre el mismo documento.
/// <para>
/// El XML se genera desde la especificación oficial del SII (<see cref="SpecXml"/>), así que el orden y los campos obligatorios los dicta el esquema, no el código.
/// Tipos que acepta el esquema DTE: 33, 34, 46, 52, 56, 61 (documentos), 43 (liquidación-factura) y 110, 111, 112 (exportación).
/// Las boletas (39 y 41) tienen su propio esquema y se arman con un generador propio.
/// </para>
/// <para>
/// Cada bloque admite los nombres «amigables» (los mismos de la versión PHP: <c>folio</c>, <c>fechaEmision</c>, <c>emisor</c>, <c>receptor</c>, <c>items</c>, <c>impuestos</c>,
/// <c>descuentosRecargos</c>, <c>referencias</c>, <c>transporte</c>, <c>totales</c>, <c>moneda</c>...) o, directamente, los nombres de elemento del SII (<c>RUTEmisor</c>, <c>GiroEmis</c>...).
/// </para>
/// </summary>
public sealed class DteBuilder
{
    public const string NamespaceUri = "http://www.sii.cl/SiiDte";
    private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly Dictionary<string, string> AliasIdDoc = new()
    {
        ["formaPago"] = "FmaPago", ["fechaVencimiento"] = "FchVenc", ["indicadorServicio"] = "IndServicio", ["indicadorTraslado"] = "IndTraslado",
        ["tipoDespacho"] = "TipoDespacho", ["periodoDesde"] = "PeriodoDesde", ["periodoHasta"] = "PeriodoHasta", ["medioPago"] = "MedioPago",
        ["indicadorNoRebaja"] = "IndNoRebaja", ["fechaCancelacion"] = "FchCancel", ["montoCancelado"] = "MntCancel", ["saldoInsoluto"] = "SaldoInsol",
        ["tipoCuentaPago"] = "TpoCtaPago", ["numeroCuentaPago"] = "NumCtaPago", ["bancoPago"] = "BcoPago",
    };

    private static readonly Dictionary<string, string> AliasEmisor = new()
    {
        ["rut"] = "RUTEmisor", ["razonSocial"] = "RznSoc", ["giro"] = "GiroEmis", ["telefono"] = "Telefono", ["correo"] = "CorreoEmisor",
        ["actividadEconomica"] = "Acteco", ["direccion"] = "DirOrigen", ["comuna"] = "CmnaOrigen", ["ciudad"] = "CiudadOrigen", ["sucursal"] = "Sucursal",
        ["codigoSucursalSii"] = "CdgSIISucur", ["codigoVendedor"] = "CdgVendedor", ["guiaExportacion"] = "GuiaExport",
    };

    private static readonly Dictionary<string, string> AliasReceptor = new()
    {
        ["rut"] = "RUTRecep", ["razonSocial"] = "RznSocRecep", ["giro"] = "GiroRecep", ["direccion"] = "DirRecep", ["comuna"] = "CmnaRecep", ["ciudad"] = "CiudadRecep",
        ["correo"] = "CorreoRecep", ["contacto"] = "Contacto", ["codigoInterno"] = "CdgIntRecep", ["extranjero"] = "Extranjero",
        ["direccionPostal"] = "DirPostal", ["comunaPostal"] = "CmnaPostal", ["ciudadPostal"] = "CiudadPostal",
    };

    private static readonly Dictionary<string, string> AliasItem = new()
    {
        ["nombre"] = "NmbItem", ["descripcion"] = "DscItem", ["cantidad"] = "QtyItem", ["unidad"] = "UnmdItem", ["precioUnitario"] = "PrcItem",
        ["descuentoPct"] = "DescuentoPct", ["descuentoMonto"] = "DescuentoMonto", ["recargoPct"] = "RecargoPct", ["recargoMonto"] = "RecargoMonto",
        ["exento"] = "IndExe", ["tipoDocumentoLiquidado"] = "TpoDocLiq", ["codigos"] = "CdgItem", ["impuestoAdicional"] = "CodImpAdic", ["monto"] = "MontoItem",
        ["retenedor"] = "Retenedor", ["fechaElaboracion"] = "FchElabor", ["fechaVencimiento"] = "FchVencim", ["otraMoneda"] = "OtrMnda",
    };

    private static readonly Dictionary<string, string> AliasTransporte = new()
    {
        ["patente"] = "Patente", ["patenteCarro"] = "PatenteCarro", ["rutTransportista"] = "RUTTrans", ["chofer"] = "Chofer", ["direccionDestino"] = "DirDest",
        ["comunaDestino"] = "CmnaDest", ["ciudadDestino"] = "CiudadDest", ["aduana"] = "Aduana", ["fechaSalida"] = "FchSalida", ["horaSalida"] = "HraSalida",
        ["fechaLlegada"] = "FchLlegada",
    };

    private static readonly Dictionary<string, string> AliasReferencia = new()
    {
        ["tipoDocumento"] = "TpoDocRef", ["folio"] = "FolioRef", ["fecha"] = "FchRef", ["codigo"] = "CodRef", ["razon"] = "RazonRef", ["global"] = "IndGlobal", ["rutOtro"] = "RUTOtr",
    };

    private static readonly Dictionary<string, string> AliasDscRcg = new()
    {
        ["tipo"] = "TpoMov", ["valorTipo"] = "TpoValor", ["valor"] = "ValorDR", ["glosa"] = "GlosaDR", ["exento"] = "IndExeDR",
    };

    private readonly decimal _tasaIva;
    private readonly bool _truncar;

    public DteBuilder(decimal tasaIva = 19m, bool truncar = false)
    {
        _tasaIva = tasaIva;
        _truncar = truncar;
    }

    public XmlDocument BuildFacturaElectronica(IDictionary<string, object?> data) => Build(DocumentType.FacturaElectronica, data);

    /// <param name="documentType">Código SII del documento.</param>
    /// <param name="data">Ver la descripción de la clase.</param>
    /// <exception cref="ArgumentException">Si falta un dato, un RUT es inválido o algún valor no cumple lo que exige el esquema del SII.</exception>
    public XmlDocument Build(int documentType, IDictionary<string, object?> data)
    {
        if (DocumentType.IsBoleta(documentType))
        {
            throw new ArgumentException($"El tipo {documentType} (boleta) tiene su propio esquema y envío: use BoletaBuilder.");
        }

        var variante = Variante(documentType);
        Validate(data);
        // Reglas del formato del SII que el esquema no expresa: las notas de crédito y débito deben referenciar el documento que modifican,
        // y la guía de despacho debe indicar el tipo de traslado.
        if (documentType is DocumentType.NotaDebito or DocumentType.NotaCredito or DocumentType.NotaDebitoExportacion or DocumentType.NotaCreditoExportacion
            && !(data.TryGetValue("referencias", out var refsReq) && SpecXml.AsList(refsReq) is { Count: > 0 }))
        {
            throw new ArgumentException("Las notas de crédito y de débito deben incluir al menos una referencia («referencias») al documento que corrigen o anulan.");
        }

        if (documentType == DocumentType.GuiaDespacho && !data.ContainsKey("indicadorTraslado") && !(data.TryGetValue("idDoc", out var idd) && SpecXml.AsMap(idd) is { } iddm && iddm.ContainsKey("IndTraslado")))
        {
            throw new ArgumentException("La guía de despacho debe indicar el tipo de traslado («indicadorTraslado», 1 a 9).");
        }

        var emisor = Alias(SpecXml.AsMap(data["emisor"])!, AliasEmisor);
        emisor["RUTEmisor"] = Rut.ToDteFormat(Convert.ToString(emisor["RUTEmisor"], CultureInfo.InvariantCulture)!);
        var receptor = Alias(SpecXml.AsMap(data["receptor"])!, AliasReceptor);
        receptor["RUTRecep"] = Rut.ToDteFormat(Convert.ToString(receptor["RUTRecep"], CultureInfo.InvariantCulture)!);

        var detalle = SpecXml.AsList(data["items"])!.Select(i => (IDictionary<string, object?>)Item(SpecXml.AsMap(i)!)).ToList();
        var dscRcg = (data.TryGetValue("descuentosRecargos", out var dr) ? SpecXml.AsList(dr) : null)?
            .Select(d => (IDictionary<string, object?>)Alias(SpecXml.AsMap(d)!, AliasDscRcg)).ToList() ?? [];

        var exportacion = documentType is DocumentType.FacturaExportacion or DocumentType.NotaDebitoExportacion or DocumentType.NotaCreditoExportacion;
        var exenta = exportacion || documentType == DocumentType.FacturaNoAfectaExenta;
        if (documentType == DocumentType.FacturaNoAfectaExenta)
        {
            // En la factura exenta cada línea lleva IndExe = 1 (es lo que el SII espera ver aunque todo el documento sea exento).
            foreach (var l in detalle)
            {
                l.TryAdd("IndExe", 1);
            }
        }

        if (documentType == DocumentType.LiquidacionFactura && !data.ContainsKey("totales"))
        {
            throw new ArgumentException("La liquidación-factura (43) lleva comisiones y cargos que no se calculan automáticamente: entregue el bloque «totales» completo.");
        }

        var tasas = new Dictionary<string, decimal>();
        if (data.TryGetValue("impuestos", out var imp) && SpecXml.AsMap(imp) is { } mapaImp)
        {
            foreach (var (codigo, tasa) in mapaImp)
            {
                tasas[codigo] = Totals.ToDecimal(tasa);
            }
        }

        var calculo = new Totals(_tasaIva, exportacion ? 2 : 0, exenta).Calcular(detalle, dscRcg, tasas);

        var idDoc = new Dictionary<string, object?> { ["TipoDTE"] = documentType, ["Folio"] = data["folio"], ["FchEmis"] = data["fechaEmision"] };
        foreach (var (k, v) in Alias(data.Where(kv => AliasIdDoc.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value), AliasIdDoc))
        {
            idDoc[k] = v;
        }

        if (data.TryGetValue("idDoc", out var raw) && SpecXml.AsMap(raw) is { } extra)
        {
            foreach (var (k, v) in extra)
            {
                idDoc.TryAdd(k, v);
            }
        }

        var encabezado = new Dictionary<string, object?> { ["IdDoc"] = idDoc, ["Emisor"] = emisor, ["Receptor"] = receptor };
        if (data.TryGetValue("transporte", out var tr) && SpecXml.AsMap(tr) is { } transporte)
        {
            encabezado["Transporte"] = Transporte(transporte);
        }

        var totales = data.TryGetValue("totales", out var tt) && SpecXml.AsMap(tt) is { } explicito ? explicito : calculo.Totales;
        if (exportacion && data.TryGetValue("moneda", out var moneda) && moneda is not null)
        {
            totales = new Dictionary<string, object?> { ["TpoMoneda"] = moneda }.Concat(totales.Where(kv => kv.Key != "TpoMoneda")).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        encabezado["Totales"] = totales;
        if (data.TryGetValue("otraMoneda", out var om) && om is not null)
        {
            encabezado["OtraMoneda"] = om;
        }

        var cuerpo = new Dictionary<string, object?>
        {
            ["@ID"] = $"DTE_T{documentType}F{data["folio"]}",
            ["Encabezado"] = encabezado,
            ["Detalle"] = calculo.Detalle.Cast<object?>().ToList(),
        };
        foreach (var (alias, sii) in new[] { ("subtotales", "SubTotInfo"), ("comisiones", "Comisiones"), ("geoReferencia", "GeoRefEmision"), ("manejoMadera", "ManejoMadera") })
        {
            if (data.TryGetValue(alias, out var v) && v is not null)
            {
                cuerpo[sii] = v;
            }
        }

        if (calculo.DscRcg.Count > 0)
        {
            cuerpo["DscRcgGlobal"] = calculo.DscRcg.Cast<object?>().ToList();
        }

        if (data.TryGetValue("referencias", out var refs) && SpecXml.AsList(refs) is { Count: > 0 } referencias)
        {
            cuerpo["Referencia"] = referencias.Select((r, i) =>
            {
                var d = Alias(SpecXml.AsMap(r)!, AliasReferencia);
                d["NroLinRef"] = i + 1;

                return (object?)d;
            }).ToList();
        }

        var doc = XmlIo.NewDocument();
        var xml = new SpecXml(NamespaceUri, ["TED", "TmstFirma"], _truncar);
        var raiz = xml.Element(doc, Spec.Load("dte"), new Dictionary<string, object?> { ["@version"] = "1.0", [variante] = cuerpo }, "DTE");
        // El <EnvioDTE> declara xmlns:xsi en su raíz y la canonicalización inclusiva de <Documento> arrastra los espacios de nombres en alcance: si el DTE se firma
        // sin xsi y luego se inserta en el sobre, su resumen cambia y la firma queda inválida. Declarándolo aquí el contexto es el mismo.
        var xmlnsXsi = doc.CreateAttribute("xmlns", "xsi", "http://www.w3.org/2000/xmlns/");
        xmlnsXsi.Value = Xsi;
        raiz.SetAttributeNode(xmlnsXsi);
        doc.AppendChild(raiz);

        return doc;
    }

    /// <summary>El monto total del documento, tal como quedó en <c>Totales/MntTotal</c>: el timbre lo necesita en <c>montoTotal</c>.</summary>
    public int MontoTotal(XmlDocument dte)
    {
        var nodos = dte.GetElementsByTagName("MntTotal");
        if (nodos.Count == 0)
        {
            throw new InvalidOperationException("El documento no tiene <MntTotal> — ¿se generó con DteBuilder.Build?");
        }

        // En exportación el monto trae decimales y el timbre (MNT) es entero.
        return (int)Math.Round(decimal.Parse(nodos[0]!.InnerText, CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero);
    }

    /// <summary>Incorpora el TED ya firmado al nodo principal del documento, junto con la marca de tiempo de la firma: lo deja listo para la firma XMLDSig.</summary>
    public void AttachTed(XmlDocument dte, XmlDocument ted)
    {
        var documento = dte.DocumentElement?.FirstChild as XmlElement;
        if (documento is null || documento.Name is not ("Documento" or "Exportaciones" or "Liquidacion"))
        {
            throw new InvalidOperationException("El DTE no tiene un nodo <Documento> — ¿se generó con DteBuilder.Build?");
        }

        var raiz = ted.DocumentElement ?? throw new ArgumentException("El documento del TED está vacío.");
        documento.AppendChild(CloneInNamespace(raiz, dte));

        // Hora de Chile explícita: es la que el SII espera ver como momento en que se firmó el documento, no la del servidor en UTC.
        var tmst = dte.CreateElement(string.Empty, "TmstFirma", NamespaceUri);
        tmst.AppendChild(dte.CreateTextNode(TedSigner.ChileNow().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)));
        documento.AppendChild(tmst);
    }

    /// <summary>Copia un elemento (el TED se firma sin espacio de nombres) dejándolo en el espacio de nombres del DTE, con el mismo texto y atributos.</summary>
    private static XmlElement CloneInNamespace(XmlElement src, XmlDocument doc)
    {
        var copia = doc.CreateElement(string.Empty, src.LocalName, NamespaceUri);
        foreach (XmlAttribute a in src.Attributes)
        {
            copia.SetAttribute(a.LocalName, a.Value);
        }

        foreach (XmlNode hijo in src.ChildNodes)
        {
            switch (hijo)
            {
                case XmlElement e:
                    copia.AppendChild(CloneInNamespace(e, doc));
                    break;
                case XmlCharacterData t when t is XmlText or XmlWhitespace or XmlSignificantWhitespace or XmlCDataSection:
                    copia.AppendChild(doc.CreateTextNode(t.Value ?? string.Empty));
                    break;
            }
        }

        return copia;
    }

    /// <summary>Elemento de la especificación («Documento», «Liquidacion» o «Exportaciones») cuyo TipoDTE admite este código.</summary>
    private static string Variante(int tipo)
    {
        var dte = Spec.Load("dte");
        foreach (var v in new[] { "Documento", "Liquidacion", "Exportaciones" })
        {
            var nodo = Spec.Child(dte, v);
            var tipoDte = nodo is null ? null : Spec.Child(Spec.Child(Spec.Child(nodo, "Encabezado")!, "IdDoc")!, "TipoDTE");
            if (tipoDte?.Type?.Enum?.Contains(tipo.ToString(CultureInfo.InvariantCulture)) == true)
            {
                return v;
            }
        }

        throw new ArgumentException($"El tipo de documento {tipo} no existe en el esquema DTE del SII (válidos: 33, 34, 43, 46, 52, 56, 61, 110, 111, 112).");
    }

    private static void Validate(IDictionary<string, object?> data)
    {
        foreach (var field in new[] { "folio", "fechaEmision", "emisor", "receptor", "items" })
        {
            if (!data.TryGetValue(field, out var v) || v is null || (v is string s && s.Length == 0) || (SpecXml.AsList(v) is { Count: 0 }))
            {
                throw new ArgumentException($"Falta el campo requerido \"{field}\".");
            }
        }

        if (Convert.ToInt32(data["folio"], CultureInfo.InvariantCulture) < 1)
        {
            throw new ArgumentException("El folio debe ser un número entero positivo.");
        }

        var f = Convert.ToString(data["fechaEmision"], CultureInfo.InvariantCulture)!;
        if (!DateTime.TryParseExact(f, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ArgumentException("La fecha de emisión debe ser una fecha válida con el formato AAAA-MM-DD.");
        }

        if (SpecXml.AsList(data["items"]) is not { Count: > 0 })
        {
            throw new ArgumentException("El documento debe tener al menos un ítem en el detalle.");
        }

        foreach (var (bloque, amigable, sii, nombre) in new[] { ("emisor", "rut", "RUTEmisor", "emisor"), ("receptor", "rut", "RUTRecep", "receptor") })
        {
            var map = SpecXml.AsMap(data[bloque]) ?? throw new ArgumentException($"El campo \"{bloque}\" debe ser un conjunto de datos.");
            var rut = map.TryGetValue(amigable, out var r1) ? r1 : (map.TryGetValue(sii, out var r2) ? r2 : null);
            if (rut is null || (rut is string rs && rs.Length == 0))
            {
                throw new ArgumentException($"Falta el RUT del {nombre}.");
            }

            if (!Rut.IsValid(Convert.ToString(rut, CultureInfo.InvariantCulture)!))
            {
                throw new ArgumentException($"El RUT del {nombre} no es válido (dígito verificador incorrecto).");
            }
        }
    }

    private static Dictionary<string, object?> Item(Dictionary<string, object?> item)
    {
        var r = Alias(item, AliasItem);
        if (r.TryGetValue("IndExe", out var ie))
        {
            if (ie is true)
            {
                r["IndExe"] = 1;
            }
            else if (ie is false)
            {
                r.Remove("IndExe");
            }
        }

        if (r.TryGetValue("CdgItem", out var cdg) && SpecXml.AsList(cdg) is { } codigos)
        {
            r["CdgItem"] = codigos.Select(c => (object?)Alias(SpecXml.AsMap(c)!, new Dictionary<string, string> { ["tipo"] = "TpoCodigo", ["valor"] = "VlrCodigo" })).ToList();
        }

        if (r.TryGetValue("CodImpAdic", out var cia) && cia is not null)
        {
            r["CodImpAdic"] = (SpecXml.AsList(cia) ?? [cia]).Select(x => (object?)Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
        }

        return r;
    }

    private static Dictionary<string, object?> Transporte(Dictionary<string, object?> t)
    {
        var r = Alias(t, AliasTransporte);
        if (r.TryGetValue("Chofer", out var ch) && SpecXml.AsMap(ch) is { } chofer)
        {
            r["Chofer"] = Alias(chofer, new Dictionary<string, string> { ["rut"] = "RUTChofer", ["nombre"] = "NombreChofer" });
        }

        if (r.TryGetValue("RUTTrans", out var rt) && rt is not null)
        {
            r["RUTTrans"] = Rut.ToDteFormat(Convert.ToString(rt, CultureInfo.InvariantCulture)!);
        }

        return r;
    }

    /// <summary>Cambia los nombres amigables por los del SII; una clave que ya es un nombre del SII pasa tal cual. Un valor nulo o vacío se omite.</summary>
    internal static Dictionary<string, object?> Alias(IDictionary<string, object?> input, IReadOnlyDictionary<string, string> map)
    {
        var r = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in input)
        {
            if (v is null || (v is string s && s.Length == 0))
            {
                continue;
            }

            r[map.TryGetValue(k, out var sii) ? sii : k] = v;
        }

        return r;
    }
}
