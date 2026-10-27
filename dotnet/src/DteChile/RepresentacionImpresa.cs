using System.Globalization;
using System.Xml;

namespace DteChile;

/// <summary>Opciones de la representación impresa.</summary>
/// <param name="UnidadSii">Texto de «S.I.I. - ...» (por defecto SANTIAGO).</param>
/// <param name="ResolucionNumero">Número de la resolución para la leyenda «Res. N° de AAAA» del timbre.</param>
/// <param name="ResolucionFecha">Fecha (AAAA-MM-DD) de esa resolución.</param>
/// <param name="Cedible">Agrega la leyenda «CEDIBLE» (solo facturas).</param>
public sealed record OpcionesPdf(string UnidadSii = "SANTIAGO", int? ResolucionNumero = null, string? ResolucionFecha = null, bool Cedible = false);

/// <summary>
/// Representación impresa (PDF, tamaño Carta) de un documento tributario electrónico ya firmado, con el timbre PDF417 y las leyendas que pide el SII:
/// recuadro rojo con RUT, tipo y folio, unidad del SII, datos del emisor y del receptor, detalle, totales y «Timbre Electrónico SII ... Verifique documento: www.sii.cl».
/// Lee todo del XML del documento (sirve para facturas, notas, guías y boletas) y no necesita dependencias.
/// </summary>
public sealed class RepresentacionImpresa
{
    private static readonly double[] Rojo = [0.85, 0.0, 0.0];
    private static readonly double[] Gris = [0.92, 0.92, 0.92];
    private const double Margen = 14.0;
    private static readonly string[] Meses = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    private PdfEscritor _pdf = new();

    private sealed record Datos(
        int Tipo, string Folio, string Fecha, string Vence, string FormaPago,
        Dictionary<string, string> Emisor, Dictionary<string, string> Receptor, Dictionary<string, string> Totales,
        List<Dictionary<string, string>> Lineas, List<Dictionary<string, string>> Referencias);

    /// <returns>Bytes del archivo PDF.</returns>
    public byte[] Generar(DocumentoEmitido documento, OpcionesPdf? opciones = null)
    {
        opciones ??= new OpcionesPdf();
        var d = Leer(documento.Xml);
        _pdf = new PdfEscritor();
        _pdf.NuevaPagina();
        var y = Encabezado(d, opciones.UnidadSii);
        y = Receptor(d, y);
        y = Referencias(d, y);
        y = Detalle(d, y);
        Pie(d, documento, y, opciones);

        return _pdf.Generar($"{DocumentType.Label(d.Tipo)} N° {d.Folio}");
    }

    private static string V(XmlElement? padre, params string[] tags)
    {
        foreach (var t in tags)
        {
            var n = padre?.GetElementsByTagName(t).Item(0);
            if (n is not null && n.InnerText.Trim().Length > 0)
            {
                return n.InnerText.Trim();
            }
        }

        return string.Empty;
    }

    private static Dictionary<string, string> Mapa(XmlElement? padre, params (string Clave, string[] Tags)[] campos)
        => campos.ToDictionary(c => c.Clave, c => V(padre, c.Tags));

    private static Datos Leer(XmlDocument xml)
    {
        if (xml.GetElementsByTagName("Documento").Item(0) is not XmlElement doc)
        {
            throw new ArgumentException("El XML no contiene un <Documento>.");
        }

        XmlElement? Hijo(XmlElement? p, string t) => p?.GetElementsByTagName(t).Item(0) as XmlElement;
        var enc = Hijo(doc, "Encabezado");
        var idDoc = Hijo(enc, "IdDoc");
        var lineas = doc.GetElementsByTagName("Detalle").Cast<XmlElement>().Select(det => Mapa(det,
            ("nombre", ["NmbItem"]), ("descripcion", ["DscItem"]), ("cantidad", ["QtyItem"]), ("unidad", ["UnmdItem"]), ("precio", ["PrcItem"]),
            ("descuentoPct", ["DescuentoPct"]), ("monto", ["MontoItem"]), ("exento", ["IndExe"]))).ToList();
        var refs = doc.GetElementsByTagName("Referencia").Cast<XmlElement>().Select(r => Mapa(r,
            ("tipo", ["TpoDocRef"]), ("folio", ["FolioRef"]), ("fecha", ["FchRef"]), ("razon", ["RazonRef"]), ("codigo", ["CodRef"]))).ToList();

        return new Datos(
            int.TryParse(V(idDoc, "TipoDTE"), out var tipo) ? tipo : 0, V(idDoc, "Folio"), V(idDoc, "FchEmis"), V(idDoc, "FchVenc"), V(idDoc, "FmaPago"),
            Mapa(Hijo(enc, "Emisor"), ("rut", ["RUTEmisor"]), ("razon", ["RznSoc", "RznSocEmisor"]), ("giro", ["GiroEmis", "GiroEmisor"]), ("direccion", ["DirOrigen"]),
                ("comuna", ["CmnaOrigen"]), ("ciudad", ["CiudadOrigen"]), ("correo", ["CorreoEmisor"]), ("telefono", ["Telefono"])),
            Mapa(Hijo(enc, "Receptor"), ("rut", ["RUTRecep"]), ("razon", ["RznSocRecep"]), ("giro", ["GiroRecep"]), ("direccion", ["DirRecep"]), ("comuna", ["CmnaRecep"]), ("ciudad", ["CiudadRecep"])),
            Mapa(Hijo(enc, "Totales"), ("neto", ["MntNeto"]), ("exento", ["MntExe"]), ("tasaIva", ["TasaIVA"]), ("iva", ["IVA"]), ("total", ["MntTotal"])),
            lineas, refs);
    }

