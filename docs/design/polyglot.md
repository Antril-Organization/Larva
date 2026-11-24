# Larva.Polyglot — documento de diseño

> Estado: **en desarrollo** — fase 1, primer incremento: el motor de nombres y el modelo (§17). Fecha: 2026-11-24.
> Ubicación: `docs/design/` (Larva no tenía carpeta de diseño; la documentación pública de Larva vive en el sitio web, este documento es interno y de referencia previa a la implementación).

## 1. Resumen

Larva.Polyglot es el camino inverso de Larva.Bindings. `larva bind` toma una librería nativa (DLL/SO, headers `.h`) y la convierte en un assembly HIR que cualquier frontend de Larva ve con sus propios nombres. `larva polyglot` toma un **assembly HIR compilado** (una librería `.dll` de Larva, con su sección `.lva`) y la expone a lenguajes que **no** son frontends de Larva —C, C#, Python, Java, TypeScript…— con nombres, tipos y hábitos de cada uno, como si la librería se hubiera escrito en ese lenguaje:

| Declaración en la librería | C | C# | Python | Java | TypeScript |
| --- | --- | --- | --- | --- | --- |
| función `Add(i32 a, i32 b) : i32` en `Acme.Math` | `acme_math_add` | `Acme.Math.MathFunctions.Add` | `acme.math.add` | `acme.math.MathFunctions.add` | `add` (de `@acme/math`) |
| propiedad `f64 Area { get; }` | `acme_geo_polygon_get_area` | `polygon.Area` | `polygon.area` | `polygon.getArea()` | `polygon.area` |
| constante `MaxDepth` | `ACME_JSON_MAX_DEPTH` | `MaxDepth` | `MAX_DEPTH` | `MAX_DEPTH` | `MAX_DEPTH` |

Principios, en orden:

1. **Velocidad primero.** Nada de marshalling genérico, reflexión en tiempo de ejecución ni IDL interpretado. Todo se genera al compilar; una llamada que cruza cuesta lo que cuesta la FFI más barata del lenguaje destino.
2. **HIR es la verdad.** No hay IDL nuevo: el generador lee el catálogo (`HirCatalog`) de la librería compilada. Lo que no está en HIR no existe para Polyglot.
3. **Reusar, no duplicar.** Las reglas de nombres, los perfiles como código y la forma de "ver" un catálogo ya existen (System.Bindings, `LanguageView`). Polyglot los extiende.
4. **Neutral respecto al lenguaje fuente.** Polyglot trabaja sobre HIR; cualquier librería de cualquier language pack sirve. Como todo producto de Larva, sus mensajes, el código que genera y sus paquetes no nombran el lenguaje en que se escribió la librería.

## 2. Objetivo y alcance

### Objetivo

Que una librería escrita para Larva se publique y se use desde otros ecosistemas como una librería nativa de ellos: instalable con su gestor de paquetes (`pip`, NuGet, Maven, npm), con nombres idiomáticos, tipos de su sistema (nullables, excepciones, iteradores, futuros), documentación en su formato y rendimiento cercano al de una llamada directa a C.

### Dentro del alcance

- Exponer la **API pública** de una librería: tipos (clases, `value`, records, interfaces, enums, bitflags), constructores, métodos, propiedades, indexadores, operadores, constantes, funciones de módulo, extensiones.
- Reglas de nombres por lenguaje, overrides por atributo y por perfil como código.
- Generar el código puente (shims) dentro de la propia librería nativa, y la fachada (wrappers, stubs, metadatos de paquete) en cada lenguaje.
- Excepciones, nullables, genéricos (con límites), iteradores, async, reactivos/eventos, callbacks, propiedad de memoria entre GCs.
- Integración con `larva` (comando propio, chunk en `.queen`, Hive).
- Estabilidad y versionado de los nombres y del ABI.

### Fuera del alcance (por ahora)

- **Lenguajes que ya son frontends de Larva.** Si un lenguaje tiene language pack, consume la librería directamente como `.dll` HIR, con su propia `LanguageView`. Polyglot es para lenguajes de afuera.
- Heredar desde el lenguaje destino clases de la librería y sobrescribir sus métodos virtuales (sí se podrán **implementar interfaces**, fase 3).
- Publicar en registros ajenos (PyPI, NuGet.org, Maven Central, npm). Larva produce los archivos del paquete; subirlos lo hace el usuario con sus credenciales.
- Exponer código `unsafe` con punteros arbitrarios más allá de lo que ya cruza con `[Export]`.
- Inlining entre lenguajes. Una llamada que cruza es siempre una llamada.

## 3. Lo que ya existe y cómo se apoya Polyglot en ello

| Pieza existente | Qué hace hoy | Qué toma Polyglot |
| --- | --- | --- |
| `[Export]` + `larva build --dll` (`ExecutableBuilder`, `NativeSymbols`) | Funciones estáticas con tipos C pasan a ser símbolos C de una DLL; el runtime va dentro de la DLL. | La base del ABI: Polyglot **genera** funciones exportadas (shims) en HIR y las compila con el mismo camino `--dll`. |
| Restricciones AN1055/AN1056 de `[Export]` | Solo números, `bool`, punteros, `func*`; sin genéricos ni `str`. | Siguen valiendo para `[Export]` escrito a mano. Los shims generados respetan lo mismo **en su firma C**: los objetos cruzan como handles (`u64`), los textos como `(u8*, i64)`; la conversión ocurre dentro del shim. |
| `HirCatalog`, `CatalogType`, `CatalogMember` (`Larva.Hir.Services/Catalog.cs`) | Vista navegable de un assembly: tipos, miembros (incluye `Property`, `Indexer`, `Event`, `Operator`, `Conversion`), genéricos, documentación. | Entrada del generador. El `PolyglotModel` se construye recorriendo el catálogo, no el HIR crudo. |
| `LanguageView` / `LanguageViews` (`Larva.Hir.Services/Views.cs`) | Cómo un lenguaje escribe tipos, firmas y un tipo "descompilado"; registro por nombre o extensión. | Cada lenguaje destino aporta una **`TargetView : LanguageView`** registrada en `LanguageViews`. Así `larva types --view python Acme.Json.dll` muestra la librería como la ve Python, y el `.pyi`/`.d.ts` son el `Decompile` sin cuerpos. |
| System.Bindings: `Names` (`Words`, `Shape`, `NameCase`, `Strip`, `Matches`, `Writable`) | Partir nombres en palabras (acrónimos incluidos: `GPUDevice` → GPU, Device) y recomponerlos en Pascal/camel/UPPER/snake. | **El mismo algoritmo** en ambas direcciones. Se mueve a un lugar común (ver §9) y se le agregan las reglas de acrónimos. |
| System.Bindings: `IBindingProfile` / `BindingProfile` / `BindingRunner` | Perfil como código: hooks `Configure`, `Start`, filtro por declaración, `Name(kind, cName, proposed)`, `Finish`; se ejecuta como código máquina en el proceso de larva (`NativeRuntime`, `Reset()` antes y después). | **`IPolyglotProfile`** con la misma forma y el mismo mecanismo de ejecución en proceso. |
| `BindingGenerator.Reserved` (palabras reservadas que vienen del `info.keywords` del language pack) | Renombrar nombres C que chocan con el lenguaje fuente. | Misma idea, pero las listas de palabras reservadas de los destinos vienen incluidas en cada `TargetView` (los destinos no son packs). |
| `Exports.Read` (`larva bind --list`) | Lee la tabla de exportaciones PE/ELF. | Verificación post-build: la DLL exporta la tabla Polyglot y los símbolos esperados. |
| Dominios de GC (`gc-domains`), `System.Exchange` | Reglas de qué cruza entre dominios de GC: solo funciones estáticas no genéricas sobre números, `bool` y paquetes `u8*`. | El mismo problema con un GC ajeno. Polyglot reusa la disciplina (frontera explícita, copia para texto) y agrega **handles** para que los objetos sí crucen por referencia. |
| `NativeLibraryBuilder` + `larva_rt.dll` compartido | Programa partido en DLLs con un runtime compartido (sin comando todavía). | Modo "runtime compartido" para varias librerías Polyglot en un mismo proceso (fase 5). |
| Hive (`HiveManifest`: `Package`, `Version`, `Kind`, `Gc`, `Files`, firmas) | Paquetes compilados o con fuentes, firmados. | El paquete Hive puede declarar sus fachadas Polyglot y el hash de nombres; `larva polyglot pack` produce los paquetes de cada ecosistema. |

Relación con `larva bind`, en una línea: **bind lee C y escribe HIR; polyglot lee HIR y escribe C (más una fachada por lenguaje)**. Esto da una prueba de ida y vuelta gratuita: el header C que genera Polyglot, pasado por `larva bind`, debe producir una librería que llame a la original y dé los mismos resultados (ver §15, fase 1).

## 4. Arquitectura

```text
  Acme.Json.dll (librería Larva: código + sección .lva con HIR)
        │
        │ HirCatalog
        ▼
  ┌─────────────────────────────┐      perfil (System.Polyglot)      atributos en la librería
  │ PolyglotModel               │◄──── IPolyglotProfile ─────────────  [PolyglotName], [PolyglotHidden]…
  │  tipos, miembros, firmas,   │
  │  nombres por lenguaje,      │
  │  instancias genéricas       │
  └───────────┬─────────────────┘
              │
     ┌────────┴──────────────────────────────────────────────┐
     ▼                                                       ▼
  Shims (HIR generado)                               Emisores por lenguaje (TargetView)
  funciones [Export] C-ABI + tabla                   C: .h          C#: .cs + .csproj
  conversión handle↔objeto, texto,                   Python: .pyi + módulo de extensión
  captura de excepciones                             Java: .java    TS: .d.ts + .js
     │                                                       │
     ▼                                                       ▼
  larva build --dll (LLVM / x64)  ──►  acme_json.dll / .so / .pyd / .node / .wasm  +  paquetes
```

Piezas nuevas en el repo:

| Proyecto / carpeta | Contenido |
| --- | --- |
| `Larva.Polyglot/` | `PolyglotModel`, construcción desde el catálogo, resolución de nombres, generación de shims en HIR, emisores, `TargetView`s. |
| `Library/System.Polyglot/` | Atributos (`PolyglotName`, etc.), `IPolyglotProfile`, `PolyglotProfile`, `PolyglotSettings`, `PolyglotRunner`; reusa `Names`. |
| `Larva.Native/Runtime/larva_rt.c` | API de handles, adjuntar hilos, ranura de error, pins de retorno (§11). También en `NativeRuntime` y `larva_rt.js`. |
| `Larva.Cli/PolyglotCommand.cs` | `larva polyglot …`. |
| `Larva.Build` | Chunk `POLYGLOT` del `.queen`. |

## 5. Lenguajes destino

### Primera ola (fases 1–4)

| Lenguaje | Por qué | Mecanismo de llamada | Prioridad |
| --- | --- | --- | --- |
| **C** | Es el ABI de todo lo demás y la prueba de ida y vuelta con `larva bind`. También sirve a C++, Zig, Go (cgo), Swift, Nim… | Llamada directa a símbolos o a la tabla. | Fase 1 |
| **C#** | El idioma más cercano al de la librería (Pascal, propiedades, genéricos reificados); valida el mapeo de conceptos "ricos". | Punteros a función no administrados obtenidos de la tabla, firmas blittables, `Span<T>` sobre memoria fijada. | Fase 2 |
| **Python** | El caso con más distancia idiomática (snake_case, sin sobrecarga, protocolos dunder) y el mayor público. | **Módulo de extensión CPython que es la propia DLL de Larva** (exporta `PyInit_<nombre>`, usa la API estable `abi3`). Sin `ctypes`. | Fase 2 |
| **Java** | Ecosistema grande, sin enteros sin signo, convenciones de getters. | API de funciones foráneas de Java 22+ (`java.lang.foreign`, `Linker`, downcalls con la opción `critical` para llamadas cortas). | Fase 4 |
| **TypeScript / JavaScript** | Web y Node. Larva ya tiene backend wasm y `larva_rt.js`. | Navegador/Node: módulo `.wasm` + glue ESM + `.d.ts`. Node nativo (opcional): addon N-API construido como la DLL de Larva, igual que Python. | Fase 4 |

### Después (fase 5+), en orden tentativo

Rust (crate con `extern "C"` + tipos seguros), Kotlin (sobre la fachada Java, con nullables y propiedades propias), Swift, Go, C++ (header-only con RAII sobre el header C).

