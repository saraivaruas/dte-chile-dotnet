using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;
using DteChile;

namespace DteChile.Tests;

/// <summary>Datos de prueba generados al vuelo, sin archivos ni secretos reales: un certificado autofirmado y un CAF falso con su propio par de llaves RSA.</summary>
public static class Fixtures
{
    public const string Password = "clave-de-prueba";

    public static (byte[] Pfx, string CertPem) NewCertificate(string rutEnSerialNumber = "11111111-1")
    {
        using var rsa = RSA.Create(2048);
        var nombre = new X500DistinguishedNameBuilder();
        nombre.AddCountryOrRegion("CL");
        nombre.AddCommonName("Persona de Prueba");
        nombre.Add("2.5.4.5", rutEnSerialNumber);
        var req = new CertificateRequest(nombre.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

        return (cert.Export(X509ContentType.Pfx, Password), cert.ExportCertificatePem());
    }

    public static Certificate Cert() => Certificate.FromPkcs12(NewCertificate().Pfx, Password);

    /// <summary>CAF falso (tipo, folios desde-hasta) y la llave pública para verificar los timbres de forma independiente.</summary>
    public static (string Xml, Caf Caf, string PublicPem) NewCaf(int tipo = 33, int desde = 1, int hasta = 100, bool indentado = true)
    {
        using var rsa = RSA.Create(1024);
        var p = rsa.ExportParameters(false);
        var nl = indentado ? "\n      " : string.Empty;
        var xml = "<?xml version=\"1.0\"?>\n<AUTORIZACION>\n  <CAF version=\"1.0\">\n    <DA>" + nl + "<RE>76123456-0</RE>" + nl + "<RS>EMPRESA DE PRUEBA SPA</RS>" + nl + $"<TD>{tipo}</TD>"
            + nl + $"<RNG><D>{desde}</D><H>{hasta}</H></RNG>" + nl + "<FA>2026-01-01</FA>" + nl + $"<RSAPK><M>{Convert.ToBase64String(p.Modulus!)}</M><E>{Convert.ToBase64String(p.Exponent!)}</E></RSAPK>"
            + nl + "<IDK>100</IDK>\n    </DA>\n    <FRMA algoritmo=\"SHA1withRSA\">" + Convert.ToBase64String(new byte[64]) + "</FRMA>\n  </CAF>\n"
            + "  <RSASK>" + rsa.ExportRSAPrivateKeyPem() + "</RSASK>\n  <RSAPUBK>" + rsa.ExportSubjectPublicKeyInfoPem() + "</RSAPUBK>\n</AUTORIZACION>\n";

        return (xml, Caf.FromXml(xml), rsa.ExportSubjectPublicKeyInfoPem());
    }

    public static Dictionary<string, object?> Factura(int folio = 1, Action<Dictionary<string, object?>>? cambios = null, IEnumerable<Dictionary<string, object?>>? items = null)
    {
        var datos = new Dictionary<string, object?>
        {
            ["folio"] = folio,
            ["fechaEmision"] = "2026-09-08",
            ["emisor"] = new Dictionary<string, object?>
            {
                ["rut"] = "76.123.456-0", ["razonSocial"] = "Inge Innova SpA", ["giro"] = "Servicios de Ingeniería", ["actividadEconomica"] = 711001,
                ["direccion"] = "Av. Siempre Viva 123", ["comuna"] = "Providencia", ["ciudad"] = "Santiago",
            },
            ["receptor"] = new Dictionary<string, object?>
            {
                ["rut"] = "77.111.222-6", ["razonSocial"] = "Cliente de Prueba Ltda", ["giro"] = "Comercio", ["direccion"] = "Calle Falsa 456", ["comuna"] = "Las Condes",
            },
            ["items"] = (items ?? [Item("Consultoría técnica", 10, 25000)]).Cast<object?>().ToList(),
        };
        cambios?.Invoke(datos);

        return datos;
    }

    public static Dictionary<string, object?> Item(string nombre, decimal cantidad, decimal precio, Action<Dictionary<string, object?>>? extra = null)
    {
        var d = new Dictionary<string, object?> { ["nombre"] = nombre, ["cantidad"] = cantidad, ["precioUnitario"] = precio };
        extra?.Invoke(d);

        return d;
    }
}

/// <summary>Verificadores independientes del código de la biblioteca.</summary>
public static class Verify
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "schemas")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la carpeta schemas/ del repositorio.");
    }

    /// <summary>Valida el XML contra un esquema oficial del SII; si no cumple, lanza con los primeros problemas.</summary>
    public static void Xsd(byte[] xml, string xsd, string ns = "http://www.sii.cl/SiiDte")
    {
        // Algunos esquemas del SII traen límites de 34 dígitos (999...9) que .NET no acepta; se validan con una copia con el límite reducido.
        var origen = Path.GetDirectoryName(Path.Combine(RepoRoot(), "schemas", xsd))!;
        var tmp = Path.Combine(Path.GetTempPath(), "xsd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        foreach (var f in Directory.GetFiles(origen, "*.xsd"))
        {
            File.WriteAllBytes(Path.Combine(tmp, Path.GetFileName(f)), Encoding.Latin1.GetBytes(Regex.Replace(Encoding.Latin1.GetString(File.ReadAllBytes(f)), @"9{19,}(\.9+)?", "999999999999999")));
        }

        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        set.Add(ns, Path.Combine(tmp, Path.GetFileName(xsd)));
        var errores = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = set, DtdProcessing = DtdProcessing.Prohibit };
        settings.ValidationEventHandler += (_, e) => errores.Add($"línea {e.Exception?.LineNumber}: {e.Message}");
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        while (reader.Read())
        {
        }

        Assert.True(errores.Count == 0, $"No cumple el esquema oficial {xsd}: " + string.Join(" | ", errores.Take(4)));
    }

    /// <summary>Verifica con la implementación de XMLDSig de Microsoft cada firma del documento; devuelve cuántas había.</summary>
    public static int XmlDsig(byte[] xml, string certPem)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(new MemoryStream(xml));
        var firmas = doc.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#");
        Assert.True(firmas.Count > 0, "No hay ninguna firma XMLDSig.");
        using var cert = X509Certificate2.CreateFromPem(certPem);
        using var rsa = cert.GetRSAPublicKey()!;
        foreach (XmlElement firma in firmas)
        {
            var signed = new SignedXml(doc);
            signed.LoadXml(firma);
            Assert.True(signed.CheckSignature(rsa), "La firma XMLDSig no es válida (resumen o firma del SignedInfo).");
        }

        return firmas.Count;
    }

    /// <summary>Verifica el timbre: firma del &lt;DD&gt; sin espacios entre etiquetas, con la llave pública del CAF, sobre los bytes finales del documento.</summary>
    public static void Ted(byte[] xml, string cafPublicPem)
    {
        var texto = Encoding.Latin1.GetString(xml);
        var dd = Regex.Match(texto, "<DD>.*</DD>", RegexOptions.Singleline);
        var frmt = Regex.Match(texto, "<FRMT[^>]*>([^<]+)</FRMT>");
        Assert.True(dd.Success && frmt.Success, "No se encontró <DD> o <FRMT> en el documento final.");
        var bytes = Encoding.Latin1.GetBytes(Regex.Replace(dd.Value, @">\s+<", "><"));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(cafPublicPem);
        Assert.True(rsa.VerifyData(bytes, Convert.FromBase64String(frmt.Groups[1].Value), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1), "Firma del timbre (TED) inválida.");
    }
}

public static class Pipeline
{
    /// <summary>DTE completo: construye, timbra y firma.</summary>
    public static (XmlDocument Firmado, (string Xml, Caf Caf, string PublicPem) Caf, Certificate Cert) Dte(
        Dictionary<string, object?> datos, int tipo = 33, (string Xml, Caf Caf, string PublicPem)? caf = null, Certificate? cert = null)
    {
        var c = caf ?? Fixtures.NewCaf(tipo, 1, 100);
        cert ??= Fixtures.Cert();
        var b = new DteBuilder();
        var doc = b.Build(tipo, datos);
        var datosTed = new Dictionary<string, object?>(datos) { ["montoTotal"] = b.MontoTotal(doc) };
        b.AttachTed(doc, new TedSigner().Sign(c.Caf, tipo, datosTed));

        return (new DteSigner().Sign(doc, cert), c, cert);
    }

    public static byte[] Bytes(XmlDocument d) => XmlIo.ToBytes(d);
}