    private double Encabezado(Datos d, string unidadSii)
    {
        var p = _pdf;
        var e = d.Emisor;
        const double boxW = 72.0;
        var boxX = PdfEscritor.AnchoMm - Margen - boxW;
        var anchoEmisor = boxX - Margen - 6;

        var y = Margen + 6;
        foreach (var l in p.Envolver(e["razon"].ToUpperInvariant(), anchoEmisor, 13, true))
        {
            p.Texto(Margen, y, l, 13, true);
            y += 5.6;
        }

        y += 1;
        static string Unir(params string[] partes) => string.Join(", ", partes.Where(s => s.Length > 0));
        foreach (var texto in new[]
        {
            e["giro"].Length > 0 ? "Giro: " + e["giro"] : string.Empty,
            Unir(e["direccion"], e["comuna"], e["ciudad"]),
            Unir(e["telefono"].Length > 0 ? "Tel.: " + e["telefono"] : string.Empty, e["correo"]),
        })
        {
            if (texto.Length == 0)
            {
                continue;
            }

            foreach (var l in p.Envolver(texto, anchoEmisor, 8.5))
            {
                p.Texto(Margen, y, l, 8.5);
                y += 4.1;
            }
        }

        const double boxY = Margen;
        const double boxH = 33.0;
        p.Rectangulo(boxX, boxY, boxW, boxH, 0.6, Rojo);
        var cx = boxX + boxW / 2;
        p.TextoCentrado(cx, boxY + 8.5, "R.U.T.: " + FormatoRut(e["rut"]), 12, true, Rojo);
        var ly = boxY + 15.5;
        foreach (var l in p.Envolver(DocumentType.Label(d.Tipo).ToUpperInvariant(), boxW - 6, 11, true))
        {
            p.TextoCentrado(cx, ly, l, 11, true, Rojo);
            ly += 5;
        }

        p.TextoCentrado(cx, boxY + boxH - 3.5, "N° " + d.Folio, 12, true, Rojo);
        p.TextoCentrado(cx, boxY + boxH + 5.5, "S.I.I. - " + unidadSii.ToUpperInvariant(), 9, true, Rojo);

        return Math.Max(y, boxY + boxH + 9) + 4;
    }

    private double Receptor(Datos d, double y)
    {
        var p = _pdf;
        var r = d.Receptor;
        const double x = Margen;
        var ancho = PdfEscritor.AnchoMm - 2 * Margen;
        var generico = r["rut"] == BoletaBuilder.RutGenerico;
        var filas = new List<(string, string)> { ("Fecha de emisión", Fecha(d.Fecha)) };
        if (d.Vence.Length > 0)
        {
            filas.Add(("Fecha de vencimiento", Fecha(d.Vence)));
        }

        filas.Add(("Señor(es)", generico && r["razon"].Length == 0 ? "Consumidor final" : r["razon"]));
        if (!generico)
        {
            filas.Add(("R.U.T.", FormatoRut(r["rut"])));
        }

        if (r["giro"].Length > 0)
        {
            filas.Add(("Giro", r["giro"]));
        }

        var dir = string.Join(", ", new[] { r["direccion"], r["comuna"], r["ciudad"] }.Where(s => s.Length > 0));
        if (dir.Length > 0)
        {
            filas.Add(("Dirección", dir));
        }

        var fp = d.FormaPago switch { "1" => "Contado", "2" => "Crédito", "3" => "Sin costo", _ => string.Empty };
        if (fp.Length > 0)
        {
            filas.Add(("Forma de pago", fp));
        }

        var alto = filas.Count * 4.8 + 3;
        p.Rectangulo(x, y, ancho, alto, 0.25);
        var fy = y + 5;
        foreach (var (k, val) in filas)
        {
            p.Texto(x + 2.5, fy, k, 8.5, true);
            p.Texto(x + 38, fy, val, 8.5);
            fy += 4.8;
        }

        return y + alto + 4;
    }

