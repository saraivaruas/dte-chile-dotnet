using System.Text;
using System.Xml;

namespace DteChile;

/// <summary>
/// Canonicalización XML inclusiva (C14N 1.0, sin comentarios), la que exige el SII en sus firmas XMLDSig.
/// Implementación propia y mínima, sin dependencias: cubre elementos, atributos y texto (lo que usan los documentos del SII), y arrastra al elemento raíz
/// los espacios de nombres en alcance de sus ancestros, como pide la canonicalización «inclusiva» de un subárbol.
/// </summary>
public static class C14n
{
    /// <summary>Forma canónica (texto Unicode; al firmar o resumir se codifica en UTF-8) del subárbol de <paramref name="root"/>.</summary>
    /// <param name="exclude">Nodos que se omiten (p. ej. la <c>Signature</c> en una firma «enveloped»).</param>
    public static string Canonicalize(XmlElement root, Func<XmlNode, bool>? exclude = null)
    {
        // Espacios de nombres en alcance fuera del subárbol (ancestros del elemento raíz).
        var inScope = new Dictionary<string, string>(StringComparer.Ordinal);
        var ancestros = new List<XmlElement>();
        for (var p = root.ParentNode as XmlElement; p is not null; p = p.ParentNode as XmlElement)
        {
            ancestros.Add(p);
        }

        ancestros.Reverse();
        foreach (var a in ancestros)
        {
            Declare(a, inScope);
        }

        var sb = new StringBuilder();
        Write(root, inScope, new Dictionary<string, string>(StringComparer.Ordinal), sb, exclude);

        return sb.ToString();
    }

    /// <summary>Bytes UTF-8 de la forma canónica.</summary>
    public static byte[] CanonicalizeBytes(XmlElement root, Func<XmlNode, bool>? exclude = null) => Encoding.UTF8.GetBytes(Canonicalize(root, exclude));

    /// <summary>
    /// Registra los espacios de nombres que el elemento pone en alcance: los declarados con xmlns y los que implican los nombres del propio elemento y de sus
    /// atributos (un nodo recién creado con <c>CreateElement(prefijo, nombre, uri)</c> todavía no trae el atributo xmlns, pero su espacio de nombres vale igual).
    /// </summary>
    private static void Declare(XmlElement e, Dictionary<string, string> scope)
    {
        foreach (XmlAttribute a in e.Attributes)
        {
            if (a.Prefix == "xmlns")
            {
                scope[a.LocalName] = a.Value;
            }
            else if (a.Prefix.Length == 0 && a.LocalName == "xmlns")
            {
                scope[string.Empty] = a.Value;
            }
        }

        scope[e.Prefix] = e.NamespaceURI;
        foreach (XmlAttribute a in e.Attributes)
        {
            if (a.Prefix.Length > 0 && a.Prefix != "xmlns" && a.Prefix != "xml")
            {
                scope[a.Prefix] = a.NamespaceURI;
            }
        }
    }

    private static void Write(XmlElement e, Dictionary<string, string> scopeParent, Dictionary<string, string> renderedParent, StringBuilder sb, Func<XmlNode, bool>? exclude)
    {
        var scope = new Dictionary<string, string>(scopeParent, StringComparer.Ordinal);
        Declare(e, scope);

        // Declaraciones que hay que escribir: las que cambian respecto de lo ya escrito por el ancestro más cercano que sí se escribió.
        var rendered = new Dictionary<string, string>(renderedParent, StringComparer.Ordinal);
        var emitir = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (prefijo, uri) in scope)
        {
            if (prefijo == "xml")
            {
                continue;
            }

            rendered.TryGetValue(prefijo, out var ya);
            if (prefijo.Length == 0 && uri.Length == 0)
            {
                if (!string.IsNullOrEmpty(ya)) // «xmlns=""» solo si antes había un espacio de nombres por defecto que anular
                {
                    emitir[prefijo] = uri;
                }
            }
            else if (uri != ya)
            {
                emitir[prefijo] = uri;
            }
        }

        foreach (var (p, u) in emitir)
        {
            rendered[p] = u;
        }

        sb.Append('<').Append(e.Name);
        foreach (var (p, u) in emitir)
        {
            sb.Append(" xmlns").Append(p.Length > 0 ? ":" + p : string.Empty).Append("=\"").Append(EscapeAttribute(u)).Append('"');
        }

        var atributos = new List<XmlAttribute>();
        foreach (XmlAttribute a in e.Attributes)
        {
            if (a.Prefix == "xmlns" || (a.Prefix.Length == 0 && a.LocalName == "xmlns"))
            {
                continue;
            }

            atributos.Add(a);
        }

        atributos.Sort((x, y) =>
        {
            var c = string.CompareOrdinal(x.NamespaceURI, y.NamespaceURI);

            return c != 0 ? c : string.CompareOrdinal(x.LocalName, y.LocalName);
        });
        foreach (var a in atributos)
        {
            sb.Append(' ').Append(a.Name).Append("=\"").Append(EscapeAttribute(a.Value)).Append('"');
        }

        sb.Append('>');
        foreach (XmlNode hijo in e.ChildNodes)
        {
            if (exclude?.Invoke(hijo) == true)
            {
                continue;
            }

            switch (hijo)
            {
                case XmlElement he:
                    Write(he, scope, rendered, sb, exclude);
                    break;
                case XmlText or XmlWhitespace or XmlSignificantWhitespace or XmlCDataSection:
                    sb.Append(EscapeText(hijo.Value ?? string.Empty));
                    break;
            }
        }

        sb.Append("</").Append(e.Name).Append('>');
    }

    private static string EscapeText(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\r", "&#xD;", StringComparison.Ordinal);

    private static string EscapeAttribute(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("\t", "&#x9;", StringComparison.Ordinal).Replace("\n", "&#xA;", StringComparison.Ordinal)
        .Replace("\r", "&#xD;", StringComparison.Ordinal);
}
