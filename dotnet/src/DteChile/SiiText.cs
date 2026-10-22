using System.Globalization;
using System.Text;

namespace DteChile;

/// <summary>
/// Único punto por el que entra texto al XML de un documento: lo deja apto para ISO-8859-1, que es lo que exige el SII.
/// Los signos tipográficos frecuentes (raya larga, comillas, puntos suspensivos...) se reemplazan; lo que no tiene equivalente se rechaza con un error claro
/// en vez de salir como una referencia numérica que cambia los bytes que luego firma el timbre.
/// </summary>
public static class SiiText
{
    private static readonly Dictionary<string, string> Equivalentes = new()
    {
        ["–"] = "-", ["—"] = "-", ["−"] = "-",
        ["‘"] = "'", ["’"] = "'", ["‚"] = "'",
        ["“"] = "\"", ["”"] = "\"", ["„"] = "\"",
        ["…"] = "...", ["•"] = "-", ["€"] = "EUR", ["™"] = "TM",
        ["​"] = "", ["﻿"] = "",
    };

    /// <exception cref="ArgumentException">Si hay un carácter de control o uno que ISO-8859-1 no puede representar.</exception>
    public static string Latin1(string value, string campo = "texto")
    {
        foreach (var (de, a) in Equivalentes)
        {
            value = value.Replace(de, a, StringComparison.Ordinal);
        }

        foreach (var rune in value.EnumerateRunes())
        {
            var v = rune.Value;
            if (v < 0x20 && v != 0x09 && v != 0x0A && v != 0x0D || v == 0x7F)
            {
                throw new ArgumentException($"El campo «{campo}» contiene caracteres de control que el XML no admite.");
            }

            if (v > 0xFF)
            {
                throw new ArgumentException($"El campo «{campo}» contiene «{rune}», que el SII no admite (solo ISO-8859-1: letras latinas, tildes, ñ y signos básicos).");
            }
        }

        return value;
    }

    /// <summary>Cantidad de caracteres de texto (elementos de texto Unicode, no unidades UTF-16).</summary>
    public static int Length(string s) => new StringInfo(s).LengthInTextElements;

    /// <summary>Recorta a <paramref name="max"/> caracteres de texto.</summary>
    public static string Truncate(string s, int max)
    {
        var info = new StringInfo(s);

        return info.LengthInTextElements <= max ? s : info.SubstringByTextElements(0, max);
    }

    public static Encoding Iso88591 => Encoding.Latin1;
}
