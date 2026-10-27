using System.Text.Json;

namespace DteChile;

/// <summary>Estado de un documento en su camino al SII, ya interpretado (sin códigos del SII).</summary>
public enum EstadoDocumento
{
    /// <summary>Emitido y firmado, pero no llegó al SII (error de red o SII caído). Se puede reenviar con <see cref="Facturador.ReenviarAsync"/>.</summary>
    Emitido,

    /// <summary>El SII lo recibió y lo está procesando: consulte de nuevo en unos minutos.</summary>
    Enviado,
    Aceptado,
    ConReparos,
    Rechazado,
}

public static class EstadoDocumentoExtensiones
{
    /// <summary>Ya no cambiará: no hace falta consultar más.</summary>
    public static bool EsFinal(this EstadoDocumento e) => e is EstadoDocumento.Aceptado or EstadoDocumento.ConReparos or EstadoDocumento.Rechazado;
}

/// <summary>Resultado de emitir y enviar un documento: qué se emitió, en qué estado está y, si algo falló, qué hacer.</summary>
/// <param name="TrackId">Número de seguimiento del SII (null si no llegó a enviarse).</param>
/// <param name="Mensaje">Explicación en lenguaje de usuario (vacía si todo va bien).</param>
/// <param name="Intentos">Cuántas veces se intentó enviar.</param>
/// <param name="DetalleTecnico">Mensaje original del SII o de la red, para registro técnico.</param>
public sealed record ResultadoEmision(DocumentoEmitido Documento, EstadoDocumento Estado, string? TrackId = null, string Mensaje = "", int Intentos = 0, string? DetalleTecnico = null)
{
    public bool Exitoso => Estado is EstadoDocumento.Aceptado or EstadoDocumento.ConReparos or EstadoDocumento.Enviado;

    public ResultadoEmision ConEstado(EstadoDocumento estado, string mensaje = "", string? detalle = null) => this with { Estado = estado, Mensaje = mensaje, DetalleTecnico = detalle };
}

/// <summary>Traduce los códigos y mensajes del SII a una explicación y una acción concreta, en lenguaje de usuario.</summary>
public static class MensajesSii
{
    private static readonly (string Fragmento, string Explicacion)[] Reglas =
    [
        ("STATUS=1", "El titular del certificado no tiene permiso para enviar por esta empresa. Autorícelo en el SII (Mi SII → Autorizar representantes de factura electrónica)."),
        ("STATUS=2", "El archivo es demasiado grande o demasiado pequeño. Envíe menos documentos por sobre."),
        ("STATUS=3", "El archivo llegó cortado. Reintente el envío."),
        ("STATUS=5", "La sesión con el SII venció. La biblioteca la renueva y reintenta sola; si persiste, revise la hora del servidor."),
        ("STATUS=6", "La empresa no está autorizada a emitir documentos electrónicos. Revise su estado de postulación y certificación en el SII."),
        ("STATUS=7", "El SII dice que el XML no cumple el esquema. Revise los datos del documento (largos, caracteres y campos obligatorios)."),
        ("STATUS=8", "El SII dice que la firma es inválida. Revise que el certificado sea del titular autorizado y que no esté vencido."),
        ("STATUS=9", "El sistema del SII está bloqueado. Reintente más tarde."),
        ("RSC", "Rechazado por error en el esquema del XML. Revise los datos del documento."),
        ("RFR", "Rechazado por error en la firma. Revise el certificado."),
        ("RCT", "Rechazado por error en la carátula (RUT del emisor, RUT que envía o resolución). Revise los datos de la empresa y la resolución."),
        ("RCH", "El SII rechazó el envío. Revise el detalle en el sitio del SII con el número de seguimiento."),
        ("HTTP 5", "El SII está con problemas temporales. El documento quedó emitido; reenvíelo en unos minutos."),
        ("HTTP 429", "El SII pidió espaciar las solicitudes. Reintente en unos minutos."),
        ("HTTP 401", "El SII no reconoció la sesión. Reintente; si persiste, revise el certificado."),
        ("no está autorizado por este CAF", "El folio no pertenece al CAF cargado. Cargue el CAF correcto o solicite uno nuevo."),
        ("Se agotaron los folios", "No quedan folios. Solicite un CAF nuevo en el SII."),
        ("timed out", "El SII no respondió a tiempo. El documento quedó emitido; reenvíelo en unos minutos."),
        ("No fue posible conectar", "No hay conexión a internet o el SII no responde. El documento quedó emitido; reenvíelo cuando haya conexión."),
    ];

    private static readonly string[] Temporales = ["HTTP 5", "HTTP 429", "timed out", "No fue posible conectar", "STATUS=3", "STATUS=9", "Connection", "task was canceled"];

    public static string Explicar(string mensajeOriginal)
    {
        foreach (var (fragmento, explicacion) in Reglas)
        {
            if (mensajeOriginal.Contains(fragmento, StringComparison.OrdinalIgnoreCase))
            {
                return explicacion;
            }
        }

        return "El SII respondió algo inesperado: " + (mensajeOriginal.Length > 200 ? mensajeOriginal[..200] : mensajeOriginal);
    }

    /// <summary>¿Vale la pena reintentar el mismo envío? Solo los fallos temporales; un rechazo del SII no cambia por reintentar.</summary>
    public static bool EsTemporal(string mensajeOriginal) => Temporales.Any(f => mensajeOriginal.Contains(f, StringComparison.OrdinalIgnoreCase));
}
