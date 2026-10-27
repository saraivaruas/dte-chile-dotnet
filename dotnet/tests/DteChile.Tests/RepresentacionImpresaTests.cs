using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DteChile;

namespace DteChile.Tests;

public class RepresentacionImpresaTests
{
    private static readonly Dictionary<string, object?> Fecha = new() { ["fechaEmision"] = "2026-09-08" };

    private static Facturador Crear(params int[] tipos)
    {
        var f = new Facturador(
            new Emisor("76.123.456-0", "Inge Innova SpA", "Servicios de Ingeniería", 711001, "Av. Siempre Viva 123", "Providencia", "Santiago"),
            Fixtures.Cert(), new MemoriaFolios());
        foreach (var t in tipos)
        {
            f.AgregarCaf(Fixtures.NewCaf(t, 1, 100).Caf);
        }

        return f;
    }

    private static Receptor Cliente() => new("77.111.222-6", "Cliente de Prueba Ltda", "Comercio", "Calle Falsa 456", "Las Condes");

    private static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);

    /// <summary>Valida la estructura del PDF: cabecera, tabla xref con desplazamientos exactos y largos de flujo correctos.</summary>
    private static void VerificaPdf(string pdf)
    {
        Assert.StartsWith("%PDF-1.4", pdf);
        Assert.EndsWith("%%EOF", pdf.TrimEnd());
        var sx = Regex.Match(pdf, @"startxref\n(\d+)\n%%EOF");
        Assert.True(sx.Success, "Falta startxref.");
        Assert.Equal("xref", pdf.Substring(int.Parse(sx.Groups[1].Value), 4));
        var total = int.Parse(Regex.Match(pdf, @"xref\n0 (\d+)\n").Groups[1].Value);
        var offs = Regex.Matches(pdf, @"(\d{10}) 00000 n \n");
        Assert.Equal(total - 1, offs.Count);
        for (var i = 0; i < offs.Count; i++)
        {
            Assert.StartsWith($"{i + 1} 0 obj", pdf[int.Parse(offs[i].Groups[1].Value)..]);
        }

        foreach (Match m in Regex.Matches(pdf, @"/Length (\d+) >>\nstream\n"))
        {
            var ini = m.Index + m.Length;
            var largo = int.Parse(m.Groups[1].Value);
            Assert.Equal("endstream", pdf.Substring(ini + largo, 9));
        }
    }

    [Fact]
    public void Factura_sale_como_PDF_valido_con_los_textos_del_SII_y_los_montos_correctos()
    {
        var f = Crear(33);
        var doc = f.Factura(Cliente(), [new Linea("Consultoría técnica", 10, 25000), new Linea("Capacitación", 1, 50000, true)], Fecha);
        var pdf = Latin1(f.Pdf(doc, new OpcionesPdf("Santiago Centro", Cedible: true)));
        VerificaPdf(pdf);
        foreach (var texto in new[] { "R.U.T.: 76.123.456-0", "S.I.I. - SANTIAGO CENTRO", "Verifique documento: www.sii.cl", "Res. 0 de 2026", "$ 250.000", "$ 47.500", "$ 50.000", "$ 347.500", "TOTAL", "CEDIBLE", "8 de septiembre de 2026", "Cliente de Prueba Ltda", "FACTURA ELECTRÓNICA" })
        {
            Assert.Contains(texto, pdf);
        }
    }

    [Fact]
    public void El_timbre_PDF417_dibujado_en_el_PDF_se_decodifica_byte_a_byte_igual_al_TED()
    {
        var f = Crear(33);
        var doc = f.Factura(Cliente(), [new Linea("Café con leche y ñandú & «cía»", 2, 2500)], Fecha);
        var pdf = Latin1(f.Pdf(doc));
        var rects = Regex.Matches(pdf, @"^([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+) re$", RegexOptions.Multiline)
            .Select(m => (X: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Y: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                W: double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), H: double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture))).ToList();
        Assert.True(rects.Count > 50, "No hay rectángulos del timbre.");
        var w = rects.Min(r => r.W);
        var h = rects[0].H;
        var x0 = rects.Min(r => r.X);
        var yMax = rects.Max(r => r.Y);
        var filas = new SortedDictionary<int, HashSet<int>>();
        foreach (var r in rects)
        {
            var fila = (int)Math.Round((yMax - r.Y) / h);
            var col = (int)Math.Round((r.X - x0) / w);
            var n = (int)Math.Round(r.W / w);
            if (!filas.TryGetValue(fila, out var set))
            {
                filas[fila] = set = [];
            }

            for (var i = 0; i < n; i++)
            {
                set.Add(col + i);
            }
        }

        var ancho = filas.Values.Max(s => s.Max()) + 1;
        var matriz = filas.Values.Select(s => Enumerable.Range(0, ancho).Select(c => s.Contains(c)).ToArray()).ToArray();
        Assert.Equal(doc.Ted, Pdf417Decoder.Decode(matriz, (ancho - 69) / 17));
    }

    [Fact]
    public void Nota_de_credito_con_referencia_boleta_sin_receptor_y_muchas_lineas_en_varias_paginas()
    {
        var f = Crear(61, 39, 33);
        var nc = f.NotaCredito(Cliente(), [new Linea("Consultoría", 1, 25000)], [new Referencia(33, 7, "2026-09-08", Referencia.Anula, "Anula factura")], Fecha);
        var pdf = Latin1(f.Pdf(nc));
        VerificaPdf(pdf);
        Assert.Contains("Referencias", pdf);
        Assert.Contains("Anula documento", pdf);
        var b = Latin1(f.Pdf(f.Boleta([new Linea("Café", 2, 2500)], null, Fecha)));
        VerificaPdf(b);
        Assert.Contains("Consumidor final", b);
        var muchas = Enumerable.Range(1, 55).Select(i => new Linea($"Producto número {i} con una descripción larga para ocupar espacio", 1, 1000 + i)).ToList();
        var grande = Latin1(f.Pdf(f.Factura(Cliente(), muchas, Fecha)));
        VerificaPdf(grande);
        Assert.True(Regex.Matches(grande, "/Type /Page ").Count >= 2, "Con 55 líneas debe usar más de una página.");
    }

    [Fact]
    public void Parentesis_y_barra_se_escapan_y_lo_que_no_existe_en_Latin1_pasa_a_signo_de_pregunta()
    {
        var f = Crear(33);
        var pdf = Latin1(f.Pdf(f.Factura(Cliente(), [new Linea(@"Servicio (mensual) \ especial", 1, 1000)], Fecha)));
        VerificaPdf(pdf);
        Assert.Contains(@"Servicio \(mensual\) \\ especial", pdf);
        Assert.Equal("Café ?", PdfEscritor.Codificar("Café ★"));
    }
}
