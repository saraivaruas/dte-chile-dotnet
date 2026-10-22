using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace DteChile;

/// <summary>
/// Arma el XML de un documento del SII a partir de diccionarios, guiado por la especificación oficial (ver <see cref="Spec"/>).
/// <list type="bullet">
/// <item>Cada elemento sale en el orden que exige el esquema, aunque los datos vengan desordenados.</item>
/// <item>Falta de un campo obligatorio, un campo que no existe (típico error de tipeo), más repeticiones de las permitidas, un valor fuera de largo, patrón,
/// enumeración o rango: todos dan un error claro con la ruta del campo, en vez de un «esquema inválido» del SII sin explicación.</item>
/// <item>Todo texto entra por <see cref="SiiText"/> (apto para ISO-8859-1).</item>
/// </list>
/// Formato de los datos: las claves son los nombres de elemento del SII (<c>RUTEmisor</c>); los atributos van con «@» (<c>@version</c>); un elemento repetible recibe
/// una lista; un elemento con atributos y texto recibe <c>{"#text": ..., "@atributo": ...}</c>.
/// </summary>
public sealed class SpecXml
{
    private readonly string _namespace;
    private readonly HashSet<string> _deferred;
    private readonly bool _truncate;

    /// <param name="ns">Espacio de nombres de los elementos.</param>
    /// <param name="deferred">Elementos obligatorios que se agregan después (p. ej. el timbre <c>TED</c> o la firma): no se exigen aquí.</param>
    /// <param name="truncate">Si es true, un texto más largo que el máximo se recorta en vez de dar error.</param>
    public SpecXml(string ns, IEnumerable<string>? deferred = null, bool truncate = false)
    {
        _namespace = ns;
        _deferred = new HashSet<string>(deferred ?? [], StringComparer.Ordinal);
        _truncate = truncate;
    }

    public XmlElement Element(XmlDocument doc, SpecNode node, object? value, string path = "")
    {
        var name = node.Name;
        path = path.Length == 0 ? name : path;
        var el = doc.CreateElement(string.Empty, name, _namespace);

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        object? texto = null;
        if (AsMap(value) is { } map)
        {
            data = map;
            if (data.TryGetValue("#text", out var t))
            {
                texto = t;
            }
        }
        else if (value is not null)
        {
            if (node.Children.Count > 0)
            {
                throw new ArgumentException($"«{name}» ({path}) debe ser un conjunto de campos, no un valor suelto.");
            }

            texto = value;
        }

        var consumed = new HashSet<string>(StringComparer.Ordinal) { "#text" };
        foreach (var attr in node.Attributes)
        {
            var key = "@" + attr.Name;
            consumed.Add(key);
            data.TryGetValue(key, out var val);
            if (val is null || (val is string s && s.Length == 0))
            {
                if (attr.Fixed is not null)
                {
                    val = attr.Fixed;
                }
                else if (attr.Required)
                {
                    throw new ArgumentException($"Falta el atributo requerido «{attr.Name}» en {path}.");
                }
                else
                {
                    continue;
                }
            }

            el.SetAttribute(attr.Name, Scalar(val, attr.Type, $"{path}/@{attr.Name}"));
        }

        if (node.Children.Count > 0)
        {
            Children(doc, el, node.Children, data, path, consumed);
        }
        else if (texto is not null && !(texto is string ts && ts.Length == 0))
        {
            el.AppendChild(doc.CreateTextNode(Scalar(texto, node.Type, path)));
        }

        var extra = data.Keys.FirstOrDefault(k => !consumed.Contains(k));
        if (extra is not null)
        {
            var validos = node.Children.Count > 0 ? Spec.ChildNames(node) : [];
            throw new ArgumentException($"El campo «{extra}» no existe en {path}." + (validos.Count > 0 ? " Campos válidos: " + string.Join(", ", validos) + "." : string.Empty));
        }

        return el;
    }

