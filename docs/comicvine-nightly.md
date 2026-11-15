# Comic Vine nightly builds

The `Comic Vine Private Nightly` workflow builds the current feature branch from
source, including its production Angular frontend and direct Comic Vine settings.
It creates an unpublished draft release in `earth4s/KavitaRPM`, visible only to
people with write access to the repository. It never publishes the draft.

All build transfers use authenticated draft-release assets. No web build or package
is uploaded as a GitHub Actions artifact, since those downloads would be accessible
to readers of this public repository. Source code and build logs remain public.
The workflow checks that anonymous requests cannot read the completed release
or its assets. Publishing the draft manually would make the downloads public.

Each release contains self-contained Linux x64, ARM64 and ARM32 archives, three
matching RPMs, `SHA256SUMS`, and `SOURCE.json` identifying the commit and workflow
run. The archives contain a `Kavita/` directory. RPMs install the same payload in
`/opt/Kavita` and add `kavita.service`; no container is needed to run Kavita.

The pipeline checks the Comic Vine, scanner, export and administrator-access tests
before packaging. Native x64 and ARM64 binaries must start successfully, serve
the UI and reject unauthenticated access to the Comic Vine settings endpoint.
ARM32 is cross-published and packaged without executing it on the ARM64 runner.
The bundled kepubify download is verified against its pinned SHA-256 hash.

The workflow runs when its packaging files change on the feature branch, or when
manually dispatched. It has no recurring schedule and does not update upstream,
publish Docker images, send notifications, or merge the feature branch.

Before upgrading an existing installation, back up the entire `config/` directory.
This branch includes upstream database migrations; restoring the backup may be
necessary to downgrade. Install the RPM matching the host architecture:

```bash
sudo dnf install ./kavita-0.9.1.23-0.nightly.*.x86_64.rpm
sudo systemctl enable --now kavita.service
```

Use the `aarch64` RPM on ARM64 or the `armv7hl` RPM on supported ARM32 systems.
The service account needs read/traverse permission on media-library directories.
Then open **Settings > Kavita+ > Comic Vine** to configure your API key.
