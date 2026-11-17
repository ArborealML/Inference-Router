#!/usr/bin/env python3
"""Extract the release-job step bodies from .github/workflows/release.yml and run them
against forged fixtures. Read-only on the repo; fixtures in /tmp/relgate.

The stub `gh` answers only the calls the steps make, so it proves control flow (what the
steps refuse and in what order), not GitHub's answers. Each case states the attack it models.
It answers with the JSON the endpoint would answer with and then applies the caller's own
--jq to it, the way gh does, so an older revision gets exactly the projection it asked for
and a difference in behaviour between revisions is the step's reading of the same answer.
"""
import atexit
import os
import shutil
import re
import shutil
import subprocess
import sys

import yaml

REPO = os.path.abspath(os.path.dirname(__file__) + "/..")
WF = os.environ.get("RELGATE_WF") or os.path.join(REPO, ".github/workflows/release.yml")
ROOT = "/tmp/relgate-%d" % os.getpid()   # per-process: two runs in one worktree collide on bin/gh
RIDS = "linux-x64 win-x64"
COMMIT_SHA = "a" * 40

STEPS = {
    "stage": "Stage, verify and bind every declared asset",
    "publish": "Create the draft, inspect the remote, then publish",
    "cleanup": "Remove a half-finished draft",
    "preflight_release_check": "Refuse if a release already exists for this tag",
}


def bodies_at(rev):
    """Step bodies from an older revision, so a fixed mistake stays reproducible."""
    import subprocess as sp
    text = sp.run(["git", "show", f"{rev}:.github/workflows/release.yml"], cwd=REPO,
                  capture_output=True, text=True, check=True).stdout
    doc = yaml.safe_load(text)
    out = {}
    for job in ("release", "preflight"):
        for step in doc["jobs"][job]["steps"]:
            for key, name in STEPS.items():
                if step.get("name") == name:
                    out[key] = step["run"]
    return out


def bodies():
    doc = yaml.safe_load(open(WF, encoding="utf-8"))
    out = {}
    for job in ("release", "preflight"):
        for step in doc["jobs"][job]["steps"]:
            for key, name in STEPS.items():
                if step.get("name") == name:
                    out[key] = step["run"]
    missing = set(STEPS) - set(out)
    if missing:
        sys.exit(f"steps not found: {missing}")
    return out


