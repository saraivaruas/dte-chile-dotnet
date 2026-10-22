namespace DteChile;

/// <summary>Códigos de tipo de documento tributario electrónico del SII.</summary>
public static class DocumentType
{
    public const int FacturaElectronica = 33;
    public const int FacturaNoAfectaExenta = 34;
    public const int BoletaElectronica = 39;
    public const int BoletaNoAfectaExenta = 41;
    public const int LiquidacionFactura = 43;
    public const int FacturaCompra = 46;
    public const int GuiaDespacho = 52;
    public const int NotaDebito = 56;
    public const int NotaCredito = 61;
    public const int FacturaExportacion = 110;
    public const int NotaDebitoExportacion = 111;
    public const int NotaCreditoExportacion = 112;

    public static readonly IReadOnlyDictionary<int, string> Labels = new Dictionary<int, string>
    {
        [FacturaElectronica] = "Factura Electrónica",
        [FacturaNoAfectaExenta] = "Factura No Afecta o Exenta Electrónica",
        [BoletaElectronica] = "Boleta Electrónica",
        [BoletaNoAfectaExenta] = "Boleta No Afecta o Exenta Electrónica",
        [LiquidacionFactura] = "Liquidación Factura Electrónica",
        [FacturaCompra] = "Factura de Compra Electrónica",
        [GuiaDespacho] = "Guía de Despacho Electrónica",
        [NotaDebito] = "Nota de Débito Electrónica",
        [NotaCredito] = "Nota de Crédito Electrónica",
        [FacturaExportacion] = "Factura de Exportación Electrónica",
        [NotaDebitoExportacion] = "Nota de Débito de Exportación Electrónica",
        [NotaCreditoExportacion] = "Nota de Crédito de Exportación Electrónica",
    };

    public static string Label(int code) => Labels.TryGetValue(code, out var l) ? l : $"Tipo {code}";

    /// <summary>true para las boletas (39 y 41): tienen su propio esquema y su propio envío, distintos de los demás documentos.</summary>
    public static bool IsBoleta(int code) => code is BoletaElectronica or BoletaNoAfectaExenta;
}
