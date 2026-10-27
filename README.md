# dte-chile-dotnet

> Versión C# / .NET de la familia [dte-chile](https://github.com/saraivaruas/dte-chile). Versión PHP: [dte-chile-php](https://github.com/saraivaruas/dte-chile-php). Ambas comparten los esquemas oficiales y las mismas reglas; cada una se publica y versiona por separado.

Biblioteca de **software libre (licencia MIT)** para generar, timbrar, firmar y enviar **Documentos Tributarios Electrónicos (DTE)** al
[SII de Chile](https://www.sii.cl), en **C# / .NET** (8 y 10). Sin dependencias externas: no usa paquetes de terceros.

> ## Alcance real — léalo antes de usarla
>
> - **Que un documento cumpla el esquema y las firmas no garantiza que el SII lo acepte.** Cada usuario debe usar su propio certificado digital y sus propios folios (CAF) autorizados para poder emitir. La biblioteca arma el XML según los esquemas oficiales (XSD) y el
>   formato publicado por el SII, y lo verifica con pruebas automáticas. La aceptación final depende del SII: su ambiente de certificación, el estado de su
>   empresa como emisora, sus folios (CAF) reales y las validaciones que el SII aplique en cada momento (ver [Resolución Exenta 121](#resolución-exenta-sii-n-121)).
> - **No ha sido probada contra el SII con documentos reales de producción.** Las pruebas automáticas usan un certificado y un CAF **falsos** generados al vuelo.
>   Antes de emitir de verdad debe pasar el proceso de certificación del SII con su propio certificado y sus propios folios.
> - **No es asesoría tributaria ni contable.** Los cálculos (IVA 19 %, impuestos adicionales, retenciones) siguen las reglas publicadas, pero las tasas de
>   impuestos adicionales y retenciones las debe indicar quien usa la biblioteca, porque cambian por ley.
> - Se entrega «tal cual», sin garantía (ver [LICENSE](LICENSE)). Usted es responsable de lo que emite.

## Qué hace

| Función | C# |
|---|:-:|
| Facturas 33, 34, 43, 46, guías 52, notas 56 y 61, exportación 110, 111 y 112 (XML guiado por los XSD oficiales, con validación de campos) | ✔ |
| Boletas 39 y 41 (esquema, IVA incluido y `EnvioBOLETA` propios) | ✔ |
| Totales: IVA, exentos, no facturables, descuentos/recargos globales, impuestos adicionales y retenciones, exportación con decimales | ✔ |
| Timbre electrónico (TED) firmado con el CAF | ✔ |
| Firma XMLDSig del documento y del envío (`EnvioDTE`, `EnvioBOLETA`) | ✔ |
| Libros de compra/venta, de guías y de boletas; consumo de folios (RCOF) | ✔ |
| Respuestas de intercambio, recibos (Ley 19.983), cesión de facturas (AEC) | ✔ |
| Autenticación (semilla/token), envío y consulta de estado (DTE y boletas) | ✔ |

Reglas del formato que el esquema no expresa también se validan: las notas de crédito y débito exigen referencia, la guía de despacho exige el tipo de
traslado, los textos se restringen a ISO-8859-1 (lo que exige el SII) con mensajes claros.

## Qué **no** hace (todavía)

- No genera la representación impresa (PDF/PDF417) ni administra folios, clientes o facturación: es una biblioteca de bajo nivel.
- No implementa el registro de compras y ventas (RCV) en línea ni el envío de correos de intercambio.
- No cubre casos especiales que requieren el bloque `totales` explícito (por ejemplo liquidación-factura, margen de comercialización).
- No hay verificación contra el SII real de todos los documentos de la lista anterior; los esquemas y las firmas sí están verificados.

## Uso rápido (C#)

```csharp
var cert = Certificate.FromPkcs12(File.ReadAllBytes("certificado.pfx"), clave);
var caf = Caf.FromXml(File.ReadAllText("CAF33.xml"));

var b = new DteBuilder();
var dte = b.Build(33, datos);
b.AttachTed(dte, new TedSigner().Sign(caf, 33, new Dictionary<string, object?>(datos) { ["montoTotal"] = b.MontoTotal(dte) }));
var firmado = new DteSigner().Sign(dte, cert);

var envio = new EnvioDteBuilder().Build([firmado], new Caratula("76123456-0", "11111111-1", "2026-01-01", 0), cert);
var sii = new SiiClient(SiiAmbiente.Certificacion);
var token = await sii.AuthenticateAsync(cert);
var (trackId, _) = await sii.UploadEnvioDteAsync(envio, token, "11111111-1", "76123456-0");
var estado = await sii.GetUploadStatusAsync("76123456-0", trackId, token);
```

## Cómo se garantiza la calidad

- **Los esquemas oficiales del SII son la fuente de verdad** (`schemas/`). `tools/gen_spec.py` los convierte en `spec/*.json` (orden, obligatoriedad,
  largos, patrones, enumeraciones) y esta biblioteca genera y valida el XML con esa misma especificación (que comparte con la versión PHP).
- Las pruebas validan cada documento contra el XSD oficial y verifican las firmas **con herramientas independientes** (la implementación de
  XMLDSig de Microsoft). Hay pruebas de tildes, `ñ`, `&`, rangos de folios, errores de usuario y cada tipo de documento.
- Los clientes del SII se prueban sin red con un transporte simulado; las URL y parámetros salen de los WSDL y manuales del SII.

```bash
dotnet test dotnet/DteChile.sln # .NET 8 y 10
```

## Resolución Exenta SII N° 121

La [Resolución Exenta N° 121 (4-sep-2026)](https://www.sii.cl/normativa_legislacion/resoluciones/2026/reso121.pdf) establece nuevas validaciones técnicas y de
negocio en la recepción de DTE, con tres resultados posibles: **aceptado**, **aceptado con reparos** y **rechazado**. El detalle de cada validación y su
cronología por fases **no está en la resolución sino en un instructivo que el SII actualiza en su sitio** (Servicios online › Factura electrónica › Sistema
de facturación de mercado › Más información › Instructivo sobre validaciones de DTE). Por eso esta biblioteca **no promete** cubrir cada validación futura:
hoy garantiza lo que la resolución menciona de forma verificable (esquema, firma, timbre, folio autorizado por el CAF, codificación ISO-8859-1, coherencia de los
montos) y se actualizará a medida que el SII publique las fases. Si el SII publica reglas nuevas, abra un *issue* con el enlace al instructivo.

## Instalación

- C#: paquete NuGet `DteChile` (cuando se publique) o referenciar `dotnet/src/DteChile`.

## Contribuir y seguridad

Ver [CONTRIBUTING.md](CONTRIBUTING.md) y [SECURITY.md](SECURITY.md). Los cambios entran por *pull request* y requieren aprobación del mantenedor.

## Licencia

[MIT](LICENSE) © 2026 Jorge Luis Saraiva Ruas. Los esquemas XSD de `schemas/` son publicados por el SII para uso de los contribuyentes; se incluyen sin
modificaciones como referencia de validación.