## 6. Reglas de nomenclatura

### 6.1 Entrada: cómo se parten los nombres

Todo nombre de la librería se descompone en **palabras** con `Names.Words` (cortes en `_`, en minúscula→mayúscula y en fin de acrónimo: `GPUDevice` → `GPU`, `Device`; `parseJSONValue` → `parse`, `JSON`, `Value`). Cada palabra se marca como **acrónimo** si está toda en mayúsculas y tiene 2+ letras. Las reglas de cada lenguaje recomponen esas palabras. El resultado se calcula **una sola vez por nombre y lenguaje** y queda en el `PolyglotModel` (y en el archivo de nombres, §13).

Se agrega a `NameCase` (o a una enumeración nueva `TargetCase`) lo que falta: `Kebab` (paquetes npm), `Lower` (paquetes Java: palabras pegadas en minúscula) y la variante de acrónimos.

### 6.2 Acrónimos

Tres políticas, elegibles por lenguaje en la configuración (`acronyms:`):

| Política | `GPUDevice` | `HTTPClient` | `IOStream` |
| --- | --- | --- | --- |
| `keep` — como lo escribió el autor | `GPUDevice` | `HTTPClient` | `IOStream` |
| `short-upper` — 2 letras en mayúscula, 3+ como palabra | `GpuDevice` | `HttpClient` | `IOStream` |
| `words` — todo acrónimo como palabra | `GpuDevice` | `HttpClient` | `IoStream` |

En los casos snake/UPPER la política no importa (`gpu_device`, `GPU_DEVICE`). **Recomendación por defecto:** `keep` en C#, Java y TypeScript (respeta al autor; muchas APIs de esos ecosistemas mantienen acrónimos: `URL`, `UUID`), `words` en Rust (lo piden sus API guidelines). Un `acronyms: short-upper` en C# queda como opción.

### 6.3 Tabla por lenguaje

| Elemento | C | C# | Python | Java | TypeScript |
| --- | --- | --- | --- | --- | --- |
| Namespace / módulo | prefijo `acme_json_` | `Acme.Json` (Pascal por segmento) | paquete `acme.json` (snake por segmento) | paquete `com.acme.json` (minúscula, palabras pegadas; raíz configurable) | paquete npm `@acme/json` (kebab); submódulos por subpath |
| Tipo (clase, value, record) | `acme_json_parser` (+ `_t` opcional) | `Parser` | `Parser` (CapWords; acrónimos en mayúscula según PEP 8) | `Parser` | `Parser` |
| Interfaz | `acme_json_reader` | `IReader` (se agrega `I` si falta) | `Reader` (clase abstracta / `Protocol`) | `Reader` (se quita `I` si la tiene: `IReader` → `Reader`) | `Reader` (se quita `I`) |
| Método | `acme_json_parser_parse` | `Parse` | `parse` | `parse` | `parse` |
| Método async | `…_parse_async` + callback | `ParseAsync` (sufijo `Async`) | `async def parse` (sin sufijo) | `parseAsync` → `CompletableFuture` | `parse` → `Promise` (sin sufijo) |
| Propiedad | `…_get_depth` / `…_set_depth` | `Depth` | `depth` (`@property`) | `getDepth()` / `setDepth()`; `bool` → `isEnabled()` | `depth` (`get`/`set`) |
| Campo público (`value`) | campo del `struct`: `depth` | `Depth` | `depth` | `depth()` en records; `getDepth()` en clases | `depth` |
| Constante | `ACME_JSON_MAX_DEPTH` | `MaxDepth` (`const`) | `MAX_DEPTH` (módulo o atributo de clase) | `MAX_DEPTH` (`static final`) | `MAX_DEPTH` |
| Enum (tipo) | `acme_json_mode` | `Mode` | `Mode(IntEnum)` | `Mode` (enum) | `Mode` (`enum` de TS) |
| Miembro de enum | `ACME_JSON_MODE_READ` | `Read` | `READ` | `READ` | `Read` |
| Bitflags | igual que enum + `|` | `[Flags] enum` | `IntFlag` | clase con constantes `int` + `EnumSet`-like helpers (ver §7.10) | `enum` + funciones `has()` |
| Parámetro | `depth` (snake) | `depth` (camel) | `depth` (snake) | `depth` (camel) | `depth` (camel) |
| Funciones de módulo | `acme_json_parse` | método estático de una clase estática (nombre en §6.5) | función del módulo | método estático de una clase `final` | `export function` |
| Extensión (`this T x`) | función con el receptor primero | método de extensión | función del módulo; método de la clase si `T` es de la misma librería | método estático | función exportada |
| Indexador | `…_get_item` / `…_set_item` | `this[...]` | `__getitem__` / `__setitem__` | `get(i)` / `set(i, v)` | `get(i)` / `set(i, v)` (sin `Proxy`, es lento) |
| Operadores | `…_op_add` | operadores de C# | dunders (`__add__`, `__eq__`, `__lt__`…) | `add`, `subtract`, `compareTo`, `equals` | `add`, `equals`… |
| `ToString` / igualdad / hash | `…_to_string` | `ToString`/`Equals`/`GetHashCode` | `__str__`/`__repr__`/`__eq__`/`__hash__` | `toString`/`equals`/`hashCode` | `toString`/`equals` |
| Archivo | `acme_json.h` | `Acme.Json.cs` (o uno por tipo) | `acme/json/__init__.pyi` | `com/acme/json/Parser.java` | `index.d.ts`, `index.js` |

Fuera de la primera ola (referencia para cuando se agreguen): Rust — tipos Pascal, funciones/módulos snake, constantes UPPER, acrónimos como palabras; Kotlin — como Java pero propiedades nativas y `suspend fun` en lugar de `CompletableFuture`; Go — exportados en Pascal, acrónimos en mayúscula (`ParseURL`, `ID`).

### 6.4 Palabras reservadas

Cada `TargetView` trae la lista de su lenguaje (incluye palabras contextuales donde molesten). Regla de escape por lenguaje:

| Lenguaje | Escape | Ejemplo |
| --- | --- | --- |
| C | sufijo `_` | `default` → `default_` |
| C# | prefijo `@` (verbatim) | `event` → `@event` (solo en la declaración; el usuario escribe `@event` igual que con cualquier API de C#) |
| Python | sufijo `_` (PEP 8) | `from` → `from_`, `lambda` → `lambda_`. Las soft keywords (`match`, `case`, `type`, `_`) **no** se escapan. Los builtins (`id`, `type`, `list`) tampoco. |
| Java | sufijo `_` (no hay escape) | `default` → `default_`, `record` (contextual) no se escapa salvo donde rompe |
| TypeScript | sufijo `_` solo para identificadores sueltos (parámetros, funciones exportadas); como nombre de miembro se deja tal cual (`obj.delete` es válido) | parámetro `new` → `new_`; método `delete` → `delete` |
| Rust | `r#` | `type` → `r#type` |

Además: nombres que empiezan con dígito → `Names.Writable` (`_` delante); nombres que chocan con miembros heredados del lenguaje (`GetType`/`getClass`/`__class__`/`constructor`) → sufijo `_` y aviso.

### 6.5 Colisiones creadas por la transformación

La recomposición puede juntar dos nombres distintos: `GetValue` y `getValue` → `get_value`; propiedad `Count` y método `GetCount()` → Java `getCount()` dos veces; funciones de módulo de dos namespaces en una clase estática de C#.

Regla: **una colisión es un error del generador**, nunca un renombre silencioso. El mensaje dice los dos orígenes, el nombre resultante y cómo resolverlo:

```text
Acme.Json.dll: error PG0101: `Parser.GetValue()` and `Parser.getValue()` both become `get_value` in python: rename one with [PolyglotName("python", …)] or in the profile's Name hook
```

Las funciones de módulo (que no tienen tipo contenedor) necesitan una clase en C# y Java. Nombre por defecto: el último segmento del namespace en Pascal + `Functions` (`Acme.Math` → `MathFunctions`); configurable (`csharp.module-class`, `java.module-class`) y por perfil.

### 6.6 Sobrecargas

| Lenguaje | Estrategia |
| --- | --- |
| C | Sin sobrecarga: la primera queda con el nombre base; las demás con sufijo de tipos de parámetro (`acme_math_add_i64`, `acme_math_add_f64`). El perfil puede elegir otros. |
| C#, Java | Sobrecargas nativas. En Java se revisa la colisión por *erasure* de genéricos (PG0102). |
| Python | **Una** función que despacha: primero por cantidad de argumentos (barato), luego por tipo (`PyLong_Check`, `PyFloat_Check`, `PyUnicode_Check`, tipo exacto de la clase envoltorio). El despacho está en el shim compilado, no en Python. Si dos sobrecargas no se distinguen desde Python (`i32` vs `i64`: ambos `int`) → error PG0103 con sugerencia de renombrar; o el perfil declara `Collapse` y se usa la más ancha. |
| TypeScript | Firmas de sobrecarga en el `.d.ts` y una implementación que despacha como en Python (`number` vs `bigint` vs `string`…). |

### 6.7 Parámetros con nombre y valores por defecto

En Python, C# y Kotlin los **nombres de parámetro son parte de la API** (argumentos con nombre). Se tratan como nombres estables (§13). Si la librería declara valores por defecto, se exponen como valores por defecto del destino donde existan (Python, C#, TS) y como sobrecargas en Java.

## 7. Mapeo de conceptos

### 7.1 Tipos primitivos y texto

| Larva | C | C# | Python | Java | TypeScript |
| --- | --- | --- | --- | --- | --- |
| `i8`…`i64` | `int8_t`…`int64_t` | `sbyte`…`long` | `int` (chequeo de rango → `OverflowError`) | `byte`…`long` | `number`; `i64` → `bigint` |
| `u8`, `u16`, `u32`, `u64` | `uint*_t` | `byte`…`ulong` | `int` | `short`, `int`, `long`, `long` (bits crudos, documentado) | `number`; `u64` → `bigint` |
| `f32`, `f64` | `float`, `double` | `float`, `double` | `float` | `float`, `double` | `number` |
| `bool` | `bool` | `bool` | `bool` | `boolean` | `boolean` |
| `iptr`, `uptr` | `intptr_t`, `uintptr_t` | `nint`, `nuint` | `int` | `long` | `bigint` |
| `str` (UTF-8) | `const char*` + longitud | `string` | `str` | `String` | `string` |
| `any` | handle | `object` (proxy) | `object` | `Object` | `unknown` |
| tuplas | struct | `ValueTuple` | `tuple` | record por aridad generado | tupla de TS |

Java sin enteros sin signo: se ensancha donde se puede (`u8` → `short`, `u16` → `int`, `u32` → `long`) y `u64` va en `long` con los bits crudos (más `Long.toUnsignedString` en la doc). Ensanchar `u64` a `BigInteger` sería idiomático pero lento: queda como opción por perfil.

**Texto, el caso caro.** `str` es UTF-8; C#, Java y JS son UTF-16; Python tiene su propia representación. Reglas:

