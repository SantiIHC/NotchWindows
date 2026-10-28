# Informe — Isla Dinámica para Windows 11

*Última actualización: 2026-10-28 · Fases 1–7 funcionando y confirmadas · Fases 8, 9 y 10 en planificación*

## Objetivo
Una "isla dinámica" / notch propio en la parte superior de la pantalla de la laptop (Windows 11) que muestre la hora, batería, notificaciones, descargas en tiempo real, multimedia/música y un **lanzador rápido automático para tus últimos juegos**, con diseño y animaciones premium, y que permita interactuar sin minimizar ventanas.

## Stack
* C# + .NET 8 + WPF (acceso a las APIs de Windows y Win32 Interop). WinForms solo para el icono de bandeja.
* Extensión de navegador (Manifest V3) para Chrome / Edge / Brave / Opera.
* Proyecto en la raíz del repositorio (`IslaDinamica.csproj`).

## Cómo ejecutarlo
1. Instalar el SDK de .NET 8 (https://dotnet.microsoft.com/download).
2. `dotnet run` en la carpeta del proyecto, o ejecutar `publish\IslaDinamica.exe`.
3. En reposo la isla muestra la **hora**, **batería**, **ecualizador** y **anillo de progreso** de descargas.
4. Pasar el mouse o hacer clic en un indicador abre su panel (**Resumen**, **Música**, **Descargas** o **Juegos**).
5. Clic derecho sobre la isla o bandeja → Salir / Cambiar pantalla / Iniciar con Windows.

## Publicar el .exe (uso diario)
```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

## Estructura
| Archivo | Función |
|---|---|
| `IslaDinamica.csproj` | Configuración WPF + WinForms, `PerMonitorV2` |
| `MainWindow.xaml` / `.cs` | Diseño negro premium, animaciones, multi-monitor, ocultación en pantalla completa, pestañas |
| `DownloadService.cs` | Descargas: vigilancia de `Downloads`, Steam, Epic, Riot, integración con extensión |
| `BrowserBridge.cs` | Servidor local para extensión de Chrome/Edge |
| `MediaService.cs` | Música vía SMTC, portadas, controles |
| `GameLauncherService.cs`| Detección automática de juegos (Steam, Epic, Riot) y carátulas |

## Estado por fases

### Fases 1 a 6 (COMPLETADAS Y PUBLICADAS)
* **Base, Multimedia y Diseño:** Ventana transparente, sin bordes, siempre encima. Reloj, ecualizador, barra de progreso. Multi-monitor.
* **Descargas:** Detección de descargas en Chrome/Edge (con pausa y cancelación real vía extensión), Steam (con porcentaje y velocidad). Arreglo de descargas rápidas.
* **Batería y Resumen:** Porcentaje e icono en reposo. Panel con tiempo restante y anillo de estado.
* **Optimización:** Consumo de CPU/RAM optimizado (reposo real, caché de portadas).

### Fase 7 — Lanzador rápido de juegos (COMPLETADA Y PUBLICADA)
* Detección automática de últimos juegos jugados en **Steam, Epic Games y Riot Games**.
* Pestaña "Juegos" con carátula destacada, plataforma (colores) y botón "Jugar".
* Top 3 recientes e integración de descargas/actualizaciones en vivo de los launchers.

### Fase 8 — Temporizador inmersivo (EN PLANIFICACIÓN)
* **Reposo (Compacto):** Diseño ultra-minimalista. Se oculta la fecha/día para mayor limpieza. El reloj principal se muestra más grande y centrado, la batería (icono + porcentaje) ampliada a la derecha, y el temporizador activo (icono + tiempo + anillo naranja) a la izquierda.
* **Panel Abierto (⏱ Temp.):** Conserva la fecha (`12:14 · mié 28 oct.`) en la esquina superior izquierda. En el centro, números grandes (`15:00`) ajustables con la rueda del ratón y fila de presets rápidos (`3m`, `5m`, `10m`, `15m`, `30m`). A la derecha, anillo de progreso dinámico naranja con botón de `[ Iniciar ]`.
* **Alarma:** Expansión automática de la isla con un resplandor (glow) naranja intermitente en el borde interior al terminar el tiempo.

### Fase 9 — Centro de Control (Wi-Fi, Brillo, Bluetooth) (EN PLANIFICACIÓN)
* **Red:** Lectura de SSID (Wi-Fi) y señal.
* **Brillo:** Slider integrado para ajustar el brillo de la pantalla mediante APIs WMI.
* **Bluetooth:** Estado de conexión de dispositivos (ej. auriculares y porcentaje de batería).

### Fase 10 — Dropzone / Gestor de Archivos Inteligente (EN PLANIFICACIÓN)
* **Drag & Drop Inteligente:** Permite arrastrar cualquier archivo desde el Explorador hacia el notch.
* **Expansión al Vuelo:** Al detectar el cursor con un archivo (`DragEnter`), la isla se expande automáticamente (con rebote elástico) revelando el panel "Dropzone".
* **Acciones Rápidas (Tiles):** Diferentes áreas o "cuadrados" donde soltar el archivo para realizar acciones inmediatas:
  * 🗑️ Enviar a la Papelera de reciclaje (con animación de borde rojo y tapa abriéndose al hacer hover).
  * 📁 Mover a rutas favoritas preconfiguradas (Documentos, Escritorio, etc.).
  * ☁️ Guardar en la nube o atajos personalizados (`+`).
* **Efecto de Absorción:** Destello visual y un check verde rápido (✓) al soltar el archivo, seguido del cierre automático del notch.

## Pendiente
* [ ] **`.tmp` en Descargas:** archivos `.tmp` de otras apps pueden dar falsos "Descargando".
* [ ] Publicar la extensión en la Chrome Web Store y crear soporte para Firefox.
* [ ] **Notificaciones de Windows:** `UserNotificationListener`.

### Ideas priorizadas (siguiente paso sugerido)
1. Iniciar la **Fase 8 (Temporizador inmersivo)** estructurando la UI (reloj centrado, batería rediseñada y la nueva pestaña) en `MainWindow.xaml`.
2. Programar la lógica del contador en un nuevo `TimerService.cs`.
3. Probar los eventos *Drag and Drop* básicos para la **Fase 10 (Dropzone)**.