GH_STUB = r"""#!/usr/bin/env bash
set -uo pipefail
echo "gh $*" >> "$GH_LOG"
sub="${1:-}"; shift || true
state() { printf '%s' "$(cat "$1" 2>/dev/null)"; }
tag_name() { printf '%s' "${TAG}"; }
assets_names() { sort "$STATE_DIR/assets.sorted" 2>/dev/null | paste -sd' ' | tr -d '\n'; }
case "$sub" in
  release)
    case "${1:-}" in
      create)
        shift                      # drop the subcommand word
        flags=(); files=(); n=0; skipv=0
        # Flag contract transcribed from `gh release create --help` (gh 2.46.0): an unknown flag is
        # an error here exactly as in the real CLI, and it fails before any HTTP. The stub used to
        # swallow --json/--jq quietly, which hid a publish step that could never have run: gh has no
        # way to make `release create` print a release id.
        for a in "$@"; do
          if [ "$skipv" = 1 ]; then skipv=0; continue; fi
          case "$a" in
            --prerelease|-p) flags+=("--prerelease") ;;
            --draft|-d|--verify-tag|--generate-notes|--latest|--notes-from-tag) : ;;
            --title|-t|--notes|-n|--notes-file|-F|--target|--discussion-category|--notes-start-tag) skipv=1 ;;
            -R|--repo) skipv=1 ;;
            -*) echo "unknown flag: $a" >&2; exit 1 ;;
            *) n=$((n+1)); [ "$n" -eq 1 ] && continue   # first operand is the tag
               files+=("$(basename "$a")") ;;
          esac
        done
        if [ "${KEEP_ASSETS:-0}" != 1 ]; then
          printf '%s\n' "${files[@]}" | sort > "$STATE_DIR/assets.sorted"
        fi
        if [ "${KEEP_PRE:-0}" != 1 ]; then
          if [ "${#flags[@]}" -gt 0 ]; then echo true > "$STATE_DIR/pre"; else echo false > "$STATE_DIR/pre"; fi
        fi
        echo true > "$STATE_DIR/draft"
        if [ "${CREATE_FAILS:-0}" = 1 ]; then echo "HTTP 422: Validation Failed (tag already has a release)" >&2; exit 1; fi
        printf '%s\n' "$REL_ID"
        ;;
      edit)
        for a in "$@"; do [ "$a" = "--draft=false" ] && echo false > "$STATE_DIR/draft"; done
        ;;
    esac
    ;;
  api)
    rest="$*"
    args=("$@")
    # The API description describes GET /repos/{owner}/{repo}/releases/tags/{tag} as "Get a published
    # release with the specified tag", and drafts are listed only to a token with push access. So the
    # stub refuses that lookup outright: a step that tries to find its own fresh draft by tag is
    # broken against the documented contract, and that is a test failure, not an answer.
    if [[ "$rest" == *"releases/tags/"* ]]; then
      echo "gh: Not Found (HTTP 404) — releases/tags/<tag> answers published releases only" >&2; exit 3
    fi
    if [[ "$rest" == *"-X DELETE"* ]]; then echo "DELETED" >> "$GH_LOG"; exit 0; fi
    if [[ "$rest" == *"-X PATCH"* && "$rest" == *"-F draft=false"* ]]; then echo false > "$STATE_DIR/draft"; fi
    # POST /releases: the create. Answers with the id and upload_url of the release just made. The
    # fields the step is required to send are enforced here, so a caller that forgets draft=true or
    # sends it with -f (a string) fails the way an API would reject it rather than passing quietly.
    if [[ "$rest" == *"-X POST"* && "$rest" == *"/releases "* ]]; then
      have_tag=0; have_draft=0; pre=0
      for a in "${args[@]}"; do
        case "$a" in
          tag_name=*) have_tag=1 ;;
          draft=true) have_draft=1 ;;
          prerelease=true) pre=1 ;;
          prerelease=false) pre=0 ;;
        esac
      done
      if [ "$have_tag" != 1 ] || [ "$have_draft" != 1 ]; then
        echo "bad create request: need -f tag_name=… and -F draft=true (got: $rest)" >&2; exit 2
      fi
      if [ "${CREATE_FAILS:-0}" = 1 ]; then echo "HTTP 422: Validation Failed (tag already has a release)" >&2; exit 1; fi
      echo true > "$STATE_DIR/draft"
      if [ "$pre" = 1 ]; then echo true > "$STATE_DIR/pre"; else echo false > "$STATE_DIR/pre"; fi
    fi
    # POST <upload_url>?name=… : one asset, body read from --input, answered with the asset id.
    if [[ "$rest" == *"-X POST"* && "$rest" == *"?name="* ]]; then
      name="${rest##*?name=}"; name="${name%%\"*}"; name="${name%% *}"
      inp=""; for ((i=0;i<${#args[@]};i++)); do [ "${args[$i]}" = "--input" ] && inp="${args[$((i+1))]:-}"; done
      if [ ! -f "$inp" ]; then echo "HTTP 422: no such input file $inp" >&2; exit 1; fi
      if [ "${UPLOAD_FAILS:-0}" = 1 ]; then echo "HTTP 500: upload failed" >&2; exit 1; fi
      ASSET_N=$(( $(cat "$STATE_DIR/asset_n" 2>/dev/null || echo 0) + 1 )); printf '%s' "$ASSET_N" > "$STATE_DIR/asset_n"; printf '%s\n' "$name" >> "$STATE_DIR/uploaded"
      if [ "${UPLOAD_NO_ID:-0}" = 1 ]; then printf ''; else printf '%s' "$((9000 + ASSET_N))"; fi
      exit 0
    fi
    # Which endpoint the call names: every endpoint these steps name is either repos/… or an
    # absolute uploads URL, and it always comes before a flag whose value could be mistaken for one.
    endpoint=""
    for a in "${args[@]}"; do
      case "$a" in repos/*|http*) endpoint="$a"; break ;; esac
    done
    jq=""
    for ((i=0;i<${#args[@]};i++)); do
      if [ "${args[$i]}" = "--jq" ]; then jq="${args[$((i+1))]:-}"; fi
    done
    # The stub answers with the JSON the endpoint answers with, then runs the caller's own --jq over
    # it with the same jq the step has — which is what gh does. Building the object and projecting it
    # afterwards, instead of hand-writing an answer per filter, is what makes an older revision get
    # exactly the projection it asked for, quirks included: join(" ") stringifies booleans, turns a
    # null into an empty field, and leaves two adjacent spaces where the null was.
    answer() { if [ -n "$jq" ]; then printf '%s' "$1" | jq -r "$jq"; else printf '%s' "$1"; fi; }
    blank=true
    # The asset array for this release, built from what the run uploaded (or from the fixture's
    # forged variants). The same objects answer the list endpoint and the .assets of the release.
    assets_answer() {
      names="$(sort "$STATE_DIR/uploaded" 2>/dev/null)"
      [ -z "$names" ] && names="$(sort "$STATE_DIR/assets.sorted" 2>/dev/null)"
      [ -n "${EXTRA_ASSET:-}" ] && names="$(printf '%s\n%s\n' "$names" "$EXTRA_ASSET" | sort)"
      [ -n "${DUP_ASSET:-}" ] && names="$(printf '%s\n' "$names"; printf '%s\n' "$names" | head -1)"
      junk="sha256:$(printf 'a different byte sequence entirely' | sha256sum | cut -d' ' -f1)"
      rows=""; n=0
      for name in $names; do
        n=$((n+1))
        if [ "${FAKE_NAMES:-0}" = 1 ]; then
          # the forged asset: named "<what we wanted> uploaded <its size> <its digest>", holding four
          # bytes of junk and reporting its own honest size and digest further along the same record
          sz="$(stat -c %s "stage/$name" 2>/dev/null || echo 1)"
          dg="$(sha256sum "stage/$name" 2>/dev/null | cut -d' ' -f1)"
          rows="$rows$(jq -n --arg n "$name uploaded $sz sha256:$dg" --arg j "$junk" '{name:$n,state:"uploaded",size:4,digest:$j,id:9000}'),"
          continue
        fi
        if [ ! -f "stage/$name" ]; then
          rows="$rows$(jq -n --arg n "$name" '{name:$n,state:"uploaded",size:1,digest:"sha256:deadbeef",id:9000}'),"
          continue
        fi
        st=uploaded
        case "${ASSET_STATE:-}" in ""|uploaded) st=uploaded ;; *) st="$ASSET_STATE" ;; esac
        [ "${ASSET_STATE_NL:-0}" = 1 ] && st="uploaded
"
        nm="$name"
        [ -n "${ASSET_NAME_NL:-}" ] && [ "$name" = "$ASSET_NAME_NL" ] && nm="$name
"
        sz="$(stat -c %s "stage/$name")"
        if [ -n "${SIZE_SHORT:-}" ] && [ "$name" = "$SIZE_SHORT" ]; then sz=$((sz-1)); fi
        dg="sha256:$(sha256sum "stage/$name" | cut -d' ' -f1)"
        [ "${ASSET_DIGEST_NL:-0}" = 1 ] && dg="$dg
"
        if [ -n "${DIGEST_DIFF:-}" ] && [ "$name" = "$DIGEST_DIFF" ]; then dg="$junk"; fi
        djson="$(jstr "$dg")"
        if [ -n "${DIGEST_NONE:-}" ] && [ "$name" = "$DIGEST_NONE" ]; then djson=null; fi
        rows="$rows$(jq -n --arg n "$nm" --arg st "$st" --argjson sz "$sz" --argjson dg "$djson" '{name:$n,state:$st,size:$sz,digest:$dg,id:9000}'),"
      done
      printf '[%s]' "${rows%,}"
    }
    jstr() { printf '%s' "$(jq -n --arg v "$1" '$v')"; }     # the JSON string for a shell value
    body=""
    case "$endpoint" in
      */releases)                                     # POST /releases — the create response
        id_val="${CREATED_ID_RAW:-$REL_ID}"
        # the address is built from how jq would PRINT the id, which is what the step compares against
        id_norm="$(jq -rn --argjson v "$id_val" '$v | tostring')"
        url="https://uploads.github.com/repos/${GH_REPO}/releases/${id_norm}/assets{?name,label}"
        [ "${NO_UPLOAD_URL:-0}" = 1 ] && url=""
        [ "${BAD_UPLOAD_URL:-0}" = 1 ] && url="https://uploads.example.com/repos/${GH_REPO}/releases/${REL_ID}/assets{?name,label}"
        [ "${URL_NO_TEMPLATE:-0}" = 1 ] && url="https://uploads.github.com/repos/${GH_REPO}/releases/${REL_ID}/assets"
        [ "${URL_EXTRA:-0}" = 1 ] && url="https://uploads.github.com/repos/${GH_REPO}/releases/${REL_ID}/assets-extra{?name,label}"
        [ "${URL_QUERY:-0}" = 1 ] && url="https://uploads.github.com/repos/${GH_REPO}/releases/${REL_ID}/assets{?name,label}&label=secret"
        [ "${URL_CASE_UPPER:-0}" = 1 ] && url="https://uploads.github.com/repos/${GH_REPO^^}/releases/${REL_ID}/assets{?name,label}"
        # canonical owner/repo spelling is fine, casing anywhere from the release id on is not
        [ "${URL_CASE_PATH:-0}" = 1 ] && url="https://uploads.github.com/repos/${GH_REPO}/releases/${REL_ID}/Assets{?name,label}"
        made_tag="${CREATED_TAG:-$TAG}"
        made_name="${CREATED_NAME:-$TAG}"
        [ "${CREATED_NAME_LONG:-0}" = 1 ] && made_name="$TAG$(printf 'y%.0s' {1..300})"
        # a title that carries a space, and a draft field that is not there at all: the two ways a
        # whitespace-joined projection stops having anything to do with the fields it came from
        [ "${CREATED_NAME_WS:-0}" = 1 ] && made_name="$TAG true"
        draft="${CREATED_DRAFT:-true}"; [ "$draft" = false ] && draft=false
        pre="$(state "$STATE_DIR/pre")"; [ "$pre" != true ] && pre=false
        [ -n "${CREATED_PRE:-}" ] && pre="$CREATED_PRE"
        # a long address must not crowd the other fields out of the truncated diagnostic
        # a long address must not crowd the other fields out of the truncated diagnostic; the tail is
        # marked with q so "the end of the address is still shown" is a testable claim
        [ "${LONG_URL:-0}" = 1 ] && url="${url}?$(printf 'p%.0s' {1..120})z$(printf 'q%.0s' {1..120})"
        url_json=null; [ -n "$url" ] && url_json="$(jstr "$url")"
        # a documented string field whose value ends in a newline: shell projection strips it, jq sees it
        [ "${URL_TRAILING_LF:-0}" = 1 ] && url_json="$(jq -n --arg u "$url" '$u + "\n"')"
        # the field arriving with the wrong type: the diagnostic must still name the fields that matched
        [ "${URL_WRONG_TYPE:-0}" = 1 ] && url_json='42'
        # the field arriving as an object holding a long string: a container must not be printed verbatim
        [ "${URL_WRONG_TYPE:-0}" = 2 ] && url_json="$(jq -n --arg k "$(printf 'z%.0s' {1..400})" '{k:$k}')"
        # a value that already spells a truncation marker: the marker is text we print, never a bound
        [ "${CREATED_NAME_FORGED_CAP:-0}" = 1 ] && made_name="$TAG+(999ch)b"
        name_json="$(jstr "$made_name")"; [ "${CREATED_NAME_NULL:-0}" = 1 ] && name_json=null
        draft_json="$(jq -n --arg v "$draft" '$v|if .=="true" then true else false end')"
        [ "${CREATED_DRAFT_NULL:-0}" = 1 ] && draft_json=null
        body="$(jq -n --argjson id "$id_val" --argjson upload_url "$url_json" --arg tag_name "$made_tag" \
                 --argjson name "$name_json" --argjson draft "$draft_json" --argjson prerelease "$pre" \
                 '{id:$id,upload_url:$upload_url,tag_name:$tag_name,name:$name,draft:$draft,prerelease:$prerelease}')"
        if [ -n "${CREATE_EXTRA_DOC:-}" ]; then body="$body
$CREATE_EXTRA_DOC"; fi
        # a whole body of the test's choosing, for shapes the hooks above cannot express
        [ -n "${CREATE_BODY_RAW:-}" ] && body="$CREATE_BODY_RAW"
        ;;
      *"/releases?"*)                                 # the releases list, for preflight
        if [ "${PREFLIGHT_FAIL:-0}" = 1 ]; then echo "HTTP 502: Bad Gateway" >&2; exit 1; fi
        # arbitrary stderr text, to pin which numbers the diagnostic may call a status
        if [ -n "${PREFLIGHT_STDERR:-}" ]; then printf '%s\n' "$PREFLIGHT_STDERR" >&2; exit 1; fi
        # a credential-bearing stderr line: the step may log the failure, never the credential
        if [ "${PREFLIGHT_ERRTOKEN:-0}" = 1 ]; then
          echo "gh: HTTP 502 (proxy) token=ghp_$(printf 'a%.0s' {1..40})" >&2; exit 1
        fi
        # gh --paginate prints the pages it did fetch and THEN exits non-zero, which is the shape
        # that a `|| true` turns into a silent "nothing exists for this tag".
        shaped=0
        if [ "${PREFLIGHT_PARTIAL:-0}" = 1 ]; then
          body='[{"id":7001,"tag_name":"v0.0.1","draft":false}]'
          partial=1; shaped=1
        fi
        if [ "${PREFLIGHT_ERRDOC:-0}" = 1 ]; then
          body='{"message":"server error","documentation_url":"https://docs.github.com/rest"}'
          shaped=1
        fi
        if [ "${PREFLIGHT_BADITEM:-0}" = 1 ]; then
          body='[{"id":7001,"tag_name":"v0.0.1","draft":false},"junk"]'
          shaped=1
        fi
        if [ "$shaped" = 0 ]; then
          if [ -n "${PREFLIGHT_PAGES:-}" ]; then body="$PREFLIGHT_PAGES"
          else
            items=""; n=0
            for d in ${PREFLIGHT_FLAGS:-}; do
              n=$((n+1))
              items="$items$(jq -n --argjson id "$((7000+n))" --arg t "$TAG" --argjson d "$d" '{id:$id,tag_name:$t,draft:$d}'),"
            done
            body="[${items%,}]"
          fi
        fi
        ;;
      */assets*)                                      # this release's own asset list, one snapshot
        body="$(assets_answer)"
        if [ -n "${ASSETS_EXTRA_DOC:-}" ]; then body="$body
$ASSETS_EXTRA_DOC"; fi
        ;;
      */commits/*)                                    # tag -> commit, as the API answers it
        SHA_CALLS=$(( $(cat "$STATE_DIR/sha_calls" 2>/dev/null || echo 0) + 1 )); printf '%s' "$SHA_CALLS" > "$STATE_DIR/sha_calls"
        sha="$COMMIT_SHA"
        if [ -n "${MOVED_TAG_SHA:-}" ] && { [ "${LATE_TAG_MOVE:-0}" != 1 ] || [ "$SHA_CALLS" -ge 2 ]; }; then
          sha="$MOVED_TAG_SHA"
        fi
        body="$(jq -n --arg s "$sha" '{sha:$s}')"
        ;;
      *)                                              # repos/{owner}/{repo}/releases/<id>
        idpart="${endpoint#*"/releases/"}"; idpart="${idpart%%\?*}"; idpart="${idpart%%/*}"
        case "$idpart" in
          ""|*[!0-9]*) body="" ;;
          *)
            if [ -n "${REL_404:-}" ]; then echo "HTTP 404: Not Found" >&2; exit 1; fi
            if [ -n "${PATCH_JSON:-}" ] && [[ "$rest" == *"-X PATCH"* ]]; then body="$PATCH_JSON"
            elif [ -n "${REL_JSON:-}" ]; then body="$REL_JSON"
            else
              REL_CALLS=$(( $(cat "$STATE_DIR/rel_calls" 2>/dev/null || echo 0) + 1 )); printf '%s' "$REL_CALLS" > "$STATE_DIR/rel_calls"
              rel_tag="$TAG"; rel_name="$TAG"; rel_id="$idpart"
              if [ -n "${LATE_REL_TAG:-}" ] && [ "$REL_CALLS" -ge 2 ]; then rel_tag="$LATE_REL_TAG"; fi
              if [ -n "${LATE_REL_NAME:-}" ] && [ "$REL_CALLS" -ge 2 ]; then rel_name="$LATE_REL_NAME"; fi
              if [ -n "${LATE_REL_ID:-}" ] && [ "$REL_CALLS" -ge 2 ]; then rel_id="$LATE_REL_ID"; fi
              if [ "${CREATED_NAME_SET:-0}" = 1 ]; then rel_name="${CREATED_NAME:-$TAG}"; fi
              d="$(state "$STATE_DIR/draft")"; [ "$d" != false ] && d=true
              p="$(state "$STATE_DIR/pre")"; [ "$p" != true ] && p=false
              # the release object carries its assets, as the API answers it
              body="$(jq -n --argjson id "$rel_id" --arg tag_name "$rel_tag" --arg name "$rel_name" \
                       --argjson draft "$d" --argjson prerelease "$p" --argjson assets "$(assets_answer)" \
                       '{id:$id,tag_name:$tag_name,name:$name,draft:$draft,prerelease:$prerelease,assets:$assets}')"
            fi
            ;;
        esac
        ;;
    esac
    if [ -n "$body" ]; then answer "$body"; rc=$?; if [ "${partial:-0}" = 1 ] && [ "$rc" = 0 ]; then echo "gh: could not fetch page 2 (HTTP 502)" >&2; rc=1; fi; exit $rc; fi
    printf ''
    ;;
esac
exit 0
"""


