using System.Globalization;
using System.Text;

namespace DteChile;

/// <summary>
/// Escritor de PDF mínimo y sin dependencias (tamaño Carta, fuentes estándar Helvetica, sin imágenes): lo justo para la representación impresa.
/// Las coordenadas son milímetros con el origen arriba a la izquierda. El texto se recibe en Unicode y se escribe en WinAnsi (compatible con ISO-8859-1).
/// </summary>
internal sealed class PdfEscritor
{
    public const double AnchoMm = 215.9;
    public const double AltoMm = 279.4;
    private const double Pt = 72 / 25.4;

    /// <summary>Anchos Helvetica (milésimas de em) para ASCII 32..126.</summary>
    private static readonly int[] Anchos =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly Dictionary<char, char> Base = new()
    {
        ['á'] = 'a', ['à'] = 'a', ['ä'] = 'a', ['é'] = 'e', ['è'] = 'e', ['ë'] = 'e', ['ó'] = 'o', ['ö'] = 'o', ['ú'] = 'u', ['ü'] = 'u', ['ñ'] = 'n',
        ['Á'] = 'A', ['É'] = 'E', ['Ó'] = 'O', ['Ú'] = 'U', ['Ñ'] = 'N', ['Í'] = 'I', ['º'] = 'o', ['°'] = 'o',
    };

    private readonly List<StringBuilder> _paginas = [];
    private readonly List<(string Datos, int Ancho, int Alto, string Espacio)> _imagenes = [];

    public int NumeroPaginas => _paginas.Count;

    /// <summary>Registra una imagen JPEG (solo JPEG: el PDF la incrusta tal cual, sin decodificarla) y devuelve su número y su tamaño en píxeles.</summary>
    public (int Numero, int Ancho, int Alto) AgregarJpeg(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            throw new ArgumentException("El logotipo debe ser una imagen JPEG.");
        }