    private double Referencias(Datos d, double y)
    {
        if (d.Referencias.Count == 0)
        {
            return y;
        }

        var p = _pdf;
        p.Texto(Margen, y + 3.5, "Referencias", 8.5, true);
        y += 6.5;
        foreach (var r in d.Referencias)
        {
            var tipo = int.TryParse(r["tipo"], out var t) ? DocumentType.Label(t) : r["tipo"];
            var cod = r["codigo"] switch { "1" => "Anula documento", "2" => "Corrige texto", "3" => "Corrige montos", _ => string.Empty };
            var texto = $"{tipo} N° {r["folio"]} del {Fecha(r["fecha"])}" + (cod.Length > 0 ? $" - {cod}" : string.Empty) + (r["razon"].Length > 0 ? $": {r["razon"]}" : string.Empty);
            foreach (var l in p.Envolver(texto, PdfEscritor.AnchoMm - 2 * Margen, 8.5))
            {
                p.Texto(Margen, y + 3, l, 8.5);
                y += 4.1;
            }
        }

        return y + 3;
    }

    private double Detalle(Datos d, double y)
    {
        var p = _pdf;
        const double x0 = Margen;
        var x1 = PdfEscritor.AnchoMm - Margen;
        var cCant = x1 - 78;
        var cPrecio = x1 - 52;
        var cDesc = x1 - 30;
        var limite = PdfEscritor.AltoMm - 78.0; // reserva el pie (totales y timbre) en la última página
        double Encabezado(double yy)
        {
            p.Rellenar(x0, yy, x1 - x0, 6.5, Gris);
            p.Rectangulo(x0, yy, x1 - x0, 6.5, 0.25);
            p.Texto(x0 + 2, yy + 4.4, "Descripción", 8.5, true);
            p.TextoDerecha(cCant, yy + 4.4, "Cant.", 8.5, true);
            p.TextoDerecha(cPrecio, yy + 4.4, "Precio", 8.5, true);
            p.TextoDerecha(cDesc, yy + 4.4, "Desc.", 8.5, true);
            p.TextoDerecha(x1 - 2, yy + 4.4, "Valor", 8.5, true);

            return yy + 6.5;
        }

        y = Encabezado(y);
        foreach (var l in d.Lineas)
        {
            var descAncho = cCant - 26 - x0 - 2;
            var texto = l["nombre"] + (l["exento"] == "1" ? " (exento)" : string.Empty) + (l["descripcion"].Length > 0 ? "\n" + l["descripcion"] : string.Empty);
            var lineas = p.Envolver(texto, descAncho, 8.5);
            var alto = lineas.Count * 4.1 + 2;
            if (y + alto > limite)
            {
                p.NuevaPagina();
                y = Encabezado(Margen + 4);
            }

            var ty = y + 3.8;
            foreach (var linea in lineas)
            {
                p.Texto(x0 + 2, ty, linea, 8.5);
                ty += 4.1;
            }

            var cant = l["cantidad"].Length > 0 ? Numero(l["cantidad"]) + (l["unidad"].Length > 0 ? " " + l["unidad"] : string.Empty) : string.Empty;
            p.TextoDerecha(cCant, y + 3.8, cant, 8.5);
            p.TextoDerecha(cPrecio, y + 3.8, l["precio"].Length > 0 ? Numero(l["precio"]) : string.Empty, 8.5);
            p.TextoDerecha(cDesc, y + 3.8, l["descuentoPct"].Length > 0 ? Numero(l["descuentoPct"]) + "%" : string.Empty, 8.5);
            p.TextoDerecha(x1 - 2, y + 3.8, Numero(l["monto"]), 8.5);
            y += alto;
            p.Linea(x0, y, x1, y, 0.1, [0.75, 0.75, 0.75]);
        }

        p.Linea(x0, y, x1, y, 0.25);

        return y + 4;
    }

