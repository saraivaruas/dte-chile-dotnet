# Guía de uso por tipo de documento

Esta guía usa la **fachada de alto nivel** (`Facturador`): usted entrega objetos tipados y ella construye el XML, calcula los totales, asigna el folio del CAF,
timbra, firma y (si quiere) envía al SII. La capa baja (`DteBuilder`, `TedSigner`, `DteSigner`, `EnvioDteBuilder`, `SiiClient`) sigue disponible para casos especiales.

## 1. Preparación (una sola vez)

1. **Certificado digital** del representante (`.pfx`/`.p12`) con su clave. Debe traer el RUT del titular.
2. **CAF** (folios autorizados) de cada tipo de documento, descargados del sitio del SII. Un CAF es de un solo tipo y de un solo RUT.
3. **Resolución** que lo autoriza como emisor (fecha y número; número 0 si fue por internet).
4. Pase primero por el **ambiente de certificación** (`maullin.sii.cl`) antes de producción.

## 2. Crear el `Facturador`

PHP:

```php
use IngeInnova\DteChile\{ArchivoFolios, Caf, Certificate, Emisor, Facturador, SiiClient};

$f = new Facturador(
    new Emisor('76.123.456-0', 'Mi Empresa SpA', 'Servicios de ingeniería', 711001, 'Av. Siempre Viva 123', 'Providencia', 'Santiago'),
    Certificate::fromPkcs12(file_get_contents('certificado.pfx'), $clave),
    new ArchivoFolios('folios.json'),            // recuerda el último folio usado
    SiiClient::CERTIFICACION,                    // SiiClient::PRODUCCION cuando corresponda
    ['fecha' => '2026-01-01', 'numero' => 0],    // resolución del SII
);
$f->agregarCaf(Caf::fromXml(file_get_contents('CAF33.xml')));
```

C#:

```csharp
var f = new Facturador(
    new Emisor("76.123.456-0", "Mi Empresa SpA", "Servicios de ingeniería", 711001, "Av. Siempre Viva 123", "Providencia", "Santiago"),
    Certificate.FromPkcs12(File.ReadAllBytes("certificado.pfx"), clave),
    new ArchivoFolios("folios.json"),
    SiiAmbiente.Certificacion, "2026-01-01", 0);
f.AgregarCaf(Caf.FromXml(File.ReadAllText("CAF33.xml")));
```

Regla de oro: **cada folio se usa una sola vez**. `ArchivoFolios` lleva ese avance con bloqueo de archivo; si tiene varios servidores, implemente `FolioStore` / `IFolioStore` sobre su base de datos.

## 3. Documentos

Todos devuelven un `DocumentoEmitido` (folio, monto total, XML firmado, timbre). La fecha de emisión por defecto es **hoy en Chile** (`America/Santiago`); para otra fecha pase `['fechaEmision' => '2026-09-08']`.

| Documento | Tipo | PHP | C# |
|---|:-:|---|---|
| Factura electrónica | 33 | `$f->factura($receptor, $lineas)` | `f.Factura(receptor, lineas)` |
| Factura no afecta o exenta | 34 | `$f->facturaExenta(...)` | `f.FacturaExenta(...)` |
| Nota de débito | 56 | `$f->notaDebito($receptor, $lineas, $referencias)` | `f.NotaDebito(...)` |
| Nota de crédito | 61 | `$f->notaCredito($receptor, $lineas, $referencias)` | `f.NotaCredito(...)` |
| Guía de despacho | 52 | `$f->guiaDespacho($receptor, $lineas, $traslado)` | `f.GuiaDespacho(...)` |
| Boleta electrónica | 39 | `$f->boleta($lineas)` | `f.Boleta(lineas)` |
| Boleta no afecta o exenta | 41 | `$f->boletaExenta($lineas)` | `f.BoletaExenta(lineas)` |
| Otros (43, 46, 110–112) | — | `$f->emitir($tipo, $receptor, $lineas, $referencias, $opciones)` | `f.Emitir(...)` |

Ejemplos ejecutables en [`php/examples/`](../php/examples): `factura.php`, `boleta.php`, `nota_credito.php`, `guia_despacho.php` y `certificacion.php`. Todos leen su configuración de variables de entorno (ver `_comun.php`) y **no envían nada** salvo que se les pase `--enviar`.

### Factura (33) y exenta (34)

```php
$doc = $f->factura($receptor, [
    new Linea('Consultoría técnica', 10, 25000),                 // precio NETO; el IVA (19 %) se calcula
    new Linea('Capacitación', 1, 50000, exento: true),           // línea exenta
    new Linea('Licencia', 1, 100000, descuentoPct: 10),
], ['formaPago' => 2, 'fechaVencimiento' => '2026-10-31']);      // opciones: campos de la capa baja
```

### Nota de crédito (61) y de débito (56)

Exigen al menos una **referencia** al documento que corrigen. Código `Referencia::ANULA` (1), `CORRIGE_TEXTO` (2) o `CORRIGE_MONTOS` (3).

```php
$doc = $f->notaCredito($receptor, $lineas, [new Referencia(33, 1, '2026-09-08', Referencia::ANULA, 'Anula factura')]);
```

