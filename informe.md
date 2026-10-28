# Informe — Isla Dinámica para Windows 11

*Última actualización: 2026-10-27 · Fases 1–5 funcionando y confirmadas · Fase 6 (batería, Resumen, panel de Música rediseñado) y arreglo de descargas rápidas publicados en el `.exe`, pendientes de probar en la laptop · Fase 7 (Lanzador de juegos) en planificación*

## Objetivo

Una "isla dinámica" / notch propio en la parte superior de la pantalla de la laptop (Windows 11) que muestre la hora, batería, notificaciones, descargas en tiempo real, multimedia/música y un **lanzador rápido automático para tus últimos juegos**, con diseño y animaciones premium, y que permita interactuar sin minimizar ventanas.

## Stack

* C# + .NET 8 + WPF (acceso a las APIs de Windows y Win32 Interop). WinForms solo para el icono de bandeja.

* Extensión de navegador (Manifest V3) para Chrome / Edge / Brave / Opera.

* Proyecto en la raíz del repositorio (`IslaDinamica.csproj`).

## Cómo ejecutarlo

1. Instalar el SDK de .NET 8 (https://dotnet.microsoft.com/download).

2. `dotnet run` en la carpeta del proyecto, o ejecutar `publish\IslaDinamica.exe`.

3. En reposo la isla muestra la **hora** en el centro, el **porcentaje y el icono de batería** y un **ecualizador blanco animado** a la derecha si suena música, y un **anillo de progreso** a la izquierda si hay una descarga activa.

4. Pasar el mouse o hacer clic en un indicador abre su panel (**Resumen**, **Música**, **Descargas** o **Juegos**). Sin música ni descargas activas se abre el Resumen.

5. Clic derecho sobre la isla o en el icono de bandeja → Salir / Cambiar pantalla / Iniciar con Windows.

## Publicar el .exe (uso diario)

```
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
| ----- | ----- | 
| `IslaDinamica.csproj` | Config (`net8.0-windows10.0.19041.0`, WPF + WinForms, `PerMonitorV2`) | 
| `App.xaml / App.xaml.cs` | Arranque e instancia única (`Mutex`) | 
| `MainWindow.xaml` | Diseño negro premium (`#050507`): vista compacta (reloj + anillo + batería + ecualizador) y expandida con pestañas Resumen / Música / Descargas / Juegos | 
| `MainWindow.xaml.cs` | Animaciones, pestañas, batería (`PowerStatus`), multi-monitor DPI, ocultación en pantalla completa, fuera de `Alt+Tab`, cancelar con doble clic | 
| `DownloadService.cs` | Descargas: vigilancia de `Downloads`, Steam (`.acf` + `content_log.txt`), integración con la extensión | 
| `BrowserBridge.cs` | Servidor local (`127.0.0.1:47831`) con el que habla la extensión para estado de descargas. | 
| `StartupManager.cs` | Inicio con Windows (`HKCU\...\Run`) | 
| `MediaService.cs` | Música vía SMTC, con caché, portada a 136 px y posición/duración de la canción | 

## Estado por fases

### Fase 1 — Base y multimedia (COMPLETADA)

* \[x\] Ventana transparente, sin bordes, siempre encima, centrada arriba, sin icono en la barra de tareas.

* \[x\] Isla compacta que se expande al pasar el mouse o hacer clic, con animación elástica.

* \[x\] Música: portada, título, artista, anterior / play-pausa / siguiente.

### Fase 2 — Pulido y diseño premium (COMPLETADA)

* \[x\] Reposo tipo Dynamic Island: reloj, ecualizador animado y anillo de descarga (verde; ámbar en pausa).

* \[x\] Controles vectoriales, pestañas, acabado negro sin sombras cuadradas.

* \[x\] La isla se abre sola 3 s al cambiar de canción; barra de progreso de la canción.

* \[x\] Multi-monitor con DPI (`PerMonitorV2`), submenú "Pantalla" en la bandeja.

* \[x\] Se oculta con apps/juegos a pantalla completa; no aparece en `Alt+Tab`.

* \[x\] Icono de bandeja, inicio con Windows, instancia única.

### Fase 3 — Descargas en tiempo real (COMPLETADA)

* \[x\] `FileSystemWatcher` sobre `Downloads`.

* \[x\] Avisos "Descarga completa" 6 s con botón "Abrir ubicación".

* \[x\] **Steam:** localiza Steam en el registro, lee bibliotecas, nombre del juego, porcentaje, velocidad.

* \[x\] Pausa/continuar y **cancelar** en Steam.

### Fase 4 — Optimización de RAM y CPU (COMPLETADA)

* \[x\] Portadas decodificadas a 136 px y cacheadas por canción.

* \[x\] Temporizador de progreso de música solo con la isla expandida.

* \[x\] **Reposo real en `DownloadService`**: sin descargas activas no se lee la red ni se relee ningún `.acf`.

### Fase 5 — Extensión del navegador: pausa y cancelar reales (COMPLETADA)

* \[x\] Conexión vía servidor local (`127.0.0.1:47831`) para evitar suspender procesos de red.

* \[x\] Pausar, reanudar y cancelar descargas nativamente en el navegador.

### Fase 6 — Batería, Resumen y Música rediseñada (PUBLICADA)

* \[x\] **Reposo:** porcentaje + icono de batería a la derecha.

* \[x\] **Panel "Resumen"**: anillo con el %, estado y tiempo restante.

* \[x\] **Panel de Música rediseñado**: carátula de 48 px, barras animadas, controles centrados.

* \[x\] **Arreglo de descargas rápidas:** ignorar IDs ya terminados.

### Fase 7 — Lanzador rápido de juegos (NUEVA - EN PLANIFICACIÓN)

**Objetivo:** Una nueva sección dentro del notch para acceder rápidamente a los últimos juegos sin abrir launchers completos, mediante detección 100% automática.

* \[ \] **Detección automática de recientes:** Escanear en segundo plano para identificar los últimos juegos ejecutados. 
    * *Steam:* Leer el archivo `localconfig.vdf` (atributo `LastPlayed`) para obtener el historial exacto de la cuenta.
    * *General:* Revisar los accesos directos recientes (`.lnk`) de Windows o monitorizar procesos de juegos conocidos.
* \[ \] **Nueva pestaña "Juegos":** Integrada en la interfaz expandida junto a Resumen, Música y Descargas.
* \[ \] **Último juego destacado:** Mostrar la carátula en alta calidad (obtenida de la caché de Steam o internet), título y un botón grande de "Ejecutar".
* \[ \] **Lista de recientes (Top 3-4):** Una lista compacta debajo del destacado con los juegos jugados anteriormente.
* \[ \] **Integración de estado (Opcional):** Si el juego detectado se está actualizando actualmente en Steam, mostrar el progreso de descarga directamente sobre su carátula en lugar del botón de jugar.

## Pendiente

* \[ \] **Desarrollo de Fase 7:** Investigar la estructura exacta de `localconfig.vdf` de Steam para la extracción de `LastPlayed` y AppIDs.
* \[ \] **`.tmp` en Descargas:** archivos `.tmp` de otras apps pueden dar falsos "Descargando".
* \[ \] Publicar la extensión en la Chrome Web Store.
* \[ \] Soporte de Firefox (API `browser.downloads`, extensión aparte).
* \[ \] **Notificaciones de Windows:** `UserNotificationListener`.

### Ideas priorizadas (siguiente paso sugerido)

1. Probar lo publicado (batería, Música, descargas rápidas).
2. Comenzar la **Fase 7 (Lanzador de juegos)**: Hacer una pequeña prueba de concepto en consola para leer el `localconfig.vdf` de Steam e imprimir el último juego jugado.