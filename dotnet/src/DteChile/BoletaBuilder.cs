using System.Globalization;
using System.Xml;

namespace DteChile;

/// <summary>
/// Arma el XML de una boleta electrónica (39) o boleta exenta (41) sin timbre ni firma; el resto del flujo es el mismo que el de <see cref="DteBuilder"/>:
/// <c>TedSigner.Sign</c> → <c>DteBuilder.AttachTed</c> → <c>DteSigner.Sign</c> → <c>EnvioDteBuilder.BuildBoleta</c> → <c>SiiClient.UploadBoletaAsync</c>.
/// <para>Diferencias con una factura, según el esquema del SII: los precios de la boleta incluyen IVA (el neto y el IVA se descomponen del total; con
/// <c>preciosNetos</c> = true, IndMntNeto 2, los precios son netos y el IVA se suma); el receptor es opcional (sin RUT se usa el genérico 66666666-6) y el emisor
/// usa los nombres RznSocEmisor / GiroEmisor.</para>
/// Entrada: folio, fechaEmision, indicadorServicio? (1, 2, 3 ventas y servicios —por defecto—, 4 espectáculos), emisor {rut, razonSocial, giro?, direccion?, comuna?, ciudad?},
/// receptor? {rut?, razonSocial?...}, items [{nombre, cantidad?, precioUnitario? | monto?, exento?, descuentoPct?...}], descuentosRecargos?, referencias?, preciosNetos?,
/// rutProveedorSw?, periodoDesde?, periodoHasta?, fechaVencimiento?, medioPago?
/// </summary>
public sealed class BoletaBuilder
{
    public const string RutGenerico = "66666666-6";

    private static readonly Dictionary<string, string> AliasEmisor = new()
    {
        ["rut"] = "RUTEmisor", ["razonSocial"] = "RznSocEmisor", ["giro"] = "GiroEmisor", ["direccion"] = "DirOrigen", ["comuna"] = "CmnaOrigen", ["ciudad"] = "CiudadOrigen",
        ["codigoSucursalSii"] = "CdgSIISucur",
    };

    private static readonly Dictionary<string, string> AliasReceptor = new()
    {
        ["rut"] = "RUTRecep", ["razonSocial"] = "RznSocRecep", ["direccion"] = "DirRecep", ["comuna"] = "CmnaRecep", ["ciudad"] = "CiudadRecep", ["correo"] = "CorreoRecep",
        ["contacto"] = "Contacto", ["telefono"] = "TelefonoRecep", ["codigoInterno"] = "CdgIntRecep",
    };

    private static readonly Dictionary<string, string> AliasItem = new()
    {
        ["nombre"] = "NmbItem", ["descripcion"] = "DscItem", ["cantidad"] = "QtyItem", ["unidad"] = "UnmdItem", ["precioUnitario"] = "PrcItem", ["descuentoPct"] = "DescuentoPct",
        ["descuentoMonto"] = "DescuentoMonto", ["recargoPct"] = "RecargoPct", ["recargoMonto"] = "RecargoMonto", ["exento"] = "IndExe", ["codigos"] = "CdgItem", ["monto"] = "MontoItem",
        ["rutMandante"] = "RUTMandante",
    };

    private static readonly Dictionary<string, string> AliasReferencia = new()
    {
        ["tipoDocumento"] = "TpoDocRef", ["folio"] = "FolioRef", ["codigo"] = "CodRef", ["razon"] = "RazonRef", ["codigoVendedor"] = "CodVndor", ["codigoCaja"] = "CodCaja",
    };

    private static readonly Dictionary<string, string> AliasDscRcg = new()
    {
        ["tipo"] = "TpoMov", ["valorTipo"] = "TpoValor", ["valor"] = "ValorDR", ["glosa"] = "GlosaDR", ["exento"] = "IndExeDR",
    };

    private readonly decimal _tasaIva;
    private readonly bool _truncar;

    public BoletaBuilder(decimal tasaIva = 19m, bool truncar = false)
    {
        _tasaIva = tasaIva;
        _truncar = truncar;
    }

