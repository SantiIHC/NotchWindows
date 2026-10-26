# Informe — Isla Dinámica para Windows 11

_Última actualización: 2026-10-26 · Fases 1, 2, 3 y optimización de RAM/CPU completadas y funcionando (.exe publicado)_

## Objetivo
Una "isla dinámica" / notch propio en la parte superior de la pantalla de la laptop (Windows 11) que muestre la hora, notificaciones, descargas en tiempo real y multimedia/música con diseño y animaciones premium.

## Stack elegido
- C# + .NET 8 + WPF (mejor acceso a las APIs de Windows y Win32 Interop).
- Proyecto en la raíz del repositorio (`IslaDinamica.csproj`).

## Cómo ejecutarlo
1. Instalar el SDK de .NET 8 (https://dotnet.microsoft.com/download).
2. En la carpeta del proyecto: `dotnet run` (o ejecutar directamente `publish\IslaDinamica.exe`).
3. En reposo la isla muestra la **hora actual** en el centro, un **ecualizador animado** a la derecha si hay música sonando, y un **anillo circular verde de progreso** a la izquierda si hay una descarga activa.
4. Pasar el mouse o hacer clic en cualquiera de los indicadores abre el panel correspondiente (**Música** o **Descargas**).
5. Clic derecho sobre la isla o en el icono de bandeja → Salir / Cambiar pantalla / Iniciar con Windows.

## Estructura
| Archivo | Función |
|---|---|
| `IslaDinamica.csproj` | Config del proyecto (`net8.0-windows10.0.19041.0`, WPF, `PerMonitorV2`, `DATAS` GC) |
| `App.xaml / App.xaml.cs` | Arranque de la app e instancia única con `Mutex` |
| `MainWindow.xaml` | Diseño negro premium (`#050507`), vista compacta (reloj + anillo verde + ecualizador) y vista expandida con pestañas |
| `MainWindow.xaml.cs` | Animaciones elásticas, selector de pestañas, soporte multi-monitor DPI, ocultación en pantalla completa y fuera de `Alt+Tab` |
| `DownloadService.cs` | Vigilancia en tiempo real de `Downloads` (con `FileStream` para NTFS, soporte para Gemini/Claude) y `Steam` (`.acf` + `content_log.txt` + delta de red en vivo) |
| `StartupManager.cs` | Activa/desactiva inicio con Windows (registro `HKCU\...\Run`) |
| `MediaService.cs` | Lee título/artista/portada/estado y controla reproducción (`SMTC`), con caché y redimensionado de portada (`136px`) para ahorrar RAM |

## Publicar el .exe (uso diario)
```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```
Ejecutar `publish\IslaDinamica.exe`, clic derecho en el icono de bandeja → "Iniciar con Windows".

## Estado por fases

### Fase 1 — Base y Multimedia (COMPLETADA)
- [x] Ventana transparente, sin bordes, siempre encima, centrada arriba, sin icono en la barra de tareas.
- [x] Isla compacta (`196x36`) que se expande (`392x142` / `392x126`) al pasar el mouse o hacer clic, con animación elástica (`BackEase` / `CubicEase`).
- [x] Multimedia: portada, título, artista, botones anterior / play-pausa / siguiente.
- [x] Clic derecho → Salir.

### Fase 2 — Pulido de la isla y Diseño Premium (COMPLETADA)
- [x] **Diseño en reposo tipo Dynamic Island:**
  - Centro: Reloj en vivo (`HH:mm`).
  - Derecha: Ecualizador animado de 4 barras de colores cuando hay música reproduciéndose.
  - Izquierda: Anillo circular verde (`#30D158`, o ámbar `#FF9F0A` cuando está en pausa) que se llena progresivamente durante las descargas y muestra `✔` al terminar.
- [x] **Controles de música vectoriales premium:**
  - Botones circulares con iconos `Path` vectoriales nítidos (Anterior / Siguiente en círculos oscuros `#17171D` con borde `#2A2A34`, y botón central Play/Pausa blanco brillante `#F5F5F7` con animación de escala al pasar el mouse).
- [x] **Interacción por clic y posición del mouse:**
  - Al pasar el mouse o hacer clic sobre el indicador izquierdo abre **Descargas**; sobre el derecho abre **Música**.
  - Píldoras selectoras (`♪ Música` / `● Descargas`) en la vista expandida con transición deslizante.
- [x] **Acabado negro premium (`#050507`):** Borde especular oscuro (`#1F1F24` → `#2E2E36`) sin artefactos ni sombras cuadradas en las esquinas redondeadas.
- [x] La isla se abre sola 3 s al cambiar de canción.
- [x] Instancia única (`Mutex`).
- [x] Barra de progreso de la canción (`GetTimelineProperties()`).
- [x] Soporte multi-monitor y posición exacta con escalado DPI (`PerMonitorV2`, submenú **Pantalla** en la bandeja del sistema y auto-reposición ante cambios de resolución/monitores).
- [x] Ocultar la isla automáticamente cuando hay aplicaciones o juegos a pantalla completa en el monitor actual.
- [x] **Integración limpia con `Alt+Tab`:**
  - La isla no aparece como ventana en la lista de `Alt+Tab` ni `Win+Tab` (`WS_EX_TOOLWINDOW`).
  - La isla no desaparece al presionar `Alt+Tab` (se excluyen las ventanas del shell `explorer.exe` y `XamlExplorerHostIslandWindow`).
- [x] Iniciar con Windows e icono en la bandeja del sistema (`NotifyIcon`).

### Fase 3 — Descargas en Tiempo Real (COMPLETADA)
- [x] `FileSystemWatcher` sobre la carpeta `Downloads` (soporta `.crdownload`, `.part`, `.opdownload`, `.download`, `.tmp`).
- [x] **Actualización en tiempo real en navegadores (NTFS):** Lectura mediante `FileStream` compartido (`FileShare.ReadWrite | FileShare.Delete`) para evitar que Windows congele el tamaño reportado por `FileInfo.Length` mientras el archivo está abierto.
- [x] **Soporte para Gemini, Claude y apps de escritorio:** Detecta descargas instantáneas (`blob:`), archivos `.tmp` y guardados directos en `Downloads`, desplegando la isla 6 s con **"Descarga completa"** y botón **"Abrir ubicación"**.
- [x] **Soporte en tiempo real para Steam:**
  - Localiza Steam en el Registro y lee todas las bibliotecas en `libraryfolders.vdf`.
  - Combina la base de `appmanifest_*.acf` con los **bytes recibidos por red cada 500 ms** (`NetworkInterface`) y el estado real en `Steam/logs/content_log.txt` (detectando pausas al instante).
  - Muestra nombre del juego, `MB/GB` descargados vs totales, porcentaje real (`0%`–`100%`), velocidad en vivo (`MB/s`), barra de progreso y círculo verde.
- [x] **Pausar y continuar descargas directamente desde la isla:**
  - Botón circular vectorial de **Pausar (`⏸`) / Continuar (`▶`)** dentro del panel de descargas (cambia el anillo y la barra a color ámbar `#FF9F0A` cuando está en pausa y a verde `#30D158` cuando está descargando).
  - En **Steam**, controla el planificador nativo mediante IPC (`steam.exe +app_download_enable 0/1` y `+app_update_cancel` / `+app_install`).
  - En **navegadores Chromium** (Chrome, Edge, Brave, Opera), pausa y reanuda el subproceso `NetworkService` (`NtSuspendProcess` / `NtResumeProcess` vía `NtQueryInformationProcess`).
- [x] **Detección de descarga eliminada / cancelada en Steam y navegadores:**
  - Si se elimina o desinstala una descarga activa o en pausa (`Uninstalling` / `Uninstalled` en `content_log.txt` o eliminación de `appmanifest_*.acf`), la isla oculta el indicador compacto de descarga, muestra el aviso **"Descarga eliminada · Ya no se está descargando nada"** durante `3.5 s` y restablece el panel a **"Sin descargas activas"**.

### Fase 4 — Optimización de RAM y CPU (COMPLETADA)
- [x] **Portadas de álbumes optimizadas (-95 % RAM):** Decodificación directa a `DecodePixelWidth = 136` y caché por canción (`_lastTrackKey`), evitando re-decodificar imágenes de alta resolución en cada pausa/play.
- [x] **Modo reposo de bajo consumo en `DownloadService`:** Cuando no hay descargas activas, reduce la frecuencia de escaneo, no consulta interfaces de red, guarda en caché las bibliotecas de Steam por 60 s y solo lee `content_log.txt` si cambió su tamaño en bytes.
- [x] **Pausa automática de animaciones no visibles:** El temporizador de progreso de música solo corre con la isla expandida, y el `Storyboard` del ecualizador se detiene cuando la isla se expande o se oculta en pantalla completa.
- [x] **Reutilización de memoria y compactación del Working Set:** Pinceles `SolidColorBrush` congelados (`Freeze()`), `RegexOptions.Compiled`, búsqueda sin asignaciones con `IndexOf`, recolector de basura adaptativo de .NET 8 (`GarbageCollectionAdaptationMode = 1` / DATAS) y `EmptyWorkingSet` periódico en reposo (~12–28 MB de RAM en el Administrador de tareas).

### Pendiente (Fases futuras opcionales)
- [ ] **Notificaciones de Windows:** `UserNotificationListener` (requiere identidad de paquete MSIX) y cola de prioridad.
- [ ] **Extras (Fase 5):** Colores adaptados a la portada del álbum, HUD de batería/volumen/brillo y ventana de configuración.

## Errores y detalles técnicos resueltos
1. `using System.Windows.Animation` → corregido a `System.Windows.Media.Animation`.
2. Nombre temporal de Chrome dependiente del idioma ("Sin confirmar 123" / "Unconfirmed 123") → normalizado a "Descargando archivo".
3. La isla desaparecía al hacer `Alt+Tab` y salía en el selector de ventanas → solucionado con `WS_EX_TOOLWINDOW` y filtro de proceso `explorer.exe`.
4. Sombra rectangular en las esquinas redondeadas → solucionado eliminando `DropShadowEffect` sobre el `Border` con `ClipToBounds="True"`.
5. Tamaño de descarga congelado en Steam y navegadores → solucionado leyendo `FileStream.Length` en vivo para NTFS y sumando el delta de red en tiempo real entre volcados de `appmanifest_*.acf` junto con `content_log.txt`.