        var i = 2;
        while (i + 9 < bytes.Length)
        {
            if (bytes[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marca = bytes[i + 1];
            if (marca == 0xFF)
            {
                i++;
                continue;
            }

            if (marca is 0xD8 or 0x01 || marca is >= 0xD0 and <= 0xD7)
            {
                i += 2;
                continue;
            }

            var largo = (bytes[i + 2] << 8) | bytes[i + 3];
            if (marca is >= 0xC0 and <= 0xCF && marca is not (0xC4 or 0xC8 or 0xCC))
            {
                var alto = (bytes[i + 5] << 8) | bytes[i + 6];
                var ancho = (bytes[i + 7] << 8) | bytes[i + 8];
                var espacio = bytes[i + 9] switch
                {
                    1 => "DeviceGray",
                    3 => "DeviceRGB",
                    _ => throw new ArgumentException("El JPEG del logotipo debe ser en escala de grises o RGB (no CMYK)."),
                };
                if (ancho < 1 || alto < 1)
                {
                    break;
                }

                _imagenes.Add((Encoding.Latin1.GetString(bytes), ancho, alto, espacio));

                return (_imagenes.Count, ancho, alto);
            }

            i += 2 + largo;
        }

        throw new ArgumentException("No se pudo leer el tamaño del JPEG del logotipo (archivo dañado).");
    }

    /// <summary>Dibuja una imagen registrada con <see cref="AgregarJpeg"/> dentro del rectángulo dado (sin deformarla; centrada).</summary>
    public void Imagen(int numero, int anchoPx, int altoPx, double x, double yMm, double anchoMax, double altoMax)
    {
        var razon = Math.Min(anchoMax / anchoPx, altoMax / altoPx);
        var w = anchoPx * razon;
        var h = altoPx * razon;
        Emitir($"q {Mm(w)} 0 0 {Mm(h)} {Mm(x + (anchoMax - w) / 2)} {Y(yMm + (altoMax + h) / 2)} cm /Im{numero} Do Q");
    }

    public void NuevaPagina() => _paginas.Add(new StringBuilder());

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Mm(double v) => F(v * Pt);

    private static string Y(double mm) => F((AltoMm - mm) * Pt);

    private void Emitir(string s) => _paginas[^1].Append(s).Append('\n');

    private static string Rgb(double[] c) => $"{F(c[0])} {F(c[1])} {F(c[2])}";

    /// <summary>Convierte a los bytes que se escriben en el PDF (Latin-1; lo que no existe en Latin-1 pasa a «?»).</summary>
    public static string Codificar(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (var c in texto)
        {
            sb.Append(c <= 255 ? c : '?');
        }

        return sb.ToString();
    }

    public double Ancho(string texto, double puntos, bool negrita = false)
    {
        var total = 0;
        foreach (var c in texto)
        {
            total += AnchoCaracter(c);
        }

        return total * puntos / 1000 * (negrita ? 1.07 : 1) / Pt;
    }

    private static int AnchoCaracter(char c)
    {
        if (c is >= ' ' and <= '~')
        {
            return Anchos[c - 32];
        }

        if (c == 'í')
        {
            return 278;
        }

        return Base.TryGetValue(c, out var b) ? Anchos[b - 32] : 556;
    }

    private static string Cadena(string texto)
    {
        var sb = new StringBuilder("(");
        foreach (var c in Codificar(texto))
        {
            sb.Append(c switch { '\\' => "\\\\", '(' => "\\(", ')' => "\\)", '\r' => "\\r", '\n' => "\\n", _ => c.ToString() });
        }

        return sb.Append(')').ToString();
    }

    public void Texto(double x, double yMm, string texto, double puntos = 9, bool negrita = false, double[]? color = null)
        => Emitir($"BT {(color is null ? "0 g" : Rgb(color) + " rg")} /{(negrita ? "F2" : "F1")} {F(puntos)} Tf {Mm(x)} {Y(yMm)} Td {Cadena(texto)} Tj ET");

    public void TextoDerecha(double xDerecha, double yMm, string texto, double puntos = 9, bool negrita = false, double[]? color = null)
        => Texto(xDerecha - Ancho(texto, puntos, negrita), yMm, texto, puntos, negrita, color);

    public void TextoCentrado(double xCentro, double yMm, string texto, double puntos = 9, bool negrita = false, double[]? color = null)
        => Texto(xCentro - Ancho(texto, puntos, negrita) / 2, yMm, texto, puntos, negrita, color);

    /// <summary>Parte un texto en líneas que caben en <paramref name="anchoMm"/>.</summary>
    public List<string> Envolver(string texto, double anchoMm, double puntos = 9, bool negrita = false)
    {
        var lineas = new List<string>();
        foreach (var parrafo in texto.Split('\n'))
        {
            var actual = string.Empty;
            foreach (var palabra in parrafo.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var prueba = actual.Length == 0 ? palabra : actual + " " + palabra;
                if (actual.Length > 0 && Ancho(prueba, puntos, negrita) > anchoMm)
                {
                    lineas.Add(actual);
                    actual = palabra;
                }
                else
                {
                    actual = prueba;
                }

                while (Ancho(actual, puntos, negrita) > anchoMm && actual.Length > 1)
                {
                    var n = actual.Length;
                    while (n > 1 && Ancho(actual[..n], puntos, negrita) > anchoMm)
                    {
                        n--;
                    }

                    lineas.Add(actual[..n]);
                    actual = actual[n..];
                }
            }

            lineas.Add(actual);
        }

        return lineas;
    }

    public void Linea(double x1, double y1, double x2, double y2, double grosorMm = 0.2, double[]? color = null)
        => Emitir($"{(color is null ? "0 G" : Rgb(color) + " RG")} {Mm(grosorMm)} w {Mm(x1)} {Y(y1)} m {Mm(x2)} {Y(y2)} l S");

    public void Rectangulo(double x, double yMm, double ancho, double alto, double grosorMm = 0.3, double[]? color = null)
        => Emitir($"{(color is null ? "0 G" : Rgb(color) + " RG")} {Mm(grosorMm)} w {Mm(x)} {Y(yMm + alto)} {Mm(ancho)} {Mm(alto)} re S");

    public void Rellenar(double x, double yMm, double ancho, double alto, double[] color)
        => Emitir($"{Rgb(color)} rg {Mm(x)} {Y(yMm + alto)} {Mm(ancho)} {Mm(alto)} re f");

    /// <summary>Dibuja una matriz de módulos (PDF417) como rectángulos negros, uniendo los módulos contiguos de cada fila.</summary>
    public void Matriz(double x, double yMm, bool[][] modulos, double moduloAncho, double moduloAlto)
    {
        var sb = new StringBuilder("0 g\n");
        for (var fila = 0; fila < modulos.Length; fila++)
        {
            var columnas = modulos[fila];
            int? inicio = null;
            for (var c = 0; c <= columnas.Length; c++)
            {
                var negro = c < columnas.Length && columnas[c];
                if (negro && inicio is null)
                {
                    inicio = c;
                }
                else if (!negro && inicio is not null)
                {
                    sb.Append($"{Mm(x + inicio.Value * moduloAncho)} {Y(yMm + (fila + 1) * moduloAlto)} {Mm((c - inicio.Value) * moduloAncho)} {Mm(moduloAlto)} re\n");
                    inicio = null;
                }
            }
        }

        sb.Append('f');
        Emitir(sb.ToString());
    }

    /// <summary>El archivo PDF completo.</summary>
    public byte[] Generar(string titulo = "")
    {
        if (_paginas.Count == 0)
        {
            NuevaPagina();
        }

        var n = _paginas.Count;
        var objetos = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, n).Select(i => $"{5 + i * 2} 0 R"))}] /Count {n} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
        };
        var w = (AnchoMm * Pt).ToString("0.00", CultureInfo.InvariantCulture);
        var h = (AltoMm * Pt).ToString("0.00", CultureInfo.InvariantCulture);
        var idInfo = 5 + n * 2;
        var xobj = _imagenes.Count == 0 ? string.Empty : " /XObject << " + string.Join(' ', _imagenes.Select((_, k) => $"/Im{k + 1} {idInfo + 1 + k} 0 R")) + " >>";
        for (var i = 0; i < n; i++)
        {
            var contenido = _paginas[i].ToString();
            objetos.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >>{xobj} >> /Contents {5 + i * 2 + 1} 0 R >>");
            objetos.Add($"<< /Length {contenido.Length} >>\nstream\n{contenido}endstream");
        }

        objetos.Add($"<< /Title {Cadena(titulo)} /Producer (dte-chile) >>");
        foreach (var im in _imagenes)
        {
            objetos.Add($"<< /Type /XObject /Subtype /Image /Width {im.Ancho} /Height {im.Alto} /ColorSpace /{im.Espacio} /BitsPerComponent 8 /Filter /DCTDecode /Length {im.Datos.Length} >>\nstream\n{im.Datos}\nendstream");
        }

        var pdf = new StringBuilder("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<int>();
        for (var i = 0; i < objetos.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objetos[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        var total = objetos.Count + 1;
        pdf.Append($"xref\n0 {total}\n0000000000 65535 f \n");
        foreach (var o in offsets)
        {
            pdf.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {total} /Root 1 0 R /Info {idInfo} 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