def sha256file(path):
    import hashlib
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def make_fixture(name, kind):
    """Build out/asset-<rid>/ trees under ROOT/name. Returns the working dir."""
    wd = os.path.join(ROOT, name)
    shutil.rmtree(wd, ignore_errors=True)
    os.makedirs(wd)

    def leg(rid, exe, tamper=None, skip=None, empty=None, extra=None, sidecar_of=None,
            dirname=None, members=None, symlink_archive=False, link_member=None):
        d = os.path.join(wd, "out", dirname or f"asset-{rid}")
        os.makedirs(d, exist_ok=True)
        arc = f"air-{rid}.tar.gz"
        if skip and arc in skip:
            pass
        else:
            stage = os.path.join(wd, ".build", rid)
            os.makedirs(stage, exist_ok=True)
            if tamper == "bogus_member":
                pack = ["airXexe"]
            elif tamper == "wrong_entry":
                pack = ["not-air"]
            elif members:
                pack = list(members)
            else:
                pack = [exe]
            for m in pack:
                open(os.path.join(stage, m), "w").write(f"member {m} of {rid}\n")
            tar = os.path.join(d, arc)
            if tamper == "corrupt":
                open(tar, "wb").write(b"\x1f\x8b junk not a tar")
            elif link_member:
                # the single member carries the right name but is a symlink to something else:
                # `tar -tzf` lists `air.exe` and the name test passes; only the type char tells
                real = os.path.join(stage, "payload.bin")
                open(real, "w").write("not the binary\n")
                os.remove(os.path.join(stage, exe))
                os.symlink(real, os.path.join(stage, exe))
                subprocess.run(["tar", "-czf", tar, "-C", stage, exe], check=True)
            else:
                subprocess.run(["tar", "-czf", tar, "-C", stage] + pack, check=True)
            if tamper == "digest":
                # record the digest of the archive the leg really built, then swap the archive for
                # a different build of the same name: only the digest binding can catch this one
                orig_dig = sha256file(tar)
                alt = os.path.join(wd, ".build", f"{rid}-alt")
                os.makedirs(alt, exist_ok=True)
                open(os.path.join(alt, exe), "w").write("a different build of the same name\n")
                subprocess.run(["tar", "-czf", tar, "-C", alt, exe], check=True)
            sc = f"{arc}.sha256"
            if skip and sc in skip:
                pass
            elif empty and sc in empty:
                open(os.path.join(d, sc), "w").write("")
            else:
                if sidecar_of:
                    other = os.path.join(wd, "out", f"asset-{sidecar_of}", f"air-{sidecar_of}.tar.gz")
                    dig, name_in_record = sha256file(other), f"air-{sidecar_of}.tar.gz"
                elif tamper == "digest":
                    dig, name_in_record = orig_dig, arc
                else:
                    dig, name_in_record = sha256file(tar), arc
                open(os.path.join(d, sc), "w").write(f"{dig}  {name_in_record}\n")
            if symlink_archive:
                real = os.path.join(wd, ".build", f"{rid}-real.tar.gz")
                shutil.copyfile(tar, real)
                os.remove(tar)
                os.symlink(real, tar)
        for e in extra or []:
            open(os.path.join(d, e), "w").write("rogue\n")

    if kind == "good":
        leg("linux-x64", "air")
        leg("win-x64", "air.exe")
    elif kind == "sidecar_names_other":
        leg("linux-x64", "air")
        leg("win-x64", "air.exe", sidecar_of="linux-x64")
    elif kind == "extra_sidecar":
        leg("linux-x64", "air", extra=["air-rogue.tar.gz.sha256"])
        leg("win-x64", "air.exe")
    elif kind == "undeclared_leg":
        leg("linux-x64", "air")
        leg("win-x64", "air.exe")
        leg("macos-arm64", "air")
    elif kind == "extra_archive":
        leg("linux-x64", "air")
        leg("win-x64", "air.exe")
        d = os.path.join(wd, "out", "asset-mac-x64")
        os.makedirs(d, exist_ok=True)
        open(os.path.join(d, "air-mac-x64.tar.gz"), "w").write("x")
    elif kind == "missing_archive":
        leg("linux-x64", "air")
        leg("win-x64", "air.exe", skip=["air-win-x64.tar.gz"])
    elif kind == "empty_sidecar":
        leg("linux-x64", "air")
        leg("win-x64", "air.exe", empty=["air-win-x64.tar.gz.sha256"])
    elif kind == "wrong_entry":
        leg("linux-x64", "air")
        leg("win-x64", "air.exe", tamper="wrong_entry")
    elif kind == "corrupt_tar":
        leg("linux-x64", "air")
        leg("win-x64", "air.exe", tamper="corrupt")
    elif kind == "tampered_archive":
        leg("linux-x64", "air")
        leg("win-x64", "air.exe", tamper="digest")
    elif kind == "compound_leg":
        # one artifact whose name is two legs glued together; a substring leg test admits it
        leg("linux-x64", "air")
        leg("win-x64", "air.exe")
        leg("linux-x64 win-x64", "air", dirname="asset-linux-x64 win-x64")
    elif kind == "bogus_member":
        # win archive whose only member is airXexe: `grep -qx "air.exe"` matched it as a regex
        leg("linux-x64", "air")
        leg("win-x64", "air.exe", tamper="bogus_member")
    elif kind == "extra_member":
        leg("linux-x64", "air")
        leg("win-x64", "air.exe", members=["air.exe", "payload.dll"])
    elif kind == "dir_archive":
        # a directory wearing the archive's name: passes `test -s`, is not a file
        leg("linux-x64", "air")
        leg("win-x64", "air.exe")
        p = os.path.join(wd, "out", "asset-win-x64", "air-win-x64.tar.gz")
        os.remove(p)
        os.makedirs(p)
    elif kind == "symlink_archive":
        leg("linux-x64", "air", symlink_archive=True)
        leg("win-x64", "air.exe")
    elif kind == "symlink_member":
        leg("linux-x64", "air")
        leg("win-x64", "air.exe", link_member=True)
    elif kind == "empty_leg_dir":
        leg("linux-x64", "air")
        os.makedirs(os.path.join(wd, "out", "asset-win-x64"), exist_ok=True)
    return wd


def run_step(body, wd, extra_env=None, step_name="step"):
    os.makedirs(os.path.join(ROOT, "bin"), exist_ok=True)
    stub = os.path.join(ROOT, "bin", "gh")
    open(stub, "w").write(GH_STUB)
    os.chmod(stub, 0o755)
    state = os.path.join(wd, ".state")
    os.makedirs(state, exist_ok=True)
    env = dict(os.environ)
    env.update({
        "EXPECTED_RIDS": RIDS,
        "TAG": "v0.1.0",
        "COMMIT": COMMIT_SHA,
        "PATH": os.path.join(ROOT, "bin") + os.pathsep + env["PATH"],
        "STATE_DIR": state,
        "GH_LOG": os.path.join(state, "gh.log"),
        "GITHUB_OUTPUT": os.path.join(state, "output"),
        "REL_ID": "12345",
        "COMMIT_SHA": COMMIT_SHA,
        "GH_TOKEN": "stub",
        "GH_REPO": "o/r",
    })
    env.update(extra_env or {})
    script = os.path.join(wd, f"{step_name}.sh")
    open(script, "w").write(body)
    return subprocess.run(["bash", script], cwd=wd, env=env, capture_output=True, text=True)


def msgs(p):
    return (p.stderr + p.stdout).strip().splitlines()


