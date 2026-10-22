# Cómo contribuir

1. Abra un *issue* describiendo el problema o la mejora. Si es una regla del SII, cite el documento oficial (resolución, instructivo, esquema).
2. Haga un *fork* y una rama; los cambios entran **solo por pull request**. `main` está protegida y cada PR requiere la aprobación del mantenedor.
3. Todo cambio de comportamiento debe traer pruebas. Las dos implementaciones (este repositorio y [dte-chile-php](https://github.com/saraivaruas/dte-chile-php)) deben seguir en paridad: si el cambio aplica a ambas, abra el issue en los dos.
4. Si cambia un esquema oficial: reemplace el XSD en `schemas/`, ejecute `python3 tools/gen_spec.py` y confirme `spec/` y
   la copia de recursos de este lenguaje juntos.
5. No incluya certificados, CAF reales, claves, RUT ni datos de contribuyentes reales, ni PDFs del SII.
6. Pruebas: `dotnet test dotnet/DteChile.sln` debe pasar.

No se aceptan copias de código con licencia incompatible con MIT (por ejemplo AGPL).
