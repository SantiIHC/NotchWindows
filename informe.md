# Informe — Isla Dinámica para Windows 11

_Última actualización: 2026-10-27 · Fases 1–5 funcionando y confirmadas · Fase 6 (batería, Resumen, panel de Música rediseñado) y arreglo de descargas rápidas publicados en el `.exe`, pendientes de probar en la laptop · Calendario aplazado_

## Objetivo
Una "isla dinámica" / notch propio en la parte superior de la pantalla de la laptop (Windows 11) que muestre la hora, batería, notificaciones, descargas en tiempo real y multimedia/música, con diseño y animaciones premium, y que permita **pausar, reanudar y cancelar descargas** desde la propia isla.

## Stack
- C# + .NET 8 + WPF (acceso a las APIs de Windows y Win32 Interop). WinForms solo para el icono de bandeja.
- Extensión de navegador (Manifest V3) para Chrome / Edge / Brave / Opera.
- Proyecto en la raíz del repositorio (`IslaDinamica.csproj`).

## Cómo ejecutarlo
1. Instalar el SDK de .NET 8 (https://dotnet.microsoft.com/download).
2. `dotnet run` en la carpeta del proyecto, o ejecutar `publish\IslaDinamica.exe`.
3. En reposo la isla muestra la **hora** en el centro, el **porcentaje y el icono de batería** y un **ecualizador blanco animado** a la derecha si suena música, y un **anillo de progreso** a la izquierda si hay una descarga activa.
4. Pasar el mouse o hacer clic en un indicador abre su panel (**Resumen**, **Música** o **Descargas**). Sin música ni descargas activas se abre el Resumen.
5. Clic derecho sobre la isla o en el icono de bandeja → Salir / Cambiar pantalla / Iniciar con Windows.

## Publicar el .exe (uso diario)
```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```
Ejecutar `publish\IslaDinamica.exe` y, desde el icono de bandeja, marcar "Iniciar con Windows" (hacerlo desde el `.exe` publicado, no desde `dotnet run`).

## Instalar la extensión del navegador (para pausar / cancelar descargas de Chrome, Edge, etc.)
1. Guardar la carpeta de la extensión (`ExtensiónChromeIsla`: `manifest.json` + `background.js`) en un lugar fijo y no moverla.
2. Chrome: `chrome://extensions` → activar **Modo de desarrollador** → **Cargar descomprimida** → elegir la carpeta. (Edge: `edge://extensions`.)
3. Tras **cualquier cambio** en `background.js` o `manifest.json`, pulsar el icono de **recargar** de la extensión; Chrome no la recarga solo. La versión actual es **1.1**.
4. Chrome en Windows puede mostrar un aviso al iniciar por ser una extensión en modo desarrollador; es normal.

## Estructura
| Archivo | Función |
|---|---|
| `IslaDinamica.csproj` | Config (`net8.0-windows10.0.19041.0`, WPF + WinForms, `PerMonitorV2`) |
| `App.xaml / App.xaml.cs` | Arranque e instancia única (`Mutex`) |
| `MainWindow.xaml` | Diseño negro premium (`#050507`): vista compacta (reloj + anillo de descarga + batería + ecualizador) y expandida con pestañas Resumen / Música / Descargas; botones de pausa y cancelar de descargas |
| `MainWindow.xaml.cs` | Animaciones, pestañas, batería (`PowerStatus`), multi-monitor DPI, ocultación en pantalla completa, fuera de `Alt+Tab`, cancelar con doble clic, red de seguridad para descargas atascadas |
| `DownloadService.cs` | Descargas: vigilancia de `Downloads`, Steam (`.acf` + `content_log.txt` + red), integración con la extensión, pausa/cancelar |
| `BrowserBridge.cs` | Servidor local (`127.0.0.1:47831`) con el que habla la extensión: recibe descargas reales y devuelve comandos pause / resume / cancel; ignora descargas ya terminadas |
| `ExtensiónChromeIsla/manifest.json`, `background.js` | Extensión MV3: envía el estado de las descargas y ejecuta `chrome.downloads.pause / resume / cancel` |
| `StartupManager.cs` | Inicio con Windows (`HKCU\...\Run`) |
| `MediaService.cs` | Música vía SMTC, con caché, portada a 136 px y posición/duración de la canción |

## Estado por fases

### Fase 1 — Base y multimedia (COMPLETADA)
- [x] Ventana transparente, sin bordes, siempre encima, centrada arriba, sin icono en la barra de tareas.
- [x] Isla compacta que se expande al pasar el mouse o hacer clic, con animación elástica.
- [x] Música: portada, título, artista, anterior / play-pausa / siguiente.

### Fase 2 — Pulido y diseño premium (COMPLETADA)
- [x] Reposo tipo Dynamic Island: reloj, ecualizador animado y anillo de descarga (verde; ámbar en pausa).
- [x] Controles vectoriales, pestañas Música / Descargas, acabado negro sin sombras cuadradas.
- [x] La isla se abre sola 3 s al cambiar de canción; barra de progreso de la canción.
- [x] Multi-monitor con DPI (`PerMonitorV2`), submenú "Pantalla" en la bandeja.
- [x] Se oculta con apps/juegos a pantalla completa; no aparece en `Alt+Tab` (`WS_EX_TOOLWINDOW`).
- [x] Icono de bandeja, inicio con Windows, instancia única.

### Fase 3 — Descargas en tiempo real (COMPLETADA)
- [x] `FileSystemWatcher` sobre `Downloads` (`.crdownload`, `.part`, `.opdownload`, `.download`, `.tmp`) con lectura de tamaño por `FileStream` compartido (NTFS).
- [x] Avisos "Descarga completa" 6 s con botón "Abrir ubicación"; detección de descargas instantáneas (Gemini/Claude).
- [x] **Steam:** localiza Steam en el registro, lee todas las bibliotecas (`libraryfolders.vdf`), nombre del juego, MB/GB, porcentaje, velocidad y barra.
- [x] Pausa/continuar en **Steam** (probado, funciona) y **cancelar** en Steam (probado, funciona; abre el diálogo de desinstalar de Steam, que pide su propia confirmación).

### Fase 4 — Optimización de RAM y CPU (COMPLETADA, con correcciones tras la revisión)
- [x] Portadas decodificadas a 136 px y cacheadas por canción.
- [x] Temporizador de progreso de música solo con la isla expandida; ecualizador detenido cuando no es visible.
- [x] Pinceles congelados, `RegexOptions.Compiled`, búsquedas sin asignaciones.
- [x] **Reposo real en `DownloadService`** (ahora sí): sin descargas activas no se lee la red ni se relee ningún `.acf`; vigilantes de `appmanifest_*.acf` por biblioteca de Steam marcan cuándo hay algo que leer, con un escaneo de respaldo cada 30 s.
- [ ] Notas honestas: `EmptyWorkingSet` (cada ~60 s en reposo) solo baja la cifra del Administrador de tareas, no la memoria que la app necesita; y `GarbageCollectionAdaptationMode=1` (DATAS) solo aplica con Server GC, que está desactivado, así que no tiene efecto.

### Fase 5 — Extensión del navegador: pausa y cancelar reales (NUEVA)
**Por qué:** la pausa original suspendía el proceso de red de Chrome (`NtSuspendProcess`). Eso congela *todo* el internet del navegador: la descarga no se pausa de verdad, el servidor cierra la conexión y al reanudar sale "conexión perdida". Se desactivó (`AllowNetworkSuspendFallback = false`).

**Cómo funciona ahora:**
- La extensión consulta `chrome.downloads` cada 0,5 s mientras haya descargas y las envía a la isla (`POST http://127.0.0.1:47831/state`). La respuesta de la isla trae los comandos pendientes.
- La isla muestra **nombre real, tamaño total, porcentaje y velocidad real** del navegador (también para descargas fuera de la carpeta Descargas).
- Pausar / reanudar: `chrome.downloads.pause / resume` (pausa de verdad, sin cortar el internet).
- Cancelar: `chrome.downloads.cancel`.
- La extensión avisa cuando una descarga **termina o se interrumpe**; la isla lo desduplica con el aviso del vigilante de archivos.
- Si la extensión no está instalada o la isla está cerrada: la isla sigue mostrando las descargas por el vigilante de archivos, pero **sin** pausa ni cancelar para navegador.
- Si se pausa desde el propio Chrome, la isla lo detecta (archivo sin crecer 4 s) y muestra "En pausa"; desde la isla no se puede reanudar ese caso.

**Botón cancelar (✕):** junto al de pausa; el primer clic lo pone rojo 3 s, el segundo confirma.

**Seguridad del puente:** escucha solo en `127.0.0.1`; solo acepta peticiones cuyo `Origin` sea `chrome-extension://` o `moz-extension://` (una página web no puede falsificarlo y no puede leer las respuestas por CORS).

**Diagnóstico:** `%LocalAppData%\IslaDinamica\debug.log` registra la versión de la extensión conectada, los comandos enviados (pause / resume / cancel) y los intentos de cancelar. Si el log dice "extensión antigua (sin versión)", falta recargarla en `chrome://extensions`.

### Fase 6 — Batería, Resumen y Música rediseñada (PUBLICADA en el `.exe`, pendiente de probar en la laptop)
- [x] **Reposo:** a la derecha de la isla, porcentaje + icono de batería (verde cargando, ámbar ≤20 %, rojo ≤10 %, blanco normal). Se oculta solo si el equipo no tiene batería. La isla compacta pasó de 196 a 240 px y la expandida de 398 a 420 px para que quepan reloj, ecualizador y batería.
- [x] **Panel "Resumen"** (nueva pestaña junto a Música y Descargas): anillo con el %, estado (Cargando / En batería / Conectada / Cargada) y tiempo restante cuando Windows lo informa.
- [x] Clic en la batería abre el Resumen; al pasar el mouse sin música ni descargas activas se muestra el Resumen en lugar de "Sin reproducción".
- [x] Coste mínimo: lectura de `SystemInformation.PowerStatus` cada ~10 s y al instante con `SystemEvents.PowerModeChanged`; la interfaz solo se toca si el estado cambió.
- [x] **Panel de Música rediseñado** (según la referencia): carátula de 48 px con punto de estado (verde sonando / gris en pausa), título y artista, barras de audio animadas a la derecha (solo con la isla abierta y sonando), barra de progreso con tiempo transcurrido y duración, y controles (anterior / play-pausa / siguiente) centrados debajo.
- [x] **Ajustes posteriores:** alto de la isla en Música subido a 190 px para que no se corten los botones; barras de audio (reposo y panel) en blanco `#F5F5F7`, igual que la hora y la batería.
- [x] **Arreglo de descargas rápidas:** una descarga pequeña que terminaba en <0,5 s dejaba la isla pegada en "Descargando archivo · 0 KB / X KB (0%)". Causa: la lista `in_progress` de la extensión se tomaba justo antes de terminar y el aviso "complete" llegaba en la misma petición, así que la descarga se mostraba como activa *después* de completarse; luego la lista llegaba vacía y nadie avisaba a la interfaz. Corregido en dos capas: `BrowserBridge` recuerda 30 s los ids terminados y los ignora, y `MainWindow` limpia el estado si pasan >4 s sin avisos de una descarga "activa".
- [ ] Recordatorios del calendario: aplazado (ver Pendiente).
- [ ] Música: volumen y dispositivo de salida (de la referencia), sin implementar.
- [ ] Música: botones aleatorio / repetir (dependen de que la app los soporte vía SMTC), sin implementar.

## Revisión de código: qué se encontró y qué se corrigió
| # | Problema | Estado |
|---|---|---|
| 1 | Pausa del navegador: doble suspensión, estado perdido, navegador congelado si la isla se cerraba a la fuerza | Corregido (PID guardados, recuperación al arrancar). Luego **sustituida** por la extensión |
| 2 | "Reposo" que seguía leyendo la red y todos los `.acf` cada 2 s | Corregido (reposo real + vigilantes de `.acf`) |
| 3 | `Poll()` desde tres hilos con campos sin proteger | Corregido (`Monitor.TryEnter`, sin bloqueo para evitar interbloqueo con la interfaz) |
| 4 | Estimación de bytes de Steam acumulaba error de red | Corregido: se resincroniza con el valor real del `.acf` en cada actualización |
| 5 | Al pausar/reanudar en Steam salía "Descarga eliminada" y luego seguía | Corregido: un tracker no se da por cancelado hasta ~10 s sin aparecer (salvo desinstalación, 100% o `StateFlags == 4`) |
| 6 | Chrome volcaba el búfer al pausar y se interpretaba como "reanudó" | Corregido (ya no se usa crecimiento de archivo para eso) |
| 7 | Velocidad del navegador calculada con la red de todo el equipo | Corregido (crecimiento del archivo / bytes de la extensión) |

## Estado de pruebas
| Función | Estado |
|---|---|
| Música, progreso, multi-monitor, pantalla completa, `Alt+Tab` | Probado, funciona |
| Descargas de navegador por vigilante de archivos | Probado, funciona |
| Steam: porcentaje, velocidad, pausa, cancelar | Probado, funciona |
| Chrome con extensión: pausa / reanudar real | Probado, funciona |
| Chrome con extensión: **cancelar (✕)** | Probado, funciona |
| Steam: arreglo del falso "eliminada" al pausar/reanudar | Probado, funciona |
| Steam: "Descarga completa" rápida con `StateFlags == 4` | Probado, funciona |
| Batería en reposo + panel Resumen (Fase 6) | Compila y publicado; pendiente de probar en la laptop (cargando / en batería / <20 %) |
| Panel de Música rediseñado (botones sin cortar, barras blancas, tiempos) | Compila y publicado; pendiente de probar |
| Descarga pequeña/rápida ya no se queda pegada | Publicado; pendiente de probar (si falla, revisar `debug.log`) |
| Edge / Brave / Opera con la extensión | Sin probar |
| Firefox | La isla muestra la descarga, sin pausa ni cancelar |

## Pendiente
- [ ] **`.tmp` en Descargas:** archivos `.tmp` de otras apps pueden dar falsos "Descargando", y cualquier archivo nuevo (p. ej. al descomprimir un zip en Descargas) dispara un "Descarga completa"; el anti-repetición es por ruta, no global.
- [ ] **Steam:** el porcentaje entre actualizaciones del `.acf` sigue siendo una estimación; la velocidad usa la red de todo el equipo (se infla si hay otro tráfico).
- [ ] Publicar la extensión en la Chrome Web Store (evita el aviso de modo desarrollador; requiere cuenta de desarrollador).
- [ ] Soporte de Firefox (API `browser.downloads`, extensión aparte).
- [ ] **Notificaciones de Windows:** `UserNotificationListener` (requiere identidad de paquete MSIX) y cola de prioridad (llamada > notificación > descarga > música).
- [ ] **Recordatorios del calendario en la isla** (punto de mejora, aplazado): contador de eventos de hoy que aún no han pasado. Falta decidir la fuente (Google Calendar / calendario de Windows–Outlook / `.ics`); el hueco ya está previsto en el panel "Resumen".
- [ ] Extras: colores adaptados a la portada, HUD de volumen/brillo, ventana de configuración, icono de bandeja propio (hoy usa el genérico de Windows).

### Ideas priorizadas (siguiente paso sugerido)
1. Probar lo publicado (batería, Música, descargas rápidas, Edge/Brave/Opera).
2. Calendario con `.ics` (sin permisos) + control de volumen.
3. Ventana de configuración y arreglo de falsos "Descarga completa".

## Errores y detalles técnicos resueltos (historial)
1. `using System.Windows.Animation` → correcto: `System.Windows.Media.Animation`.
2. Faltaba `using System.IO;` en `MainWindow.xaml.cs` (`File.Exists`).
3. Nombre temporal de Chrome dependiente del idioma ("Sin confirmar 123" / "Unconfirmed 123") → "Descargando archivo".
4. La isla desaparecía con `Alt+Tab` y salía en el selector → `WS_EX_TOOLWINDOW` + filtro de `explorer.exe`.
5. Sombra rectangular en las esquinas redondeadas → sin `DropShadowEffect` sobre el `Border` con `ClipToBounds`.
6. Tamaño de descarga congelado → lectura con `FileStream.Length` en vivo (NTFS).
7. Pausa de Chrome "se va el internet" / "conexión perdida" → sustituida por la extensión (pausa nativa del navegador).
8. Botón de pausa de Chrome sin efecto → abrir los procesos con permisos mínimos, ignorar el volcado de búfer y, finalmente, usar la extensión.
9. Cancelar en Chrome sin efecto → extensión vieja sin el comando `cancel`; recargarla (v1.1) y revisar `debug.log`.
10. Isla pegada en "0 KB / X KB (0%)" tras una descarga muy rápida → ids terminados ignorados en `BrowserBridge` + limpieza por inactividad en `MainWindow` (ver Fase 6).
11. Botones de Música cortados al rediseñar el panel → alto de la isla en Música 168 → 190 px.
12. `dotnet publish` falla con "archivo en uso" si la isla está abierta → cerrarla (bandeja → Salir) antes de publicar.

## Notas técnicas
- La ventana es fija y transparente; solo se anima el `Border` interior; los píxeles transparentes dejan pasar los clics.
- `Poll()` usa `Monitor.TryEnter` y no bloquea: `PollCore` invoca `Changed` → `Dispatcher.Invoke`, y un `lock` bloqueante podría provocar un interbloqueo con el hilo de la interfaz.
- Las descargas del navegador con extensión conectada (informe en los últimos 3 s) tienen prioridad sobre las detectadas por archivos temporales.
- Cancelar en Steam usa `steam://uninstall/<AppId>`; si el AppId no son solo dígitos, no se ejecuta.
- `dotnet build` y `dotnet publish` compilan directamente en el workspace; si `publish\IslaDinamica.exe` está en ejecución al publicar, hay que cerrar antes la isla desde el icono de bandeja.