def refusal(p):
    m = msgs(p)
    return next((l for l in reversed(m) if "::error::" in l), (m[-1] if m else ""))


def gh_log(wd):
    path = os.path.join(wd, ".state", "gh.log")
    return open(path, encoding="utf-8").read() if os.path.exists(path) else ""


def main():
    b = bodies()
    fails = 0
    cases = [
        # (fixture, expect_zero, note, step, env)
        ("good", True, "all legs correct", "stage", None),
        ("sidecar_names_other", False, "P1 counterexample: both sidecars name the linux archive", "stage", None),
        ("extra_sidecar", False, "undeclared sidecar inside a leg dir", "stage", None),
        ("undeclared_leg", False, "artifact for a leg not declared", "stage", None),
        ("extra_archive", False, "artifact dir with an undeclared archive", "stage", None),
        ("missing_archive", False, "leg produced no archive", "stage", None),
        ("empty_leg_dir", False, "leg artifact dir arrived empty", "stage", None),
        ("empty_sidecar", False, "leg wrote an empty sidecar", "stage", None),
        ("wrong_entry", False, "tar unpacks but holds no air binary", "stage", None),
        ("corrupt_tar", False, "tar.gz is not readable", "stage", None),
        ("tampered_archive", False, "archive changed after its sidecar", "stage", None),
        ("compound_leg", False, "R2: artifact named for two legs at once", "stage", None),
        ("bogus_member", False, "R2: member airXexe stood in for air.exe", "stage", None),
        ("extra_member", False, "R3: archive smuggles a second member", "stage", None),
        ("dir_archive", False, "R3: directory wearing the archive name", "stage", None),
        ("symlink_archive", False, "R3: symlink wearing the archive name", "stage", None),
        ("symlink_member", False, "R4: archive's single member is a symlink named air.exe", "stage", None),
    ]
    for name, expect_zero, note, step, env in cases:
        wd = make_fixture(name, name)
        p = run_step(b[step], wd, env, step)
        ok = (p.returncode == 0) == expect_zero
        print(f"{'PASS' if ok else 'FAIL'}  {name:<18} rc={p.returncode} want_zero={int(expect_zero)}  {note}")
        if not ok:
            fails += 1
            print("      output:", " | ".join(msgs(p)[-4:]))
        elif not expect_zero:
            print("      refusal:", refusal(p).strip()[:160])

    if os.environ.get("RELGATE_STAGE_ONLY"):
        print(f"\n{fails} harness failure(s) [stage-step cases only]")
        return 1 if fails else 0

    def publish_case(label, env, expect_zero, expect_published, note, bodies=None):
        nonlocal fails
        bd = bodies or b
        wd = make_fixture(label, "good")
        s = run_step(bd["stage"], wd, env, "stage")
        assert s.returncode == 0, msgs(s)
        p = run_step(bd["publish"], wd, env, "publish")
        log = gh_log(wd)
        try:
            published = open(os.path.join(wd, ".state", "draft"), encoding="utf-8").read().strip() == "false"
        except FileNotFoundError:
            published = False
        ok = (p.returncode == 0) == expect_zero and published == expect_published
        print(f"{'PASS' if ok else 'FAIL'}  publish: {label:<18} rc={p.returncode} published={published} want_published={int(expect_published)}  {note}")
        if not ok:
            fails += 1
            print("      ", " | ".join(msgs(p)[-3:]), "\n      gh:", log.replace("\n", " | ")[:300])
        return wd

    publish_case("pub_stable", None, True, True, "stable tag: no --prerelease")
    publish_case("pub_rc", {"TAG": "v0.2.0-rc1"}, True, True, "prerelease tag adds --prerelease")
    publish_case("pub_extra_asset", {"EXTRA_ASSET": "surprise.bin"}, False, False,
                 "remote draft holds an undeclared asset -> stop before un-draft")
    publish_case("pub_dup_asset", {"DUP_ASSET": "1"}, False, False,
                 "R5: the same asset name appears twice on the release -> not exactly one row, stop")
    publish_case("pub_no_upload_url", {"NO_UPLOAD_URL": "1"}, False, False,
                 "R5: create answered no upload_url -> stop before uploading anything")
    publish_case("pub_bad_upload_url", {"BAD_UPLOAD_URL": "1"}, False, False,
                 "R6: upload_url names another host than this repo and release id -> refuse to post binaries at it")
    publish_case("pub_created_wrong_tag", {"CREATED_TAG": "v0.0.9"}, False, False,
                 "R6: the create response carries a different tag -> that 2xx is not this run's release")
    publish_case("pub_created_published", {"CREATED_DRAFT": "false"}, False, False,
                 "R6: the create response says published, not draft -> stop")
    publish_case("pub_created_pre_mismatch", {"TAG": "v0.2.0-rc1", "CREATED_PRE": "false"}, False, False,
                 "R6: prerelease asked for true, response says false -> stop")
    publish_case("pub_release_rettagged", {"LATE_REL_TAG": "other-tag"}, False, False,
                 "R6: the release object stops carrying our tag between the two inspections -> the second one catches it")

    publish_case("pub_created_name_renamed", {"CREATED_NAME": "Nightly build"}, False, False,
                 "R7: create answered a title that is not the tag -> stop before uploading")
    publish_case("pub_url_no_template", {"URL_NO_TEMPLATE": "1"}, False, False,
                 "R7: upload_url is the right path minus the URI template -> the compare is exact, so stop")
    publish_case("pub_url_assets_extra", {"URL_EXTRA": "1"}, False, False,
                 "R7: upload_url ends assets-extra -> the v0.12.14 prefix match accepted it, exact compare refuses")
    publish_case("pub_url_extra_query", {"URL_QUERY": "1"}, False, False,
                 "R7: upload_url carries a query outside the template -> refuse to post binaries at it")
    publish_case("pub_url_case_folded", {"URL_CASE_UPPER": "1"}, True, True,
                 "R7: owner/repo spelling differs in case -> canonical casing is still this repo+id, so upload proceeds")
    publish_case("pub_release_renamed", {"LATE_REL_NAME": "Nightly build"}, False, False,
                 "R7: the draft was retitled between the two inspections -> not publishing")
    publish_case("pub_release_readback_other_id", {"LATE_REL_ID": "777"}, False, False,
                 "R7: the second read-back answers a different release id -> not publishing")
    publish_case("pub_url_path_case", {"URL_CASE_PATH": "1"}, False, False,
                 "R8: owner/repo casing is canonical and folded, but /Assets from the id on is not what this gate asked for -> exact compare refuses")
    wdi = make_fixture("pub_created_name_refused", "good")
    si = run_step(b["stage"], wdi, {"CREATED_NAME": "::warning::pwned"}, "stage")
    assert si.returncode == 0, msgs(si)
    pi = run_step(b["publish"], wdi, {"CREATED_NAME": "::warning::pwned"}, "publish")
    outw = pi.stdout + pi.stderr
    ok = "::warning::pwned" not in outw and "%3A%3Awarning%3A%3Apwned" in outw
    print(f"{'PASS' if ok else 'FAIL'}  publish: pub_log_injection      raw_command={int('::warning::pwned' in outw)} escaped={int('%3A%3Awarning%3A%3Apwned' in outw)}  the response's own text reaches the log only escaped, so it cannot speak as a runner command")
    if not ok:
        fails += 1
        print("      ", " | ".join([L for L in outw.splitlines() if "pwned" in L])[:300])
    publish_case("pub_patch_answers_draft", {"PATCH_JSON": '{"id":12345,"tag_name":"v0.1.0","name":"v0.1.0","draft":true,"prerelease":false}'}, False, True,
                 "R8: the un-draft PATCH answered something that is not this release published -> stop, and the release did go public: the message may not imply otherwise")
    publish_case("pub_upload_fails", {"UPLOAD_FAILS": "1"}, False, False,
                 "R5: an upload call dies -> stop, never un-draft, leave the draft for cleanup")
    publish_case("pub_upload_no_id", {"UPLOAD_NO_ID": "1"}, False, False,
                 "R5: upload answered no asset id -> stop")
    publish_case("pub_tag_moved_late", {"MOVED_TAG_SHA": "c" * 40, "LATE_TAG_MOVE": "1"}, False, False,
                 "R5: tag moves after the first resolve -> the pre-PATCH re-resolve catches it")

    # R5: this run never asks for a release by tag, because a fresh draft is not findable that way.
    wd = make_fixture("pub_no_tag_lookup", "good")
    s = run_step(b["stage"], wd, None, "stage"); assert s.returncode == 0, msgs(s)
    p = run_step(b["publish"], wd, None, "publish")
    log = gh_log(wd)
    state_reads = sum(1 for L in log.splitlines()
                      if re.fullmatch(r"gh api repos/\{owner\}/\{repo\}/releases/\d+", L))
    create_line = [L for L in log.splitlines() if "-X POST repos/{owner}/{repo}/releases " in L]
    ok = p.returncode == 0 and "releases/tags/" not in log and create_line \
        and "-f name=v0.1.0" in log and log.count("assets?per_page=100") == 2 and state_reads == 3 \
        and not any("--jq" in L for L in create_line)
    print(f"{'PASS' if ok else 'FAIL'}  publish: pub_no_tag_lookup   rc={p.returncode} by-tag_calls={'releases/tags/' in log} snapshot_calls={log.count('assets?per_page=100')} state_reads={state_reads} create_projected={any('--jq' in L for L in create_line)}  create answers with the id and the whole object; nothing looks a draft up by tag; the draft is inspected twice")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-3:]), "\n      gh:", log.replace("\n", " | ")[:300])
    publish_case("pub_state", {"ASSET_STATE": "error"}, False, False,
                 "asset never finished uploading -> stop before un-draft")
    publish_case("pub_size", {"SIZE_SHORT": "air-win-x64.tar.gz"}, False, False,
                 "remote asset one byte short -> stop before un-draft")
    publish_case("pub_digest", {"DIGEST_DIFF": "air-win-x64.tar.gz"}, False, False,
                 "R4: remote digest does not match the staged bytes -> stop before un-draft")
    publish_case("pub_digest_absent", {"DIGEST_NONE": "air-win-x64.tar.gz.sha256"}, False, False,
                 "R4: remote reports no digest -> unverifiable is not passing")
    publish_case("pub_tag_moved", {"MOVED_TAG_SHA": "b" * 40}, False, False,
                 "tag moved off the built commit -> refuse before create")

    # cleanup: only the release this run can prove it created is ever deleted. Model a run that
    # created the draft and then failed before un-drafting it, so the draft is still there.
    wd = make_fixture("cleanup_mine", "good")
    s = run_step(b["stage"], wd, None, "stage"); assert s.returncode == 0
    p = run_step(b["publish"], wd, {"SIZE_SHORT": "air-win-x64.tar.gz"}, "publish")
    assert p.returncode != 0, msgs(p)
    created = open(os.path.join(wd, ".state", "output"), encoding="utf-8").read().strip()
    p = run_step(b["cleanup"], wd, {"CREATED_ID": created.splitlines()[-1].split("=")[1]}, "cleanup")
    log = gh_log(wd)
    ok = "DELETED" in log and p.returncode == 0
    print(f"{'PASS' if ok else 'FAIL'}  cleanup: this run's draft (id {created.splitlines()[-1].split('=')[1]}) deleted={int('DELETED' in log)}")
    fails += 0 if ok else 1
    if not ok:
        print("      ", " | ".join(msgs(p)[-2:]))

    for label, created, reljson, expect_delete, note in [
        ("cleanup_no_id", "", "", False, "create returned nothing -> touch nothing (P1 fix)"),
        ("cleanup_foreign", "999", '{"id":12345,"tag_name":"v0.1.0","draft":true}', False, "looked-up id is not ours -> leave it"),
        ("cleanup_published", "12345", '{"id":12345,"tag_name":"v0.1.0","draft":false}', False, "already published -> leave it"),
        ("cleanup_not_draft", "12345", '{"id":12345,"tag_name":"v0.1.0","draft":"true"}', False,
         "R8: draft is the string \"true\", not a boolean -> not provably a draft, leave it"),
        ("cleanup_lie", "12345", '{"id":12345,"tag_name":"v0.1.0 true","draft":null}', False,
         "R8: tag_name carries the draft column and the draft is null -> a joined projection would read this as our own draft; leave it"),
        ("cleanup_gone", "12345", "404", False, "release is gone -> leave it"),
    ]:
        wd = make_fixture(label, "good")
        env = {"CREATED_ID": created, "REL_JSON": reljson} if reljson != "404" else {"CREATED_ID": created, "REL_404": "1"}
        p = run_step(b["cleanup"], wd, env, "cleanup")
        log = gh_log(wd)
        deleted = "DELETED" in log
        ok = deleted == expect_delete and p.returncode == 0
        print(f"{'PASS' if ok else 'FAIL'}  cleanup: {label:<18} deleted={int(deleted)} want={int(expect_delete)}  {note}")
        if not ok:
            fails += 1
            print("      ", " | ".join(msgs(p)[-3:]))

    # create itself fails (the API refuses a tag that already carries a release): the step dies at
    # the call, publishes nothing, and cleanup is left with an attempt marker and no id
    publish_case("pub_create_fails", {"CREATE_FAILS": "1"}, False, False,
                 "create refused -> stop, never un-draft")
    wd = make_fixture("cleanup_attempted_no_id", "good")
    s = run_step(b["stage"], wd, None, "stage"); assert s.returncode == 0
    p = run_step(b["publish"], wd, {"CREATE_FAILS": "1"}, "publish")
    assert p.returncode != 0, msgs(p)
    out = open(os.path.join(wd, ".state", "output"), encoding="utf-8").read()
    p = run_step(b["cleanup"], wd, {"CREATED_ID": "", "CREATE_ATTEMPTED": "1" if "create_attempted=1" in out else "0"}, "cleanup")
    log = gh_log(wd)
    warned = "manual review" in (p.stdout + p.stderr).lower()
    ok = "DELETED" not in log and warned and p.returncode == 0 and "create_attempted=1" in out
    print(f"{'PASS' if ok else 'FAIL'}  cleanup: attempted_no_id   deleted=0 warned={int(warned)}  create attempted, no id -> loud manual-intervention note, nothing deleted")
    if not ok:
        fails += 1
        print("      output:", " | ".join(msgs(p)[-3:]), "\n      outputs:", out.strip().replace("\n", " | "))

    # The by-tag lookup that cd4fcdd shipped is not a draft-visible call at all: the stub refuses it,
    # so that revision's publish step is shown failing against the documented API rather than green.
    old5 = bodies_at("cd4fcdd")
    wd = make_fixture("pub_old_bytag_lookup", "good")
    s = run_step(old5["stage"], wd, None, "stage"); assert s.returncode == 0, msgs(s)
    p = run_step(old5["publish"], wd, None, "publish")
    log = gh_log(wd)
    ok = p.returncode != 0 and "releases/tags/" in log and "release_id=" not in open(os.path.join(wd, ".state", "output"), encoding="utf-8").read()
    print(f"{'PASS' if ok else 'FAIL'}  publish: pub_old_bytag_lookup rc={p.returncode}  cd4fcdd could never publish: its id came from a published-release-only endpoint")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-3:]), "\n      gh:", log.replace("\n", " | ")[:300])

    # R8 differential: four server answers where a jq join(" ") projection and a shell `read` invent
    # field boundaries that the response never had. Each pair runs the same forged answer through the
    # revision that read it that way (c6c73f7) and through this one; publishing / deleting under the
    # old reading and refusing under the new one is the hole being demonstrated, not an assumption.
    prev = bodies_at("c6c73f7")
    publish_case("prev_green", None, True, True, "c6c73f7 still publishes under the JSON-answering stub", bodies=prev)
    for label, env, old_zero, old_pub, note in [
        ("hole_created_name_ws", {"CREATED_NAME_WS": "1", "CREATED_DRAFT_NULL": "1"}, True, True,
         'create answered name "v0.1.0 true" with no draft field: joined and re-split, it satisfied tag, name, draft and prerelease'),
        ("hole_asset_forged_name", {"FAKE_NAMES": "1"}, True, True,
         "an asset named \"<wanted> uploaded <size> <digest>\" carries its own state, size and digest into the columns the snapshot read"),
        ("hole_readback_draft_string",
         {"REL_JSON": '{"id":12345,"tag_name":"v0.1.0","name":"v0.1.0","draft":"true","prerelease":false}'}, False, True,
         'the draft field is the string "true": a joined projection cannot tell it from boolean true, so c6c73f7 un-drafted it and only noticed at the read-back after going public'),
    ]:
        publish_case(label + "@c6c73f7", env, old_zero, old_pub, "OLD reading published it: " + note, bodies=prev)
        publish_case(label, env, False, False, "R8: " + note + " -> this revision refuses before uploading, or before un-drafting")

    for label, created, reljson, note in [
        ("hole_cleanup_lie", "12345", '{"id":12345,"tag_name":"v0.1.0 true","draft":null}',
         'a published release answered with tag_name "v0.1.0 true" and no draft read, column by column, as this run\'s own draft'),
    ]:
        for bd2, expect_delete, tag in ((prev, True, "OLD reading deletes"), (b, False, "R8 refuses")):
            wd = make_fixture(label, "good")
            p = run_step(bd2["cleanup"], wd, {"CREATED_ID": created, "REL_JSON": reljson}, "cleanup")
            deleted = "DELETED" in gh_log(wd)
            ok = deleted == expect_delete and p.returncode == 0
            print(f"{'PASS' if ok else 'FAIL'}  cleanup: {label} {tag:<20} deleted={int(deleted)} want={int(expect_delete)}  {note}")
            if not ok:
                fails += 1
                print("      ", " | ".join(msgs(p)[-3:]))

    # The gh contract the stub now enforces, and the hole it closed: the shipped publish step of
    # 6b91de4 asked `gh release create … --json id --jq .id`, which gh 2.46.0 refuses at argument
    # parsing ("unknown flag: --json", exit 1) before it reaches the API. Nothing was ever published
    # by that step, and nothing was created, so no orphan either. Pinned to the sha so the mistake
    # stays reproducible instead of remembered.
    for label, expect_zero, note, env in [
        ("contract_unknown_flag", False, "stub refuses what gh refuses: create --json id", None),
    ]:
        wd = make_fixture(label, "good")
        body = 'set -euo pipefail\ngh release create "$TAG" --draft --json id --jq .id\necho "UNREACHABLE"\n'
        p = run_step(body, wd, None, "contract")
        ok = p.returncode != 0 and "unknown flag: --json" in (p.stdout + p.stderr) and "UNREACHABLE" not in p.stdout
        print(f"{'PASS' if ok else 'FAIL'}  gh contract: {label:<18} rc={p.returncode}  {note}")
        if not ok:
            fails += 1
            print("      ", " | ".join(msgs(p)[-3:]))

    old = bodies_at("6b91de4")
    wd = make_fixture("pub_old_cli_contract", "good")
    s = run_step(old["stage"], wd, None, "stage")
    assert s.returncode == 0, msgs(s)
    p = run_step(old["publish"], wd, None, "publish")
    log = gh_log(wd)
    ok = p.returncode != 0 and "unknown flag: --json" in (p.stdout + p.stderr) and "release_id=" not in log
    print(f"{'PASS' if ok else 'FAIL'}  publish: pub_old_cli_contract rc={p.returncode}  6b91de4 publish step cannot run against the real gh contract")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-3:]), "\n      gh:", log.replace("\n", " | ")[:200])

    # The digest finding is a hole in this workflow, not a reviewer's generalization: the pre-fix
    # asset comparison — state and size only — is run here in isolation, against a stored asset of
    # exactly the right size and the wrong bytes. Accepted (rc=0) is the failure being demonstrated.
    old_assets = old["publish"].split("for n in $want_assets; do", 1)[1].split("\ndone", 1)[0]
    frag = ("set -euo pipefail\n"
            'want_assets="air-linux-x64.tar.gz air-win-x64.tar.gz air-linux-x64.tar.gz.sha256 '
            'air-win-x64.tar.gz.sha256 SHA256SUMS"\nrel_id=12345\n'
            "for n in $want_assets; do" + old_assets + "\ndone\n")
    wd = make_fixture("remote_equal_size_swap", "good")
    s = run_step(b["stage"], wd, None, "stage")
    assert s.returncode == 0, msgs(s)
    with open(os.path.join(wd, ".state", "uploaded"), "w", encoding="utf-8") as fh:
        fh.write("air-linux-x64.tar.gz\nair-win-x64.tar.gz\nair-linux-x64.tar.gz.sha256\nair-win-x64.tar.gz.sha256\nSHA256SUMS\n")
    p = run_step(frag, wd, {"DIGEST_DIFF": "air-win-x64.tar.gz"}, "assetloop")
    ok = p.returncode == 0
    print(f"{'PASS' if ok else 'FAIL'}  hole: remote_equal_size_swap  rc={p.returncode} want_zero=1  6b91de4 asset loop accepted a stored asset of the right size and the wrong bytes")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-3:]))

    # preflight: an existing release of any kind blocks the build, across pages
    for label, flaglist, expect_zero, note in [
        ("pre_absent", "", True, "no release for the tag"),
        ("pre_draft", "true", False, "leftover draft blocks"),
        ("pre_published", "false", False, "published release blocks"),
        ("pre_pages", "false true", False, "match on a later page still blocks"),
        ("pre_two", "true false", False, "two releases on one tag reported, not silently released"),
    ]:
        wd = make_fixture(label, "good")
        p = run_step(b["preflight_release_check"], wd, {"PREFLIGHT_FLAGS": flaglist}, "preflight")
        ok = (p.returncode == 0) == expect_zero
        print(f"{'PASS' if ok else 'FAIL'}  preflight: {label:<14} rc={p.returncode} want_zero={int(expect_zero)}  {note}")
        if not ok:
            fails += 1
            print("      ", " | ".join(msgs(p)[-3:]))

    # ── the eighth deep review (over fd76021): whole-string regex anchors, one JSON document per
    # answer, a canonical integer id, a preflight read that fails closed, and a sanitizer that keeps
    # control characters out of the log. Differential pairs run the same forged answer through
    # fd76021 — the revision that read it the loose way — and through the working tree.
    prev8 = bodies_at("fd76021")
    publish_case("prev8_green", None, True, True, "fd76021 still publishes under this stub", bodies=prev8)

    # `jq -e` reports the status of its LAST result, so an answer of two JSON documents is judged by
    # the last one. These sites read the answer as a predicate only — no field is extracted from it —
    # so the earlier revision acted on document two and never looked at document one.
    readback_two = ('{"id":12345,"tag_name":"someone-elses-tag","name":"Nightly","draft":false,"prerelease":false}\n'
                    '{"id":12345,"tag_name":"v0.1.0","name":"v0.1.0","draft":true,"prerelease":false}')
    for label, env, old_zero, old_pub, note in [
        ("hole_readback_two_docs", {"REL_JSON": readback_two}, False, True,
         'the read-back answers two documents: the first is a published release on another tag, the second is our draft'),
        ("hole_patch_two_docs", {"PATCH_JSON": '{"id":12345,"tag_name":"v0.1.0","draft":true,"prerelease":false}\n'
                                                '{"id":12345,"tag_name":"v0.1.0","draft":false,"prerelease":false}'},
         True, True, "the un-draft PATCH answers two documents and only the last one says published"),
    ]:
        publish_case(label + "@fd76021", env, old_zero, old_pub, "OLD reading: " + note, bodies=prev8)
        publish_case(label, env, False, int(label == "hole_patch_two_docs"),
                     "eighth review: " + note + " -> this revision requires exactly one document")

    # the destructive version of the same thing: cleanup deletes on the last document it read
    for bd2, expect_delete, tag in ((prev8, True, "OLD reading deletes"), (b, False, "eighth review refuses")):
        wd = make_fixture("hole_cleanup_two_docs", "good")
        p = run_step(bd2["cleanup"], wd, {"CREATED_ID": "12345",
                                          "REL_JSON": '{"id":12345,"tag_name":"v0.1.0","draft":false}\n'
                                                      '{"id":12345,"tag_name":"v0.1.0","draft":true}'}, "cleanup")
        deleted = "DELETED" in gh_log(wd)
        ok = deleted == expect_delete and p.returncode == 0
        print(f"{'PASS' if ok else 'FAIL'}  cleanup: hole_cleanup_two_docs {tag:<20} deleted={int(deleted)} want={int(expect_delete)}  a GET that answers two documents: the first says the release is published, the last says it is a draft")
        if not ok:
            fails += 1
            print("      ", " | ".join(msgs(p)[-3:]))

    # whole-string anchors: in Oniguruma `$` also matches before a final newline, so "uploaded\n" and
    # "air-win-x64.tar.gz\n" are clean under ^...$ — and the snapshot is one physical line per asset.
    # Neither revision publishes these: the earlier one caught them at the name inventory instead of at
    # hygiene, which is why these are pattern cases and not differential pairs.
    publish_case("pub_asset_name_newline", {"ASSET_NAME_NL": "air-win-x64.tar.gz"}, False, False,
                 "eighth review: an asset name ending in a newline passes ^...$ in jq's regex engine; \\A..\\z refuses it")
    publish_case("pub_asset_state_newline", {"ASSET_STATE_NL": "1"}, False, False,
                 "eighth review: state \"uploaded\\n\" is not a whole string under ^[A-Za-z]+$")
    publish_case("pub_asset_digest_newline", {"ASSET_DIGEST_NL": "1"}, False, False,
                 "eighth review: a digest with a trailing newline is not ^sha256:<64 hex>$")
    for label, env, note in [
        ("pub_assets_two_docs", {"ASSETS_EXTRA_DOC": '[{"name":"junk.bin","state":"uploaded","size":1,"digest":null,"id":9500}]'},
         "the asset endpoint answers two documents -> that is not one snapshot of one release"),
        ("pub_create_two_docs", {"CREATE_EXTRA_DOC": '{"id":999,"upload_url":null,"tag_name":"x","name":"x","draft":true,"prerelease":false}'},
         "the create response answers two documents -> the last one is not the whole answer"),
        ("pub_create_fractional_id", {"CREATED_ID_RAW": "12345.5"},
         "id 12345.5 is a number, and an id is used to build a URL and a compare target"),
    ]:
        publish_case(label, env, False, False, "eighth review: " + note)

    # the ceiling: the inventory is one page of 100 required to be exactly the declared count, so a
    # declared set that fills the page is refused before anything is created
    wd = make_fixture("pub_asset_ceiling", "good")
    p = run_step(b["publish"], wd, {"EXPECTED_RIDS": " ".join(f"linux-x64-{i}" for i in range(50))}, "publish")
    ok = p.returncode != 0 and "paginate" in (p.stdout + p.stderr) and gh_log(wd) == ""
    print(f"{'PASS' if ok else 'FAIL'}  publish: pub_asset_ceiling   rc={p.returncode} gh_calls={len(gh_log(wd).splitlines())}  101 declared assets would make a one-page inventory incomplete -> refuse before creating")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-3:]))

    # the sanitizer: control characters, ANSI escapes and a bidi override from a response must not reach
    # the log as themselves, and the 200-character cut must not emit broken UTF-8
    wd = make_fixture("pub_log_control", "good")
    nasty = "\x1b[31mred\x1b[0m\u202e" + "caf\u00e9 " * 80
    s = run_step(b["stage"], wd, {"CREATED_NAME": nasty}, "stage")
    assert s.returncode == 0, msgs(s)
    p = run_step(b["publish"], wd, {"CREATED_NAME": nasty}, "publish")
    outw = p.stdout + p.stderr
    seg = outw.split("(fields: ", 1)[1].split(")", 1)[0] if "(fields: " in outw else ""
    ok = (p.returncode != 0 and "\x1b" not in outw
          and ("?[31mred?[0m" in outw or "\\u001b[31mred" in outw)
          and seg.isascii() and "caf\u00e9" not in outw and "\u202e" not in outw and "red" in outw)
    print(f"{'PASS' if ok else 'FAIL'}  publish: pub_log_control      esc_bytes={int(chr(27) in outw)} encoded={int('?31mred?' in outw)} field_text_ascii={int(seg.isascii())}  a response's control characters and non-ASCII reach the log only as ? or as a visible JSON escape, never raw")
    if not ok:
        fails += 1
        print("      ", " | ".join(outw.splitlines()[-3:])[:300])

    # a preflight that cannot read the release list is a refusal, not an absence: GitHub lets two
    # releases carry one tag, so the earlier revision failing open here meant a transient 5xx could
    # put a second release on a published tag.
    for bd2, expect_zero, tag in ((prev8, True, "OLD failed open: an unreadable list read as no existing release"),
                                  (b, False, "eighth review: an unreadable list stops the build")):
        wd = make_fixture("pre_read_fails", "good")
        p = run_step(bd2["preflight_release_check"], wd, {"PREFLIGHT_FAIL": "1"}, "preflight")
        ok = (p.returncode == 0) == expect_zero
        print(f"{'PASS' if ok else 'FAIL'}  preflight: pre_read_fails {tag:<62} rc={p.returncode} want_zero={int(expect_zero)}")
        if not ok:
            fails += 1
            print("      ", " | ".join(msgs(p)[-3:]))

    # Ninth review over 05f224b. The release-list read is judged by gh's exit status and its shape is
    # checked before the tag is selected. The previous revision ran that read through `2>/dev/null ||
    # true`, so anything gh printed with a non-zero status - a page that failed mid-walk, an error body,
    # a junk element - became "no existing release for $TAG", and GitHub lets two releases carry one
    # tag. Differential pairs run 05f224b itself and show it failing open.
    prev9 = bodies_at("05f224b")
    pre_cases = [
        ("pre_partial_page", {"PREFLIGHT_PARTIAL": "1"},
         "gh exits 1 after printing the pages it fetched: a half-read list is not an absence",
         True, "gh exited"),
        ("pre_errdoc_body", {"PREFLIGHT_ERRDOC": "1"},
         "a non-array body is refused, not filtered away as no-match", True, "not a JSON array"),
        # the junk element was already caught by the previous revision, but by accident: jq errored on
        # indexing a string, so the read failed to parse rather than being checked. The new shape rule
        # refuses it as a shape, and the message says so.
        ("pre_junk_item", {"PREFLIGHT_BADITEM": "1"},
         "an array element that is not a release object refuses the whole read",
         False, "not a JSON array"),
    ]
    for name, env, why, old_zero, new_msg in pre_cases:
        for bd2, expect_zero, label in ((prev9, old_zero, "05f224b"), (b, False, "ninth review")):
            wd = make_fixture(name, "good")
            p = run_step(bd2["preflight_release_check"], wd, env, "preflight")
            out = p.stdout + p.stderr
            ok = (p.returncode == 0) == expect_zero and (bd2 is prev9 or new_msg in out)
            print(f"{'PASS' if ok else 'FAIL'}  preflight: {name:<19} {label:<12} rc={p.returncode} want_zero={int(expect_zero)} msg={new_msg if bd2 is not prev9 else '-':<17}  {why}")
            if not ok:
                fails += 1
                print("      ", " | ".join(msgs(p)[-3:]))

    # the existing-release annotation carries a server-chosen id. Printed with jq -r, a string id
    # whose JSON value contains a newline opens its own workflow command on a second line; jq -c keeps
    # it inside one JSON string. Both revisions refuse - the difference is what the log looks like.
    forged = '[{"id":"7\\n::error::pwned","tag_name":"v0.1.0","draft":true}]'
    for bd2, want_lines, label in ((prev9, 2, "05f224b emits the injected annotation"),
                                   (b, 1, "ninth review keeps it as escaped JSON")):
        wd = make_fixture("pre_id_annotation", "good")
        p = run_step(bd2["preflight_release_check"], wd, {"PREFLIGHT_PAGES": forged}, "preflight")
        out = p.stdout + p.stderr
        n_lines = sum(1 for ln in out.splitlines() if ln.lstrip().startswith("::error::"))
        ok = p.returncode != 0 and n_lines == want_lines
        print(f"{'PASS' if ok else 'FAIL'}  preflight: pre_id_annotation     {label:<34} rc={p.returncode} error_lines={n_lines} want={want_lines}  a server-chosen id cannot open a second workflow command")
        if not ok:
            fails += 1
            print("      ", " | ".join(msgs(p)[-3:]))

    # The create answer read as JSON fields rather than as shell variables. jq -r prints the JSON number
    # 1.0e2 as 1.0E+2, and a command substitution strips a trailing newline, so a compare done on projected
    # values can agree while the field itself disagrees. Each pair runs 05f224b and shows what it did.
    # The exponent id is NOT a demonstrated bypass: 05f224b also stopped, but only at the read-back, after
    # five uploads addressed from the non-canonical spelling. The new check refuses before any upload.
    wd = publish_case("pub_id_exponent_old", {"CREATED_ID_RAW": "1.0e2"}, False, False,
                      "05f224b: stopped late, at the read-back, having already uploaded five assets",
                      bodies=prev9)
    odd_uploads = sum(1 for ln in gh_log(wd).splitlines() if "1.0E+2" in ln and "?name=" in ln)
    ok_old = odd_uploads == 5
    print(f"{'PASS' if ok_old else 'FAIL'}  publish: {'pub_id_exponent_old':<18} odd_address_uploads={odd_uploads} want=5  05f224b posted every asset at an address spelled from a non-canonical id before refusing")
    if not ok_old:
        fails += 1
    wd = publish_case("pub_id_exponent", {"CREATED_ID_RAW": "1.0e2"}, False, False,
                      "ninth review: a canonical-integer test on the printed form refuses before any upload")
    odd_uploads = sum(1 for ln in gh_log(wd).splitlines() if "1.0E+2" in ln and "?name=" in ln)
    ok_new = odd_uploads == 0
    print(f"{'PASS' if ok_new else 'FAIL'}  publish: {'pub_id_exponent':<18} odd_address_uploads={odd_uploads} want=0  nothing is posted at a non-canonical address")
    if not ok_new:
        fails += 1
    publish_case("pub_url_trailing_lf_old", {"URL_TRAILING_LF": "1"}, True, True,
                 "05f224b: upload_url ends in a newline; jq -r strips it, so the shell compare saw an exact match and published",
                 bodies=prev9)
    publish_case("pub_url_trailing_lf", {"URL_TRAILING_LF": "1"}, False, False,
                 "ninth review: the address compare runs inside jq, where the newline is part of the field")

    # the stderr of a failed read is a diagnostic, not a value: a credential on it must not reach the log
    wd = make_fixture("pre_errtoken", "good")
    p = run_step(b["preflight_release_check"], wd, {"PREFLIGHT_ERRTOKEN": "1"}, "preflight")
    out = p.stdout + p.stderr
    secret = "ghp_" + "a" * 40
    ok = (p.returncode != 0 and secret not in out and "token=" not in out
          and "gh exited 1" in out and "502" in out
          and "(proxy)" not in out and "status-like numbers" in out)
    print(f"{'PASS' if ok else 'FAIL'}  preflight: pre_errtoken        rc={p.returncode} secret_in_log={int(secret in out)} text_from_stderr={int('token=' in out)} status_named={int('502' in out)} honest_label={int('status-like numbers' in out)} raw_stderr={int('(proxy)' in out)}  nothing gh wrote reaches the log: only the exit code and numbers that look like statuses")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-3:])[:300])

    # the upload target is built from repository context plus the validated id, not sliced from the
    # server's upload_url: an answer that spells owner/repo differently cannot route the request
    wd = publish_case("pub_url_case_folded", {"URL_CASE_UPPER": "1"}, True, True,
                      "R7: owner/repo spelling differs in case -> canonical casing is still this repo+id, so upload proceeds")
    log = gh_log(wd)
    ours = sum(1 for ln in log.splitlines() if "repos/o/r/releases/12345/assets?name=" in ln)
    theirs = sum(1 for ln in log.splitlines() if "repos/O/R" in ln)
    ok = ours == 5 and theirs == 0
    print(f"{'PASS' if ok else 'FAIL'}  publish: {'built_upload_target':<18} our_target_uploads={ours} want=5 server_spelling_uploads={theirs} want=0  the request target comes from this step, the server's address is only checked")
    if not ok:
        fails += 1
        print("      gh:", log.replace("\n", " | ")[:300])

    # the diagnostic keeps its fields when the server's address is long: bounded per field, not one joined
    # string that a long value pushes the rest out of
    wd = make_fixture("pub_long_url", "good")
    s = run_step(b["stage"], wd, {"LONG_URL": "1"}, "stage")
    assert s.returncode == 0, msgs(s)
    p = run_step(b["publish"], wd, {"LONG_URL": "1"}, "publish")
    out = p.stdout + p.stderr
    ok = (p.returncode != 0 and "id=12345" in out and "draft=true" in out and "upload_url=" in out
          and "ch)" in out and "p" * 100 not in out and "q" * 10 in out)
    print(f"{'PASS' if ok else 'FAIL'}  publish: {'diag_fields_survive':<18} rc={p.returncode} id={int('id=12345' in out)} draft={int('draft=true' in out)} cap_and_len={int('ch)' in out)} tail_shown={int('q' * 10 in out)}  every field survives the 200-character cut, and a long address shows its end too")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-2:])[:300])

    # differential pair: the same fixture against the previous revision, which cut the address after the
    # head only, so the part of the address that identifies the release was the part that disappeared
    prev12 = bodies_at("8f78a73")
    wd = make_fixture("pub_long_url_prev", "good")
    s = run_step(prev12["stage"], wd, {"LONG_URL": "1"}, "stage")
    assert s.returncode == 0, msgs(s)
    p = run_step(prev12["publish"], wd, {"LONG_URL": "1"}, "publish")
    out = p.stdout + p.stderr
    ok = p.returncode != 0 and "upload_url=" in out and "q" * 10 not in out
    print(f"{'PASS' if ok else 'FAIL'}  publish: {'long_url_prev':<18} rc={p.returncode} tail_shown={int('q' * 10 in out)}  8f78a73 showed only the head of a long address: the distinguishing end was cut")
    if not ok:
        fails += 1

    # a container field is named as a container: an object is not printed with its contents, however long
    wd = make_fixture("pub_url_object", "good")
    s = run_step(b["stage"], wd, {"URL_WRONG_TYPE": "2"}, "stage")
    assert s.returncode == 0, msgs(s)
    p = run_step(b["publish"], wd, {"URL_WRONG_TYPE": "2"}, "publish")
    out = p.stdout + p.stderr
    ok = (p.returncode != 0 and "upload_url=<object>" in out and "z" * 40 not in out
          and "id=12345" in out and "draft=true" in out)
    print(f"{'PASS' if ok else 'FAIL'}  publish: {'url_object_field':<18} rc={p.returncode} as_object={int('upload_url=<object>' in out)} contents_leaked={int('z' * 40 in out)}  an object-valued field is named, not dumped")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-2:])[:300])

    # differential pair: 8f78a73 printed containers with tojson and no cap, so a long string nested in one
    # walked into the log up to the outer 200-character cut
    wd = make_fixture("pub_url_object_prev", "good")
    s = run_step(prev12["stage"], wd, {"URL_WRONG_TYPE": "2"}, "stage")
    assert s.returncode == 0, msgs(s)
    p = run_step(prev12["publish"], wd, {"URL_WRONG_TYPE": "2"}, "publish")
    out = p.stdout + p.stderr
    ok = p.returncode != 0 and "z" * 40 in out
    print(f"{'PASS' if ok else 'FAIL'}  publish: {'url_object_prev':<18} rc={p.returncode} contents_leaked={int('z' * 40 in out)}  8f78a73 printed a container's contents uncapped: the finding was real")
    if not ok:
        fails += 1

    # the marker is presentation: a short value that already spells "+(999ch)" passes through as text and
    # is not treated as truncated, so the count is ours but the shape is not a proof about the value
    wd = make_fixture("pub_name_forged_cap", "good")
    s = run_step(b["stage"], wd, {"CREATED_NAME_FORGED_CAP": "1"}, "stage")
    assert s.returncode == 0, msgs(s)
    p = run_step(b["publish"], wd, {"CREATED_NAME_FORGED_CAP": "1"}, "publish")
    out = p.stdout + p.stderr
    ok = p.returncode != 0 and "+(999ch)b" in out and "upload_url=" in out
    print(f"{'PASS' if ok else 'FAIL'}  publish: {'name_forged_cap':<18} rc={p.returncode} marker_verbatim={int('+(999ch)b' in out)} fields={int('upload_url=' in out)}  a value can spell a truncation marker: the summary is a diagnostic, not a proof")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-2:])[:300])
    # a 300-character title must not push the flags out of the summary either
    wd = make_fixture("pub_name_long", "good")
    s = run_step(b["stage"], wd, {"CREATED_NAME_LONG": "1"}, "stage")
    assert s.returncode == 0, msgs(s)
    p = run_step(b["publish"], wd, {"CREATED_NAME_LONG": "1"}, "publish")
    out = p.stdout + p.stderr
    ok = (p.returncode != 0 and "draft=true" in out and "pre=false" in out and "upload_url=" in out
          and "y" * 200 not in out)
    print(f"{'PASS' if ok else 'FAIL'}  publish: {'name_crowding':<18} rc={p.returncode} draft={int('draft=true' in out)} pre={int('pre=false' in out)} url={int('upload_url=' in out)}  a 300-character title cannot hide the flags behind it")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-2:])[:300])

    # The declared line bound must never bite: with a long tag AND a long upload address, every label
    # and the END of the address still reach the log, and no marker carries the length of the whole
    # line (that would mean brief() cut the line rather than a field). Escaping still applies, so the
    # segment is ASCII and every colon is %3A.
    long_tag = "v0.1.0" + "T" * 94                                  # 100 characters
    long_url = ("https://uploads.github.com/repos/o/r/releases/12345/assets"
                + "u" * 34 + "-TAILMARK")                          # 103 characters
    raw_long = ('{"id":12345,"tag_name":"%s","name":"v0.1.0","draft":true,"prerelease":false,'
                '"upload_url":"%s"}') % (long_tag, long_url)
    wd = make_fixture("pub_line_bound", "good")
    p = run_step(b["publish"], wd, {"CREATE_BODY_RAW": raw_long}, "publish")
    out = p.stdout + p.stderr
    seg = out.split("(fields: ", 1)[1].split(") \u2014 not publishing", 1)[0] if "(fields: " in out else ""
    marks = [int(m) for m in re.findall(r"\+\((\d+)ch\)+", seg)]
    ok = (p.returncode != 0 and all(lbl in seg for lbl in
          ("id=", " tag=", " name=", " draft=", " pre=", " upload_url="))
          and "TAILMARK" in seg and seg.isascii() and "%3A" in seg
          and bool(marks) and max(marks) <= 103)
    print(f"{'PASS' if ok else 'FAIL'}  publish: {'line_bound_tail':<18} rc={p.returncode} labels={int(all(l in seg for l in ('id=',' tag=',' name=',' draft=',' pre=',' upload_url=')))} tail={int('TAILMARK' in seg)} maxmark={max(marks) if marks else 0}  a long tag and a long address cannot erase the field that explains the refusal")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-2:])[:300])

    # Type and presence are visible: the string "false" is quoted and a boolean is not, an absent field
    # says <missing>, a null says <null>, and a value that happens to spell "absent" is quoted so it
    # cannot be read as a missing field.
    for raw, want, label in (
        ('{"id":12345,"tag_name":"v0.1.0","name":"v0.1.0","draft":"false","prerelease":false,'
         '"upload_url":"https://uploads.github.com/repos/o/r/releases/12345/assets{?name,label}"}',
         'draft="false"', "a string flag is quoted where a boolean is not"),
        ('{"id":12345,"tag_name":"v0.1.0","name":"v0.1.0","draft":false,"prerelease":false}',
         'draft=false pre=false upload_url=<missing>', "an absent field says <missing>"),
        ('{"id":12345,"tag_name":"v0.1.0","name":"absent","draft":null,"prerelease":false}',
         'name="absent" draft=<null>', "a value that spells absent is quoted; a null says <null>"),
    ):
        wd = make_fixture("pub_type_presence", "good")
        p = run_step(b["publish"], wd, {"CREATE_BODY_RAW": raw}, "publish")
        out = p.stdout + p.stderr
        seg = out.split("(fields: ", 1)[1].split(") \u2014 not publishing", 1)[0] if "(fields: " in out else ""
        ok = p.returncode != 0 and want in seg
        print(f"{'PASS' if ok else 'FAIL'}  publish: {'type_presence':<18} want={want!r:<42} rc={p.returncode}  {label}")
        if not ok:
            fails += 1
            print("      ", " | ".join(msgs(p)[-2:])[:300])

    # Which numbers the diagnostic may call a status: HTTP/2 500 is one, and HTTP/2 5000, 500ms and
    # 4040 are not. A wrong census sends a human hunting for a status that was never returned.
    for err, want_codes in (("curl: (22) The requested URL returned error: HTTP/2 500", "500"),
                            ("note: HTTP/2 5000 and 500ms and 4040 in one line", "none")):
        wd = make_fixture("pre_http2_status", "good")
        p = run_step(b["preflight_release_check"], wd, {"PREFLIGHT_STDERR": err}, "preflight")
        out = p.stdout + p.stderr
        ok = p.returncode != 0 and ("mentioned in stderr: " + want_codes) in out
        print(f"{'PASS' if ok else 'FAIL'}  preflight: {'http2_status':<16} rc={p.returncode} census={want_codes!r:<6} text={err[:24]!r}  an HTTP/2 status line is read; a length or a longer number is not")
        if not ok:
            fails += 1
            print("      ", " | ".join(msgs(p)[-2:])[:300])

    # A refusal about an upload address names where the address first parts from the documented one.
    want_addr = "https://uploads.github.com/repos/o/r/releases/12345/assets{?name,label}"
    got_addr = want_addr[:70] + "x" + want_addr[71:]
    wd = make_fixture("pub_fdiff_offset", "good")
    raw_fdiff = ('{"id":12345,"tag_name":"v0.1.0","name":"v0.1.0","draft":true,"prerelease":false,'
                 '"upload_url":"%s"}') % got_addr
    p = run_step(b["publish"], wd, {"CREATE_BODY_RAW": raw_fdiff}, "publish")
    out = p.stdout + p.stderr
    ok = (p.returncode != 0 and "first difference at char 71" in out
          and "upload_url " in out and "not publishing" in out)
    print(f"{'PASS' if ok else 'FAIL'}  publish: {'fdiff_offset':<18} rc={p.returncode} offset={int('first difference at char 71' in out)}  an address that differs in the middle is reported by where it differs, not by a rendering")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-2:])[:300])

    # a wrong-typed address must not collapse the diagnostic: the fields that matched are the evidence
    wd = make_fixture("pub_url_wrong_type", "good")
    s = run_step(b["stage"], wd, {"URL_WRONG_TYPE": "1"}, "stage")
    assert s.returncode == 0, msgs(s)
    p = run_step(b["publish"], wd, {"URL_WRONG_TYPE": "1"}, "publish")
    out = p.stdout + p.stderr
    ok = (p.returncode != 0 and "id=12345" in out and "upload_url=42" in out
          and "unreadable response" not in out)
    print(f"{'PASS' if ok else 'FAIL'}  publish: {'url_wrong_type':<18} rc={p.returncode} id={int('id=12345' in out)} url_as_json={int('upload_url=42' in out)} collapsed={int('unreadable response' in out)}  a non-string field is printed as what it is, and the other fields survive")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-2:])[:300])

    # the six shape checks redirect stdout only, so jq's own stderr reaches the log: that is allowed on
    # purpose, and this pins why it is safe. jq names the offending value for typed operations only, to
    # ten characters ("SSSSSSSSSS...) number required), and every typed operation in these programs sits
    # behind a type guard, so the reachable errors are the indexing family, which names the index and
    # never the value. Refusal still fires, and the value text stays out of the log.
    wd = make_fixture("pub_shape_jq_stderr", "good")
    p = run_step(b["publish"], wd, {"CREATE_BODY_RAW": '["' + "S" * 40 + 'SECRETVALUE"]'}, "publish")
    out = p.stdout + p.stderr
    ok = (p.returncode != 0 and "S" * 4 not in out and "SECRETVALUE" not in out
          and "not publishing" in out)
    print(f"{'PASS' if ok else 'FAIL'}  publish: {'shape_jq_stderr':<18} rc={p.returncode} value_in_log={int('SSSS' in out)} refusal_text={int('not publishing' in out)}  a shape check that makes jq itself error prints no value text and still refuses")
    if not ok:
        fails += 1
        print("      ", " | ".join(msgs(p)[-2:])[:300], "| jq:", out.replace("\n", " | ")[:200])

    print(f"\n{fails} harness failure(s)")
    return 1 if fails else 0


if __name__ == "__main__":
    atexit.register(shutil.rmtree, ROOT, True)
    sys.exit(main())
