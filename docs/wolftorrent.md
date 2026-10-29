# WolfTorrent in this fork

This fork adds the WolfTorrent indexer and its tests. Its internal ID remains
`wolfmax4k` so existing Jackett configuration and integration URLs remain valid.
Its display name is **WolfTorrent**.

Downloads require a separate Byparr service. Configure **Byparr URL** in the
indexer. In Docker, use a service URL such as `http://byparr:8191`, not localhost.
Configure Byparr's proxy to use the same network exit as Jackett. No advertising
clicks are required by the indexer. Upstream website changes may break access.
Byparr v3.0.4 was tested; its current development branch differs from that release.

## Versioned image

`ghcr.io/anubisreal/jackett-wolftorrent:v0.24.2961` is an example version tag.
The GitHub Actions workflow checks the latest official Jackett release daily,
checks out that exact release, adds only this indexer and its tests, and runs
the tests before building. It retains the matching LinuxServer image, entrypoint,
runtime, ports, user mapping and `/config` layout, replacing only
`Jackett.Common.dll`. No private settings or local test artifacts are included.
The image is built for amd64 and arm64. The upstream Jackett version is retained;
a label identifies the WolfTorrent addition. A source-commit tag and digest allow
pinning and rollback. `latest` points to the most recently built image.

The image's built-in `AUTO_UPDATE` is disabled: self-updating would overwrite
the custom assembly. Update by pulling a new tested image. For an existing stack,
keep all volumes, ports, environment and networks, change the image and explicitly
set `AUTO_UPDATE=false` if the stack previously enabled it. Back up `/config`
before deployment. To roll back, restore the previous image reference.

Byparr is deployed separately, for example `ghcr.io/thephaseless/byparr:v3.0.4`,
reachable only on the Docker network. Do not publish its port unless required.
The official LinuxServer and Jackett licenses and notices remain applicable.

This workflow tracks official release source for each image independently of the
fork's snapshot of master. If an upstream API changes, the build/test fails rather
than publishing an untested replacement. Image publication does not automatically
redeploy any server; the deployed digest remains under the operator's control.
