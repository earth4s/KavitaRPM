%global _build_id_links none
%global debug_package %{nil}

# Kavita's release archives are already-built application payloads. Keep their
# binaries byte-for-byte intact instead of running host-architecture brp-strip
# processing. This is also what permits the linux-arm archive to be wrapped as
# an armv7hl RPM on GitHub's ARM64 runner.
%global __os_install_post %{nil}

%global kavita_version %{?kavita_version_override}%{!?kavita_version_override:0.0.0}
%global kavita_release %{?kavita_release_override}%{!?kavita_release_override:1}
%global kavita_rid %{?kavita_rid_override}%{!?kavita_rid_override:linux-x64}
%global kavita_name %{?kavita_name_override}%{!?kavita_name_override:kavita}
%global kavita_musl %{?kavita_musl_override}%{!?kavita_musl_override:0}

Name:           %{kavita_name}
Version:        %{kavita_version}
Release:        %{kavita_release}%{?dist}
Summary:        Cross-platform reading server for books, comics, and manga

License:        GPL-3.0-only
URL:            https://github.com/Kareadita/Kavita
Source0:        kavita-%{kavita_rid}.tar.gz
Source1:        kavita.service

ExclusiveArch:  x86_64 aarch64 armv7hl

BuildRequires:  systemd-rpm-macros

Requires:       tzdata
Requires(pre):  shadow-utils
Requires(post): systemd
Requires(preun): systemd
Requires(postun): systemd

%if 0%{?kavita_musl}
# This is a distinct package because the upstream musl and glibc archives are
# both x86_64 and cannot share the same RPM NEVRA.
Conflicts:      kavita
%else
Requires:       libgdiplus
Requires:       libicu
Conflicts:      kavita-musl
%endif

%description
Kavita is a self-hosted reading server for manga, comics, PDFs, EPUB books,
and other reading material. This package repackages one of Kavita's official,
self-contained Linux release archives and installs a systemd service.

%prep
%setup -q -n Kavita

%build
# The upstream archive is already a compiled, self-contained application.

%install
rm -rf %{buildroot}

install -d %{buildroot}/opt/Kavita
cp -a . %{buildroot}/opt/Kavita/

install -Dm0644 %{SOURCE1} \
    %{buildroot}%{_unitdir}/kavita.service

install -Dm0644 LICENSE.txt \
    %{buildroot}%{_licensedir}/%{name}/LICENSE.txt
rm -f %{buildroot}/opt/Kavita/LICENSE.txt

chmod 0755 %{buildroot}/opt/Kavita/Kavita
chmod 0750 %{buildroot}/opt/Kavita/config
chmod 0640 %{buildroot}/opt/Kavita/config/appsettings-init.json

# Generate the payload list dynamically so new upstream runtime files are
# included automatically. Quote every path because the upstream web assets
# contain directory names with spaces. Construct the RPM "percent-dir" marker
# at shell runtime so neither rpmbuild nor find interprets it as formatting.
pushd %{buildroot}
pct="$(printf '\045')"
find opt/Kavita -mindepth 1 \
    -path 'opt/Kavita/config' -prune -o \
    \( -type d -o -type f -o -type l \) -print |
while IFS= read -r path; do
    escaped="$(printf '%s' "$path" | sed 's/\\/\\\\/g; s/"/\\"/g')"
    if [ -d "$path" ] && [ ! -L "$path" ]; then
        echo "${pct}dir \"/${escaped}\""
    else
        echo "\"/${escaped}\""
    fi
done > %{_builddir}/kavita-files.list
popd

%pre
getent group kavita >/dev/null 2>&1 || \
    groupadd --system kavita

getent passwd kavita >/dev/null 2>&1 || \
    useradd --system \
        --gid kavita \
        --home-dir /opt/Kavita \
        --shell /sbin/nologin \
        --comment "Kavita Server" \
        kavita

exit 0

%post
%systemd_post kavita.service

%preun
%systemd_preun kavita.service

%postun
%systemd_postun_with_restart kavita.service

%files -f %{_builddir}/kavita-files.list
%license %{_licensedir}/%{name}/LICENSE.txt
%{_unitdir}/kavita.service
%dir /opt/Kavita
%attr(0750,kavita,kavita) %dir /opt/Kavita/config
%config(noreplace) %attr(0640,kavita,kavita) /opt/Kavita/config/appsettings-init.json

%changelog
* Thu Sep 03 2026 John Martinez <earth4s@users.noreply.github.com> - 0.0.0-1
- Package all official Kavita Linux release variants
- Add ARM32 armv7hl and musl x86_64 RPM variants
- Fix dynamic file list handling for directories with spaces

* Tue Aug 04 2026 John Martinez <earth4s@users.noreply.github.com> - 0.0.0-1
- Add automated RPM repackaging for official Kavita Linux releases
