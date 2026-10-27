using System.Xml;

namespace DteChile;

/// <summary>Empresa que emite los documentos. <paramref name="ActividadEconomica"/> son uno o más códigos de actividad del SII (hasta 4).</summary>
public sealed record Emisor(
    string Rut, string RazonSocial, string Giro, IReadOnlyList<int> ActividadEconomica,
    string? Direccion = null, string? Comuna = null, string? Ciudad = null, string? Correo = null, string? Telefono = null)
{
    /// <summary>Atajo para un solo código de actividad.</summary>
    public Emisor(string rut, string razonSocial, string giro, int actividadEconomica, string? direccion = null, string? comuna = null, string? ciudad = null, string? correo = null, string? telefono = null)
        : this(rut, razonSocial, giro, [actividadEconomica], direccion, comuna, ciudad, correo, telefono)
    {
    }

    public string Rut { get; } = Dte.RutValido(Rut, "emisor");

    internal Dictionary<string, object?> ToMap(bool boleta)
    {
        var m = new Dictionary<string, object?> { ["rut"] = Rut, ["razonSocial"] = RazonSocial, ["giro"] = Giro };
        Dte.Poner(m, "direccion", Direccion);
        Dte.Poner(m, "comuna", Comuna);
        Dte.Poner(m, "ciudad", Ciudad);
        if (!boleta)
        {
            // El esquema de boleta no tiene actividad económica, correo ni teléfono del emisor.
            m["actividadEconomica"] = ActividadEconomica.Count == 1 ? ActividadEconomica[0] : ActividadEconomica.Cast<object?>().ToList();
            Dte.Poner(m, "correo", Correo);
            Dte.Poner(m, "telefono", Telefono);
        }

        return m;
    }
}

/// <summary>Cliente que recibe el documento.</summary>
public sealed record Receptor(string Rut, string RazonSocial, string? Giro = null, string? Direccion = null, string? Comuna = null, string? Ciudad = null, string? Correo = null)
{
    public string Rut { get; } = Dte.RutValido(Rut, "receptor");

    internal Dictionary<string, object?> ToMap()
    {
        var m = new Dictionary<string, object?> { ["rut"] = Rut, ["razonSocial"] = RazonSocial };
        Dte.Poner(m, "giro", Giro);
        Dte.Poner(m, "direccion", Direccion);
        Dte.Poner(m, "comuna", Comuna);
        Dte.Poner(m, "ciudad", Ciudad);
        Dte.Poner(m, "correo", Correo);

        return m;
    }
}

/// <summary>Una línea de detalle. En boletas el precio incluye IVA; en el resto es neto.</summary>
public sealed record Linea(string Nombre, decimal Cantidad, decimal PrecioUnitario, bool Exento = false, decimal? DescuentoPct = null, string? Descripcion = null, string? Unidad = null)
{
    public string Nombre { get; } = string.IsNullOrWhiteSpace(Nombre) ? throw new ArgumentException("La línea necesita un nombre.") : Nombre;

    public decimal Cantidad { get; } = Cantidad > 0 ? Cantidad : throw new ArgumentException($"La cantidad debe ser mayor que cero (línea «{Nombre}»).");

    public decimal PrecioUnitario { get; } = PrecioUnitario >= 0 ? PrecioUnitario : throw new ArgumentException($"El precio no puede ser negativo (línea «{Nombre}»).");

    internal Dictionary<string, object?> ToMap()
    {
        var m = new Dictionary<string, object?> { ["nombre"] = Nombre, ["cantidad"] = Cantidad, ["precioUnitario"] = PrecioUnitario };
        if (Exento)
        {
            m["exento"] = true;
        }

        if (DescuentoPct is not null)
        {
            m["descuentoPct"] = DescuentoPct;
        }

        Dte.Poner(m, "descripcion", Descripcion);
        Dte.Poner(m, "unidad", Unidad);

        return m;
    }
}

/// <summary>Referencia a otro documento (obligatoria en notas de crédito y débito).</summary>
public sealed record Referencia(int TipoDocumento, int Folio, string Fecha, int? Codigo = null, string? Razon = null)
{
    public const int Anula = 1;
    public const int CorrigeTexto = 2;
    public const int CorrigeMontos = 3;

