using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using DteChile;
using Microsoft.Data.Sqlite;

namespace DteChile.Tests;

public class IntegracionesTests
{
    private const string JpegBase64 = "/9j/4AAQSkZJRgABAQEAYABgAAD//gA7Q1JFQVRPUjogZ2QtanBlZyB2MS4wICh1c2luZyBJSkcgSlBFRyB2ODApLCBxdWFsaXR5ID0gODAK/9sAQwAGBAUGBQQGBgUGBwcGCAoQCgoJCQoUDg8MEBcUGBgXFBYWGh0lHxobIxwWFiAsICMmJykqKRkfLTAtKDAlKCko/9sAQwEHBwcKCAoTCgoTKBoWGigoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgo/8AAEQgAPAB4AwEiAAIRAQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMBAQEBAQEBAQEAAAAAAAABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMRBAUhMQYSQVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJicoKSo1Njc4OTpDREVGR0hJSlNUVVZXWFlaY2RlZmdoaWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uLj5OXm5+jp6vLz9PX29/j5+v/aAAwDAQACEQMRAD8A87ooor7Y+QCiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigD/9k=";

    private static readonly Dictionary<string, object?> Fecha = new() { ["fechaEmision"] = "2026-09-08" };

    private static Facturador Crear()
    {
        var f = new Facturador(
            new Emisor("76.123.456-0", "Inge Innova SpA", "Servicios de Ingeniería", 711001, "Av. Siempre Viva 123", "Providencia", "Santiago"),
            Fixtures.Cert(), new MemoriaFolios());
        f.AgregarCaf(Fixtures.NewCaf(33, 1, 100).Caf);

        return f;
    }

    private static Receptor Cliente() => new("77.111.222-6", "Cliente de Prueba Ltda", "Comercio", "Calle Falsa 456", "Las Condes");

    [Fact]
    public void DbFolios_reserva_folios_consecutivos_por_tipo_persiste_entre_instancias_y_avisa_cuando_se_agotan()
    {
        using var mantener = new SqliteConnection("Data Source=folios1;Mode=Memory;Cache=Shared");
        mantener.Open();
        DbFolios Nuevo(string tabla = "dte_folios") => new(() =>
        {
            var c = new SqliteConnection("Data Source=folios1;Mode=Memory;Cache=Shared");
            c.Open();

            return c;
        }, tabla);
        var a = Nuevo();
        Assert.Equal([5, 6], new[] { a.Siguiente(33, 5, 8), a.Siguiente(33, 5, 8) });
        Assert.Equal(1, a.Siguiente(61, 1, 3));
        Assert.Equal(7, Nuevo().Siguiente(33, 5, 8));
        Assert.Equal(8, a.Siguiente(33, 5, 8));
        Assert.Contains("Se agotaron los folios", Assert.Throws<InvalidOperationException>(() => a.Siguiente(33, 5, 8)).Message);
        Assert.Equal(20, Nuevo().Siguiente(33, 20, 30));
        Assert.Contains("nombre de la tabla", Assert.Throws<ArgumentException>(() => Nuevo("dte; DROP TABLE x")).Message);
    }

    [Fact]
    public void DbFolios_dos_conexiones_a_la_misma_base_nunca_entregan_el_mismo_folio()
    {
        var archivo = Path.Combine(Path.GetTempPath(), $"folios_{Guid.NewGuid():N}.sqlite");
        try
        {
            DbFolios Nuevo() => new(() =>
            {
                var c = new SqliteConnection($"Data Source={archivo};Pooling=False");
                c.Open();

                return c;
            });
            var a = Nuevo();
            var b = Nuevo();
            var vistos = Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? a : b).Siguiente(33, 1, 100)).ToList();
            Assert.Equal(Enumerable.Range(1, 20), vistos);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(archivo);
        }
    }

    [Fact]
    public void El_logotipo_JPEG_se_incrusta_sin_deformarse_y_el_PDF_sigue_siendo_valido()
    {
        var f = Crear();
        var doc = f.Factura(Cliente(), [new Linea("Servicio", 1, 1000)], Fecha);
        var pdf = Encoding.Latin1.GetString(f.Pdf(doc, new OpcionesPdf(Logo: Convert.FromBase64String(JpegBase64))));
        foreach (var t in new[] { "/Subtype /Image", "/Filter /DCTDecode", "/XObject <<", " Do Q" })
        {
            Assert.Contains(t, pdf);
        }

        Assert.DoesNotContain("/XObject", Encoding.Latin1.GetString(f.Pdf(doc)));
        // Los desplazamientos de la tabla xref siguen siendo exactos con la imagen incluida.
        var total = int.Parse(Regex.Match(pdf, @"xref\n0 (\d+)\n").Groups[1].Value);
        var offs = Regex.Matches(pdf, @"(\d{10}) 00000 n \n");
        Assert.Equal(total - 1, offs.Count);
        for (var i = 0; i < offs.Count; i++)
        {
            Assert.StartsWith($"{i + 1} 0 obj", pdf[int.Parse(offs[i].Groups[1].Value)..]);
        }

        Assert.Contains("JPEG", Assert.Throws<ArgumentException>(() => f.Pdf(doc, new OpcionesPdf(Logo: [1, 2, 3, 4, 5]))).Message);
    }

    [Fact]
    public async Task EnviarPorCorreo_arma_el_mensaje_con_el_XML_y_el_PDF_y_valida_los_destinatarios()
    {
        var f = Crear();
        var envios = new List<(string Para, string De, string Asunto, string[] Adjuntos)>();
        f.EnviadorCorreo = (m, _, _) =>
        {
            envios.Add((m.To.Single().Address, m.From!.Address, m.Subject, m.Attachments.Select(a => a.Name ?? string.Empty).ToArray()));

            return Task.CompletedTask;
        };
        var doc = f.Factura(Cliente(), [new Linea("Servicio", 1, 1000)], Fecha);
        var cfg = new ConfigSmtp("smtp.ejemplo.cl", 587, "facturacion@ejemplo.cl", "usuario", "clave", true, "Inge Innova SpA");
        await f.EnviarPorCorreoAsync(doc, ["cliente@ejemplo.cl"], cfg);
        await f.EnviarPorCorreoAsync(doc, ["cliente@ejemplo.cl"], cfg, new OpcionesCorreo(ConPdf: false));
        Assert.Equal(2, envios.Count);
        Assert.Equal("cliente@ejemplo.cl", envios[0].Para);
        Assert.Equal("facturacion@ejemplo.cl", envios[0].De);
        Assert.Contains("Factura Electrónica N° 1 de Inge Innova SpA", envios[0].Asunto);
        Assert.Equal(new[] { "33-1.xml", "33-1.pdf" }, envios[0].Adjuntos);
        Assert.Equal(new[] { "33-1.xml" }, envios[1].Adjuntos);
        await Assert.ThrowsAsync<ArgumentException>(() => f.EnviarPorCorreoAsync(doc, [], cfg));
        Assert.Throws<FormatException>(() => new ConfigSmtp("s", 25, "malo"));
    }
}
