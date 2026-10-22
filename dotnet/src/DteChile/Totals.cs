using System.Globalization;

namespace DteChile;

public sealed record TotalsResult(
    List<Dictionary<string, object?>> Detalle,
    List<Dictionary<string, object?>> DscRcg,
    Dictionary<string, object?> Totales);

/// <summary>
/// Cálculo de las líneas de detalle y de los totales de un DTE, con las reglas del «Formato Documentos Tributarios Electrónicos» del SII:
/// <list type="bullet">
/// <item>MontoItem = cantidad × precio − descuento + recargo (redondeado).</item>
/// <item>Ítems afectos (sin IndExe), exentos (IndExe 1) y no facturables (IndExe 2) se suman por separado.</item>
/// <item>Los descuentos/recargos globales ajustan el grupo al que apuntan (IndExeDR): vacío = afectos, 1 = exentos, 2 = no facturables.</item>
/// <item>IVA = Monto neto × tasa (19 %), redondeado. Los impuestos adicionales y retenciones usan la tasa que entregue quien llama (cambian por ley).</item>
/// <item>Monto total = neto + exento + IVA + impuestos adicionales − retenciones. Lo no facturable (MontoNF) va aparte y no suma al total.</item>
/// </list>
/// Para lo que esta clase no cubre (liquidaciones-factura, margen de comercialización, créditos especiales, otras monedas...) el documento admite
/// un bloque <c>totales</c> explícito, que reemplaza al calculado. Se usa <see cref="decimal"/>: sin errores de coma flotante en dinero.
/// </summary>
public sealed class Totals
{
    /// <summary>Códigos del SII que son retenciones de IVA (restan del total) y no impuestos adicionales (suman).</summary>
    public static readonly IReadOnlySet<string> Retenciones = new HashSet<string>
    {
        "15", "30", "31", "32", "33", "34", "36", "37", "38", "39", "41", "46", "47", "48", "49", "301", "321", "331", "341", "361", "371", "481",
    };

    private readonly decimal _tasaIva;
    private readonly int _decimales;
    private readonly bool _todoExento;

    /// <param name="tasaIva">Tasa de IVA en porcentaje (19).</param>
    /// <param name="decimales">0 para pesos; 2 para documentos de exportación en moneda extranjera.</param>
    /// <param name="todoExento">true en factura exenta y en exportación: no hay IVA y todo el monto va a «Monto exento».</param>
    public Totals(decimal tasaIva = 19m, int decimales = 0, bool todoExento = false)
    {
        _tasaIva = tasaIva;
        _decimales = decimales;
        _todoExento = todoExento;
    }

    private decimal R(decimal x) => Math.Round(x, _decimales, MidpointRounding.AwayFromZero);

    private object Num(decimal d) => _decimales == 0 ? (object)(long)d : d;