    private void Pie(Datos d, DocumentoEmitido documento, double y, OpcionesPdf opciones)
    {
        var p = _pdf;
        var x1 = PdfEscritor.AnchoMm - Margen;
        var pieY = PdfEscritor.AltoMm - 76.0;
        if (y > pieY)
        {
            p.NuevaPagina();
        }

        var t = d.Totales;
        var filas = new List<(string, string)>();
        if (t["neto"].Length > 0)
        {
            filas.Add(("Monto neto", Numero(t["neto"])));
        }

        if (t["exento"].Length > 0)
        {
            filas.Add(("Monto exento", Numero(t["exento"])));
        }

        if (t["iva"].Length > 0)
        {
            filas.Add(($"IVA ({(t["tasaIva"].Length > 0 ? Numero(t["tasaIva"]) : "19")}%)", Numero(t["iva"])));
        }

        const double anchoT = 62.0;
        var xt = x1 - anchoT;
        var ty = pieY;
        foreach (var (k, val) in filas)
        {
            p.Texto(xt + 2, ty + 4, k, 9);
            p.TextoDerecha(x1 - 2, ty + 4, "$ " + val, 9);
            ty += 5.2;
        }

        p.Rellenar(xt, ty, anchoT, 7, Gris);
        p.Rectangulo(xt, ty, anchoT, 7, 0.3);
        p.Texto(xt + 2, ty + 4.9, "TOTAL", 10, true);
        p.TextoDerecha(x1 - 2, ty + 4.9, "$ " + Numero(t["total"]), 10, true);

        // Se prueban varias cantidades de columnas y se elige la que da el módulo más grande dentro de 80 x 38 mm (lo más fácil de escanear).
        var enc = new Pdf417Encoder();
        bool[][]? modulos = null;
        var escala = 0.0;
        for (var columnas = 4; columnas <= 20; columnas++)
        {
            bool[][] m;
            try
            {
                m = enc.Encode(documento.Ted, columnas);
            }
            catch (ArgumentException)
            {
                continue; // con tan pocas columnas el símbolo excede las 90 filas permitidas
            }

            var e = Math.Min(80.0 / m[0].Length, 38.0 / (m.Length * 3));
            if (e > escala)
            {
                (modulos, escala) = (m, e);
            }
        }

        escala = Math.Max(escala, Pdf417Encoder.MinXDimMm);
        var cols = modulos![0].Length;
        var filasN = modulos.Length;
        const double tx = Margen;
        p.Matriz(tx, pieY, modulos, escala, escala * 3);
        var altoT = filasN * escala * 3;
        var anchoTimbre = cols * escala;
        var res = opciones.ResolucionNumero is { } n ? $"Res. {n} de {(opciones.ResolucionFecha ?? d.Fecha)[..4]} - " : string.Empty;
        p.TextoCentrado(tx + anchoTimbre / 2, pieY + altoT + 4, "Timbre Electrónico SII", 8, true);
        p.TextoCentrado(tx + anchoTimbre / 2, pieY + altoT + 7.8, res + "Verifique documento: www.sii.cl", 7.5);

        if (opciones.Cedible && d.Tipo is 33 or 34 or 46)
        {
            p.TextoDerecha(x1, PdfEscritor.AltoMm - 12, "CEDIBLE", 10, true);
        }
    }

    private static string FormatoRut(string rut)
    {
        var partes = Rut.ToDteFormat(rut).Split('-');

        return Miles(partes[0]) + "-" + (partes.Length > 1 ? partes[1] : string.Empty);
    }

    private static string Fecha(string iso)
        => DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? $"{f.Day} de {Meses[f.Month - 1]} de {f.Year}" : iso;

    private static string Miles(string digitos)
    {
        var s = digitos.TrimStart('-');
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && (s.Length - i) % 3 == 0)
            {
                sb.Append('.');
            }

            sb.Append(s[i]);
        }

        return (digitos.StartsWith('-') ? "-" : string.Empty) + sb;
    }

    /// <summary>Número con separador de miles «.» y decimal «,».</summary>
    private static string Numero(string n)
    {
        if (n.Length == 0)
        {
            return string.Empty;
        }

        var partes = n.Split('.');
        var dec = partes.Length > 1 ? partes[1].TrimEnd('0') : string.Empty;

        return Miles(partes[0]) + (dec.Length > 0 ? "," + dec : string.Empty);
    }
}
