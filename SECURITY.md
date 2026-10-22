# Seguridad

Esta biblioteca maneja certificados digitales, claves privadas de CAF y tokens del SII.

- **Reporte de vulnerabilidades:** use *Security advisories* privados de GitHub («Report a vulnerability») o escriba al mantenedor. No publique detalles en un *issue*.
- La biblioteca no guarda ni transmite claves a terceros; solo se comunica con los servidores del SII. Nunca registre ni suba a un repositorio sus `.pfx`, CAF ni contraseñas.
- La firma RSA-SHA1 es la que exige el SII para estos documentos; no se usa para otros fines.