### Guía de despacho (52)

Exige el **indicador de traslado** (1 operación constituye venta … 9 venta para exportación). Datos de transporte por `opciones['transporte']`.

### Boletas (39 y 41)

Los precios **incluyen IVA** (se descompone del total), el receptor es opcional (se usa el RUT genérico 66666666-6) y viajan en un envío aparte (`EnvioBOLETA`, hasta 500 por sobre).

```php
$doc = $f->boleta([new Linea('Café', 2, 2500), new Linea('Pan', 1, 1190)]);
file_put_contents('timbre.svg', $doc->timbreSvg());   // PDF417 del timbre para la representación impresa
```

## 4. Emitir y enviar en una llamada

```php
$r = $f->emitirYEnviar(DocumentType::FACTURA_ELECTRONICA, $receptor, [new Linea('Consultoría', 10, 25000)]);
// C#: var r = await f.EmitirYEnviarAsync(DocumentType.FacturaElectronica, receptor, [new Linea("Consultoría", 10, 25000)]);

$r->estado;      // EstadoDocumento::Enviado | Emitido | Aceptado | ConReparos | Rechazado
$r->trackId;     // seguimiento del SII
$r->mensaje;     // qué pasó y qué hacer, en lenguaje de usuario (vacío si todo va bien)
$r = $f->consultar($r);   // más tarde: actualiza el estado (aceptado, con reparos, rechazado)
```

- Reintenta solo ante fallos temporales (red, SII caído); `$f->reintentos(3, 500)` ajusta cuántas veces y cuánto espera. Un rechazo del SII no se reintenta.
- Si el SII no responde, el resultado queda en **Emitido**: el folio ya se usó y el documento está firmado. Reenvíelo con `$f->reenviar($r->documento)`; no se emite otro.
- Nunca lanza por un fallo del SII: el motivo viene en `mensaje`, y el texto original en `detalleTecnico`.

## 5. Representación impresa (PDF)

```php
$pdf = $f->pdf($doc, ['unidadSii' => 'Santiago Centro', 'cedible' => true]);   // bytes del PDF
file_put_contents("factura-{$doc->folio}.pdf", $pdf);
// C#: File.WriteAllBytes("factura.pdf", f.Pdf(doc, new OpcionesPdf("Santiago Centro", Cedible: true)));
```

Genera una hoja Carta con el recuadro rojo (RUT, tipo y folio), la unidad del SII, emisor, receptor, referencias, detalle (pasa a otra página si hace falta), totales y el **timbre PDF417** con «Verifique documento: www.sii.cl». Usa el número y la fecha de resolución del `Facturador`. Sirve para todos los tipos, boletas incluidas.

- `unidadSii`: la unidad del SII que le corresponde a su empresa (por defecto SANTIAGO).
- `cedible`: agrega la leyenda CEDIBLE (solo facturas).
- El PDF no lleva logotipo; si lo necesita, genere el PDF y superponga su imagen, o use el XML con su propio formato de impresión.

## 6. Enviar por pasos y consultar

```php
$envio = $f->enviar([$doc1, $doc2]);          // autentica (semilla + token firmados) y sube; no mezcle boletas con el resto
$estado = $f->estadoEnvio($envio->trackId);   // ['estado' => 'EPR', 'aceptados' => ..., 'rechazados' => ...]
```

Si prefiere enviar por otro medio, `$f->sobre([$doc])` entrega el `EnvioDTE`/`EnvioBOLETA` firmado sin subirlo.

## 7. Probar la conexión (sin CAF y sin enviar nada)

```php
$r = $f->probarConexion();     // C#: await f.ProbarConexionAsync()
// $r->facturas, $r->boletas, $r->ok(), $r->rutTitular, $r->certificadoVence, $r->errores
```

Autentica en los servicios de facturas y de boletas y dice si el SII aceptó su certificado. No envía documentos, no necesita CAF y no expone tokens. Ejemplo: `php php/examples/probar_conexion.php`.

## 8. Certificación ante el SII

El SII autoriza a emitir tras superar su proceso de certificación con **su set de pruebas** (documentos con datos fijos que usted descarga, genera, envía y el SII revisa), más la simulación y la declaración de cumplimiento de requisitos. Esta biblioteca arma y envía los documentos; **el resultado de la certificación depende del SII** y de su empresa. Pasos:

1. Configure las variables de entorno y los CAF de **certificación** (ver `php/examples/_comun.php`).
2. `php php/examples/certificacion.php` genera un documento de cada tipo en `./salida` sin enviar nada; revise los XML.
3. Reemplace en `certificacion.php` las líneas y receptores por los del set de pruebas del SII y ejecute con `--enviar`.
4. Consulte el estado con `estadoEnvio(trackId)` hasta que el SII lo procese (`EPR`) y revise rechazos y reparos.
5. Cuando el SII lo apruebe, cambie a producción con CAF y resolución de producción.

> Mientras no haya una certificación documentada con un emisor real, el README mantiene la advertencia de que la biblioteca **no ha sido probada contra el SII con documentos de producción**.
