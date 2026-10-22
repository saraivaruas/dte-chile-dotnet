using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DteChile;

/// <summary>
/// Certificado digital (.pfx/.p12) con que la empresa firma los DTE y se autentica ante el SII. Se abre desde memoria (nunca toca el disco) y no sabe nada de
/// cómo el proyecto que lo use lo almacena.
/// </summary>
public sealed class Certificate
{
    private readonly X509Certificate2 _cert;

    private Certificate(X509Certificate2 cert) => _cert = cert;

    /// <exception cref="ArgumentException">Si el archivo no es un .pfx/.p12 válido, la contraseña es incorrecta o no trae llave privada RSA.</exception>
    public static Certificate FromPkcs12(byte[] rawPfx, string password)
    {
        try
        {
            var cert = new X509Certificate2(rawPfx, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            if (cert.GetRSAPrivateKey() is null)
            {
                throw new ArgumentException("El certificado no trae una llave privada RSA.");
            }

            return new Certificate(cert);
        }
        catch (CryptographicException)
        {
            throw new ArgumentException("No fue posible abrir el certificado — revise el archivo y la contraseña.");
        }
    }

    public RSA PrivateKey => _cert.GetRSAPrivateKey() ?? throw new InvalidOperationException("No fue posible leer la llave privada del certificado.");

    /// <summary>Certificado en formato PEM (texto, con -----BEGIN CERTIFICATE-----).</summary>
    public string CertificatePem => _cert.ExportCertificatePem();

    /// <summary>El certificado en DER codificado en base64, sin cabeceras ni saltos de línea (lo que va en <c>X509Certificate</c>).</summary>
    public string CertificateBase64 => Convert.ToBase64String(_cert.RawData);

    /// <summary>
    /// RUT de la persona o empresa dueña del certificado, tal como la autoridad certificadora lo codifica en el campo Subject: serialNumber o CN son lo más común,
    /// y los certificados de persona natural de E-CertChile lo traen en OU.
    /// </summary>
    public string? SubjectRut()
    {
        string? Campo(string oid) => _cert.SubjectName.EnumerateRelativeDistinguishedNames().Where(r => r.GetSingleElementType().Value == oid)
            .Select(r => r.GetSingleElementValue()).FirstOrDefault();

        var raw = Campo("2.5.4.5");
        var ou = Campo("2.5.4.11");
        if (string.IsNullOrEmpty(raw) && ou is not null && System.Text.RegularExpressions.Regex.IsMatch(ou, @"^\d{1,8}-[\dKk]$"))
        {
            raw = ou;
        }

        if (string.IsNullOrEmpty(raw) && Campo("2.5.4.3") is { } cn && System.Text.RegularExpressions.Regex.Match(cn, @"\d{1,8}-[\dKk]") is { Success: true } m)
        {
            raw = m.Value;
        }

        return string.IsNullOrEmpty(raw) ? null : Rut.ToDteFormat(raw);
    }

    public DateTimeOffset NotAfter => _cert.NotAfter.ToUniversalTime();
}