    /// <param name="detalle">Líneas con nombres del SII (NmbItem, QtyItem, PrcItem, IndExe, CodImpAdic, descuentos y recargos...).</param>
    /// <param name="dscRcg">Descuentos y recargos globales (TpoMov, TpoValor, ValorDR, IndExeDR, GlosaDR).</param>
    /// <param name="tasasImpuestos">Tasa (%) de cada código de impuesto adicional usado en el detalle.</param>
    public TotalsResult Calcular(
        IEnumerable<IDictionary<string, object?>> detalle,
        IEnumerable<IDictionary<string, object?>> dscRcg,
        IReadOnlyDictionary<string, decimal>? tasasImpuestos = null,
        bool conIva = true)
    {
        tasasImpuestos ??= new Dictionary<string, decimal>();
        var grupos = new Dictionary<string, decimal> { ["afecto"] = 0, ["exento"] = 0, ["nf"] = 0 };
        var baseImpuesto = new Dictionary<string, decimal>();
        var lineas = new List<Dictionary<string, object?>>();

        var n = 0;
        foreach (var original in detalle)
        {
            n++;
            var linea = new Dictionary<string, object?>(original, StringComparer.Ordinal) { ["NroLinDet"] = n };
            var monto = Linea(linea, n);
            lineas.Add(linea);

            var ind = linea.TryGetValue("IndExe", out var ie) && ie is not null ? Convert.ToString(ie, CultureInfo.InvariantCulture)! : string.Empty;
            var grupo = ind switch
            {
                "" => "afecto",
                "1" => "exento",
                "2" or "6" => "nf",
                _ => throw new ArgumentException($"Línea {n}: el indicador de exención {ind} no se calcula automáticamente; entregue el bloque «totales» completo."),
            };
            grupos[grupo] += monto;
            if (grupo == "afecto" && linea.TryGetValue("CodImpAdic", out var cod) && cod is not null)
            {
                foreach (var codigo in AsStrings(cod))
                {
                    baseImpuesto[codigo] = baseImpuesto.GetValueOrDefault(codigo) + monto;
                }
            }
        }

        var mixto = baseImpuesto.Count > 1 || (baseImpuesto.Count == 1 && baseImpuesto.Values.Sum() != grupos["afecto"]);
        var netoAntes = grupos["afecto"];
        var globales = new List<Dictionary<string, object?>>();
        var i = 0;
        foreach (var original in dscRcg)
        {
            i++;
            var g = new Dictionary<string, object?>(original, StringComparer.Ordinal) { ["NroLinDR"] = i };
            var ind = g.TryGetValue("IndExeDR", out var ie) && ie is not null ? Convert.ToString(ie, CultureInfo.InvariantCulture)! : string.Empty;
            var clave = ind switch
            {
                "" => "afecto",
                "1" => "exento",
                "2" => "nf",
                _ => throw new ArgumentException($"Descuento/recargo {i}: IndExeDR debe ser vacío, 1 o 2."),
            };
            var valor = ToDecimal(g["ValorDR"]);
            var tpoValor = g.TryGetValue("TpoValor", out var tv) ? Convert.ToString(tv, CultureInfo.InvariantCulture) : string.Empty;
            if (tpoValor == "$" && mixto && clave == "afecto")
            {
                throw new ArgumentException("Con ítems de distintos impuestos adicionales, el descuento o recargo global debe ser un porcentaje (TpoValor \"%\"), según el formato del SII.");
            }

            var monto = tpoValor == "%" ? R(grupos[clave] * valor / 100m) : R(valor);
            grupos[clave] += (Convert.ToString(g["TpoMov"], CultureInfo.InvariantCulture) == "D" ? -1 : 1) * monto;
            globales.Add(g);
        }

        if (_todoExento)
        {
            // Factura exenta y exportación: lo afecto y lo exento se informan juntos como «Monto exento».
            grupos["exento"] += grupos["afecto"];
            grupos["afecto"] = 0;
            conIva = false;
        }

        var neto = grupos["afecto"];
        var exento = grupos["exento"];
        var totales = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (neto != 0 || exento == 0)
        {
            totales["MntNeto"] = Num(neto);
        }

        if (exento != 0)
        {
            totales["MntExe"] = Num(exento);
        }

        var iva = 0m;
        if (conIva && neto > 0)
        {
            iva = R(neto * _tasaIva / 100m);
            totales["TasaIVA"] = _tasaIva;
            totales["IVA"] = Num(iva);
        }

        var suma = 0m;
        var reten = new List<object?>();
        var razon = netoAntes > 0 ? neto / netoAntes : 1m; // el descuento global también reduce la base de cada impuesto
        foreach (var (codigo, baseMonto) in baseImpuesto)
        {
            if (!tasasImpuestos.TryGetValue(codigo, out var tasa))
            {
                throw new ArgumentException($"El impuesto adicional o retención código {codigo} necesita su tasa: indíquela en «impuestos» (por ejemplo [\"{codigo}\"] = 15).");
            }

            var montoImp = R(baseMonto * razon * tasa / 100m);
            reten.Add(new Dictionary<string, object?> { ["TipoImp"] = codigo, ["TasaImp"] = tasa, ["MontoImp"] = Num(montoImp) });
            suma += (Retenciones.Contains(codigo) ? -1 : 1) * montoImp;
        }

        if (reten.Count > 0)
        {
            totales["ImptoReten"] = reten;
        }

        if (grupos["nf"] != 0)
        {
            totales["MontoNF"] = Num(grupos["nf"]);
        }

        totales["MntTotal"] = Num(neto + exento + iva + suma);

        return new TotalsResult(lineas, globales, totales);
    }

    private decimal Linea(Dictionary<string, object?> linea, int n)
    {
        decimal baseMonto;
        if (linea.TryGetValue("PrcItem", out var precio) && precio is not null && !(precio is string ps && ps.Length == 0))
        {
            var qty = linea.TryGetValue("QtyItem", out var q) && q is not null ? ToDecimal(q) : 1m;
            baseMonto = qty * ToDecimal(precio);
        }
        else if (linea.TryGetValue("MontoItem", out var m) && m is not null)
        {
            baseMonto = ToDecimal(m);
        }
        else
        {
            throw new ArgumentException($"Línea {n} («{linea.GetValueOrDefault("NmbItem") ?? "?"}»): indique precioUnitario (con cantidad) o el monto de la línea.");
        }

        var desc = linea.TryGetValue("DescuentoMonto", out var dm) && dm is not null
            ? R(ToDecimal(dm))
            : linea.TryGetValue("DescuentoPct", out var dp) && dp is not null ? R(baseMonto * ToDecimal(dp) / 100m) : 0m;
        var rec = linea.TryGetValue("RecargoMonto", out var rm) && rm is not null
            ? R(ToDecimal(rm))
            : linea.TryGetValue("RecargoPct", out var rp) && rp is not null ? R(baseMonto * ToDecimal(rp) / 100m) : 0m;

        if (linea.ContainsKey("DescuentoPct") && !linea.ContainsKey("DescuentoMonto"))
        {
            linea["DescuentoMonto"] = Num(desc);
        }

        if (linea.ContainsKey("RecargoPct") && !linea.ContainsKey("RecargoMonto"))
        {
            linea["RecargoMonto"] = Num(rec);
        }

        var monto = R(baseMonto - desc + rec);
        if (monto < 0 && !linea.ContainsKey("IndExe"))
        {
            throw new ArgumentException($"Línea {n}: el monto no puede ser negativo.");
        }

        linea["MontoItem"] = Num(monto);

        return monto;
    }

    internal static decimal ToDecimal(object? v) => v switch
    {
        decimal d => d,
        double d => Convert.ToDecimal(d),
        float f => Convert.ToDecimal(f),
        string s => decimal.Parse(s, CultureInfo.InvariantCulture),
        null => 0m,
        _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
    };

    private static IEnumerable<string> AsStrings(object v) => v is string s
        ? [s]
        : v is System.Collections.IEnumerable e ? e.Cast<object?>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)!) : [Convert.ToString(v, CultureInfo.InvariantCulture)!];
}
