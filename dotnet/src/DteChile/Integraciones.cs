using System.Data.Common;
using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace DteChile;

/// <summary>
/// Folios en su propia base de datos (SQL Server, MySQL, PostgreSQL, SQLite...), sin bloqueos largos: cada folio se reserva con una actualización
/// condicional («compare-and-swap»), así dos procesos o servidores nunca reciben el mismo folio. Crea la tabla si no existe.
/// </summary>
/// <param name="abrir">Entrega una conexión abierta (se cierra al terminar cada reserva).</param>
public sealed partial class DbFolios : IFolioStore
{
    private readonly Func<DbConnection> _abrir;
    private readonly string _tabla;
    private readonly int _intentos;

    public DbFolios(Func<DbConnection> abrir, string tabla = "dte_folios", int intentos = 25)
    {
        if (!NombreTabla().IsMatch(tabla))
        {
            throw new ArgumentException("El nombre de la tabla de folios solo admite letras, números y «_».");
        }

        _abrir = abrir;
        _tabla = tabla;
        _intentos = intentos;
        using var c = _abrir();
        Ejecutar(c, $"CREATE TABLE IF NOT EXISTS {_tabla} (tipo INT NOT NULL PRIMARY KEY, ultimo INT NOT NULL)");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NombreTabla();

    public int Siguiente(int tipo, int desde, int hasta)
    {
        for (var i = 0; i < _intentos; i++)
        {
            using var c = _abrir();
            var fila = Escalar(c, $"SELECT ultimo FROM {_tabla} WHERE tipo = @t", ("@t", tipo));
            int? actual = fila is null or DBNull ? null : Convert.ToInt32(fila);
            var n = Math.Max(actual ?? 0, desde - 1) + 1;
            if (n > hasta)
            {
                throw new InvalidOperationException($"Se agotaron los folios del tipo {tipo} (rango {desde}-{hasta}). Solicite un CAF nuevo al SII.");
            }

            try
            {
                if (actual is null)
                {
                    Ejecutar(c, $"INSERT INTO {_tabla} (tipo, ultimo) VALUES (@t, @n)", ("@t", tipo), ("@n", n));

                    return n;
                }

                if (Ejecutar(c, $"UPDATE {_tabla} SET ultimo = @n WHERE tipo = @t AND ultimo = @a", ("@n", n), ("@t", tipo), ("@a", actual.Value)) == 1)
                {
                    return n;
                }
            }
            catch (DbException)
            {
                // otro proceso insertó la fila al mismo tiempo: se vuelve a leer
            }
        }

        throw new InvalidOperationException("No fue posible reservar un folio: la tabla de folios está muy disputada. Reintente.");
    }

    private static DbCommand Comando(DbConnection c, string sql, (string, object)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (nombre, valor) in ps)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = nombre;
            p.Value = valor;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    private static int Ejecutar(DbConnection c, string sql, params (string, object)[] ps)
    {
        using var cmd = Comando(c, sql, ps);

        return cmd.ExecuteNonQuery();
    }

    private static object? Escalar(DbConnection c, string sql, params (string, object)[] ps)
    {
        using var cmd = Comando(c, sql, ps);

        return cmd.ExecuteScalar();
    }
}

/// <summary>Servidor SMTP para enviar el documento por correo. <paramref name="Ssl"/> activa el cifrado (STARTTLS en 587 o SSL implícito en 465, según el servidor).</summary>
public sealed record ConfigSmtp(string Servidor, int Puerto, string Remitente, string? Usuario = null, string? Clave = null, bool Ssl = true, string NombreRemitente = "", int TimeoutSegundos = 20)
{
    public string Remitente { get; } = new MailAddress(Remitente).Address;
}

/// <summary>Opciones de <see cref="Facturador.EnviarPorCorreoAsync"/>.</summary>
/// <param name="ConPdf">Adjuntar también el PDF de la representación impresa.</param>
/// <param name="Pdf">Opciones del PDF (logotipo, unidad del SII, cedible).</param>
public sealed record OpcionesCorreo(string? Asunto = null, string? Texto = null, bool ConPdf = true, OpcionesPdf? Pdf = null);

internal static class Smtp
{
    public static async Task EnviarAsync(MailMessage mensaje, ConfigSmtp cfg, CancellationToken ct)
    {
        using var smtp = new SmtpClient(cfg.Servidor, cfg.Puerto) { EnableSsl = cfg.Ssl, Timeout = cfg.TimeoutSegundos * 1000 };
        if (cfg.Usuario is not null)
        {
            smtp.Credentials = new NetworkCredential(cfg.Usuario, cfg.Clave);
        }

        await smtp.SendMailAsync(mensaje, ct);
    }
}
