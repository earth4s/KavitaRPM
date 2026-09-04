# Kavita RPM packaging

This directory contains the RPM definition used by
`.github/workflows/package-kavita-rpm.yml`.

The workflow repackages Kavita's official self-contained Linux release archives;
it does not rebuild the .NET and Angular source tree. This keeps each RPM payload
aligned with the corresponding binary archive published by `Kareadita/Kavita`.

## Linux packages

Every Linux archive currently published by Kavita is represented:

| Upstream archive | RPM architecture | RPM package |
| --- | --- | --- |
| `kavita-linux-x64.tar.gz` | `x86_64` | `kavita` |
| `kavita-linux-arm.tar.gz` | `armv7hl` | `kavita` |
| `kavita-linux-arm64.tar.gz` | `aarch64` | `kavita` |
| `kavita-linux-musl-x64.tar.gz` | `x86_64` | `kavita-musl` |

The musl archive uses the separate package name `kavita-musl` because it has the
same RPM architecture as the normal glibc x86_64 build. The two packages
conflict intentionally because both install Kavita into `/opt/Kavita` and
provide the same `kavita.service`.

For Fedora, Rocky Linux, and RHEL, use the normal `kavita` package matching the
system architecture. `kavita-musl` is a direct RPM repackaging of Kavita's
upstream musl build and is not the normal choice for glibc-based distributions.

## Automation behavior

The workflow has no `push` or `pull_request` trigger. It runs only:

- on its six-hour schedule after the workflow exists on the default branch; or
- when started manually with `workflow_dispatch`.

The scheduled run checks the latest stable upstream Kavita release. A manual run
can package a specific stable or prerelease tag by setting `upstream_tag`.
Existing RPM release tags are detected and skipped.

The workflow verifies that all four expected Linux assets exist before starting.
It builds the x86_64 packages on GitHub's x86_64 runner and the ARM packages on
GitHub's ARM64 runner. ARM32 is a packaging-only cross-architecture operation:
Kavita's already-built `linux-arm` payload is wrapped as an `armv7hl` RPM without
rewriting or stripping its binaries.

Release tags use the format `v0.9.1.4-rpm.1`.

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

When multiple RPM files are in the same directory, select the glibc package for
your architecture rather than using a wildcard that also matches
`kavita-musl`.
