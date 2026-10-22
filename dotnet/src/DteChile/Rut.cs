using System.Text.RegularExpressions;

namespace DteChile;

/// <summary>RUT chileno: normalización al formato del SII (<c>12345678-9</c>, sin puntos) y validación del dígito verificador (módulo 11).</summary>
public static partial class Rut
{
    /// <summary>Quita puntos y espacios, pasa la K a mayúscula y deja el guion antes del dígito verificador.</summary>
    public static string ToDteFormat(string rut)
    {
        var limpio = Regex.Replace(rut, @"[.\s]", "").ToUpperInvariant();
        if (limpio.Length > 1 && !limpio.Contains('-'))
        {
            limpio = limpio[..^1] + "-" + limpio[^1];
        }

        return limpio;
    }

    /// <summary>true si el RUT tiene formato válido y su dígito verificador es correcto.</summary>
    public static bool IsValid(string rut)
    {
        var limpio = ToDteFormat(rut);
        var m = FormatoRut().Match(limpio);
        if (!m.Success)
        {
            return false;
        }

        var suma = 0;
        var multiplicador = 2;
        foreach (var digito in m.Groups[1].Value.Reverse())
        {
            suma += (digito - '0') * multiplicador;
            multiplicador = multiplicador == 7 ? 2 : multiplicador + 1;
        }

        var resto = 11 - (suma % 11);
        var esperado = resto switch { 11 => "0", 10 => "K", _ => resto.ToString() };

        return m.Groups[2].Value == esperado;
    }

    [GeneratedRegex(@"^(\d{1,8})-([\dK])$")]
    private static partial Regex FormatoRut();
}
