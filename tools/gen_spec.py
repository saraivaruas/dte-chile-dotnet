#!/usr/bin/env python3
"""Convierte los esquemas oficiales del SII (schemas/**/*.xsd) en una especificación JSON compacta (spec/*.json).

La especificación describe, para cada elemento raíz, el árbol completo: orden de los hijos, mínimo y máximo de ocurrencias, atributos y las restricciones
de cada tipo simple (largo, patrón, enumeración, rango, dígitos). Las bibliotecas PHP y .NET la usan para armar el XML en el orden correcto, rechazar
claves desconocidas con un mensaje claro y validar largos antes de llegar al esquema oficial.

Uso:  python3 tools/gen_spec.py        (desde la raíz del repositorio; reescribe spec/)
"""
import json
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

XS = "{http://www.w3.org/2001/XMLSchema}"
RAIZ = Path(__file__).resolve().parent.parent
ESQUEMAS = [
    # (carpeta, archivo, nombre de la especificación)
    ("dte", "DTE_v10.xsd", "dte"),
    ("dte", "EnvioDTE_v10.xsd", "envio_dte"),
    ("envio_bol", "EnvioBOLETA_v11.xsd", "envio_boleta"),
    ("iecv", "LibroCV_v10.xsd", "libro_compra_venta"),
    ("lgd", "LibroGuia_v10.xsd", "libro_guia"),
    ("libro_bol", "LibroBOLETA_v10.xsd", "libro_boleta"),
    ("consumo_folios", "ConsumoFolio_v10.xsd", "consumo_folios"),
    ("ic", "RespuestaEnvioDTE_v10.xsd", "respuesta_dte"),
    ("19983", "EnvioRecibos_v10.xsd", "envio_recibos"),
    ("19983", "Recibos_v10.xsd", "recibo"),
    ("cesion", "AEC_v10.xsd", "aec"),
    ("cesion", "DTECedido_v10.xsd", "dte_cedido"),
    ("cesion", "Cesion_v10.xsd", "cesion"),
]
SKIP = {"annotation", "documentation", "appinfo"}