    /// <exception cref="ArgumentException">Si falta un dato, un RUT es inválido o algún valor no cumple lo que exige el esquema del SII.</exception>
    public XmlDocument Build(int tipo, IDictionary<string, object?> data)
    {
        if (!DocumentType.IsBoleta(tipo))
        {
            throw new ArgumentException($"BoletaBuilder arma boletas (39 y 41); para el tipo {tipo} use DteBuilder.");
        }

        foreach (var campo in new[] { "folio", "fechaEmision", "emisor", "items" })
        {
            if (!data.TryGetValue(campo, out var v) || v is null || (v is string s && s.Length == 0) || SpecXml.AsList(v) is { Count: 0 })
            {
                throw new ArgumentException($"Falta el campo requerido \"{campo}\".");
            }
        }

        if (Convert.ToInt32(data["folio"], CultureInfo.InvariantCulture) < 1)
        {
            throw new ArgumentException("El folio debe ser un número entero positivo.");
        }

        var emisor = DteBuilder.Alias(SpecXml.AsMap(data["emisor"])!, AliasEmisor);
        if (!emisor.TryGetValue("RUTEmisor", out var re) || !Rut.IsValid(Convert.ToString(re, CultureInfo.InvariantCulture)!))
        {
            throw new ArgumentException("El RUT del emisor falta o no es válido.");
        }

        emisor["RUTEmisor"] = Rut.ToDteFormat(Convert.ToString(re, CultureInfo.InvariantCulture)!);

        var receptor = DteBuilder.Alias(data.TryGetValue("receptor", out var rec) && SpecXml.AsMap(rec) is { } rm ? rm : new Dictionary<string, object?>(), AliasReceptor);
        var rutRecep = Rut.ToDteFormat(receptor.TryGetValue("RUTRecep", out var rr) ? Convert.ToString(rr, CultureInfo.InvariantCulture)! : RutGenerico);
        if (!Rut.IsValid(rutRecep))
        {
            throw new ArgumentException("El RUT del receptor no es válido.");
        }

        receptor["RUTRecep"] = rutRecep;

        var exenta = tipo == DocumentType.BoletaNoAfectaExenta;
        var netos = data.TryGetValue("preciosNetos", out var pn) && pn is true;
        if (exenta && netos)
        {
            throw new ArgumentException("La boleta exenta (41) no tiene IVA: no admite preciosNetos.");
        }

        var detalle = SpecXml.AsList(data["items"])!.Select(i =>
        {
            var d = DteBuilder.Alias(SpecXml.AsMap(i)!, AliasItem);
            if (d.TryGetValue("IndExe", out var ie))
            {
                if (ie is true)
                {
                    d["IndExe"] = 1;
                }
                else if (ie is false)
                {
                    d.Remove("IndExe");
                }
            }

            if (d.TryGetValue("CdgItem", out var cdg) && SpecXml.AsList(cdg) is { } codigos)
            {
                d["CdgItem"] = codigos.Select(c => (object?)DteBuilder.Alias(SpecXml.AsMap(c)!, new Dictionary<string, string> { ["tipo"] = "TpoCodigo", ["valor"] = "VlrCodigo" })).ToList();
            }

            if (exenta)
            {
                d.TryAdd("IndExe", 1);
            }

            return (IDictionary<string, object?>)d;
        }).ToList();
        var dscRcg = (data.TryGetValue("descuentosRecargos", out var dr) ? SpecXml.AsList(dr) : null)?
            .Select(d => (IDictionary<string, object?>)DteBuilder.Alias(SpecXml.AsMap(d)!, AliasDscRcg)).ToList() ?? [];

        var calculo = new Totals(_tasaIva, 0, exenta).Calcular(detalle, dscRcg, null, conIva: false);
        var t = calculo.Totales;
        var afecto = t.TryGetValue("MntNeto", out var a) ? Convert.ToDecimal(a, CultureInfo.InvariantCulture) : 0m;
        var exento = t.TryGetValue("MntExe", out var e) ? Convert.ToDecimal(e, CultureInfo.InvariantCulture) : 0m;
        var totales = new Dictionary<string, object?>();
        decimal total;
        if (afecto > 0)
        {
            // Con precios con IVA incluido el afecto es el total bruto: se descompone. Con precios netos el IVA se suma.
            var neto = netos ? afecto : Math.Round(afecto / (1 + _tasaIva / 100), MidpointRounding.AwayFromZero);
            var iva = netos ? Math.Round(afecto * _tasaIva / 100, MidpointRounding.AwayFromZero) : afecto - neto;
            totales["MntNeto"] = (long)neto;
            totales["IVA"] = (long)iva;
            total = neto + iva + exento;
        }
        else
        {
            total = exento;
        }

        if (exento > 0)
        {
            totales["MntExe"] = (long)exento;
        }

        if (!totales.ContainsKey("MntNeto") && !totales.ContainsKey("MntExe"))
        {
            totales["MntExe"] = 0L;
        }

        totales["MntTotal"] = (long)total;
        if (t.TryGetValue("MontoNF", out var nf))
        {
            totales["MontoNF"] = nf;
        }

        foreach (var (alias, sii) in new[] { ("totalPeriodo", "TotalPeriodo"), ("saldoAnterior", "SaldoAnterior"), ("valorPagar", "VlrPagar") })
        {
            if (data.TryGetValue(alias, out var v) && v is not null)
            {
                totales[sii] = v;
            }
        }

        var idDoc = new Dictionary<string, object?>
        {
            ["TipoDTE"] = tipo, ["Folio"] = data["folio"], ["FchEmis"] = data["fechaEmision"],
            ["IndServicio"] = data.TryGetValue("indicadorServicio", out var isv) && isv is not null ? isv : 3,
        };
        if (netos)
        {
            idDoc["IndMntNeto"] = 2;
        }

        foreach (var (alias, sii) in new[] { ("periodoDesde", "PeriodoDesde"), ("periodoHasta", "PeriodoHasta"), ("fechaVencimiento", "FchVenc"), ("medioPago", "MedioPago") })
        {
            if (data.TryGetValue(alias, out var v) && v is not null)
            {
                idDoc[sii] = v;
            }
        }

        var encabezado = new Dictionary<string, object?> { ["IdDoc"] = idDoc, ["Emisor"] = emisor, ["Receptor"] = receptor };
        if (data.TryGetValue("rutProveedorSw", out var sw) && sw is not null)
        {
            encabezado["RUTProvSW"] = Rut.ToDteFormat(Convert.ToString(sw, CultureInfo.InvariantCulture)!);
        }

        encabezado["Totales"] = totales;

        var cuerpo = new Dictionary<string, object?>
        {
            ["@ID"] = $"T{tipo}F{data["folio"]}",
            ["Encabezado"] = encabezado,
            ["Detalle"] = calculo.Detalle.Cast<object?>().ToList(),
        };
        if (calculo.DscRcg.Count > 0)
        {
            cuerpo["DscRcgGlobal"] = calculo.DscRcg.Cast<object?>().ToList();
        }

        if (data.TryGetValue("referencias", out var refs) && SpecXml.AsList(refs) is { Count: > 0 } referencias)
        {
            cuerpo["Referencia"] = referencias.Select((r, i) =>
            {
                var d = DteBuilder.Alias(SpecXml.AsMap(r)!, AliasReferencia);
                d["NroLinRef"] = i + 1;

                return (object?)d;
            }).ToList();
        }

        var doc = XmlIo.NewDocument();
        var xml = new SpecXml(DteBuilder.NamespaceUri, ["TED", "TmstFirma"], _truncar);
        var raiz = xml.Element(doc, Spec.Load("boleta"), new Dictionary<string, object?> { ["@version"] = "1.0", ["Documento"] = cuerpo }, "DTE");
        var xmlnsXsi = doc.CreateAttribute("xmlns", "xsi", "http://www.w3.org/2000/xmlns/"); // mismo contexto de firma que dentro del sobre
        xmlnsXsi.Value = "http://www.w3.org/2001/XMLSchema-instance";
        raiz.SetAttributeNode(xmlnsXsi);
        doc.AppendChild(raiz);

        return doc;
    }
}
