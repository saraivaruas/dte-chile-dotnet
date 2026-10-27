# Cambios

## 0.2.0 — 2026-10
- `emitirYEnviar()` / `EmitirYEnviarAsync()`: emite y envía en una llamada, con reintentos ante fallos temporales (red, SII caído) y estado ya interpretado (`EstadoDocumento`: emitido, enviado, aceptado, con reparos, rechazado).
- `reenviar()` para documentos que quedaron emitidos sin llegar al SII (el folio no se vuelve a usar) y `consultar()` para actualizar el estado.
- Errores del SII traducidos a una explicación y una acción concreta (`MensajesSii`), conservando el detalle técnico.
- `probarConexion()`: autenticación en facturas y boletas sin CAF ni envío.

## 0.1.0 — 2026-10
Primera versión pública.
- Autenticación con el SII (certificación) comprobada con un certificado real, facturas y boletas; ejemplos `probar_conexion.php` y `verificar_certificado.php`.
- Fachada de alto nivel `Facturador` (PHP y C#) con objetos tipados (`Emisor`, `Receptor`, `Linea`, `Referencia`), folios automáticos (`FolioStore`) y fecha de emisión en hora de Chile.
- Timbre PDF417 (`Pdf417Encoder`, `timbreSvg()`).
- Guía por tipo de documento (`docs/guia.md`) y ejemplos ejecutables (`php/examples`).
- Flujos de GitHub para release (NuGet) y sincronización a `dte-chile-php` y `dte-chile-dotnet`.
- PHP 8.3+ y C# (.NET 8 y 10) con la misma especificación generada desde los esquemas oficiales del SII.
- DTE 33, 34, 43, 46, 52, 56, 61, 110, 111, 112; boletas 39 y 41; timbre, firma y sobres de envío.
- Libros (compra/venta, guías, boletas), consumo de folios, respuestas de intercambio, recibos y cesión (AEC).
- Cliente del SII: semilla/token, envío y consulta de estado de DTE y de boletas (API REST).
- Reglas por tipo: notas de crédito/débito exigen referencia; guía de despacho exige tipo de traslado.
