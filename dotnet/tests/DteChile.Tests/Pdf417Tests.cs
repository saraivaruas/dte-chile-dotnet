using System.Reflection;
using System.Xml;
using DteChile;

namespace DteChile.Tests;

/// <summary>
/// Decodificador PDF417 independiente del encoder (solo para pruebas): reconstruye los bytes originales a partir
/// de la matriz de módulos. Reimplementa, a partir de la especificación, el mismo criterio que usa el decoder de
/// referencia de ZXing (DecodedBitStreamParser.byteCompaction) para distinguir un grupo de 5 codewords (6 bytes)
/// de una cola literal de 1-5 bytes: solo es un grupo de 6 bytes si, tras consumirlo, sigue habiendo más datos (o
/// el modo era 924); si no, se reinterpreta como bytes literales.
/// </summary>
internal static class Pdf417Decoder
{
    private static readonly Dictionary<int, (int Cluster, int Word)> Reverse;
    private static readonly int StartPattern;
    private static readonly int StopPattern;

    static Pdf417Decoder()
    {
        var type = typeof(Pdf417Encoder);
        var table = (int[][]) type.GetField("CodewordTable", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        StartPattern = (int) type.GetField("StartPattern", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        StopPattern = (int) type.GetField("StopPattern", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        Reverse = new Dictionary<int, (int, int)>();
        for (var cluster = 0; cluster < table.Length; cluster++)
        {
            for (var word = 0; word < table[cluster].Length; word++)
            {
                Reverse[table[cluster][word]] = (cluster, word);
            }
        }
    }

    public static void AssertValidSymbol(bool[][] matrix)
    {
        Assert.InRange(matrix.Length, 3, 90);
        foreach (var fila in matrix)
        {
            Assert.Equal(matrix[0].Length, fila.Length);
        }
    }

    public static string Decode(bool[][] matrix, int columns)
    {
        var codewords = new List<int>();
        foreach (var bits in matrix)
        {
            var pos = 0;
            Assert.Equal(StartPattern, Read(bits, ref pos, 17));
            Assert.True(Reverse.ContainsKey(Read(bits, ref pos, 17)), "indicador izquierdo no reconocido");
            for (var c = 0; c < columns; c++)
            {
                var pattern = Read(bits, ref pos, 17);
                Assert.True(Reverse.TryGetValue(pattern, out var cw), "codeword no reconocido en la tabla");
                codewords.Add(cw.Word);
            }
            Assert.True(Reverse.ContainsKey(Read(bits, ref pos, 17)), "indicador derecho no reconocido");
            Assert.Equal(StopPattern, Read(bits, ref pos, 18));
            Assert.Equal(bits.Length, pos);
        }

        var lengthDescriptor = codewords[0];
        var extended = codewords.GetRange(0, lengthDescriptor); // sin los codewords de corrección de errores
        var mode = extended[1]; // primer codeword tras el length descriptor
        var dataYRelleno = extended.GetRange(2, extended.Count - 2);

        var corte = dataYRelleno.Count;
        for (var i = 0; i < dataYRelleno.Count; i++)
        {
            if (dataYRelleno[i] >= 900)
            {
                corte = i;
                break;
            }
        }
        var palabras = dataYRelleno.GetRange(0, corte);

        var bytes = new List<byte>();
        var idx = 0;
        var total = palabras.Count;
        while (idx < total)
        {
            long value = 0;
            var count = 0;
            do
            {
                value = 900 * value + palabras[idx++];
                count++;
            } while (count < 5 && idx < total);

            if (count == 5 && (mode == 924 || idx < total))
            {
                for (var j = 0; j < 6; j++)
                {
                    bytes.Add((byte) ((value >> (8 * (5 - j))) & 0xFF));
                }
            }
            else
            {
                idx -= count;
                for (var k = 0; k < count; k++)
                {
                    bytes.Add((byte) palabras[idx++]);
                }
            }
        }

        var chars = new char[bytes.Count];
        for (var i = 0; i < bytes.Count; i++)
        {
            chars[i] = (char) bytes[i];
        }

        return new string(chars);
    }

    private static int Read(bool[] bits, ref int pos, int len)
    {
        var value = 0;
        for (var i = 0; i < len; i++)
        {
            value = (value << 1) | (bits[pos++] ? 1 : 0);
        }

        return value;
    }
}

public class Pdf417Tests
{
    [Fact]
    public void Factura_normal_se_decodifica_igual_al_original()
    {
        var caf = Fixtures.NewCaf(33, 1, 100);
        var datos = Fixtures.Factura(7);
        var b = new DteBuilder();
        var doc = b.Build(33, datos);
        var datosTed = new Dictionary<string, object?>(datos) { ["montoTotal"] = b.MontoTotal(doc) };
        var tedDoc = new TedSigner().Sign(caf.Caf, 33, datosTed);
        var tedBytes = Pdf417Encoder.TedToLatin1(tedDoc);

        var matrix = new Pdf417Encoder().Encode(tedBytes);
        Pdf417Decoder.AssertValidSymbol(matrix);

        var columnas = (matrix[0].Length - 17 - 17 - 17 - 18) / 17;
        Assert.Equal(tedBytes, Pdf417Decoder.Decode(matrix, columnas));
    }

    [Fact]
    public void Se_decodifica_igual_con_tildes_ene_y_ampersand()
    {
        var caf = Fixtures.NewCaf(33, 1, 100);
        var datos = Fixtures.Factura(9, d =>
        {
            d["receptor"] = new Dictionary<string, object?> { ["rut"] = "77111222-6", ["razonSocial"] = "Peña & Cía Ñandú" };
        }, items: [Fixtures.Item("Diseño año – café, piñón & limón", 2, 1500)]);
        var b = new DteBuilder();
        var doc = b.Build(33, datos);
        var datosTed = new Dictionary<string, object?>(datos) { ["montoTotal"] = b.MontoTotal(doc) };
        var tedDoc = new TedSigner().Sign(caf.Caf, 33, datosTed);
        var tedBytes = Pdf417Encoder.TedToLatin1(tedDoc);
        Assert.Contains('Ñ', tedBytes); // Ñ en ISO-8859-1 (byte 0xD1 leído como char)

        var matrix = new Pdf417Encoder().Encode(tedBytes);
        Pdf417Decoder.AssertValidSymbol(matrix);

        var columnas = (matrix[0].Length - 17 - 17 - 17 - 18) / 17;
        Assert.Equal(tedBytes, Pdf417Decoder.Decode(matrix, columnas));
    }

    [Fact]
    public void Timbre_de_una_boleta_tambien_se_decodifica_igual_al_original()
    {
        var caf = Fixtures.NewCaf(39, 1, 100);
        var datos = B.Boleta(3);
        var doc = new BoletaBuilder().Build(39, datos);
        var monto = new DteBuilder().MontoTotal(doc);
        var tedDoc = new TedSigner().Sign(caf.Caf, 39, new Dictionary<string, object?>(datos) { ["montoTotal"] = monto });
        var tedBytes = Pdf417Encoder.TedToLatin1(tedDoc);

        var matrix = new Pdf417Encoder().Encode(tedBytes);
        Pdf417Decoder.AssertValidSymbol(matrix);

        var columnas = (matrix[0].Length - 17 - 17 - 17 - 18) / 17;
        Assert.Equal(tedBytes, Pdf417Decoder.Decode(matrix, columnas));
    }

    [Fact]
    public void Rechaza_datos_vacios() =>
        Assert.Throws<ArgumentException>(() => new Pdf417Encoder().Encode(string.Empty));

    [Fact]
    public void ToSvg_produce_un_svg_valido_con_la_quiet_zone_y_la_razon_3a1_minimas()
    {
        var encoder = new Pdf417Encoder();
        var matrix = encoder.Encode("<TED version=\"1.0\"><DD><RE>11111111-1</RE></DD></TED>");
        var svg = encoder.ToSvg(matrix);

        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.EndsWith("</svg>", svg, StringComparison.Ordinal);
        Assert.True(CountOccurrences(svg, "<rect") > matrix.Length, "El SVG debería tener al menos un <rect> por fila además del fondo.");

        Assert.Throws<ArgumentException>(() => encoder.ToSvg(matrix, 0.1));
    }

    [Fact]
    public void Columnas_explicitas_fuera_de_1_30_se_rechazan()
    {
        var encoder = new Pdf417Encoder();
        Assert.Throws<ArgumentException>(() => encoder.Encode("datos de prueba", 0));
        Assert.Throws<ArgumentException>(() => encoder.Encode("datos de prueba", 31));
    }

    private static int CountOccurrences(string texto, string sub)
    {
        var count = 0;
        var idx = 0;
        while ((idx = texto.IndexOf(sub, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += sub.Length;
        }

        return count;
    }
}
