using System.Security.Cryptography;
using System.Xml;

namespace DteChile;

/// <summary>
/// Firma la solicitud de token (<c>&lt;getToken&gt;&lt;item&gt;&lt;Semilla&gt;…&lt;/Semilla&gt;&lt;/item&gt;&lt;/getToken&gt;</c>) con el certificado de la empresa: una firma XMLDSig
/// «enveloped» (referencia el documento completo con <c>URI=""</c>) con UN solo Transform (<c>enveloped-signature</c>), tal como exige el manual oficial del SII
/// (OI2007_AUTAUTOM_MDE): agregar un segundo Transform de C14N hace que el SII la rechace.
/// </summary>
public sealed class TokenRequestSigner
{
    private const string EnvelopedTransform = "http://www.w3.org/2000/09/xmldsig#enveloped-signature";

    public XmlDocument Sign(string semilla, Certificate certificate)
    {
        // Sin encoding en el prólogo: así lo muestra el manual oficial del SII (OI2007_AUTAUTOM_MDE, figura 1.4).
        var doc = XmlIo.NewDocument();
        var getToken = doc.CreateElement("getToken");
        doc.AppendChild(getToken);
        var item = doc.CreateElement("item");
        var s = doc.CreateElement("Semilla");
        s.AppendChild(doc.CreateTextNode(SiiText.Latin1(semilla, "Semilla")));
        item.AppendChild(s);
        getToken.AppendChild(item);

        // El resumen se calcula ANTES de agregar <Signature>: es sobre el documento tal como queda sin ella.
        var digest = Convert.ToBase64String(SHA1.HashData(C14n.CanonicalizeBytes(getToken)));
        DteSigner.BuildSignature(doc, getToken, string.Empty, digest, certificate, [EnvelopedTransform]);

        return doc;
    }
}
