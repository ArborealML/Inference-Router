#!/usr/bin/env bash
set -euo pipefail

# This required check runs in the disposable hosted VM. It never uses a machine
# Docker endpoint or changes accounts, groups, weights, or production service files.
dotnet_path="$(command -v dotnet)"
scratch="$(mktemp -d /tmp/air-stock-proof-XXXXXX)"
trap 'rm -rf -- "$scratch"' EXIT
chmod 0755 "$scratch"
test_dir=tests/Arboreal.InferenceRouter.Tests/bin/Release/net11.0
publish_dir=bin/Release/net11.0/linux-x64/publish
cp "src/Arboreal.InferenceRouter.Cli/$publish_dir/air" "$scratch/"
for project in Server OperationHead SuccessorObserver; do
  base="src/Arboreal.InferenceRouter.$project"
  "$dotnet_path" publish "$base" -c Release -r linux-x64 -p:PublishAot=true -p:TrimmerSingleWarn=false -v minimal
done
cp "src/Arboreal.InferenceRouter.Server/$publish_dir/Arboreal.InferenceRouter.Server" "$scratch/"
cp "src/Arboreal.InferenceRouter.OperationHead/$publish_dir/air-operation-head" "$scratch/"
cp "src/Arboreal.InferenceRouter.SuccessorObserver/$publish_dir/air-successor-observer" "$scratch/"
cp "$test_dir/Arboreal.InferenceRouter.HostReservationProbe."{dll,deps.json,runtimeconfig.json} "$scratch/"
cp "$test_dir/Arboreal.InferenceRouter.dll" "$test_dir/CommissionedProcess/sockets.py" "$scratch/"
sha256sum "$scratch/"{air,Arboreal.InferenceRouter.Server,air-operation-head,air-successor-observer}
git rev-parse HEAD

sudo -n unshare --net -- bash -euc '
  umask 022
  ip link set lo up
  for uid in 1 2 3 4 65534; do getent passwd "$uid" >/dev/null; done
  export AIR_REQUIRE_STOCK_PROCESS_PROOF=1 AIR_STOCK_PROCESS_BINARIES="$2" DOTNET_HOST_PATH="$1"
  exec "$1" test tests/Arboreal.InferenceRouter.Tests -c Release --no-build \
    --filter "FullyQualifiedName~StockProcessRoutesTheOriginalQueuedSuccessor" \
    --logger "trx;LogFileName=stock-process.trx" --results-directory "$2"
' bash "$dotnet_path" "$scratch"

python3 - "$scratch/stock-process.trx" <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

path = Path(sys.argv[1])
results = ET.parse(path).findall(".//{*}UnitTestResult")
assert len(results) == 2, f"expected both stock process cases, got {len(results)}"
for result in results:
    assert "StockProcessRoutesTheOriginalQueuedSuccessor" in result.attrib["testName"]
    assert result.attrib["outcome"] == "Passed", result.attrib
    for output in result.findall(".//{*}StdOut"):
        if output.text:
            print(output.text)
PY

cat "$scratch/air.proof" "$scratch/standalone.proof"
