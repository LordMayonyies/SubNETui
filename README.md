# Sub.NETUI

Sub.NETUI es una TUI para explorar una biblioteca de Navidrome mediante la API compatible con Subsonic y reproducir música con `mpv`.

## Requisitos y configuración

- .NET 10 SDK para compilar.
- Una instancia Navidrome accesible por HTTP/HTTPS y sus credenciales.
- `mpv` instalado y disponible en `PATH` para reproducir audio.

La configuración se lee de `~/.config/subnetui/config.json` y puede sobrescribirse mediante las variables de entorno `NAVIDROME_URL`, `NAVIDROME_USER` y `NAVIDROME_PASSWORD`:

```json
{
  "Url": "https://navidrome.example.com",
  "Username": "usuario",
  "Password": "contraseña"
}
```

Si falta la configuración o no es válida, se abre una ventana de inicio de sesión para introducir o corregir la URL y las credenciales. La aplicación comprueba la conexión con Navidrome antes de guardar los datos en `~/.config/subnetui/config.json`; en sistemas Unix el archivo queda accesible solo para el usuario actual. También se aceptan URL con una ruta base, por ejemplo `https://servidor/musica`.

## Ejecutar y publicar

```sh
dotnet run --project Sub.NETUI
dotnet build Sub.NETUI.slnx
dotnet publish Sub.NETUI/Sub.NETUI.csproj -c Release -r linux-x64
```

Native AOT genera un ejecutable nativo para el runtime indicado en `-r`; usa el identificador de tu sistema y arquitectura. La serialización de configuración y las respuestas Subsonic usan `System.Text.Json` source generation para evitar reflexión dinámica durante el publish. `mpv` es un proceso externo y no se incluye en el ejecutable.

## Uso

- Flechas: navegar artistas, álbumes y canciones.
- `Tab`: cambiar entre los paneles de artistas, álbumes y canciones.
- `Enter` sobre una canción: reproducirla y continuar con las siguientes canciones del álbum.
- `Espacio`: pausar o reanudar; `Ctrl+S`: detener.
- `N` / `P`: siguiente / anterior; `Ctrl+Q`: salir.

El reproductor muestra mensajes comprensibles si `mpv` falta o si no se puede iniciar. La barra de progreso consulta a `mpv` una vez por segundo y se desactiva al pausar o detener.

## Arquitectura

- `Configuration`: configuración de ejecución, precedencia de variables de entorno y JSON source-generated.
- `Subsonic`: modelos, cliente HTTP y errores de API. Cada llamada crea `salt` aleatorio y el token MD5 exigido por Subsonic; el mismo `HttpClient` se reutiliza durante toda la aplicación.
- `Playback`: cola de canciones y contrato `IAudioPlayer`; `MpvAudioPlayer` mantiene un proceso sin vídeo y un canal IPC para comandos y eventos de fin de pista.
- `UI`: `MainWindow` coordina vistas y flujo de selección; las vistas no acceden directamente a HTTP. `LibraryListPane<T>` mantiene los modelos tipados y presenta sus nombres en el `ListView` de Terminal.Gui.
- `Program`: composición manual de dependencias (Pure DI), configuración e inicio de Terminal.Gui.

### Ideas de Terminal.Gui v2 aplicadas

- `Application.Create()` crea una instancia de aplicación. `app.Run(window)` ejecuta el bucle principal; las vistas y sus eventos deben manipularse desde el hilo de UI.
- Las llamadas de red y al reproductor se esperan fuera del hilo de UI. El resultado se aplica con `app.Invoke(...)`, que agenda el cambio en el hilo de la aplicación.
- `Pos` describe la posición relativa a otras vistas y `Dim` el tamaño relativo al espacio disponible. Los paneles se componen como vistas hijas para que el layout pueda cambiar sin mezclarlo con la lógica Subsonic.
- La navegación se observa mediante `ListView.ValueChanged`; las acciones confirmadas se enlazan a `Accepted`. `Accepting` es el evento cancelable previo a la aceptación y conviene reservarlo para validar o impedir una acción, no para ejecutar side effects.
- El foco pertenece a las vistas que reciben entrada; `Tab` lo mueve explícitamente entre las listas. Las `SuperView` contenedoras también deben tener `CanFocus = true` para que Terminal.Gui pueda enfocar correctamente sus vistas descendientes. Los paneles son reutilizables y no contienen lógica de red.
- Las solicitudes de selección se cancelan al cambiar de artista o álbum. El debounce evita llamadas repetidas mientras el usuario recorre rápidamente una lista, y las respuestas antiguas se ignoran.

## Ampliar la aplicación

Para agregar una función, añade primero el tipo y el método de API en `Subsonic`, registra los nuevos tipos raíz en `SubsonicJsonContext` para mantener la compatibilidad AOT, y expón una vista o un servicio pequeño desde su capa. Mantén HTTP fuera de las vistas; deja que `MainWindow` coordine las acciones y use `app.Invoke` para aplicar resultados. Si una nueva operación tiene efectos secundarios, liga su ejecución a `Accepted` o a un atajo claro y cubre la cancelación y los errores de API.