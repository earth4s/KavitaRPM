%global _build_id_links none
%global debug_package %{nil}

%global kavita_version %{?kavita_version_override}%{!?kavita_version_override:0.0.0}
%global kavita_release %{?kavita_release_override}%{!?kavita_release_override:1}

%ifarch x86_64
%global kavita_rid linux-x64
%endif

%ifarch aarch64
%global kavita_rid linux-arm64
%endif

Name:           kavita
Version:        %{kavita_version}
Release:        %{kavita_release}%{?dist}
Summary:        Cross-platform reading server for books, comics, and manga

License:        GPL-3.0-only
URL:            https://github.com/Kareadita/Kavita
Source0:        kavita-%{kavita_rid}.tar.gz
Source1:        kavita.service

ExclusiveArch:  x86_64 aarch64

BuildRequires:  systemd-rpm-macros

Requires:       libgdiplus
Requires:       libicu
Requires:       tzdata
Requires(pre):  shadow-utils
Requires(post): systemd
Requires(preun): systemd
Requires(postun): systemd

%description
Kavita is a self-hosted reading server for manga, comics, PDFs, EPUB books,
and other reading material. This package repackages Kavita's official,
self-contained Linux release and installs a systemd service.

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
# included automatically. The writable config directory is packaged below.
pushd %{buildroot}
find opt/Kavita -mindepth 1 \
    -path 'opt/Kavita/config' -prune -o \
    -type d -printf '%%dir /%p\n' -o \
    -type f -printf '/%p\n' -o \
    -type l -printf '/%p\n' \
    > %{_builddir}/kavita-files.list
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
* Tue Aug 04 2026 John Martinez <earth4s@users.noreply.github.com> - 0.0.0-1
- Add automated RPM repackaging for official Kavita Linux releases