    private void Children(XmlDocument doc, XmlElement parent, IReadOnlyList<SpecItem> items, Dictionary<string, object?> data, string path, HashSet<string> consumed)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case SpecChoice c:
                    Choice(doc, parent, c, data, path, consumed);
                    break;
                case SpecSequence s:
                    Children(doc, parent, s.Items, data, path, consumed);
                    break;
                case SpecNode n:
                    Member(doc, parent, n, data, path, consumed);
                    break;
            }
        }
    }

    private void Member(XmlDocument doc, XmlElement parent, SpecNode item, Dictionary<string, object?> data, string path, HashSet<string> consumed)
    {
        var name = item.Name;
        consumed.Add(name);
        if (item.Ref is not null || name == "Signature") // ds:Signature (referenciada o declarada en línea, como en LibroBoleta): la agrega la firma, no los datos
        {
            if (data.ContainsKey(name))
            {
                throw new ArgumentException($"«{name}» ({path}) no se entrega como dato: lo agrega la firma.");
            }

            return;
        }

        data.TryGetValue(name, out var value);
        if (IsEmpty(value))
        {
            if (item.Min >= 1 && !_deferred.Contains(name))
            {
                throw new ArgumentException($"Falta el campo requerido «{name}» en {path}.");
            }

            return;
        }

        var lista = AsList(value);
        if (lista is not null && item.Max == 1)
        {
            throw new ArgumentException($"«{name}» ({path}) admite un solo valor, no una lista.");
        }

        var valores = lista ?? [value];
        if (item.Max is { } max && valores.Count > max)
        {
            throw new ArgumentException($"«{name}» ({path}) admite como máximo {max} repeticiones y se entregaron {valores.Count}.");
        }

        for (var i = 0; i < valores.Count; i++)
        {
            var sub = $"{path}/{name}" + (valores.Count > 1 ? $"[{i + 1}]" : string.Empty);
            parent.AppendChild(Element(doc, item, valores[i], sub));
        }
    }

    private void Choice(XmlDocument doc, XmlElement parent, SpecChoice choice, Dictionary<string, object?> data, string path, HashSet<string> consumed)
    {
        var alternativas = choice.Items.OfType<SpecNode>().ToList();
        var nombres = alternativas.Select(a => a.Name).ToList();
        foreach (var n in nombres)
        {
            consumed.Add(n);
        }

        var presentes = alternativas.Where(a => data.TryGetValue(a.Name, out var v) && !IsEmpty(v)).ToList();
        if (presentes.Count == 0)
        {
            if (choice.Min >= 1 && !nombres.Any(_deferred.Contains))
            {
                throw new ArgumentException("Debe indicar uno de: " + string.Join(", ", nombres) + $" en {path}.");
            }

            return;
        }

        if (presentes.Count > 1 && choice.Max == 1)
        {
            throw new ArgumentException("Indique solo uno de: " + string.Join(", ", nombres) + $" en {path}.");
        }

        foreach (var alt in presentes)
        {
            Member(doc, parent, alt, data, path, consumed);
        }
    }

    // ---------------------------------------------------------------- valores

    private static bool IsEmpty(object? v) => v is null || (v is string s && s.Length == 0) || (AsList(v) is { Count: 0 }) || (AsMap(v) is { Count: 0 });

    internal static Dictionary<string, object?>? AsMap(object? v) => v switch
    {
        Dictionary<string, object?> d => d,
        IDictionary<string, object?> d => new Dictionary<string, object?>(d, StringComparer.Ordinal),
        IReadOnlyDictionary<string, object?> d => d.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        _ => null,
    };

    internal static List<object?>? AsList(object? v)
    {
        if (v is null or string || AsMap(v) is not null || v is not IEnumerable e)
        {
            return null;
        }

        return e.Cast<object?>().ToList();
    }

    private string Scalar(object? value, SimpleType? type, string path)
    {
        if (value is bool || value is null || AsMap(value) is not null || (AsList(value) is not null))
        {
            throw new ArgumentException($"El valor de {path} debe ser un texto o un número.");
        }

        var baseName = type?.Base ?? "string";
        var texto = value switch
        {
            double d => Decimal(Convert.ToDecimal(d), type),
            float f => Decimal(Convert.ToDecimal(f), type),
            decimal m => Decimal(m, type),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture).Trim(),
            _ => value.ToString()!.Trim(),
        };

        if (baseName != "base64Binary")
        {
            texto = SiiText.Latin1(texto, path);
        }

        if (type is null)
        {
            return texto;
        }

        return Validate(baseName, type, texto, path);
    }

    private static string Decimal(decimal v, SimpleType? type)
    {
        var dec = type?.FractionDigits ?? 6;
        var s = Math.Round(v, Math.Min(dec, 12), MidpointRounding.AwayFromZero).ToString("0." + new string('#', Math.Max(dec, 1)), CultureInfo.InvariantCulture);

        return s is "" or "-0" ? "0" : s;
    }

    private static readonly string[] Enteros = ["integer", "int", "long", "unsignedLong", "positiveInteger", "nonNegativeInteger", "short", "byte"];

    private string Validate(string baseName, SimpleType t, string texto, string path)
    {
        if (Enteros.Contains(baseName))
        {
            if (!Regex.IsMatch(texto, @"^-?\d+$"))
            {
                throw new ArgumentException($"{path}: «{texto}» no es un número entero.");
            }

            var n = double.Parse(texto, CultureInfo.InvariantCulture);
            if (baseName == "positiveInteger" && n < 1)
            {
                throw new ArgumentException($"{path}: debe ser un entero positivo (recibido {texto}).");
            }

            if (baseName is "nonNegativeInteger" or "unsignedLong" && n < 0)
            {
                throw new ArgumentException($"{path}: no puede ser negativo (recibido {texto}).");
            }

            Range(t, n, texto, path);
        }
        else if (baseName == "decimal")
        {
            if (!Regex.IsMatch(texto, @"^-?\d+(\.\d+)?$"))
            {
                throw new ArgumentException($"{path}: «{texto}» no es un número decimal.");
            }

            var partes = texto.TrimStart('-').Split('.');
            var fraccion = partes.Length > 1 ? partes[1].TrimEnd('0') : string.Empty;
            if (t.FractionDigits is { } fd && fraccion.Length > fd)
            {
                throw new ArgumentException($"{path}: admite como máximo {fd} decimales («{texto}»).");
            }

            if (t.TotalDigits is { } td && partes[0].TrimStart('0').Length + fraccion.Length > td)
            {
                throw new ArgumentException($"{path}: admite como máximo {td} dígitos («{texto}»).");
            }

            Range(t, double.Parse(texto, CultureInfo.InvariantCulture), texto, path);
        }
        else if (baseName == "date")
        {
            if (!DateTime.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                throw new ArgumentException($"{path}: «{texto}» no es una fecha válida (formato AAAA-MM-DD).");
            }

            if ((t.MinInclusive is not null && string.CompareOrdinal(texto, t.MinInclusive) < 0) || (t.MaxInclusive is not null && string.CompareOrdinal(texto, t.MaxInclusive) > 0))
            {
                throw new ArgumentException($"{path}: la fecha {texto} está fuera del rango permitido ({t.MinInclusive} a {t.MaxInclusive}).");
            }
        }
        else if (baseName == "dateTime")
        {
            if (!Regex.IsMatch(texto, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}$"))
            {
                throw new ArgumentException($"{path}: «{texto}» no es una fecha y hora válida (formato AAAA-MM-DDTHH:MM:SS).");
            }
        }
        else if (baseName == "base64Binary")
        {
            if (!Regex.IsMatch(texto, @"^[A-Za-z0-9+/=\s]*$"))
            {
                throw new ArgumentException($"{path}: no es base64 válido.");
            }

            return texto;
        }

        return CheckText(t, texto, path);
    }

    private static void Range(SimpleType t, double n, string texto, string path)
    {
        void Check(string? limite, Func<double, double, bool> falla, string nombre)
        {
            if (limite is not null && double.TryParse(limite, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) && falla(n, l))
            {
                throw new ArgumentException($"{path}: {texto} está fuera del rango permitido ({nombre} {limite}).");
            }
        }

        Check(t.MinInclusive, (v, l) => v < l, "minInclusive");
        Check(t.MaxInclusive, (v, l) => v > l, "maxInclusive");
        Check(t.MinExclusive, (v, l) => v <= l, "minExclusive");
        Check(t.MaxExclusive, (v, l) => v >= l, "maxExclusive");
    }

    private string CheckText(SimpleType t, string texto, string path)
    {
        var largo = SiiText.Length(texto);
        if (t.MaxLength is { } max && largo > max)
        {
            if (_truncate)
            {
                texto = SiiText.Truncate(texto, max);
                largo = SiiText.Length(texto);
            }
            else
            {
                throw new ArgumentException($"{path}: el texto tiene {largo} caracteres y el máximo es {max}.");
            }
        }

        if (t.MinLength is { } min && largo < min)
        {
            throw new ArgumentException($"{path}: el texto debe tener al menos {min} caracteres.");
        }

        if (t.Length is { } exacto && largo != exacto)
        {
            throw new ArgumentException($"{path}: el texto debe tener exactamente {exacto} caracteres.");
        }

        if (t.Enum is { } en && !en.Contains(texto))
        {
            throw new ArgumentException($"{path}: «{texto}» no es un valor permitido (" + string.Join(", ", en.Take(12)) + (en.Count > 12 ? ", …" : string.Empty) + ").");
        }

        if (t.Pattern is { } patron && !Regex.IsMatch(texto, "^(?:" + patron + ")$"))
        {
            throw new ArgumentException($"{path}: «{texto}» no tiene el formato esperado.");
        }

        return texto;
    }
}
