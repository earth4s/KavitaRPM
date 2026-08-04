# Kavita RPM packaging

This directory contains the RPM definition used by
`.github/workflows/package-kavita-rpm.yml`.

The workflow repackages Kavita's official self-contained Linux release archives;
it does not rebuild the .NET and Angular source tree. This keeps the RPM payload
identical to the binaries published by `Kareadita/Kavita`.

## Automation behavior

The workflow has no `push` or `pull_request` trigger. It runs only:

- on its six-hour schedule after the workflow exists on the default branch; or
- when started manually with `workflow_dispatch`.

The scheduled run checks the latest stable upstream Kavita release. A manual run
can package a specific stable or prerelease tag by setting `upstream_tag`.
Existing RPM release tags are detected and skipped.

For each upstream release, the workflow creates native packages on GitHub-hosted
x86_64 and ARM64 runners and publishes them in this repository's Releases page.
Release tags use the format `v0.9.0.2-rpm.1`.

To rebuild the same Kavita version after changing the package, increment
`RPM_RELEASE` in the workflow.

## Installed paths

- Application: `/opt/Kavita`
- Persistent configuration and database: `/opt/Kavita/config`
- systemd service: `/usr/lib/systemd/system/kavita.service`
- Default HTTP port: `5000/tcp`

Kavita runs as the `kavita` system user. Grant that user read and traverse access
to each media-library directory. One common approach is to add the service user
to the group that owns the library:

```bash
sudo usermod -aG media kavita
sudo systemctl restart kavita.service
```

## Install an RPM

Fedora:

```bash
sudo dnf install ./kavita-*.rpm
sudo systemctl enable --now kavita.service
```

Rocky Linux or RHEL may require EPEL for `libgdiplus`:

```bash
sudo dnf install epel-release
sudo dnf install ./kavita-*.rpm
sudo systemctl enable --now kavita.service
```