class Esquema:
    def __init__(self, carpeta: str, archivo: str):
        self.base = RAIZ / "schemas" / carpeta
        self.simple: dict[str, dict] = {}
        self.complejo: dict[str, ET.Element] = {}
        self.elementos: dict[str, ET.Element] = {}
        self.principales: list[str] = []  # elementos declarados en el archivo principal (no los importados, como ds:Signature)
        self._cargar(archivo, set(), True)

    def _cargar(self, archivo: str, visto: set, principal: bool = False) -> None:
        ruta = self.base / archivo
        if archivo in visto or not ruta.exists():
            return
        visto.add(archivo)
        raiz = ET.parse(ruta).getroot()
        for c in raiz:
            tag = c.tag.replace(XS, "")
            if tag == "simpleType":
                self.simple[c.get("name")] = self._simple(c)
            elif tag == "complexType":
                self.complejo[c.get("name")] = c
            elif tag == "element":
                self.elementos[c.get("name")] = c
                if principal:
                    self.principales.append(c.get("name"))
            elif tag in ("include", "import") and c.get("schemaLocation"):
                self._cargar(c.get("schemaLocation"), visto, False)

    # ------------------------------------------------------------------ tipos simples
    def _simple(self, st: ET.Element) -> dict:
        r = st.find(XS + "restriction")
        if r is None:
            return {"base": "string"}
        res: dict = {"base": (r.get("base") or "string").split(":")[-1]}
        enum = []
        for f in r:
            tag = f.tag.replace(XS, "")
            v = f.get("value")
            if tag == "enumeration":
                enum.append(v)
            elif tag in ("maxLength", "minLength", "length", "totalDigits", "fractionDigits"):
                res[tag] = int(v)
            elif tag in ("minInclusive", "maxInclusive", "minExclusive", "maxExclusive", "pattern"):
                res[tag] = v
        if enum:
            res["enum"] = enum
        base = res["base"]
        if base in self.simple:  # hereda las restricciones del tipo base nombrado
            res = {**self.simple[base], **{k: v for k, v in res.items() if k != "base"}}
        return res

    def _tipo(self, nombre: str | None) -> dict | None:
        if not nombre:
            return None
        n = nombre.split(":")[-1]
        if n in self.simple:
            return self.simple[n]
        return {"base": n} if n in ("string", "integer", "int", "decimal", "date", "dateTime", "token", "positiveInteger", "nonNegativeInteger", "long", "base64Binary", "ID", "anyURI", "boolean") else None

    # ------------------------------------------------------------------ elementos
    def nodo(self, e: ET.Element) -> dict:
        n: dict = {"name": e.get("name") or "ref:" + str(e.get("ref")), "min": int(e.get("minOccurs", "1")), "max": self._max(e.get("maxOccurs", "1"))}
        ref = e.get("ref")
        if ref:  # referencia a un elemento global (p. ej. ds:Signature)
            n["ref"] = ref
            n["name"] = ref.split(":")[-1]
            return n
        tipo = e.get("type")
        if tipo and tipo.split(":")[-1] in self.complejo:
            n.update(self._complejo(self.complejo[tipo.split(":")[-1]]))
        elif tipo:
            t = self._tipo(tipo)
            if t:
                n["type"] = t
        for sub in e:
            tag = sub.tag.replace(XS, "")
            if tag == "complexType":
                n.update(self._complejo(sub))
            elif tag == "simpleType":
                n["type"] = self._simple(sub)
        return n

    @staticmethod
    def _max(v: str):
        return None if v == "unbounded" else int(v)

    def _complejo(self, ct: ET.Element) -> dict:
        out: dict = {}
        hijos = []
        atributos = []
        for c in ct:
            tag = c.tag.replace(XS, "")
            if tag in ("sequence", "choice", "all"):
                hijos.extend(self._grupo(c))
            elif tag == "attribute":
                atributos.append({"name": c.get("name"), "required": c.get("use") == "required", "type": self._tipo(c.get("type")) if c.get("type") else (self._simple(c.find(XS + "simpleType")) if c.find(XS + "simpleType") is not None else None), "fixed": c.get("fixed")})
        if hijos:
            out["children"] = hijos
        if atributos:
            out["attributes"] = atributos
        return out

    def _items(self, g: ET.Element) -> list:
        """Hijos de un grupo (sequence/choice/all): elementos, secuencias repetibles y alternativas."""
        out = []
        for c in g:
            t = c.tag.replace(XS, "")
            if t in SKIP:
                continue
            if t == "element":
                out.append(self.nodo(c))
            elif t in ("sequence", "choice", "all"):
                out.extend(self._grupo(c))
        return out

    def _grupo(self, g: ET.Element) -> list:
        """Un grupo como lista de ítems del nivel superior: la secuencia simple se aplana; la alternativa o la secuencia repetible se conservan."""
        tag = g.tag.replace(XS, "")
        mn, mx = int(g.get("minOccurs", "1")), self._max(g.get("maxOccurs", "1"))
        items = self._items(g)
        if tag == "choice":
            return [{"choice": items, "min": mn, "max": mx}]
        if (mn, mx) == (1, 1):
            return items
        return [{"sequence": items, "min": mn, "max": mx}]


def main() -> int:
    salida = RAIZ / "spec"
    salida.mkdir(exist_ok=True)
    for carpeta, archivo, nombre in ESQUEMAS:
        esq = Esquema(carpeta, archivo)
        raices = [esq.elementos[n] for n in esq.principales]
        if not raices:
            print(f"SIN ELEMENTOS: {carpeta}/{archivo}", file=sys.stderr)
            return 1
        arbol = esq.nodo(raices[0])
        arbol["schema"] = f"{carpeta}/{archivo}"
        arbol["namespace"] = ET.parse(RAIZ / "schemas" / carpeta / archivo).getroot().get("targetNamespace")
        (salida / f"{nombre}.json").write_text(json.dumps(arbol, ensure_ascii=False, indent=1, sort_keys=False) + "\n", encoding="utf-8")
        print(f"spec/{nombre}.json  ({archivo})")
        if nombre == "envio_boleta":
            # La boleta no tiene un esquema DTE propio: su <DTE> está definido dentro de EnvioBOLETA. Se extrae como especificación independiente.
            set_dte = next(c for c in arbol["children"] if c.get("name") == "SetDTE")
            dte = next(c for c in set_dte["children"] if c.get("name") == "DTE")
            dte = {**dte, "min": 1, "max": 1, "schema": arbol["schema"], "namespace": arbol["namespace"]}
            (salida / "boleta.json").write_text(json.dumps(dte, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
            print("spec/boleta.json  (derivada de EnvioBOLETA)")
    # Copias que usan los paquetes (PHP y .NET las incluyen en su distribución); la fuente única es spec/.
    import shutil
    for destino in (RAIZ / "php" / "resources" / "spec", RAIZ / "dotnet" / "src" / "DteChile" / "Resources" / "spec"):
        destino.mkdir(parents=True, exist_ok=True)
        for f in salida.glob("*.json"):
            shutil.copy2(f, destino / f.name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
