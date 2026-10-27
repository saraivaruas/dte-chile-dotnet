# Cambios

## 0.1.0 — 2026-10
Primera versión pública.
- Fachada de alto nivel `Facturador` (PHP y C#) con objetos tipados (`Emisor`, `Receptor`, `Linea`, `Referencia`), folios automáticos (`FolioStore`) y fecha de emisión en hora de Chile.
- Timbre PDF417 (`Pdf417Encoder`, `timbreSvg()`).
- Guía por tipo de documento (`docs/guia.md`) y ejemplos ejecutables (`php/examples`).
- Flujos de GitHub para release (NuGet) y sincronización a `dte-chile-php` y `dte-chile-dotnet`.
- PHP 8.3+ y C# (.NET 8 y 10) con la misma especificación generada desde los esquemas oficiales del SII.
- DTE 33, 34, 43, 46, 52, 56, 61, 110, 111, 112; boletas 39 y 41; timbre, firma y sobres de envío.
- Libros (compra/venta, guías, boletas), consumo de folios, respuestas de intercambio, recibos y cesión (AEC).
- Cliente del SII: semilla/token, envío y consulta de estado de DTE y de boletas (API REST).
- Reglas por tipo: notas de crédito/débito exigen referencia; guía de despacho exige tipo de traslado.
