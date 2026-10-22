using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace DteChile;

/// <summary>Restricciones de un tipo simple del esquema del SII (largo, patrón, enumeración, rango, dígitos).</summary>
public sealed class SimpleType
{
    public string Base { get; init; } = "string";
    public int? MaxLength { get; init; }
    public int? MinLength { get; init; }
    public int? Length { get; init; }
    public int? TotalDigits { get; init; }
    public int? FractionDigits { get; init; }
    public string? MinInclusive { get; init; }
    public string? MaxInclusive { get; init; }
    public string? MinExclusive { get; init; }
    public string? MaxExclusive { get; init; }
    public string? Pattern { get; init; }
    public IReadOnlyList<string>? Enum { get; init; }
}

public sealed class SpecAttribute
{
    public required string Name { get; init; }
    public bool Required { get; init; }
    public SimpleType? Type { get; init; }
    public string? Fixed { get; init; }
}

/// <summary>Un ítem del contenido de un elemento: otro elemento, una alternativa o una secuencia repetible.</summary>
public abstract class SpecItem
{
    public int Min { get; init; } = 1;
    /// <summary>null = sin límite.</summary>
    public int? Max { get; init; } = 1;
}

public sealed class SpecNode : SpecItem
{
    public required string Name { get; init; }
    public string? Ref { get; init; }
    public SimpleType? Type { get; init; }
    public IReadOnlyList<SpecAttribute> Attributes { get; init; } = [];
    public IReadOnlyList<SpecItem> Children { get; init; } = [];
    public string? Namespace { get; init; }
}

public sealed class SpecChoice : SpecItem
{
    public required IReadOnlyList<SpecItem> Items { get; init; }
}

public sealed class SpecSequence : SpecItem
{
    public required IReadOnlyList<SpecItem> Items { get; init; }
}

/// <summary>
/// Especificación de los documentos del SII, generada desde sus esquemas oficiales (tools/gen_spec.py → Resources/spec/*.json, incrustada en el ensamblado).
/// Describe, por elemento: orden de los hijos, mínimo y máximo de ocurrencias, atributos y las restricciones de cada tipo simple.
/// </summary>
public static class Spec
{
    private static readonly ConcurrentDictionary<string, SpecNode> Cache = new();

    /// <summary>Árbol del elemento raíz («dte», «envio_dte», «libro_compra_venta», «consumo_folios»...).</summary>
    public static SpecNode Load(string name) => Cache.GetOrAdd(name, Read);

    /// <summary>Nodo hijo directo con ese nombre (buscando también dentro de las alternativas).</summary>
    public static SpecNode? Child(SpecNode node, string name) => Flatten(node.Children).FirstOrDefault(c => c.Name == name);

    /// <summary>Nombres de los hijos directos (incluye las alternativas).</summary>
    public static IReadOnlyList<string> ChildNames(SpecNode node) => Flatten(node.Children).Select(c => c.Name).ToList();

    internal static IEnumerable<SpecNode> Flatten(IEnumerable<SpecItem> items)
    {
        foreach (var i in items)
        {
            switch (i)
            {
                case SpecNode n:
                    yield return n;
                    break;
                case SpecChoice c:
                    foreach (var x in Flatten(c.Items)) yield return x;
                    break;
                case SpecSequence s:
                    foreach (var x in Flatten(s.Items)) yield return x;
                    break;
            }
        }
    }

    private static SpecNode Read(string name)
    {
        if (name.Any(ch => !(char.IsAsciiLetterLower(ch) || ch == '_')))
        {
            throw new InvalidOperationException($"Nombre de especificación no válido: {name}.");
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"spec/{name}.json")
            ?? throw new InvalidOperationException($"No se encontró la especificación «{name}».");
        using var doc = JsonDocument.Parse(stream);
        var raiz = ReadNode(doc.RootElement);

        return doc.RootElement.TryGetProperty("namespace", out var ns) && ns.ValueKind == JsonValueKind.String
            ? new SpecNode
            {
                Name = raiz.Name, Min = raiz.Min, Max = raiz.Max, Ref = raiz.Ref, Type = raiz.Type, Attributes = raiz.Attributes, Children = raiz.Children,
                Namespace = ns.GetString(),
            }
            : raiz;
    }

    private static SpecItem ReadItem(JsonElement e)
    {
        var min = e.TryGetProperty("min", out var mn) ? mn.GetInt32() : 1;
        int? max = e.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetInt32() : null;
        if (e.TryGetProperty("choice", out var ch))
        {
            return new SpecChoice { Items = ch.EnumerateArray().Select(ReadItem).ToList(), Min = min, Max = max };
        }

        if (e.TryGetProperty("sequence", out var sq))
        {
            return new SpecSequence { Items = sq.EnumerateArray().Select(ReadItem).ToList(), Min = min, Max = max };
        }

        return ReadNode(e);
    }

    private static SpecNode ReadNode(JsonElement e)
    {
        var min = e.TryGetProperty("min", out var mn) ? mn.GetInt32() : 1;
        int? max = e.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetInt32() : null;

        return new SpecNode
        {
            Name = e.GetProperty("name").GetString()!,
            Min = min,
            Max = max,
            Ref = e.TryGetProperty("ref", out var r) ? r.GetString() : null,
            Type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Object ? ReadType(t) : null,
            Attributes = e.TryGetProperty("attributes", out var at)
                ? at.EnumerateArray().Select(a => new SpecAttribute
                {
                    Name = a.GetProperty("name").GetString()!,
                    Required = a.TryGetProperty("required", out var rq) && rq.GetBoolean(),
                    Type = a.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.Object ? ReadType(ty) : null,
                    Fixed = a.TryGetProperty("fixed", out var fx) && fx.ValueKind == JsonValueKind.String ? fx.GetString() : null,
                }).ToList()
                : [],
            Children = e.TryGetProperty("children", out var c) ? c.EnumerateArray().Select(ReadItem).ToList() : [],
        };
    }

    private static SimpleType ReadType(JsonElement t)
    {
        int? I(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        string? S(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        return new SimpleType
        {
            Base = S("base") ?? "string",
            MaxLength = I("maxLength"), MinLength = I("minLength"), Length = I("length"), TotalDigits = I("totalDigits"), FractionDigits = I("fractionDigits"),
            MinInclusive = S("minInclusive"), MaxInclusive = S("maxInclusive"), MinExclusive = S("minExclusive"), MaxExclusive = S("maxExclusive"), Pattern = S("pattern"),
            Enum = t.TryGetProperty("enum", out var en) ? en.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
        };
    }
}