    internal Dictionary<string, object?> ToMap()
    {
        var m = new Dictionary<string, object?> { ["tipoDocumento"] = TipoDocumento, ["folio"] = Folio, ["fecha"] = Fecha };
        if (Codigo is not null)
        {
            m["codigo"] = Codigo;
        }

        Dte.Poner(m, "razon", Razon);

        return m;
    }
}

/// <summary>Documento ya construido, timbrado y firmado, listo para enviar al SII.</summary>
/// <param name="Ted">Bytes ISO-8859-1 del timbre (TED) como texto, los que se codifican en el PDF417 impreso.</param>
public sealed record DocumentoEmitido(int Tipo, int Folio, long MontoTotal, string FechaEmision, string RutEmisor, string RutReceptor, XmlDocument Xml, string Ted)
{
    public bool EsBoleta => DocumentType.IsBoleta(Tipo);

    /// <summary>XML firmado tal como se transmite (ISO-8859-1).</summary>
    public string ToXml() => XmlIo.ToXml(Xml);

    /// <summary>El timbre como código PDF417 en SVG, listo para incrustar en la representación impresa.</summary>
    public string TimbreSvg()
    {
        var encoder = new Pdf417Encoder();

        return encoder.ToSvg(encoder.Encode(Ted));
    }
}

/// <summary>Resultado de subir un envío al SII. El <paramref name="TrackId"/> sirve para consultar el estado después.</summary>
public sealed record ResultadoEnvio(string TrackId, int Documentos);

/// <summary>Entrega el siguiente folio libre de un tipo de documento. Cada folio debe usarse una sola vez.</summary>
public interface IFolioStore
{
    /// <summary>Reserva y devuelve el siguiente folio del rango [desde, hasta] del CAF.</summary>
    /// <exception cref="InvalidOperationException">Si el rango se agotó.</exception>
    int Siguiente(int tipo, int desde, int hasta);
}

/// <summary>Folios en memoria: sirve para pruebas; al terminar el proceso se pierde el avance.</summary>
public sealed class MemoriaFolios : IFolioStore
{
    private readonly Dictionary<int, int> _ultimo = [];
    private readonly object _lock = new();

    public int Siguiente(int tipo, int desde, int hasta)
    {
        lock (_lock)
        {
            var n = Math.Max(_ultimo.GetValueOrDefault(tipo), desde - 1) + 1;
            if (n > hasta)
            {
                throw new InvalidOperationException($"Se agotaron los folios del tipo {tipo} (rango {desde}-{hasta}). Solicite un CAF nuevo al SII.");
            }

            return _ultimo[tipo] = n;
        }
    }
}

/// <summary>Folios persistidos en un archivo JSON, con bloqueo de archivo para que dos procesos no entreguen el mismo folio.</summary>
public sealed class ArchivoFolios(string ruta) : IFolioStore
{
    public int Siguiente(int tipo, int desde, int hasta)
    {
        using var fs = new FileStream(ruta, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string texto;
        using (var lector = new StreamReader(fs, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true))
        {
            texto = lector.ReadToEnd();
        }

        var datos = string.IsNullOrWhiteSpace(texto)
            ? new Dictionary<string, int>()
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(texto) ?? [];
        var n = Math.Max(datos.GetValueOrDefault(tipo.ToString()), desde - 1) + 1;
        if (n > hasta)
        {
            throw new InvalidOperationException($"Se agotaron los folios del tipo {tipo} (rango {desde}-{hasta}). Solicite un CAF nuevo al SII.");
        }

        datos[tipo.ToString()] = n;
        fs.SetLength(0);
        using var escritor = new StreamWriter(fs, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true);
        escritor.Write(System.Text.Json.JsonSerializer.Serialize(datos, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");

        return n;
    }
}

internal static class Dte
{
    internal static string RutValido(string rut, string quien)
        => Rut.IsValid(rut) ? rut : throw new ArgumentException($"El RUT del {quien} no es válido: {rut}");

    internal static void Poner(Dictionary<string, object?> m, string clave, string? valor)
    {
        if (valor is not null)
        {
            m[clave] = valor;
        }
    }
}
