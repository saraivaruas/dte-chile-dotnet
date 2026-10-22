using System.Security.Cryptography;
using System.Xml;

namespace DteChile;

/// <summary>
/// Firma XMLDSig (RSA-SHA1 + C14N no exclusivo, tal como exige el SII) del nodo principal del DTE —<c>Documento</c>, <c>Exportaciones</c> o <c>Liquidacion</c>—
/// con el certificado digital de la empresa, y agrega la <c>Signature</c> como hermana de ese nodo dentro de <c>DTE</c>.
/// </summary>
public sealed class DteSigner
{
    internal const string Ds = "http://www.w3.org/2000/09/xmldsig#";
    internal const string C14nAlgorithm = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315";
    internal const string SignatureMethod = "http://www.w3.org/2000/09/xmldsig#rsa-sha1";
    internal const string DigestMethod = "http://www.w3.org/2000/09/xmldsig#sha1";

    private static readonly string[] Nodos = ["Documento", "Exportaciones", "Liquidacion"];

    /// <exception cref="InvalidOperationException">Si el DTE no tiene el nodo a firmar o éste no tiene atributo ID.</exception>
    public XmlDocument Sign(XmlDocument dteDocument, Certificate certificate)
    {
        // El nodo firmado es el hijo directo de <DTE>: «Documento» en los documentos comunes, «Exportaciones» en los de exportación y «Liquidacion» en la
        // liquidación-factura (el esquema del SII los nombra distinto, pero todos llevan el atributo ID y el mismo tratamiento).
        var nombre = dteDocument.DocumentElement?.FirstChild?.Name;
        if (nombre is null || !Nodos.Contains(nombre))
        {
            throw new InvalidOperationException("El DTE no tiene un nodo <Documento>, <Exportaciones> o <Liquidacion> para firmar.");
        }

        return SignElement(dteDocument, nombre, certificate);
    }

    /// <summary>Firma XMLDSig cualquier elemento identificado por nombre de etiqueta y atributo ID (lo usa también el sobre de envío para firmar el <c>SetDTE</c>).</summary>
    public XmlDocument SignElement(XmlDocument document, string tagName, Certificate certificate, string? id = null)
    {
        // Se re-serializa y re-lee el documento antes de firmarlo: así la canonicalización que se firma es exactamente la que recalculará quien reciba los bytes
        // finales (el SII incluido), sin depender de cómo quedó armado el árbol en memoria.
        var doc = XmlIo.Reparse(document);
        // Con id se firma el elemento con ese ID (hay documentos con varios del mismo nombre, como los <Recibo> de un EnvioRecibos); sin id, el primero.
        // La firma queda como último hijo del padre del elemento firmado (<DTE>, <EnvioDTE>, <Cesion>, <AEC>...).
        var target = doc.GetElementsByTagName(tagName).Cast<XmlElement>().FirstOrDefault(e => id is null || e.GetAttribute("ID") == id)
            ?? throw new InvalidOperationException($"El documento no tiene un nodo <{tagName}>" + (id is null ? string.Empty : $" con ID {id}") + " para firmar.");
        id = target.GetAttribute("ID");
        if (id.Length == 0)
        {
            throw new InvalidOperationException($"El nodo <{tagName}> no tiene atributo ID — no se puede referenciar desde la firma.");
        }

        var digest = Convert.ToBase64String(SHA1.HashData(C14n.CanonicalizeBytes(target)));
        BuildSignature(doc, (XmlElement)target.ParentNode!, "#" + id, digest, certificate, [C14nAlgorithm]);

        return doc;
    }

    /// <summary>Agrega la <c>Signature</c> (SignedInfo, SignatureValue y KeyInfo con módulo/exponente y certificado) como último hijo de <paramref name="parent"/>.</summary>
    internal static void BuildSignature(XmlDocument doc, XmlElement parent, string uri, string digestValue, Certificate certificate, string[] transforms)
    {
        // <Signature> se adjunta al árbol ANTES de canonicalizar <SignedInfo>: la canonicalización inclusiva arrastra los espacios de nombres en alcance de los
        // ancestros, y debe ser la misma que recalcula cualquier verificador sobre el documento final.
        var signature = El(doc, "Signature");
        parent.AppendChild(signature);

        var signedInfo = El(doc, "SignedInfo");
        signature.AppendChild(signedInfo);
        signedInfo.AppendChild(El(doc, "CanonicalizationMethod", ("Algorithm", C14nAlgorithm)));
        signedInfo.AppendChild(El(doc, "SignatureMethod", ("Algorithm", SignatureMethod)));

        var reference = El(doc, "Reference", ("URI", uri));
        var tr = El(doc, "Transforms");
        foreach (var alg in transforms)
        {
            tr.AppendChild(El(doc, "Transform", ("Algorithm", alg)));
        }

        reference.AppendChild(tr);
        reference.AppendChild(El(doc, "DigestMethod", ("Algorithm", DigestMethod)));
        reference.AppendChild(El(doc, "DigestValue", text: digestValue));
        signedInfo.AppendChild(reference);

        // El SignatureValue se firma sobre la forma canónica de SignedInfo, no sobre el documento directamente.
        using var rsa = certificate.PrivateKey;
        var valor = rsa.SignData(C14n.CanonicalizeBytes(signedInfo), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        signature.AppendChild(El(doc, "SignatureValue", text: Convert.ToBase64String(valor)));
        signature.AppendChild(KeyInfo(doc, certificate));
    }

    private static XmlElement KeyInfo(XmlDocument doc, Certificate certificate)
    {
        using var rsa = certificate.PrivateKey;
        var p = rsa.ExportParameters(false);

        var keyInfo = El(doc, "KeyInfo");
        var rsaKeyValue = El(doc, "RSAKeyValue");
        rsaKeyValue.AppendChild(El(doc, "Modulus", text: Convert.ToBase64String(p.Modulus!)));
        rsaKeyValue.AppendChild(El(doc, "Exponent", text: Convert.ToBase64String(p.Exponent!)));
        var keyValue = El(doc, "KeyValue");
        keyValue.AppendChild(rsaKeyValue);
        keyInfo.AppendChild(keyValue);

        var x509 = El(doc, "X509Data");
        x509.AppendChild(El(doc, "X509Certificate", text: certificate.CertificateBase64));
        keyInfo.AppendChild(x509);

        return keyInfo;
    }

    private static XmlElement El(XmlDocument doc, string name, (string, string)? attr = null, string? text = null)
    {
        var e = doc.CreateElement(string.Empty, name, Ds);
        if (attr is { } a)
        {
            e.SetAttribute(a.Item1, a.Item2);
        }

        if (text is not null)
        {
            e.AppendChild(doc.CreateTextNode(text));
        }

        return e;
    }
}