- **Entrada** (destino → Larva): el shim recibe `(ptr, len)` en la **codificación nativa del destino** y construye el `str` en una pasada (transcodifica UTF-16→UTF-8 al copiar). Por eso los shims con texto se generan en dos familias: UTF-8 (C, Python, Rust, Go) y UTF-16 (C#, Java, JS). Una copia, ninguna intermedia.
- **Salida** (Larva → destino): el shim devuelve `(ptr, len)` apuntando al payload del `str`, **fijado** en la ranura de pin de retorno del hilo (§11.4); el envoltorio copia inmediatamente al string nativo (`PyUnicode_DecodeUTF8`, `string.Create` con transcodificación, etc.) y el pin se libera en el siguiente cruce del hilo. Una copia, sin `malloc`/`free` por llamada.
- `System.Exchange` (paquetes) no se usa en el camino rápido: implica un `alloc` y una copia extra.

### 7.2 Clases, `value` y records

| Larva | Cruza como | C# | Python | Java | TypeScript |
| --- | --- | --- | --- | --- | --- |
| `class` | handle opaco (`u64`) | `sealed class` envoltorio con el handle | clase de extensión con el handle en el objeto | `final class` con el handle | `class` con el handle |
| `value` **blittable** (solo números, `bool`, punteros, otros `value` blittables) | **por valor**, con el layout C de la librería | `struct` con `[StructLayout(Sequential)]` idéntico (cero copias extra) | clase con `__slots__` (copia) o vista sobre buffer para arrays | `record` (copia) | objeto plano (copia) |
| `value` no blittable (contiene objetos/`str`) | boxeado como handle | `sealed class` | clase | `final class` | `class` |
| `record class` / `record value` | como su base | `record` / `record struct` | `@dataclass(frozen=True)`-like (generado en C) | `record` | `class` con `equals` |
| `static class` | — | `static class` | módulo o clase sin instancias | `final class` con constructor privado | objeto/namespace exportado |
| interfaz | handle + tabla de métodos | `interface` | clase abstracta (`Protocol` en el `.pyi`) | `interface` | `interface` |

Herencia: la jerarquía de clases de la librería se reproduce en las fachadas (el envoltorio de `Kelvin` hereda del de `Temperature`) para que los tipos de parámetro funcionen. Un objeto que vuelve de Larva se envuelve con el **tipo dinámico real** (se lee el id de tipo del encabezado: una lectura, sin reflexión) para que `isinstance`/`is`/`instanceof` funcionen.

### 7.3 Genéricos

Larva compila genéricos `shared` o `specialize` (BUILD `generics:`), y el monomorfizador decide instancias al enlazar. Un lenguaje de afuera no puede pedir una instancia nueva en tiempo de ejecución, así que:

1. **Argumentos de tipo referencia → una instancia compartida.** Con `generics: shared`, todo `T` que sea objeto comparte código (son handles). Polyglot exporta **una** instancia por definición genérica para todos los `T` referencia, y la fachada la tipa con los genéricos del destino: C# `List<T> where T : LarvaObject`, Python `Generic[T]` en el `.pyi`, Java genéricos (el *erasure* encaja exacto), TS genéricos. Sin costo por tipo.
2. **Argumentos primitivos o `value` → instancias cerradas.** Se instancian automáticamente todas las que aparezcan en firmas públicas (`List<i32>` como resultado de un método público ya está instanciado). Las demás se piden con `[PolyglotInstances(List<f64>, Dictionary<str, i32>)]` en la librería o en el perfil. En C# la fachada sigue siendo `List<double>`; en Python `List[float]`; en Java `List<Double>` (con métodos primitivos extra `getDouble(i)` para no boxear); en TS `List<number>`.
3. **Métodos genéricos**: igual, por instancias declaradas o usadas.
4. **Genéricos variádicos** (`T...`) y restricciones numéricas: solo por instancias cerradas.
5. Una instancia no disponible es un error en la generación del binding (PG0201), nunca en tiempo de ejecución.

### 7.4 Nullables

| Larva | C | C# | Python | Java | TypeScript |
| --- | --- | --- | --- | --- | --- |
| objeto `T?` | handle `0` = null | `T?` (anotaciones de nulabilidad activadas en el archivo generado) | `T \| None` | `@Nullable T` (anotaciones JSpecify) | `T \| null` |
| `T` no nullable | handle ≠ 0 (el shim lo verifica al entrar) | `T` | `T` (pasar `None` → `TypeError`) | `T` (+ `Objects.requireNonNull` en el envoltorio) | `T` |
| valor `i32?` | `bool has` + valor (par en registros o puntero de salida) | `int?` | `int \| None` | `Integer` (boxeado) o `OptionalInt` (opción) | `number \| null` |

No se usa `Optional<T>` de Java para parámetros (no es idiomático) ni para resultados por defecto (costo de asignación); queda como opción de perfil.

### 7.5 Errores y excepciones

Ninguna excepción **desenrolla a través de la frontera**, en ningún sentido. En Windows las excepciones de Larva son excepciones estructuradas (`0xE04C5641`) y el runtime de C#/Java/Python no las espera; en POSIX el mecanismo tampoco es compartido.

**Larva → destino:**

- Cada shim tiene un `try` que captura `Exception` (en Windows con tablas `.pdata`: costo cero en el camino sin error).
- Firma C de un shim: el error sale por un **puntero de salida al final** (`larva_error* err`), que el envoltorio pone en una variable de su pila. Comprobarlo es un compare. Se descartó un "último error" en TLS: una lectura TLS por llamada en C# y Java, y problemas de reentrada con callbacks.
- `larva_error` es un handle a la excepción. El envoltorio crea la excepción idiomática del destino conservando el handle (mensaje, tipo, traza de pila de Larva, `InnerException`/`__cause__`).
- Jerarquía: cada clase de excepción pública de la librería tiene su clase en el destino (`ParseException` → C# `ParseException : LarvaException`; Python `ParseError(LarvaError)` — sufijo `Error` por convención; Java `ParseException extends RuntimeException` — **unchecked**, porque la librería no declara qué lanza). Las excepciones estándar se mapean a las nativas cuando el significado coincide:

| System.Core | C# | Python | Java | TypeScript |
| --- | --- | --- | --- | --- |
| `ArgumentException` | `ArgumentException` | `ValueError` | `IllegalArgumentException` | `RangeError`/`TypeError` |
| `ArgumentNullException` | `ArgumentNullException` | `TypeError` | `NullPointerException` | `TypeError` |
| `IndexOutOfRangeException` | `IndexOutOfRangeException` | `IndexError` | `IndexOutOfBoundsException` | `RangeError` |
| `KeyNotFoundException` | `KeyNotFoundException` | `KeyError` | `NoSuchElementException` | `Error` |
| `InvalidOperationException` | `InvalidOperationException` | `RuntimeError` | `IllegalStateException` | `Error` |
| `NotSupportedException` | `NotSupportedException` | `NotImplementedError` | `UnsupportedOperationException` | `Error` |
| `IOException` | `IOException` | `OSError` | `java.io.UncheckedIOException` | `Error` |
| `OverflowException` | `OverflowException` | `OverflowError` | `ArithmeticException` | `RangeError` |

(Los nombres exactos de System.Core se verifican al implementar; la tabla vive en código, no en cada emisor.)

- **Fallas que no son excepciones** (accesos inválidos, aborts del runtime) siguen terminando el proceso, como hoy.

**Destino → Larva (callbacks):** el trampolín que llama al callable del destino captura su excepción, la guarda (referencia fuerte del lado destino) y lanza en Larva una `ForeignException` con el mensaje. Si esa `ForeignException` vuelve a cruzar hacia el destino sin que nadie la atrape, el envoltorio relanza **el objeto original**: la identidad del error se conserva en el viaje de ida y vuelta.

### 7.6 Propiedades e indexadores

Las propiedades (`T X { get; set; }`) se ven como en §6.3. Una propiedad de solo lectura no genera setter; un `private set` tampoco. Propiedades estáticas: C# `static`, Python atributo de clase vía descriptor, Java `getX()` estático, TS `static get`. Indexadores con varios parámetros: C# multi-índice, Python `__getitem__` con tupla, Java/TS `get(a, b)`.

### 7.7 Eventos y valores reactivos

El catálogo tiene `CatalogMemberKind.Event`; la librería estándar tiene `System.Reactive` (`Source`, `Computed<T>`, `Effect`, `Reactive.Watch`), y los miembros `reactive`/`computed` se reescriben en propiedades + `Source`/`Computed`. Mapeo:

| Larva | C | C# | Python | Java | TypeScript |
| --- | --- | --- | --- | --- | --- |
| evento | `…_add_<evento>(fn, ctx) → token`, `…_remove(token)` | `event` con `add`/`remove` | `obj.on_<evento>(fn) → Subscription` (context manager, `.close()`) | `addXListener(listener) → AutoCloseable` | `on<Evento>(fn) → () => void` |
| `reactive T X` | propiedad + `…_watch_x` | propiedad + `INotifyPropertyChanged` (opcional por perfil) y `WatchX(Action<T>)` | propiedad + `watch_x(fn)` | `getX`/`setX` + `watchX(Consumer<T>)` | propiedad + `watchX(fn)` |
| `computed T X` | getter | propiedad de solo lectura | propiedad | `getX()` | `get x` |
| `Source` / `Computed<T>` expuestos | handle | `IObservable<T>` (adaptador) | protocolo de suscripción | `Flow.Publisher<T>` (adaptador) | `subscribe(fn)` |

Las notificaciones llegan en el hilo donde Larva las dispara. El envoltorio no cambia de hilo salvo que el perfil pida un "dispatcher" (UI).

### 7.8 Callbacks y funciones

Un parámetro de tipo función (`func<A> : R`) o una interfaz que el destino implementa necesita un **trampolín**: el shim recibe un puntero a función C del envoltorio y un contexto (`void*`) que identifica el callable del destino (`GCHandle` en C#, una referencia con `Py_INCREF` en Python, una upcall stub de Java, un índice en una tabla en JS). Larva lo guarda en un objeto función propio; al liberarse ese objeto (destructor) se llama a `release(ctx)`.

Ciclos entre GCs (un objeto de Larva que guarda un callback que captura un envoltorio del mismo objeto) **no se recolectan**: ningún GC ve el ciclo completo. Se documenta, y los eventos devuelven siempre una suscripción cerrable para cortar el ciclo a mano. Ver pregunta abierta Q6.

### 7.9 Iteradores, colecciones y streams

| Larva | C | C# | Python | Java | TypeScript |
| --- | --- | --- | --- | --- | --- |
| función con `yield` / iterable | `…_next(it, &out) → bool` | `IEnumerable<T>` | iterador (`__iter__`/`__next__`, `StopIteration`) | `Iterable<T>` + `stream()` vía `Spliterator` | `Iterable<T>` (`[Symbol.iterator]`) |
| `List<T>` | handle + `len`/`get`/`set`/`add` | `IList<T>` (envoltorio vivo, sin copia) | `MutableSequence` (`__len__`, `__getitem__`…) | `java.util.List<T>` (`AbstractList`) | clase con `length`, `get`, `[Symbol.iterator]` |
| `Dictionary<K,V>` | handle + funciones | `IDictionary<K,V>` | `MutableMapping` | `java.util.Map<K,V>` | `Map`-like |
| `T[]` de primitivos | `(ptr, len)` | `Span<T>` / `ReadOnlySpan<T>` sobre memoria fijada | `memoryview` (buffer protocol) | `MemorySegment` | `TypedArray` sobre la memoria wasm |
| `System.IO.Stream` | `read`/`write` | `Stream` (subclase adaptadora) | objeto binario tipo `io.RawIOBase` | `InputStream`/`OutputStream` | `ReadableStream` |

Velocidad: un iterador de primitivos o `value` blittables cruza **por lotes** (`…_next_batch(it, buf, cap) → n`, el envoltorio itera su buffer local). Un cruce cada 64–256 elementos en lugar de uno por elemento. Las colecciones se envuelven "vivas" (sin copia) y además ofrecen copia masiva (`ToArray()`, `to_list()`, `toArray()`) en una sola llamada.

Las vistas de arrays (`Span`, `memoryview`) fijan el array (pin) mientras viven: el nursery de Larva copia objetos. En C# la vista va en un `ref struct` acotado a un `using`/scope; en Python el buffer se libera en `PyBuffer_Release`.

### 7.10 Enums y bitflags

Cruzan como su entero subyacente, sin conversión. C#: `enum`/`[Flags] enum` con el mismo subyacente. Python: `IntEnum`/`IntFlag` (crear el miembro desde el entero es una búsqueda en dict; se cachea). Java: `enum` con campo `value` y `fromValue(int)` por tabla; los bitflags no son un `enum` de Java (no combinan) sino una clase `final` con constantes `int` y métodos `has`/`with`. TS: `enum` numérico.

### 7.11 Async

`async` en Larva produce `Task`/`Task<T>` sobre un scheduler de un hilo (`System.Threading.Tasks.Scheduler`); `Wait()`/`Result` lo hacen correr.

Dos modos (`async:` en la configuración):

- **`thread` (por defecto en hosts con hilos: C#, Java, Python, Node nativo).** La librería arranca, en la primera llamada async, un hilo propio que corre el scheduler. Una llamada async encola el trabajo y devuelve de inmediato un futuro del destino; al completarse la tarea, Larva llama un callback que lo resuelve: C# `TaskCompletionSource` (con `RunContinuationsAsynchronously`), Python `loop.call_soon_threadsafe(fut.set_result, …)` sobre el loop de `asyncio` capturado al llamar, Java `CompletableFuture.complete`, JS `Promise` vía cola thread-safe de N-API.
- **`pump` (por defecto en wasm y opción para hosts de un solo hilo).** No hay hilo de Larva: el envoltorio corre `larva_scheduler_run_ready()` desde el loop del destino (microtareas en JS, un callback en el loop de asyncio).

Cancelación: `CancellationToken` de C#, `asyncio.CancelledError`, `CompletableFuture.cancel`, `AbortSignal` → se propagan como cancelación de la tarea de Larva si la librería la soporta (pregunta abierta Q7).

### 7.12 GC y propiedad de memoria

El problema central. Los objetos de Larva viven en su heap (tracing generacional con nursery **que copia**, refcount, arena o manual según `gc:` de la librería). El destino tiene su propio GC (o ninguno, en C).

**Handles.** Un objeto nunca cruza como dirección del payload (el nursery lo movería). Cruza como **handle**: índice en una tabla de handles del runtime (`slot` + generación en 64 bits). La tabla es un conjunto de raíces precisas para el collector, que actualiza los slots al mover objetos. API nueva del runtime (§11):

```c
uint64_t larva_handle_new(void *obj);      /* raíz fuerte; en refcount: retain */
void    *larva_handle_get(uint64_t h);     /* carga del slot; la generación se verifica en builds de diagnóstico */
void     larva_handle_free(uint64_t h);    /* libera la raíz; en refcount: release */
uint64_t larva_handle_dup(uint64_t h);
```

`larva_handle_get` es una carga y un desplazamiento: el shim lo inlinea (LLVM lo ve como IR, no como llamada).

**Quién libera.** Cada envoltorio es dueño de **un** handle:

| Lenguaje | Liberación determinista | Red de seguridad |
| --- | --- | --- |
| C | `acme_json_parser_free(p)` | ninguna |
| C# | `IDisposable` (`Dispose`) si el tipo tiene `destroy()` o recursos; si no, nada | finalizador vía `SafeHandle` |
| Python | context manager (`with`) si el tipo tiene `destroy()` | `tp_dealloc` (refcount de CPython: inmediato en la práctica) |
| Java | `AutoCloseable` | `Cleaner` |
| TypeScript | `[Symbol.dispose]` (`using`) | `FinalizationRegistry` |

`destroy()` de Larva se expone como `Dispose`/`close`/`__exit__` solo si la clase lo declara; liberar el último handle no llama a `destroy()` directamente, solo suelta la raíz (el GC de Larva decide, como siempre).

**Modos de GC de la librería:**

| `gc:` | Comportamiento con Polyglot |
| --- | --- |
| `default`/`tracing` | Recomendado. Tabla de handles como raíces. |
| `refcount` | Handle = una referencia contada (`retain`/`release`). Sin movimiento; ciclos sin recolectar, como siempre. |
| `arena` | Solo cruzan objetos creados fuera de toda `region { }` (viven hasta el fin del programa): el chequeo de regiones (`RegionChecker`) ya rechaza que un objeto de una región escape por un shim (`RegionEscape`). Aviso PG0301 al generar. |
| `manual` | El handle no mantiene vivo nada: el destino debe llamar `Dispose`/`close`, que hace `delete`. Usar un handle liberado es un error detectado por la generación del handle. Aviso PG0302. |

**Hilos.** Un hilo del destino que llama a la librería por primera vez se **adjunta** al runtime (`larva_thread_attach`, que crea su `LarvaThread` y su bloque de nursery; activa el modo multihilo del runtime si es el segundo hilo). El envoltorio guarda en TLS del destino un flag "ya adjuntado": el costo es una lectura de TLS **solo en la primera llamada de cada hilo**… en la práctica se comprueba con un `[ThreadStatic]`/`threading.local`/`ThreadLocal` barato, o se evita del todo si el host garantiza un solo hilo (Python con GIL activo: se adjunta el hilo principal al importar). Los hilos adjuntados cuentan como "bloqueados" para el collector mientras están fuera de Larva (`BLOCKING_ENTER` al salir del shim), así una colección nunca espera a un hilo que está corriendo código del destino.

**Identidad.** Por defecto dos envoltorios distintos pueden referir al mismo objeto (sin cache de identidad: costo cero). `==`/`Equals`/`__eq__` de los envoltorios comparan identidad del objeto de Larva (o la igualdad que la clase defina). Para tipos donde `is`/`ReferenceEquals` importe, `[PolyglotIdentity]` activa una tabla débil handle→envoltorio (pregunta abierta Q5).

**Varias librerías en un proceso.** Hoy cada DLL de Larva lleva su runtime y su heap: dos librerías Polyglot en el mismo proceso Python no pueden pasarse objetos entre sí (como entre dominios de GC distintos). Fase 5: modo `runtime: shared` con `larva_rt.dll` compartido (`NativeLibraryBuilder`).

## 8. Overrides y alias por lenguaje

Dos mecanismos, con precedencia **perfil > atributo > reglas**:

### 8.1 Atributos en la librería (intención del autor)

En `System.Polyglot`, conservados en HIR (son atributos de librería, se leen del catálogo):

| Atributo | Sobre | Efecto |
| --- | --- | --- |
| `[PolyglotName("python", "loads")]` | tipo, miembro, parámetro | Nombre exacto en ese lenguaje (repetible; `"*"` = todos). |
| `[PolyglotHidden]`, `[PolyglotHidden("java")]` | tipo, miembro | No se expone (en todos o en uno). |
| `[PolyglotInstances(List<i32>, Box<f64>)]` | assembly o tipo | Instancias genéricas cerradas a exportar. |
| `[PolyglotIdentity]` | clase | Conserva la identidad del envoltorio (tabla débil). |
| `[PolyglotModule("csharp", "JsonFunctions")]` | namespace / funciones de módulo | Clase contenedora en C#/Java. |
| `[PolyglotCollapse]` | sobrecarga | En lenguajes sin sobrecarga, esta sobrecarga absorbe a las que no se distinguen. |

Los atributos expresan decisiones que valen para todo consumidor de la librería. Son opcionales: una librería sin ninguno debe producir un binding correcto.

### 8.2 Perfil como código (política, y librerías ajenas)

Para reglas en bloque y para librerías que no son tuyas (no podés agregarles atributos). Misma forma que System.Bindings:

```antril
// Polyglot.ant — perfil de la fachada de Acme.Json.
import System;
import System.Polyglot;

public class AcmeJsonPolyglot : PolyglotProfile
{
	public:
		override Configure(PolyglotSettings s)
		{
			s.Languages.Add(TargetLanguage.Python);
			s.Languages.Add(TargetLanguage.CSharp);
			s.JavaPackageRoot = "com.acme";
			s.Acronyms(TargetLanguage.CSharp, AcronymPolicy.ShortUpper);
		}

		// Ocultar lo experimental en todos los lenguajes.
		override Member(PolyglotMember m) : bool => !m.Source.StartsWith("Experimental");

		// Python: `Parse(str)` se llama `loads`, como el módulo json.
		override Name(TargetLanguage lang, NameKind kind, str source, str proposed) : str
		{
			if (lang == TargetLanguage.Python && source == "Acme.Json.Json.Parse")
				return "loads";
			return proposed;
		}
}
```

`IPolyglotProfile`: `Configure(PolyglotSettings)`, `Start(PolyglotModel)`, `Type(PolyglotType) : bool`, `Member(PolyglotMember) : bool`, `Name(TargetLanguage, NameKind, str source, str proposed) : str`, `Finish(PolyglotModel)`. Se ejecuta igual que un perfil de bind: compilado y corrido en el proceso de larva con `NativeRuntime` (con `Reset()` antes y después). `NameKind` se extiende con `Property`, `Method`, `Namespace`, `Module`, `Event` (hoy tiene `Type`, `Function`, `Constant`, `Field`, `Parameter`, `EnumMember`).

## 9. Reuso del código de nombres

`Names` y `NameCase` viven hoy en `Library/System.Bindings/Names.ant`. Opciones:

- **A (recomendada):** moverlos a una librería pequeña común (`System.Naming`, o dentro de System.Core como `System.Text.Naming`), de la que dependen System.Bindings y System.Polyglot. Un único algoritmo de palabras/acrónimos para los dos sentidos.
- B: System.Polyglot referencia a System.Bindings. Funciona, pero arrastra el modelo C a quien solo quiere Polyglot.

El lado C# de larva (generador) necesita las mismas reglas: se porta `Words`/`Shape` a `Larva.Polyglot` con **tests de equivalencia** contra la versión de la librería (mismas entradas, mismas salidas), o el generador llama al perfil por defecto para todo nombre (más lento; descartado para el camino por defecto).

## 10. Artefactos generados e integración con `larva`

### 10.1 Por lenguaje

| Lenguaje | Binario | Fachada | Paquete (`--pack`) |
| --- | --- | --- | --- |
| C | `acme_json.dll`/`.so` + `.lib` | `acme_json.h` (con docs como comentarios) | zip con `include/`, `lib/`, `bin/` por plataforma |
| C# | la misma DLL nativa | `Acme.Json.cs` (o un archivo por tipo), `Acme.Json.csproj`, doc XML | `.nupkg` con `runtimes/<rid>/native/` |
| Python | **la DLL renombrada** `acme_json.cp310-abi3-win_amd64.pyd` (o `.abi3.so`): exporta `PyInit_acme_json` | `acme/json/__init__.pyi`, `py.typed`, `pyproject.toml` | wheel `cp310-abi3-<plataforma>` |
| Java | la misma DLL nativa | `com/acme/json/*.java`, `module-info.java`, `pom.xml` | `.jar` con la librería nativa en recursos y cargador |
| TypeScript | `acme_json.wasm` (backend wasm) o `acme_json.node` (N-API, construido como la DLL) | `index.js` (ESM), `index.d.ts`, `package.json` | `.tgz` de npm |

**Python sin C intermedio.** En lugar de generar una extensión en C que llame a la DLL (doble salto) o usar `ctypes` (≈10× más lento por llamada), el shim de Python **es** código de Larva: se genera HIR que usa la API estable de CPython (`python3.dll`/`libpython3.so`, ligada con un binding hecho con `larva bind` sobre los headers de Python, abi3). La DLL de la librería es el módulo de extensión. Ventajas: un solo binario, un solo salto, no hace falta compilador C para el usuario, y los métodos se registran con `METH_FASTCALL` (estable desde 3.10). Lo mismo para el addon N-API de Node. Respaldo (opción `python.mode: c`): emitir un `.c` de extensión para plataformas donde lo anterior no funcione.

**C# y Java** sí tienen FFI propia y rápida: la DLL exporta la tabla C y el envoltorio llama por punteros a función.

Todo archivo generado empieza con un encabezado neutro ("Generated by larva polyglot <versión> from Acme.Json 1.2.0. Do not edit.") sin nombrar el lenguaje fuente.

### 10.2 La tabla de funciones

La DLL exporta **un** símbolo por librería además de los shims sueltos de C:

```c
typedef struct {
    uint32_t abi;          /* versión del ABI Polyglot */
    uint32_t count;        /* funciones en la tabla */
    uint64_t api_hash;     /* hash de firmas + nombres de la API expuesta */
    void    *functions[];  /* en el orden del PolyglotModel */
} larva_polyglot_table;

const larva_polyglot_table *larva_polyglot_acme_json(void);
```

El envoltorio carga la tabla una vez, verifica `abi` y `api_hash` (una fachada de 1.2.0 contra una DLL de 1.3.0 falla al cargar con un mensaje claro, no con un crash) y guarda los punteros en campos estáticos (C#: punteros a función no administrados en campos estáticos de solo lectura; Java: `MethodHandle` en `static final`, que el JIT trata como constante). Costo por llamada: una llamada indirecta, igual que `dlsym`.

### 10.3 Comando

```text
larva polyglot <Lib.dll | proyecto.queen> --lang csharp,python [--profile Polyglot.ant] [-o out/] [--pack]
larva polyglot names <Lib.dll> --lang python [--filter Acme.Json.Parser]   # tabla origen → nombre
larva polyglot diff <vieja.dll> <nueva.dll> [--lang all]                    # cambios de API por lenguaje
larva types --view python Acme.Json.dll                                      # gracias a la TargetView
larva show --view csharp Acme.Json.Parser Acme.Json.dll
```

`larva polyglot` sobre un `.dll` ya compilado **sin** shims recompila desde la sección `.lva` agregando los shims (el HIR completo está en la librería). Sobre un proyecto, compila con los shims de una vez.

### 10.4 `.queen`

Chunk nuevo `POLYGLOT`:

```queen
chunk POLYGLOT:
   languages: [csharp, python, java]
   profile: Polyglot.ant
   output: bin/polyglot
   async: thread
   python.package: acme_json
   java.package: com.acme.json
   csharp.namespace: Acme.Json
   csharp.acronyms: keep
   instances: [List<i32>, Dictionary<str, f64>]
   names: polyglot.names      # archivo de nombres congelados (§13)
```

Con el chunk presente, `larva build` de la librería produce la DLL con shims y tabla, y corre los emisores; `larva build --pack` produce los paquetes. El chunk solo tiene sentido en proyectos de librería (error si el proyecto tiene `Main` y no `--dll`).

### 10.5 Hive

- El manifiesto de un paquete Hive agrega `polyglot: { languages, apiHash, namesHash }` cuando el paquete se construyó con fachadas.
- `publish: compiled` lleva la DLL con shims; las fachadas se regeneran del lado del consumidor con `larva polyglot` (son deterministas), o viajan en el paquete si el autor usó un perfil (el perfil es parte de la fuente).
- `larva polyglot diff` puede comparar contra la última versión publicada en Hive (`larva polyglot diff --against hive`), y `larva hive publish` puede exigir que un cambio incompatible de nombres suba la versión mayor (§13).

## 11. ABI subyacente y rendimiento

### 11.1 Forma de un shim

Para `public Parse(str text, i32 depth) : Node?` en `Acme.Json.Parser`:

```text
// HIR generado (pseudocódigo), compilado a la DLL con [Export]
[Export("acme_json_parser_parse_u8")]
static $pg_Parser_Parse_u8(u64 self, u8* text, i64 textLen, i32 depth, u64* err) : u64
{
    try {
        Parser p = handle.get<Parser>(self);              // carga del slot, inlineada
        str t = str.FromUtf8(text, textLen);              // una copia
        Node? r = p.Parse(t, depth);
        return r is Node n ? handle.new(n) : 0;
    } catch (Exception e) { *err = handle.new(e); return 0; }
    finally { runtime.leave(); }                          // BLOCKING_ENTER al volver al destino
}
```

y su gemela `_u16` para C#/Java/JS. Si el método no tiene texto, hay un solo shim.

### 11.2 Lo que hace falta en el runtime

| API | Para qué | Dónde |
| --- | --- | --- |
| `larva_handle_new/get/free/dup` | Objetos que cruzan | `larva_rt.c`, `NativeRuntime`, `larva_rt.js` |
| `larva_thread_attach/detach` | Hilos del destino | ídem (en wasm: no aplica) |
| `larva_runtime_enter/leave` (o reusar `BLOCKING_ENTER`/`blocking_leave`) | Que el collector no espere a hilos que están en código ajeno | ídem |
| ranura de pin de retorno por hilo | Devolver texto/arrays sin `malloc` | ídem |
| `larva_polyglot_init(opciones)` | Leer configuración del runtime al cargar como extensión (hoy lo hace `DllMain`) | ídem |
| `larva_scheduler_run_ready()` | Modo `async: pump` | System.Core + runtime |

### 11.3 Presupuesto de costo por llamada (objetivos a medir en la fase 0)

| Destino | Mecanismo | Objetivo (llamada vacía, args numéricos) |
| --- | --- | --- |
| C | llamada directa | ≈ llamada C normal |
| C# | puntero a función no administrado, blittable, sin transición extra donde el runtime lo permita | pocos ns |
| Java | downcall FFM con `critical` para funciones cortas | pocos ns; decenas sin `critical` |
| Python | `METH_FASTCALL` dentro de la DLL | del orden de una llamada a una función C de extensión (decenas de ns) |
| JS (wasm) | export de wasm directo | pocos ns para números; texto: `TextEncoder`/`TextDecoder` |

Los números reales se miden y se registran antes de comprometer el diseño de la fase 2 (ver Q1). Reglas para no perder velocidad:

1. Sin asignaciones por llamada en el camino sin texto ni objetos nuevos.
2. Sin TLS por llamada (error por puntero de salida, adjunto de hilo cacheado).
3. Sin locks en la frontera.
4. Sin reflexión: todo despacho se resuelve al generar.
5. Valores blittables por valor; arrays de primitivos como vistas fijadas.
6. Iteradores por lotes; colecciones con operaciones masivas.
7. Texto: una copia, en la codificación del destino.
8. Medir cada emisor con el mismo micro-benchmark (`bench/` con casos vacíos, numéricos, texto, objetos, iteradores, callbacks) en CI. Python con `-I`.

## 12. Documentación

Los comentarios de documentación (`CatalogType.Doc`, `CatalogMember.Doc`) se traducen al formato del destino: comentarios de header en C, `///` XML en C#, docstrings en el `.pyi` y en `__doc__` del módulo de extensión, Javadoc, TSDoc. Las referencias a nombres dentro de la doc (`` `Parse` ``) se reescriben con el nombre del destino cuando se resuelven en el catálogo.

## 13. Versionado y estabilidad de nombres

Un nombre en el destino es función de **(nombre fuente, versión de reglas, overrides)**. Cualquiera de los tres puede romper a los usuarios.

1. **Versión de reglas.** Cada `TargetView` declara `rules: N`. El `.queen` (o el archivo de nombres) fija la versión con que se generó. Una versión de larva con reglas nuevas sigue generando con la versión fijada hasta que el autor la suba a mano.
2. **Archivo de nombres (`polyglot.names`)**, opcional pero recomendado para librerías publicadas: una línea por símbolo y lenguaje, `Acme.Json.Parser.GetValue  python  get_value`. Se genera la primera vez y se versiona en el repo. En las siguientes generaciones, un nombre que cambiaría (por un cambio de reglas o de override) **mantiene** el del archivo y da un aviso PG0401; `--update-names` lo acepta explícitamente.
3. **Hash de API** (`api_hash`): firmas + nombres de todo lo expuesto. Va en la tabla de la DLL (verificación al cargar) y en el manifiesto de Hive.
4. **`larva polyglot diff`** clasifica cambios por lenguaje: agregado (compatible), renombrado/eliminado/firma cambiada (incompatible), **nombre de parámetro cambiado** (incompatible en Python, C#, Kotlin; compatible en Java/C/TS). Un cambio de nombre fuente que solo altera la capitalización puede ser compatible en Python y no en C#; el diff lo dice por lenguaje.
5. Política de semver: cambio incompatible en cualquier lenguaje publicado → versión mayor. `larva hive publish` lo verifica si el paquete declara `polyglot`.
6. Renombrar en la librería sin romper: `[PolyglotName("*", "ViejoNombre")]` como alias temporal, más `[Obsolete]` mapeado a `[Obsolete]`/`DeprecationWarning`/`@Deprecated`/`@deprecated`.

## 14. Preguntas abiertas (con recomendación)

| # | Pregunta | Opciones | Recomendación |
| --- | --- | --- | --- |
| Q1 | ¿Shim de Python dentro de la DLL (HIR con la API de CPython) o extensión en C generada? | (a) dentro de la DLL; (b) `.c` + compilador del usuario; (c) `ctypes`/`cffi` | **(a)**, con (b) como respaldo. (c) descartada por velocidad. Validar en fase 0 que `larva bind` de los headers abi3 cubre lo necesario. |
| Q2 | ¿Tabla de funciones o símbolos sueltos? | tabla; símbolos; ambos | **Ambos**: símbolos para C (y para `larva bind --list`), tabla para las fachadas (verificación de versión, una sola búsqueda). |
| Q3 | ¿Dónde se generan los shims: HIR o IR? | HIR (como un frontend más); IR (después de lowering) | **HIR**: pasan por el mismo lowering, chequeos (`RegionChecker`, borrow checker) y backends; y `larva types` los muestra. IR solo si algo no se puede expresar. |
| Q4 | ¿Los emisores son internos o "target packs" cargables como los language packs? | internos; packs con ABI JSON | **Internos** hasta la fase 4. Diseñar la interfaz `TargetView` + emisor pensando en extraerla; definir un método `polyglot.emit` del ABI de packs en la fase 5 si aparece demanda. |
| Q5 | ¿Identidad de envoltorios por defecto? | siempre (tabla débil); nunca; por atributo | **Por atributo** (`[PolyglotIdentity]`). Una tabla débil por objeto cruzado cuesta en cada retorno. |
| Q6 | Ciclos entre GCs (callbacks que capturan envoltorios) | ignorar; referencias débiles en callbacks de eventos; un "ciclo-detector" cruzado | **Documentar + suscripciones cerrables**, y en eventos guardar el callback del destino con referencia débil al envoltorio cuando el destino lo permita (C# `WeakReference`, Python `weakref.WeakMethod`). Sin detector cruzado. |
| Q7 | Cancelación de `async` | sin cancelación; token propio en System.Core | Depende de que System.Core tenga cancelación. **Fase 3 sin cancelación** (el futuro del destino puede cancelarse, la tarea de Larva sigue y su resultado se descarta). |
| Q8 | Java: ¿`u64` en `long` o `BigInteger`? | `long` crudo; `BigInteger`; ambos | **`long` crudo** + doc; `BigInteger` por perfil. |
| Q9 | C#: ¿política de acrónimos por defecto? | `keep`; `short-upper` | **`keep`** (lo que escribió el autor; las fachadas de C# quedan idénticas a la fuente). |
| Q10 | ¿Una DLL por librería o runtime compartido? | autocontenida; `larva_rt.dll` compartido | **Autocontenida** en fases 1–4 (lo que ya hace `--dll`); compartido en fase 5 para varias librerías que se pasan objetos. |
| Q11 | ¿Exponer `List<T>` de Larva o convertir a colecciones nativas? | envoltorio vivo; copia a nativa | **Envoltorio vivo** por defecto (sin copia, semántica de referencia correcta) + métodos de copia masiva. |
| Q12 | ¿Interfaces implementables desde el destino? | no; sí con tabla de trampolines | **Sí, fase 3**, solo interfaces (no subclases de clases). |
| Q13 | Nombre del atributo base | `[PolyglotName]`; `[ForeignName]`; `[As]` | **`[PolyglotName]`** y familia: explícito, buscable, no choca con nada existente. |

## 15. Plan por fases

Cada fase termina con tests (unitarios del modelo y de nombres, más casos de extremo a extremo que construyen una librería, generan la fachada y la ejecutan desde el lenguaje destino; Python siempre con `-I`).

### Fase 0 — Fundamentos y medición

- Runtime: tabla de handles, `thread_attach/detach`, enter/leave, pin de retorno; en `larva_rt.c` y `NativeRuntime` (wasm después).
- Mover `Names`/`NameCase` a la librería común; tests de equivalencia con el port del lado del generador.
- Prototipo a mano de un shim + fachada mínima en C, C#, Python (vía DLL-extensión) y Java para `Add(i32, i32)`, `Concat(str, str)`, un objeto con un método y una excepción. **Medir** y registrar los números de §11.3.
- Decidir Q1 y Q3 con los datos.

### Fase 1 — Modelo y destino C

- `Larva.Polyglot`: `PolyglotModel` desde `HirCatalog`; resolución de nombres (reglas, reservadas, colisiones PG01xx); generación de shims en HIR; tabla; build por el camino `--dll`.
- Emisor C (`.h`) y `CTargetView` registrada (`larva types --view c`).
- Soporta: números, `bool`, `str`, clases como handles, `value` blittables, enums/bitflags, constantes, nullables, excepciones (error por puntero), funciones de módulo, propiedades/indexadores como funciones.
- `larva polyglot` y `larva polyglot names`.
- **Prueba de ida y vuelta:** `larva bind` del `.h` generado → librería que llama a la DLL → mismos resultados que llamar a la librería directamente.

### Fase 2 — C# y Python

- `CSharpTargetView` y `PythonTargetView`: nombres, sobrecargas (despacho en Python), propiedades, operadores/dunders, `ToString`/igualdad, jerarquías de envoltorios, excepciones mapeadas, docs.
- Python: DLL como módulo de extensión abi3 (`METH_FASTCALL`), `.pyi`, `py.typed`.
- C#: `.cs` + `.csproj`, `IDisposable`/`SafeHandle`, `Span<T>` para arrays.
- Chunk `POLYGLOT` en `.queen`; `--pack` (`.nupkg`, wheel).
- System.Polyglot: atributos y `IPolyglotProfile` ejecutado en proceso.

### Fase 3 — Conceptos ricos

- Iteradores (con lotes), colecciones vivas, streams.
- Callbacks y funciones como parámetros; interfaces implementables desde el destino.
- Eventos y `System.Reactive`.
- `async` en modo `thread` (y `pump`).
- Genéricos: instancia compartida para referencias + instancias cerradas (`[PolyglotInstances]`, `instances:`).

### Fase 4 — Java y TypeScript

- Java (FFM, Java 22+): `.java`, `module-info`, `pom.xml`, `.jar` con nativos; getters/setters, enteros sin signo, `AutoCloseable`/`Cleaner`, `CompletableFuture`.
- TypeScript: backend wasm + glue ESM + `.d.ts` (requiere la tabla de handles en `larva_rt.js`); addon N-API construido como la DLL para Node nativo (opcional).

### Fase 5 — Estabilidad, ecosistema y más lenguajes

- `polyglot.names`, versión de reglas, `larva polyglot diff`, integración con `larva hive publish` (semver por cambios de API por lenguaje).
- Runtime compartido (`larva_rt.dll`) para varias librerías en un proceso.
- Rust, Kotlin, Swift, Go, C++; evaluar extraer los emisores a "target packs" (Q4).

## 16. Diagnósticos previstos

| Código | Cuándo |
| --- | --- |
| PG0101 | Dos nombres fuente dan el mismo nombre en un lenguaje. |
| PG0102 | Sobrecargas que chocan por *erasure* en Java. |
| PG0103 | Sobrecargas indistinguibles en un lenguaje sin sobrecarga. |
| PG0201 | Instancia genérica usada y no exportada. |
| PG0202 | Tipo en una firma pública que no puede cruzar (puntero a objeto, referencia `&T`, `func*` con tipos no C). |
| PG0301 | Librería `gc: arena`: solo cruzan objetos fuera de regiones. |
| PG0302 | Librería `gc: manual`: el destino debe liberar explícitamente. |
| PG0401 | Un nombre congelado en `polyglot.names` difiere del que darían las reglas. |
| PG0501 | La DLL no exporta la tabla o un shim esperado (verificado con `Exports.Read`). |

Los mensajes están en inglés, como el resto de los de larva, y no nombran el lenguaje fuente de la librería.

## 17. Bitácora de implementación

### Incremento 1 — nombres y modelo (2026-11-24)

Hecho:

- `Larva.Polyglot/` (nuevo proyecto, compatible con AOT):
  - `Naming.cs`: `Naming.Words`, un port de `System.Bindings.Names.Words` (mismos cortes; los tests fijan los casos que documenta Names.ant), y `Naming.Shape` con `NameStyle` (`Pascal`, `Camel`, `Snake`, `UpperSnake`, `Lower`, `Kebab`) y `AcronymPolicy` (`Keep`, `ShortUpper`, `Words`).
  - `Targets.cs`: `TargetRules` y una clase por lenguaje (C, C#, Python, Java, TypeScript): estilos por `NameKind`, palabras reservadas y escape, sobrecarga nativa o despacho, operadores, miembros heredados del lenguaje, prefijo `I` de interfaces.
  - `PolyglotModel.cs`: el modelo de una librería a partir de `HirCatalog` (tipos públicos, anidados, el `$Module` de cada namespace, miembros con sus nombres por lenguaje y los de sus parámetros), con el hook `Rename` de las opciones, que es donde entrarán los atributos y el perfil, y los chequeos de colisiones y sobrecargas.
  - `NamesTable.cs`: la tabla que imprime `larva polyglot names`.
- `larva polyglot names <Lib.dll> [--lang …] [--java-package …] [--acronyms lang=policy] [--core …] [filtro]` (`Larva.Cli/PolyglotCommand.cs`).
- `tests/Larva.Polyglot.Tests`: 47 tests (palabras, estilos, acrónimos, lenguajes; el modelo de una librería HIR armada a mano).

Probado sobre librerías reales: System.Bindings sale sin errores; System.Core da 2233 filas y 6 errores, todos ambigüedades reales (`Task.Run` con un callable que no se distingue de otro, `Task.GetResult()` frente a la propiedad `Result`, enums que en TS son números).

Decisiones que cambiaron respecto del diseño, tomadas con esos datos:

| Tema | Antes | Ahora | Por qué |
| --- | --- | --- | --- |
| Tipos en C | `acme_json_parser` (`_t` opcional) | `acme_json_parser_t`; las funciones usan el prefijo sin `_t` | En System.Core, `ListIterator` y `List.Iterator()` daban el mismo símbolo C (8 casos). Con `_t`, un tipo y una función no se cruzan nunca. |
| Sobrecargas que solo difieren en ancho numérico (`Write(i8)` … `Write(i64)`) | PG0103, o `[PolyglotCollapse]` | Se juntan solas en una entrada que elige la sobrecarga según el rango del valor (`PolyglotMember.ValueDispatch`) | System.Core tenía 162 errores PG0103, casi todos de este tipo. Pedir un atributo para cada uno no es usable. |
| Prefijo `I` en Python, Java y TS | Se quita siempre | Se quita salvo que el nombre sin `I` ya sea de otro tipo del mismo ámbito (`INumber` y `Number`, `IBindingProfile` y `BindingProfile`) | Con la regla anterior, esos pares chocaban. |
| Operadores en lenguajes sin sintaxis de operadores (C no, Java y TS sí) | Siempre como método con nombre | Si el tipo ya tiene un método con ese nombre (`TimeSpan.Add` y `+`), el operador no se repite | Eran 20 colisiones en Java y TS. El método es la forma idiomática de hacerlo. |
| Arrays de primitivos para el despacho | "secuencia" | Buffer tipado (Python) o `TypedArray` (TS), según el tipo de elemento | `Sort(i32[])` y `Sort(u64[])` sí se distinguen por el tipo del buffer. |
| Enums en el despacho de Python | `int` | Su clase `IntEnum` (se comprueba antes que `int`) | `Seek(i64, SeekOrigin)` y `Seek(i64, i32)` se distinguen. En TS no, y sigue siendo error. |

Diagnósticos nuevos: **PG0104** (miembro sin forma en un lenguaje, como un operador propio de la librería: se omite ahí), **PG0105** (las conversiones todavía no se ofrecen), **PG0106** (aviso: un nombre ocultaría un miembro que todo objeto del lenguaje tiene, como `getClass` en Java o `GetType` en C#; se le agrega `_`).

Pendiente para cerrar la fase 1: los atributos `[PolyglotName]`/`[PolyglotHidden]` (System.Polyglot) conectados a `Rename`, el perfil como código, el `PolyglotModel` con los tipos de cada firma (lo que cruza y cómo), los métodos de extensión, y después los shims en HIR, la tabla de funciones y el emisor de `.h`, con la prueba de ida y vuelta por `larva bind`. Los cambios del runtime de la fase 0 (tabla de handles, adjuntar hilos) son necesarios antes de los shims.

### Incremento 2 — runtime de la fase 0 (2026-11-24)

Hecho:

- **Handles** en los cuatro runtimes. En `larva_rt.c` el slot es una raíz precisa: el minor lo actualiza con `evacuate` y el major lo marca. El handle lleva generación para detectar uso después de liberar, que detiene el programa con exit 101. Los slots viven en bloques de 4096 que nunca se mueven, así que `get` lee sin lock; `new` y `free` toman `rt_lock`, que la colección sostiene. Un handle es dueño de una referencia (retain/release), así que en `refcount` mantiene vivo su objeto. También en `NativeRuntime`, la VM (`Interpreter.Handles.cs`) y `larva_rt.js` (cada slot es una raíz del collector JS).
- **Hilos de otros lenguajes** (`larva_rt.c`):
  - `larva_runtime_enter` adjunta el hilo en su primera llamada. El primer hilo que entra pasa a ser el registro principal y activa el modo multihilo. Cualquier otro recibe un registro propio, que se lista con el mundo detenido porque toma el heap compartido.
  - `larva_runtime_leave` deja el hilo como detenido. En profundidad 0 no hay nada que escanear (`saved_sp = stack_high`) ni registros que guardar. En una llamada hacia afuera (profundidad > 0) se guardan los registros y se escanea desde ahí.
  - `larva_thread_detach` saca el hilo de la lista y libera su registro.
  - `NativeRuntime` y la VM solo cuentan la profundidad.
- **Intrínsecos 97–103**: `handle.new/get/free/count`, `runtime.enter/leave`, `thread.detach` (tabla del IR, LLVM, x64, wasm, VM). Son lo que van a usar los shims.
- **Camino rápido sin locks** en `enter` y `leave`: un incremento atómico de `running_count` y después la lectura de `stop_world` (protocolo de Dekker contra el collector, con un fence donde se fija `stop_world`). Todas las modificaciones de `running_count` pasaron a ser atómicas.
- `Toolchain.CompileHost` compila un host C contra DLLs de Larva (para los tests de Polyglot).
- Tests (`tests/Larva.Tests/PolyglotRuntimeTests.cs`):
  - Handles en VM, en proceso (LLVM y x64 JIT) y como ejecutables reales (LLVM y x64, con y sin stress de GC), más el handle vencido (exit 101).
  - Una DLL de Larva cargada por un host C con 5 hilos que entran, salen y se desadjuntan, con unas 400 colecciones por corrida: 0 lecturas erróneas y 0 handles al final.
  - Comprobación negativa: sin actualizar los slots en el minor, el test de LLVM bajo stress falla (3602000 en lugar de 597002000).

Medición (Windows x64, LLVM, la misma DLL, 10M llamadas):

| Llamada desde C | Antes del camino rápido | Después |
| --- | --- | --- |
| Export sin `enter` (`pg_count`) | 2,6 ns | 2,5 ns |
| `enter` + `handle.get` + campo + `leave` (`pg_read`) | ~105 ns | **~12,6 ns** |
| `pg_make` + `pg_free` (2 llamadas, una asignación, 2 locks de la tabla) | ~250 ns | ~65 ns |

Los ~100 ns de antes venían de `RtlCaptureContext` en cada `leave` y del lock del mundo en cada transición. Los ~10 ns que quedan son las dos operaciones atómicas (lock xadd) y la lectura del TLS. Bajarlos más requiere un protocolo sin contador compartido: cada hilo publica su estado y el collector recorre los registros. Queda como opción si las mediciones de la fase 2 lo piden.

Requisitos y límites que salieron:

- **Un shim no puede asignar memoria antes de `enter`**: un poll de entrada correría en un hilo no adjuntado. Los shims tienen que ser envoltorios mínimos (enter, llamada a la función real, leave). El generador de la fase 1 los va a emitir así.
- **Polls en todo bucle**: una librería que llaman varios hilos necesita `Safepoints.Threaded`, o un bucle que no asigna nunca llega a un safepoint y una colección o un attach lo espera indefinidamente. Hoy eso solo se activa si el programa usa `thread.start`. Hay que agregar una opción de compilación (`polyglot` implica threaded).
- **Las DLLs no leen `entry_info`**: una librería `gc: refcount` construida con `--dll` hoy no desactiva el nursery (`counted_program` queda en 0). Es un problema anterior a Polyglot, pero Polyglot lo hereda.
- **El stress de GC en DLLs** solo existe si el runtime se compila con `LARVA_DIAGNOSTICS`. Por eso el test de hilos pide colecciones con `gc.request`.
- Faltan los handles en el runtime wasm con refcount (no hace retain) y el adjunte de hilos en POSIX probado en Linux (el código está, pero no se ejecutó).

### Incremento 3 — shims y destino C (2026-11-24)

Hecho:

- **`Larva.Polyglot/Shims.cs`** (`ShimGenerator`): a partir del modelo genera un assembly HIR `<Lib>.Polyglot` que referencia la librería. Cada miembro produce dos funciones:
  - el **export**, mínimo: `runtime.enter`, la llamada a la función interna y `runtime.leave`. No asigna memoria, así que no tiene poll de entrada antes de `enter`.
  - la **función interna**, que convierte los argumentos, llama al miembro, convierte el resultado y captura `Exception` hacia `*error` (si el puntero no es nulo), devolviendo 0.
  - Las conversiones son:
    - **Handles:** `handle.get` + downcast verificado. Un handle de otro tipo es un error, no un crash, y 0 donde no se admite nulo es un `ArgumentError` que nombra el parámetro.
    - **Texto que entra:** puntero + longitud UTF-8, con `Marshal.PtrToStringUTF8`.
    - **Texto que sale:** `char *` con `Marshal.StringToHGlobalUTF8`, que se libera con `<prefijo>_string_free`.
    - **Enums:** su entero.
  - Helpers de la librería: `<prefijo>_release` (cualquier handle, incluidos los errores), `<prefijo>_string_free`, `<prefijo>_error_message`, `<prefijo>_thread_detach`.
- **`Larva.Polyglot/CHeader.cs`**: el `.h`, con typedefs de handles, enums con un typedef de entero exacto, `#define` de constantes, funciones documentadas con la firma de origen, `extern "C"` y la macro `<PREFIJO>_API` (dllimport en Windows). El encabezado no nombra el lenguaje fuente.
- **`larva polyglot build [<Lib.dll>|<proyecto.queen>] --lang c [-o carpeta] [--backend llvm|x64] [-O0..3]`**: sin librería, toma el proyecto librería de la carpeta: compila sus fuentes como `larva lib` (en `obj\polyglot\`), deja la salida en `bin\polyglot\` y copia sus librerías nativas (`lib:` en DEPS) a cada carpeta. Luego enlaza en HIR System.Core, las librerías estándar, la librería y los shims, baja solo lo alcanzado desde los exports y construye la DLL (`ExecutableBuilder.BuildLibrary`). Escribe una carpeta por lenguaje dentro de `-o` (por defecto `polyglot\` al lado de la librería): `c\` con `.dll`, `.lib` y `.h`, y `csharp\` con `.cs`, `.csproj` y su propia copia de la `.dll`. Cada carpeta es completa por sí sola, para copiarla o referenciarla tal cual; la librería nativa se construye una vez y se copia a cada una. En `csharp\`, si el ensamblado y la librería nativa se llamarían igual sin distinguir mayúsculas (`Geometria.dll` y `geometria.dll`), la nativa es `<prefijo>_native.dll`: Windows no los distingue en la salida del programa. Un `override` cuya clase base C# ya tiene el mismo miembro no se repite en C#: el de la base llama a la librería, que despacha virtualmente.
- **Lowering**: un export cuyo cuerpo empieza llamando a un `extern("runtime.enter")` ejecuta `$StaticInit` después de esa llamada, no antes. Antes los constructores estáticos corrían en un hilo no adjuntado.
- Test `PolyglotCTests`:
  - System.Bindings compilada se construye para C (182 funciones, 12 tipos handle, 4 enums).
  - Un programa C con 4 hilos crea, lee y cambia objetos, pasa texto en los dos sentidos, usa un enum, llama métodos estáticos y de instancia, propiedades y campos, recibe errores con su mensaje y libera los handles. Todo correcto.
  - El test se omite si no hay MSVC o System.Core/System.Bindings compilados.

Mensaje real de un error: `` `settings` of System.Bindings.BindingModel.Read(BindingSettings, str) is null ``.

Lo que el destino C todavía no ofrece (aviso **PG0202** por cada uno, y el miembro se omite):

- **Colecciones y arrays** (`List<str>`, `T[]`): son la mayoría de lo omitido en System.Bindings (25 miembros). Siguiente paso natural, con los iteradores de la fase 3.
- **Tipos genéricos, métodos genéricos y tipos `value`**. Los `value` blittables, por valor, son lo siguiente para C.
- **Nullables de valores, `str?`, funciones como parámetro, indexadores, eventos y operadores.**

Notas para lo que sigue:

- **El texto de salida hace un `malloc` por llamada.** El diseño (§7.1) prefiere un pin de retorno y una sola copia hacia el string nativo. Para C la copia es idiomática; para C#/Python conviene el pin.
- **`$StaticInit` no es seguro entre hilos**: dos hilos que entran por primera vez a la vez pueden correrlo los dos (su bandera no es atómica). Falta una bandera con CAS.
- **La prueba de ida y vuelta por `larva bind` del `.h` generado** (fase 1 del plan) todavía no está.
- **La DLL se construye sin `QuoteTranslation`**: una librería con funciones citadas que se traducen al compilar necesitaría ese paso.

### Incremento 4 — values, arrays, listas y opcionales en C (2026-11-24)

Hecho (`Shims.cs` reestructurado: un resultado ahora puede agregar parámetros de salida y sentencias, no solo una expresión):

| Tipo en la librería | Como parámetro en C | Como resultado en C |
| --- | --- | --- |
| `value` blittable: campos de números, bools, enums u otros values así, sin `[ValueLayout]` | `const S *` (copiado; NULL es un error) | `void f(..., S *result, error)` (se escribe si `result` no es NULL) |
| `T[]` de números, enums o values blittables | `const T *items, size_t items_count` (copiado con `Memory.Copy`) | `T *f(..., size_t *result_count, error)`: bloque nativo que se libera con `<prefijo>_free` |
| `List<T>`, con T número, enum, `str`, handle o value | handle `<prefijo>_list_<t>_t` | el mismo handle (la lista misma, no una copia) |
| `T?` de número, enum o value | `const T *` (NULL es nulo) | `bool f(..., T *result, error)`: si hay valor, y el valor |
| `str?` | puntero NULL es nulo | NULL es nulo |
| `T*` y `void*` a números | tal cual | tal cual |

- Cada `List<T>` usada en una firma tiene su tipo handle y sus funciones: `_new`, `_count`, `_get`, `_set`, `_add`, `_remove_at` y `_clear`.
- El header declara los values como `typedef struct x { ... } x_t;`, en orden de dependencia.
- `larva polyglot build --exclude <Tipo[.Miembro]>` deja declaraciones afuera, por ejemplo para resolver una colisión sin perfil.
- `Toolchain.LinkDll` pasa los `/EXPORT` en un archivo de respuesta: con miles de exports, la línea de comandos superaba el límite de Windows.
- **System.Core completa** se construye para C: 913 funciones, 75 tipos handle, 9 enums, `TimeSpan` y `DateTime` como structs. Hay que excluir un solo miembro, `Task.GetResult`, que choca con la propiedad `Result`. Lo que se sigue omitiendo, en orden de cantidad: métodos genéricos (79), tipos genéricos (62), operadores (27), values no blittables (19), punteros a punteros y funciones como parámetro.
- Test nuevo, `PolyglotCTests.Values_arrays_lists_and_optionals_cross_to_C`. Un programa C con 4 hilos usa:
  - `TimeSpan` y `DateTime` (structs que entran y salen);
  - `DateTime.TryParse` (opcional como resultado);
  - `Unicode.ToUtf16` (un array que sale) y `Array.Equals` (arrays que entran);
  - una `List<str>` creada en C, modificada, escrita con `File.WriteAllLines`, leída de vuelta con `File.ReadAllLines` y vaciada;
  - y la lectura más allá del final de una lista, que devuelve un error y no rompe el programa.

**Bug encontrado y arreglado: `$StaticInit` no era seguro entre hilos.** Con System.Core, que tiene constructores estáticos, el programa de 4 hilos terminaba con una violación de acceso en 2 de cada 3 corridas: los hilos arrancaban a la vez y corrían los constructores en paralelo. Con una primera llamada desde el hilo principal no fallaba nunca, y eso confirmó la causa. Arreglo, para todos los programas y no solo para Polyglot:
- Dos intrínsecos nuevos, **104 `init.lock`** y **105 `init.unlock`**: un lock del runtime, reentrante para el hilo que lo tiene. Un hilo que espera, espera bloqueado, así que no traba una colección que los constructores disparen. Es un SRWLOCK en Windows y un mutex en POSIX; en `NativeRuntime` y en wasm no hace nada, porque corre un solo hilo a la vez; en la VM es un `Monitor`.
- **El estado vive en el programa**, no en el runtime: un runtime puede correr varios programas. Un primer intento con el estado en el runtime rompió `StaticInitTests`, porque el runtime en proceso corre x64 y después LLVM.
- `$StaticInit` queda así: si `done`, retorna; si no, toma el lock, y si no empezó, marca `started`, corre los constructores, marca `done` y suelta el lock. `started` le permite retornar al instante a un constructor que vuelve a llamar.
- Pendiente: en ARM64 la lectura de `done` del camino rápido tiene que ser *acquire* (en x86 lo es sin más). El IR todavía no tiene la dirección de un global para usar `atomic.load`.

**Segundo bug, mismo test: el primer `attach` no era atómico.** Con 4 hilos haciendo su primera llamada a la vez, dos podían ver `threading == 0` y correr los dos `threads_init`: reinicializaban los locks del mundo en uso y se adueñaban los dos del registro principal. Se veía como cuelgues (un proceso con todos los hilos en espera y 0 de CPU) y como panics esporádicos. Ahora un CAS sobre `attach_first` elige un solo hilo que inicializa, y los demás esperan a que `threading` quede en 1 antes de adjuntarse como hilos propios.

Resultado con los dos arreglos: **200 de 200 corridas** del programa de 4 hilos con System.Core, sin cuelgues (timeout de 30 s por corrida).

Límites que quedan:
- **Un array que entra se copia.** Si la librería lo modifica (por ejemplo `Array.Sort(i32[])`), C no ve el cambio. Para eso hacen falta vistas fijadas (`Span`, §7.9).
- **Si un constructor estático lanza**, el lock queda tomado y los demás hilos esperan para siempre. Hay que liberarlo y propagar el error.
- **`Downcast` a `List<T>` no distingue argumentos de tipo** si los genéricos se comparten: un handle de `List<i32>` pasado donde se espera `List<str>` no se detecta.

### Incremento 5 — destino C# (2026-11-24)

Hecho:

- **`Larva.Polyglot/CSharpEmitter.cs`**: la fachada C# sobre las funciones C. Escribe un `.cs` y un `.csproj` (librería; la DLL nativa se copia al output y se empaqueta en `runtimes/win-x64/native`).
  - **Llamadas**: un `DllImport` por export, con firmas **100% blittables** (punteros, `byte` para `bool`, `nuint` para longitudes) y sin marshalling.
  - **Clases**: una clase sobre un handle que hereda de `LarvaObject` (`IDisposable` y un finalizador que devuelve el handle). La jerarquía de la librería se reproduce. Los constructores obtienen el handle con una función estática.
  - **Interfaces**: una interfaz C#, más una clase `XProxy` que la implementa para lo que la librería devuelve.
  - **Values blittables**: `[StructLayout(Sequential)] struct` con todos los campos en orden (los privados, ocultos). Sus métodos, propiedades y constructores llaman pasando `&copia`.
  - **Enums**: el mismo enum, con `[Flags]` para los bitflags.
  - **Constantes**: `const`.
  - **`List<T>`**: una clase `XList : IList<T>` sobre la lista de la librería, sin copia. Implementa `Insert`, `IndexOf`, `Remove` y la enumeración. Un parámetro `IList<T>` que no es nativo se copia a una lista nueva.
  - **Texto**: UTF-8 en un `stackalloc` de 512 bytes (en el heap si es más largo).
  - **Arrays**: entran como `ReadOnlySpan<T>` (fijados, no copiados por C#) y salen como `T[]`.
  - **Nulos**: `T?` es `T?`; `string?` y los handles nulables también.
  - **Errores**: un `ArgumentNullException` del lado de C# cuando falta un argumento obligatorio, y `LarvaException` con el mensaje de la librería para lo que la librería lanza.
  - **Miembros que C# ya tiene**: `ToString()` y `GetHashCode()` son `override`. `Dispose()` de la librería es `override` del de `LarvaObject` (llama al de la librería y después suelta el handle). Un miembro que tapa a otro de la base con la misma firma lleva `new`.
  - Todas las referencias al BCL van con `global::`, y el handle interno se llama `__Handle`. Esto último hizo falta porque `Mutex.Handle` de System.Core tapaba el `Handle` interno.
- **CLI**: `larva polyglot build --lang c,csharp [--csharp-root <ns>] [--csharp-framework <tfm>]`. `--csharp-root` antepone un namespace, necesario para System.Core: su `System.TimeSpan` chocaría con el de .NET.
- **Nombres**: una palabra suelta en mayúsculas (`IO`, `URL`) es un acrónimo, no un nombre "gritado". Antes `System.IO` salía como `Io` en C#.
- **Tests** (`PolyglotCSharpTests`). Los dos generan la librería, la compilan con `dotnet build -warnaserror` y ejecutan un programa C# que la referencia:
  - **System.Bindings**: constructor, propiedades, métodos, clase estática con sobrecargas, enum, `IList<string>` nativo y copiado, `ArgumentNullException`, `LarvaException`, 4 hilos con `Parallel.For`, y objetos dejados al finalizador.
  - **System.Core** (con `--csharp-root LarvaCore`): `TimeSpan` y `DateTime` como structs, `DateTime.TryParse` que devuelve `DateTime?`, `Unicode.ToUtf16` que devuelve `ushort[]`, `Array.Equals` con arrays, y `File.WriteAllLines`/`ReadAllLines` con listas, en 4 hilos.

Medición (C#, Release, 5M llamadas):

| Llamada | Costo |
| --- | --- |
| Método de struct (`TimeSpan.TotalSeconds`) | ~43 ns |
| Método sobre handle (`StringList.Count`) | ~41–49 ns |
| Texto que entra + array que sale (`Unicode.ToUtf16("hola")`) | ~600–850 ns |

Desde C la misma llamada cuesta ~12,6 ns, así que la transición nativa de .NET suma ~25–30 ns. `[SuppressGCTransition]` la eliminaría, pero no es seguro: una llamada a Larva puede bloquearse esperando una colección o el lock de `$StaticInit`. Podría aplicarse a funciones marcadas como hojas que no bloquean, con un atributo de la librería. El texto con array es caro por las copias y asignaciones (str de Larva, array de Larva, bloque nativo, array de C#). Es el caso para el pin de retorno (§7.1) y para escribir directo en un buffer del llamador.

Pendiente en C#:
- **Una clase de la librería que implementa una interfaz** no la implementa en C#. Lo que la librería devuelve como interfaz es un proxy; un objeto de C# que implementa la interfaz no puede pasarse a la librería, porque no hay callbacks todavía.
- **Un resultado de tipo clase se envuelve con el tipo estático**, no con el dinámico: falta preguntar el tipo del objeto.
- **Todas las excepciones son `LarvaException`**. Falta mapearlas a las de .NET (§7.5), y para eso hace falta exportar el tipo del error.
- **Tipos anidados, genéricos, eventos, operadores e indexadores** de la librería no se emiten todavía.
- **`thread_detach`** no se llama: C# no da un gancho para el fin de un hilo. Un hilo que termina deja su registro listado y detenido, sin nada que escanear.

### Incremento 6 — C# pulido (2026-11-24)

Hecho:

- **Excepciones de .NET.** Un export nuevo, `<prefijo>_error_type(error)`, da el nombre completo del tipo del error. Lo decide una cadena de `is` en el shim, de la clase más derivada a la más general, sin depender de la reflexión, que en una DLL puede estar recortada. En C#:
  - **Las de System.Core** pasan a la de .NET: `ArgumentError` → `ArgumentException`, `ArgumentOutOfRangeError` → `ArgumentOutOfRangeException`, `IndexError` → `IndexOutOfRangeException`, `KeyNotFoundError` → `KeyNotFoundException`, `InvalidOperationError`, `OverflowError`, `FormatError`, `IO.IOError` → `IOException`, `IO.EndOfStreamError`, `MissingMemberError`, `InvalidCastError` y `NotSupportedError`. Llevan una `LarvaException` en `InnerException`, con `TypeName`.
  - **Las de la librería** pasan a clases generadas: `ParseError` → `ParseException`, con la misma jerarquía. Si heredan de una de System.Core, la clase deriva de la de .NET (una `ArgumentError` propia es una `ArgumentException`).
  - **El resto** es `LarvaException(message, typeName)`.
- **Tipo dinámico.** Otro export nuevo, `<prefijo>_type_of(handle)`, da el número de la clase más derivada que C ve del objeto, otra vez con una cadena de `is`. Un resultado se envuelve con su clase real solo cuando hace falta: si el tipo declarado tiene subclases visibles o es una interfaz (`LarvaNative.Wrap<T>`, que verifica con `IsAssignableFrom`). Si no, con `new T(h)` directo, sin llamada extra.
- **Interfaces.**
  - Una interfaz C# hereda de las que extiende.
  - Una clase de la librería implementa en C# las interfaces que declara. Lo hace con implementaciones explícitas (`IBindingProfile.Name(...)`) que llaman a la función de la interfaz, y la librería despacha al método de la clase. Así no hay choques con los miembros propios de la clase.
  - El proxy de una interfaz implementa la interfaz y sus bases del mismo modo.
  - Los miembros de una interfaz ahora entran al modelo aunque su acceso en el HIR no sea `Public`; antes `IBindingProfile` salía vacía.
- **Constructor implícito.** Una clase que no declara constructores recibe `<tipo>_new`, que usa el constructor que generó el frontend (inicializadores de campos) si existe. En C# es `public X()`. Antes `BindingSettings` y `BindingProfile` no se podían crear desde C#.
- **Tests de C#:**
  - System.Bindings: un `IBindingProfile` que es un `BindingProfile` (`is` y llamada por la interfaz), `new BindingSettings()`, y `IndexOutOfRangeException` con `TypeName` `System.IndexError` al leer más allá del final de una `StringList`.
  - System.Core: `BinaryWriter.BaseStream is MemoryStream`, y `TimeSpan.Parse` de algo inválido como `FormatException`.

Sigue pendiente en C#:
- Implementar desde C# una interfaz de la librería y pasarla (callbacks, fase 3).
- Genéricos, tipos anidados, eventos, operadores (los `operator` de C#) e indexadores (`this[]`).
- Mapear también las excepciones de .NET que lanza un callback hacia la librería (fase 3).

### Incremento 7 — callbacks (2026-11-24)

Hecho:

- **Runtime**: dos intrínsecos nuevos, **106 `runtime.callout`** y **107 `runtime.callin`**. Sirven para llamar desde código Larva a una función de otro lenguaje en cualquier hilo: los que inició Larva, el principal o uno adjuntado. Mientras está afuera, el hilo cuenta como detenido para el GC.
  - Cada hilo lleva un contador `outside`, más un bit por nivel que dice si ese callout detuvo al hilo, así el anidamiento queda exacto.
  - `enter`/`leave` dentro de un callout (C# que vuelve a llamar a la librería) ponen el hilo en marcha y lo detienen otra vez, conservando los frames de Larva que tiene arriba.
  - **El hilo que detuvo el mundo** no cambia de estado en un callout. El runtime ahora lo registra en `world_stopper` (en `threaded_poll` y en el attach), porque `collect_major` corre finalizadores al final de la colección, con el mundo detenido y `collecting` ya en 0. Sin esto había un deadlock: el `callin` del finalizador esperaba que terminara la colección que él mismo estaba haciendo. Lo encontré con una traza temporal (ya retirada).
- **Shims**: un tipo función `func<A...> : R` cruza como callback si sus argumentos son números, enums, texto, objetos o values, y su resultado es nada, un número, un enum o un objeto.
  - **En C** son tres parámetros: `R (*fn)(void *context, args..., error_t *error)`, `void *context` y `void (*release)(void *context)`. El header declara los typedefs (`<prefijo>_fn_<args>_<resultado>_t`, `<prefijo>_release_t`).
  - **Del lado de Larva**, cada firma tiene una clase `$Polyglot.Callback<n>` con `fn`, `context` y `release`. Su `Invoke` convierte los argumentos: el texto se copia para la llamada y se libera después, un objeto pasa como un handle que el otro lenguaje posee y un value como puntero a una copia. Después hace `callout`, llama, hace `callin` y lanza el error si lo hubo. Su `destroy()` llama a `release(context)` cuando el GC la recolecta.
  - **El valor función** que recibe la librería es una lambda que captura el objeto y llama a su `Invoke`. Antril no usa `MethodRefExpr` para funciones de Larva.
  - **Errores**: `<prefijo>_callback_error(error, mensaje, longitud)` convierte el mensaje en la excepción que recibe el que llamó al callback.
- **C#**: un parámetro función es un `Action<...>`/`Func<...>` (nulable si la librería admite null). El contexto es un `GCHandle` al delegado, que se libera en `Release`. Por cada firma hay un trampolín `[UnmanagedCallersOnly]` que convierte los argumentos y llama al delegado; una excepción de C# se reporta con `callback_error`.
- **System.Core** ahora ofrece 930 funciones en C: `Parallel.For`, `Mutex.Run`, `new Thread(fn)`, `Task.Run(fn)`, `ThreadPool.QueueUserWorkItem`, `Reactive.Watch`/`Batch` y `new Effect(fn)`.
- **Tests:**
  - **En C**: `Parallel.For` (callbacks desde hilos de Larva), `Mutex.Run` 100 veces, un callback que falla, uno que vuelve a llamar a la librería, 20 `Thread`, y la liberación de contextos por el GC. Estable en 20 de 20 corridas, con 119 de 121 contextos liberados (los otros 2 los retiene el escaneo conservador).
  - **En C#**: `Parallel.For` con lambda, `Mutex.Run`, `InvalidOperationException("boom")` en el callback que llega como `LarvaException("boom")`, un callback que llama a `TimeSpan`, y `Thread` con `Start`/`Join`.

Pendiente de la fase 3:
- **Interfaces implementadas en el otro lenguaje** (un objeto de C# como `IBindingProfile`): es lo mismo, con una tabla de callbacks por interfaz y una clase proxy en Larva.
- **Callbacks que devuelven texto, arrays o values.**
- **Excepciones de C# con su tipo**: hoy llegan como `System.Exception` con el mensaje. Faltan el tipo y la identidad en la ida y vuelta (§7.5).
- Eventos y `System.Reactive` desde C# (`IObservable`), iteradores por lotes y `async`.
- **Un callback en un finalizador**, que corre con el mundo detenido, no puede volver a llamar a la librería: asignaría memoria durante la colección. El `release` de C# no lo hace, pero conviene documentarlo como regla del ABI.

### Incremento 8 — interfaces implementadas desde otro lenguaje (2026-11-24)

Hecho:

- **Shims**: cada interfaz que C ofrece, y cuyos miembros (los suyos y los de las interfaces que extiende) son todos métodos que pueden llamar hacia afuera, recibe:
  - Una clase `$Polyglot.Foreign<n>` que **implementa la interfaz en Larva**. Tiene un puntero a función por método, más `context` y `release`. Cada método llama hacia afuera igual que un callback, y `destroy()` llama a `release(context)`.
  - `<interfaz>_new_foreign(fn_método..., context, release, error)`, que crea uno de esos objetos y lo devuelve como un handle de la interfaz. El header declara un typedef por método (`<miembro>_fn_t`).
  - `<prefijo>_foreign_context(handle)`, que devuelve el contexto de un objeto foráneo, o NULL si el objeto es de la librería. Sirve para recuperar la identidad en la ida y vuelta.
- **Texto como resultado de una función de afuera** (callbacks y métodos foráneos): la función crea el string con `<prefijo>_string_new(texto, longitud)` y devuelve el handle; Larva toma el string y devuelve el handle. Así ningún lado libera memoria del allocator del otro.
- **Refactor**: `OutCall` (la llamada hacia afuera) y `Destroy` (liberar el contexto) son comunes a callbacks y clases foráneas.
- **C#**:
  - Por cada interfaz hay una clase `LarvaForeign<n>`, con un trampolín `[UnmanagedCallersOnly]` por método y un `Make(IFoo)` que crea el objeto de la librería; el contexto es un `GCHandle` al objeto C#.
  - Un parámetro de tipo interfaz acepta un objeto de la librería (por su handle) o **cualquier objeto C# que implemente la interfaz**. En ese caso se crea el objeto foráneo para la llamada y se devuelve su handle después; la librería conserva el suyo si lo guarda.
  - Un resultado de tipo interfaz que sea un objeto foráneo vuelve como **el mismo objeto C#**, no como un proxy (`LarvaNative.Foreign`).
- **Test**: una clase C# que implementa `IBindingProfile`, con estado, se le pasa a `BindingRunner.Run` de la librería. La librería llama a `Start`, a `Function` por cada función y a `Finish`. El renombre de `Name` (`SDL_Init` → `InitEverything`, texto que vuelve) y el filtro de `Function` (un `bool`) quedan en la descripción que la librería escribe. Un perfil que lanza en `Start` hace que `Run` lance `LarvaException("no start")`.

Pendiente:
- **La identidad en la ida y vuelta** está implementada pero no probada: System.Bindings y System.Core no tienen una función que devuelva una interfaz recibida.
- **Interfaces con propiedades o eventos** todavía no se pueden implementar desde afuera.
- **Pasar el mismo objeto C# dos veces** crea dos objetos foráneos distintos. Hace falta un caché débil si la identidad importa del lado de Larva.
- **Heredar desde C# una clase de la librería** para sobrescribir sus métodos virtuales sigue fuera del alcance (§2).
