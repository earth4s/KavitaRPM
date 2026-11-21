#!/usr/bin/env bash
set -euo pipefail

: "${BUILD_RID:?Set BUILD_RID}"
: "${RPM_ARCH:?Set RPM_ARCH}"
: "${GITHUB_SHA:?Set GITHUB_SHA}"
: "${GITHUB_RUN_NUMBER:?Set GITHUB_RUN_NUMBER}"

case "${BUILD_RID}:${RPM_ARCH}" in
  linux-x64:x86_64)
    kepubify_name=kepubify-linux-64bit
    kepubify_sha=37d7628d26c5c906f607f24b36f781f306075e7073a6fe7820a751bb60431fc5
    executable_arch='x86-64'
    ;;
  linux-arm64:aarch64)
    kepubify_name=kepubify-linux-arm64
    kepubify_sha=5a15b8f6f6a96216c69330601bca29638cfee50f7bf48712795cff88ae2d03a3
    executable_arch='aarch64'
    ;;
  linux-arm:armv7hl)
    kepubify_name=kepubify-linux-arm
    kepubify_sha=07f23275c4e674093443f01a591aa0980b0b87dbb0a10986d5001e9d56b0e1e7
    executable_arch='ARM'
    ;;
  *) echo 'Unsupported runtime and RPM architecture combination' >&2; exit 1 ;;
esac

source scripts/nightly/version.sh
stage="$PWD/_nightly/${BUILD_RID}"
payload="$stage/Kavita"
mkdir -p "$payload" "$PWD/_nightly/dist"

dotnet publish Kavita.Server/Kavita.Server.csproj -c Release -r "$BUILD_RID" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=None \
  -p:InformationalVersion="$informational_version" -p:IncludeSourceRevisionInInformationalVersion=false \
  -o "$payload"
mv "$payload/Kavita.Server" "$payload/Kavita"
install -m 0644 INSTALL.txt "$payload/README.txt"
install -m 0644 LICENSE "$payload/LICENSE.txt"
# Only ship the clean first-run configuration, never databases or generated credentials.
rm -rf "$payload/config" "$payload/BuildHost-net472" "$payload/BuildHost-netcore"
install -d -m 0750 "$payload/config"
install -m 0640 Kavita.Server/config/appsettings.json "$payload/config/appsettings-init.json"
curl -fL --retry 3 "https://github.com/pgaskin/kepubify/releases/download/v4.0.4/$kepubify_name" -o "$payload/kepubify"
echo "$kepubify_sha  $payload/kepubify" | sha256sum --check --status
chmod 0755 "$payload/kepubify" "$payload/Kavita"
file "$payload/Kavita" | grep -q "$executable_arch"
test -f "$payload/wwwroot/index.html"
grep -rqF --include='*.js' 'admin-comicvine' "$payload/wwwroot"
grep -rqF --include='*.js' 'comicvine/search?seriesId=' "$payload/wwwroot"
jq -n --arg commit "$GITHUB_SHA" --arg rid "$BUILD_RID" --arg version "$fork_version" \
  --arg upstream_version "$version" --arg informational_version "$informational_version" \
  '{distribution:"KavitaRPM",channel:"nightly",commit:$commit,runtime:$rid,version:$version,upstream_version:$upstream_version,informational_version:$informational_version}' > "$payload/BUILD-INFO.json"

if [[ "${SMOKE_TEST:-false}" == true ]]; then
  smoke="$(mktemp -d)"
  cp -a "$payload/." "$smoke/"
  jq '.Port=5808 | .IpAddresses="127.0.0.1"' "$smoke/config/appsettings-init.json" > "$smoke/settings.json"
  mv "$smoke/settings.json" "$smoke/config/appsettings-init.json"
  (cd "$smoke" && exec timeout 100 ./Kavita > server.log 2>&1) &
  smoke_pid=$!
  trap 'kill "$smoke_pid" 2>/dev/null || true' EXIT
  ready=false
  for attempt in $(seq 1 75); do
    if curl --fail --silent http://127.0.0.1:5808/ > "$smoke/index.html"; then
      ready=true
      break
    fi
    if ! kill -0 "$smoke_pid" 2>/dev/null; then break; fi
    sleep 1
  done
  if [[ "$ready" != true ]]; then cat "$smoke/server.log"; exit 1; fi
  grep -q app-root "$smoke/index.html"
  test "$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:5808/api/settings/comicvine)" = 401
  test "$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:5808/api/comicvine/counts)" = 401
  kill "$smoke_pid" 2>/dev/null || true
  wait "$smoke_pid" 2>/dev/null || true
  trap - EXIT
  rm -rf "$smoke"
fi

archive="$PWD/_nightly/dist/kavita-${fork_version}-${BUILD_RID}.tar.gz"
tar -C "$stage" -czf "$archive" Kavita
topdir="$PWD/_nightly/rpmbuild-${BUILD_RID}"
mkdir -p "$topdir"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}
cp "$archive" "$topdir/SOURCES/kavita-${BUILD_RID}.tar.gz"
cp packaging/rpm/kavita.service "$topdir/SOURCES/"

# The container is only a build tool; the resulting RPM installs a native systemd service.
docker run --rm -v "$PWD:/workspace" -w /workspace \
  -e BUILD_RID -e RPM_ARCH -e RPM_VERSION="$version" -e RPM_RELEASE="$rpm_release" \
  fedora:44 bash -euo pipefail -c '
    dnf install -y rpm-build systemd-rpm-macros findutils tar gzip shadow-utils
    rpmbuild -bb --target "$RPM_ARCH" \
      --define "_topdir /workspace/_nightly/rpmbuild-$BUILD_RID" \
      --define "_rpmformat 4" \
      --define "kavita_version_override $RPM_VERSION" \
      --define "kavita_release_override $RPM_RELEASE" \
      --define "kavita_rid_override $BUILD_RID" \
      packaging/rpm/kavita.spec
    find "_nightly/rpmbuild-$BUILD_RID/RPMS" -name "*.rpm" -exec cp {} _nightly/dist/ \;
    test "$(rpm -qp --qf "%{VERSION}-%{RELEASE}" _nightly/dist/*.rpm)" = "$RPM_VERSION-$RPM_RELEASE.fc44"
    rpm -K _nightly/dist/*.rpm
    rpm -qpl _nightly/dist/*.rpm | grep -Fx /opt/Kavita/Kavita
    rpm -qpl _nightly/dist/*.rpm | grep -Fx /usr/lib/systemd/system/kavita.service
  '
