#!/usr/bin/env bash
# Keep every build transfer inside an unpublished GitHub release.
set -euo pipefail
: "${GITHUB_REPOSITORY:?Set GITHUB_REPOSITORY}"
: "${NIGHTLY_TAG:?Set NIGHTLY_TAG}"
: "${GH_TOKEN:?Set GH_TOKEN}"

assert_draft() {
  local state
  state="$(gh release view "$NIGHTLY_TAG" --repo "$GITHUB_REPOSITORY" --json isDraft)"
  if ! jq -e '.isDraft == true' <<< "$state" > /dev/null; then
    echo 'Refusing to transfer build files: the release is not a draft.' >&2
    exit 1
  fi
}

assert_draft
case "${1:-assert}" in
  assert) ;;
  upload)
    shift
    gh release upload "$NIGHTLY_TAG" --repo "$GITHUB_REPOSITORY" --clobber "$@"
    ;;
  download)
    gh release download "$NIGHTLY_TAG" --repo "$GITHUB_REPOSITORY" \
      --dir "${2:?Set destination directory}" --pattern "${3:?Set asset pattern}" --clobber
    ;;
  remove-web)
    gh release delete-asset "$NIGHTLY_TAG" nightly-web.tar.gz --repo "$GITHUB_REPOSITORY" --yes
    ;;
  notes)
    gh release edit "$NIGHTLY_TAG" --repo "$GITHUB_REPOSITORY" \
      --draft --prerelease --latest=false --notes-file "${2:?Set notes file}"
    ;;
  *) echo 'Unknown draft release operation' >&2; exit 1 ;;
esac
assert_draft
