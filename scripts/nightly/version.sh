#!/usr/bin/env bash
# Source from the repository root. Keep every job on the frontend's build date.
version="$(sed -n 's:.*<AssemblyVersion>\([^<]*\)</AssemblyVersion>.*:\1:p' Kavita.Common/Kavita.Common.csproj)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]
: "${GITHUB_RUN_NUMBER:?Set GITHUB_RUN_NUMBER}"
: "${GITHUB_SHA:?Set GITHUB_SHA}"
date_stamp="${NIGHTLY_DATE:-$(TZ=America/Chicago date +%Y%m%d)}"
build_attempt="${GITHUB_RUN_ATTEMPT:-1}"
[[ "$date_stamp" =~ ^[0-9]{8}$ && "$GITHUB_RUN_NUMBER" =~ ^[0-9]+$ && "$build_attempt" =~ ^[0-9]+$ ]]
fork_version="${version}-kavitarpm.nightly.${date_stamp}.${GITHUB_RUN_NUMBER}.${build_attempt}"
informational_version="${fork_version}+${GITHUB_SHA:0:7}"
# Preserve ordering against existing 0.nightly.DATE.RUN RPMs; attempts also increase.
rpm_release="0.nightly.${date_stamp}.${GITHUB_RUN_NUMBER}.kavitarpm.${build_attempt}"
