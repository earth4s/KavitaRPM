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
Then open **Settings > Kavita+ > Meta API Connections > Comic Vine** to configure your API key.

## Fork version identity

These builds use a `kavitarpm` suffix to distinguish them from mainline Kavita.
For example, `0.9.1.23-kavitarpm.nightly.20261116.8.1` appears in the server
information panel, release title, tag, archive filenames and build manifests.
The final two numbers are the workflow build number and attempt. The assembly
informational version also records the short source commit after `+`.

An x64 archive is named
`kavita-0.9.1.23-kavitarpm.nightly.20261116.8.1-linux-x64.tar.gz`.
The matching RPM is
`kavita-0.9.1.23-0.nightly.20261116.8.kavitarpm.1.fc44.x86_64.rpm`.
Its package name, service and installation paths remain compatible with earlier
RPMs. The release field preserves chronological upgrade ordering and identifies
the fork. Previously built releases are not renamed.

Numeric assembly/database versions remain upstream-compatible; the suffix is a
display/build identity, not a schema version. Local builds default to
`<upstream-version>-kavitarpm`. The nightly overrides this with its unique build
version. Both SOURCE.json and BUILD-INFO.json retain `upstream_version`
separately from the fork's `version`.

## Comic Specials folders

Files inside a comic's `Specials/` subtree belong to that parent comic and appear
in its Specials volume, even when annual filenames or ComicInfo identify a
separate annual series. This applies to Comic and Comic (Flexible) libraries.
Folder-name matching is case insensitive and supports nested subfolders. An
unrelated title such as `Holiday Specials` is not treated as a Specials container.

After installing the fix, run a forced full library scan to regroup files that
were previously indexed under a bogus `Specials` series. Annuals keep their own
embedded Comic Vine issue IDs; the parent series match does not replace them
with the same-numbered ordinary issue. See upstream issue
[Kareadita/Kavita#3942](https://github.com/Kareadita/Kavita/issues/3942).
