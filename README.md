# Jackett con WolfTorrent

Este fork añade **WolfTorrent** a Jackett para buscar y descargar torrents.
La integración está en `Wolfmax4K.cs` y necesita una instancia de **Byparr**
para preparar las descargas. No necesitas instalar nada en el navegador.

La imagen se construye sobre las versiones oficiales de Jackett, conservando
su numeración y añadiendo el indexador. Se han probado búsquedas y descargas
completas con Byparr v3.0.4. Los cambios de la web pueden afectar al funcionamiento.

## Instalar en Portainer

1. Abre **Stacks → Add stack** y ponle un nombre, por ejemplo `jackett`.
2. Pega este stack. Cambia `/ruta/jackett/config` y `/ruta/jackett/downloads`
   por las carpetas de tu servidor. Ajusta `PUID` y `PGID` si tu usuario no usa 1000.
3. Pulsa **Deploy the stack**.

```yaml
services:
  jackett:
    image: ghcr.io/anubisreal/jackett-wolftorrent:v0.24.2961
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=Europe/Madrid
      - AUTO_UPDATE=false
      - RUN_OPTS=--NoUpdates
    volumes:
      - /ruta/jackett/config:/config
      - /ruta/jackett/downloads:/downloads
    ports:
      - 9117:9117
    restart: unless-stopped
  byparr:
    image: ghcr.io/thephaseless/byparr@sha256:874f719518f617d03a60e03411fc5d090647e1a877041e81f8dc965927c7deb6
    environment:
      - PORT=8191
      # Si necesitas WARP, añade tu proxy:
      # - PROXY_SERVER=socks5://IP_DEL_PROXY:1080
    shm_size: 1gb
    restart: unless-stopped
```

La imagen de Byparr está fijada a la versión probada mediante su digest.
Su puerto no se publica: Jackett se conecta a él por la red del stack.

## Configurar WolfTorrent

1. Abre `http://IP_DE_TU_SERVIDOR:9117`.
2. Pulsa **Add indexer**, busca **WolfTorrent** y añádelo.
3. En **Byparr URL** escribe `http://byparr:8191` y guarda.
4. Ya puedes probar una búsqueda y descargar su torrent.

**Si ya tienes Jackett:** conserva tus rutas actuales de `/config` y `/downloads`,
y los demás ajustes de tu stack. Haz una copia de `/config` antes de cambiar la
imagen. El identificador interno sigue siendo `wolfmax4k` para conservar la
compatibilidad con configuraciones anteriores.

**Si necesitas WARP o un proxy:** descomenta `PROXY_SERVER` en Byparr y pon tu
dirección real. Configura el proxy de Jackett desde su panel para utilizar la
misma salida de red. Si ya usas FlareSolverr para otros indexadores, puedes
mantenerlo; el campo **Byparr URL** pertenece únicamente a WolfTorrent.

## Actualizaciones

GitHub Actions comprueba cada día la última versión oficial de Jackett y
construye la imagen con nuestro indexador. El ejemplo fija `v0.24.2961`, la
versión probada. Para actualizar, cambia esa etiqueta por una nueva publicada
y vuelve a desplegar el stack. También existe la etiqueta `latest`.

Mantén `AUTO_UPDATE=false` y `RUN_OPTS=--NoUpdates`: la actualización interna
de Jackett podría sustituir nuestra compilación y eliminar WolfTorrent.
Actualizar la imagen conserva tus datos mientras mantengas el volumen `/config`.

[Detalles técnicos](docs/wolftorrent.md) · [Compilaciones de la imagen](https://github.com/AnubisReal/Jackett/actions/workflows/wolftorrent-image.yml)
