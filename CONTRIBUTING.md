# Cómo contribuir

1. Abra un *issue* describiendo el problema o la mejora. Si es una regla del SII, cite el documento oficial (resolución, instructivo, esquema).
2. Haga un *fork* y una rama; los cambios entran **solo por pull request**. `main` está protegida y cada PR requiere la aprobación del mantenedor.
3. Todo cambio de comportamiento debe traer pruebas, en **PHP y en C#** (las dos implementaciones deben seguir en paridad).
4. Si cambia un esquema oficial: reemplace el XSD en `schemas/`, ejecute `python3 tools/gen_spec.py` y confirme `spec/`, `php/resources/spec` y
   `dotnet/src/DteChile/Resources/spec` juntos.
5. No incluya certificados, CAF reales, claves, RUT ni datos de contribuyentes reales, ni PDFs del SII.
6. Pruebas: `php php/tests/run.php` y `dotnet test dotnet/DteChile.sln` deben pasar.

No se aceptan copias de código con licencia incompatible con MIT (por ejemplo AGPL).
