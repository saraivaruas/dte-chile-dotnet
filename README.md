# dte-chile

**Biblioteca de facturación electrónica para Chile de alto nivel**: usted entrega los datos del documento (factura, boleta, guía, nota de crédito o débito) y ella arma el XML, calcula los totales, timbra, firma y lo envía al SII. Es de **software libre (licencia MIT)** y sirve para generar, timbrar, firmar y enviar **Documentos Tributarios Electrónicos (DTE)** al
[SII de Chile](https://www.sii.cl), en **PHP 8.3+** (probada en 8.3, 8.4 y 8.5) y **C# / .NET** (8 y 10). Sin dependencias externas: no usa `ext-soap` ni
paquetes de terceros.

> ## Alcance real — léalo antes de usarla
>
> - **Que un documento cumpla el esquema y las firmas no garantiza que el SII lo acepte.** La biblioteca arma el XML según los esquemas oficiales (XSD) y el
>   formato publicado por el SII, y lo verifica con pruebas automáticas. La aceptación final depende del SII: su ambiente de certificación, el estado de su
>   empresa como emisora, sus folios (CAF) reales y las validaciones que el SII aplique en cada momento (ver [Resolución Exenta 121](#resolución-exenta-sii-n-121)).
> - **Qué está verificado contra el SII real y qué no.** La autenticación (semilla firmada y token) se probó contra el ambiente de certificación del SII con un certificado digital real, tanto en el servicio de facturas como en el de boletas (octubre de 2026, versión PHP). Los documentos se validan contra los esquemas XSD oficiales y sus firmas se verifican con herramientas independientes. **Aún no se ha probado el envío de documentos con folios (CAF) reales**: las pruebas automáticas usan un certificado y un CAF falsos. Antes de emitir de verdad debe pasar el proceso de certificación del SII con su propio certificado y sus propios folios.
> - **No es asesoría tributaria ni contable.** Los cálculos (IVA 19 %, impuestos adicionales, retenciones) siguen las reglas publicadas, pero las tasas de
>   impuestos adicionales y retenciones las debe indicar quien usa la biblioteca, porque cambian por ley.
> - Se entrega «tal cual», sin garantía (ver [LICENSE](LICENSE)). Usted es responsable de lo que emite.

## Qué hace

| Función | PHP | C# |
|---|:-:|:-:|
| Facturas 33, 34, 43, 46, guías 52, notas 56 y 61, exportación 110, 111 y 112 (XML guiado por los XSD oficiales, con validación de campos) | ✔ | ✔ |
| Boletas 39 y 41 (esquema, IVA incluido y `EnvioBOLETA` propios) | ✔ | ✔ |
| Totales: IVA, exentos, no facturables, descuentos/recargos globales, impuestos adicionales y retenciones, exportación con decimales | ✔ | ✔ |
| Timbre electrónico (TED) firmado con el CAF | ✔ | ✔ |
| Firma XMLDSig del documento y del envío (`EnvioDTE`, `EnvioBOLETA`) | ✔ | ✔ |
| Libros de compra/venta, de guías y de boletas; consumo de folios (RCOF) | ✔ | ✔ |
| Respuestas de intercambio, recibos (Ley 19.983), cesión de facturas (AEC) | ✔ | ✔ |
| Autenticación (semilla/token), envío y consulta de estado (DTE y boletas) | ✔ | ✔ |
| Representación impresa en PDF (Carta) con el timbre PDF417 y logotipo opcional (JPEG) | ✔ | ✔ |
| Folios en archivo o en su propia base de datos (`ArchivoFolios`, `PdoFolios`/`DbFolios`, o su propio `FolioStore`) | ✔ | ✔ |
| Envío del documento por correo (SMTP) con el XML y el PDF adjuntos | ✔ | ✔ |

Reglas del formato que el esquema no expresa también se validan: las notas de crédito y débito exigen referencia, la guía de despacho exige el tipo de
traslado, los textos se restringen a ISO-8859-1 (lo que exige el SII) con mensajes claros.

## Qué **no** hace (todavía)

- No incluye interfaz de usuario ni administración de clientes: es una biblioteca que se integra en el sistema de quien la use.
- No lee casillas de correo (IMAP) ni procesa los correos de intercambio que le llegan; sí **envía** el documento por correo.
- No implementa el Registro de Compras y Ventas (RCV) en línea.
- No calcula automáticamente casos especiales como la liquidación-factura o el margen de comercialización: ahí hay que entregar el bloque `totales` completo.
- El logotipo del PDF debe ser JPEG (escala de grises o RGB).
- El envío de documentos con folios reales no está probado contra el SII (falta un CAF de certificación); la autenticación, los esquemas y las firmas sí están verificados.

## Uso rápido (PHP) — alto nivel

```php
use IngeInnova\DteChile\{ArchivoFolios, Caf, Certificate, Emisor, Facturador, Linea, Receptor, SiiClient};

$f = new Facturador(
    new Emisor('76.123.456-0', 'Mi Empresa SpA', 'Servicios de ingeniería', 711001, 'Av. Siempre Viva 123', 'Providencia', 'Santiago'),
    Certificate::fromPkcs12(file_get_contents('certificado.pfx'), $clave),
    new ArchivoFolios('folios.json'),
    SiiClient::CERTIFICACION,
);
$f->agregarCaf(Caf::fromXml(file_get_contents('CAF33.xml')));

$doc = $f->factura(new Receptor('77.111.222-6', 'Cliente Ltda'), [new Linea('Consultoría técnica', 10, 25000)]);
$envio = $f->enviar([$doc]);                       // arma el sobre, lo firma y lo sube al SII
$estado = $f->estadoEnvio($envio->trackId);
```

Factura, exenta, notas de crédito y débito, guías y boletas: ver la [guía por tipo de documento](docs/guia.md) y los [ejemplos](php/examples).

## Uso rápido (C#) — alto nivel

```csharp
var f = new Facturador(
    new Emisor("76.123.456-0", "Mi Empresa SpA", "Servicios de ingeniería", 711001, "Av. Siempre Viva 123", "Providencia", "Santiago"),
    Certificate.FromPkcs12(File.ReadAllBytes("certificado.pfx"), clave),
    new ArchivoFolios("folios.json"));
f.AgregarCaf(Caf.FromXml(File.ReadAllText("CAF33.xml")));

var doc = f.Factura(new Receptor("77.111.222-6", "Cliente Ltda"), [new Linea("Consultoría técnica", 10, 25000)]);
var envio = await f.EnviarAsync([doc]);
var estado = await f.EstadoEnvioAsync(envio.TrackId);
```

## Capa baja (control total)

Para casos especiales (liquidación-factura, bloques con nombres del SII, flujos propios) puede usar directamente las clases que usa la fachada.

### Capa baja en PHP

```php
use IngeInnova\DteChile\{Caf, Certificate, DteBuilder, TedSigner, DteSigner, EnvioDteBuilder, SiiClient};

$cert = Certificate::fromPkcs12(file_get_contents('certificado.pfx'), $clave);
$caf  = Caf::fromXml(file_get_contents('CAF33.xml'));

$builder = new DteBuilder();
$dte = $builder->build(33, $datos);                                   // $datos: ver docs en DteBuilder
$builder->attachTed($dte, (new TedSigner())->sign($caf, 33, $datos + ['montoTotal' => $builder->montoTotal($dte)]));
$firmado = (new DteSigner())->sign($dte, $cert);

$envio = (new EnvioDteBuilder())->build([$firmado], ['rutEmisor' => '76123456-0', 'rutEnvia' => '11111111-1', 'fechaResolucion' => '2026-01-01', 'numeroResolucion' => 0], $cert);
$sii = new SiiClient(SiiClient::CERTIFICACION);
$token = $sii->authenticate($cert);
$resultado = $sii->uploadEnvioDte($envio, $token, '11111111-1', '76123456-0');   // ['trackId' => ...]
$estado = $sii->getUploadStatus('76123456-0', $resultado['trackId'], $token);
```

### Capa baja en C#

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
  largos, patrones, enumeraciones) y ambas implementaciones generan y validan el XML con esa misma especificación.
- Las pruebas validan cada documento contra el XSD oficial y verifican las firmas **con herramientas independientes** (OpenSSL en PHP; la implementación de
  XMLDSig de Microsoft en .NET). Hay pruebas de tildes, `ñ`, `&`, rangos de folios, errores de usuario y cada tipo de documento.
- Los clientes del SII se prueban sin red con un transporte simulado; las URL y parámetros salen de los WSDL y manuales del SII.

```bash
php php/tests/run.php          # PHP (sin dependencias)
dotnet test dotnet/DteChile.sln # C# (.NET 8 y 10)
```

## Resolución Exenta SII N° 121

La [Resolución Exenta N° 121 (4-sep-2026)](https://www.sii.cl/normativa_legislacion/resoluciones/2026/reso121.pdf) establece nuevas validaciones técnicas y de
negocio en la recepción de DTE, con tres resultados posibles: **aceptado**, **aceptado con reparos** y **rechazado**. El detalle de cada validación y su
cronología por fases **no está en la resolución sino en un instructivo que el SII actualiza en su sitio** (Servicios online › Factura electrónica › Sistema
de facturación de mercado › Más información › Instructivo sobre validaciones de DTE). Por eso esta biblioteca **no promete** cubrir cada validación futura:
hoy garantiza lo que la resolución menciona de forma verificable (esquema, firma, timbre, folio autorizado por el CAF, codificación ISO-8859-1, coherencia de los
montos) y se actualizará a medida que el SII publique las fases. Si el SII publica reglas nuevas, abra un *issue* con el enlace al instructivo.

## Instalación

- PHP: `composer require saraivaruas/dte-chile` ([packagist.org/packages/saraivaruas/dte-chile](https://packagist.org/packages/saraivaruas/dte-chile)) o copiar `php/src` y `php/resources`.
- C#: `dotnet add package DteChile` ([nuget.org/packages/DteChile](https://www.nuget.org/packages/DteChile)) o referenciar `dotnet/src/DteChile`.

## Estado de la certificación con el SII

| Verificación | Estado |
|---|---|
| Autenticación con el SII (semilla firmada → token), facturas y boletas | **Probada** contra el ambiente de certificación con un certificado real (versión PHP) |
| Documentos válidos contra los XSD oficiales y firmas verificadas de forma independiente | **Probada** (pruebas automáticas, PHP y C#) |
| Envío de documentos con folios (CAF) reales y aceptación del SII | **Pendiente**: requiere un CAF de certificación |
| Set de pruebas y declaración de cumplimiento del SII | **Pendiente** |

El proceso de certificación del SII lo debe superar cada emisor con su propio certificado y folios; la [guía](docs/guia.md) explica cómo usar `php/examples/probar_conexion.php` y `php/examples/certificacion.php`. Cuando exista una certificación aprobada con un emisor real, se documentará aquí.

## Contribuir y seguridad

Ver [CONTRIBUTING.md](CONTRIBUTING.md) y [SECURITY.md](SECURITY.md). Los cambios entran por *pull request* y requieren aprobación del mantenedor.

## Licencia

[MIT](LICENSE) © 2026 Jorge Luis Saraiva Ruas. Los esquemas XSD de `schemas/` son publicados por el SII para uso de los contribuyentes; se incluyen sin
modificaciones como referencia de validación.
