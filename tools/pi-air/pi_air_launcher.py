"""Resolve a local AIR listener and its default model without invoking inference."""
import json
import os
import re
import tomllib
import urllib.request

import air_provider


def router_provider(path):
    with open(path, "rb") as stream:
        config = json.load(stream) if str(path).lower().endswith(".json") else tomllib.load(stream)
    origin = config["listen"].rstrip("/")
    if not re.fullmatch(r"http://127\.0\.0\.1:[1-9][0-9]{0,4}", origin):
        raise ValueError("The Pi AIR launcher requires a loopback listener")
    key = os.environ.get("AIR_API_KEY")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), air_provider.RefuseRedirect())
    def snapshot(surface):
        request = urllib.request.Request(origin + "/admin/" + surface,
                                     headers={"Authorization": "Bearer " + key} if key else {})
        with opener.open(request, timeout=10) as response:
            return air_provider.read_json(response, 4 * 1024 * 1024)

    catalog = snapshot("catalog")
    rows = [model for engine in catalog["Engines"] for model in engine["Models"]]
    if not rows:
        raise ValueError("AIR has no configured models")
    default = catalog.get("DefaultModel")
    model = next((r for r in rows if r["Id"] == default or default in r["Aliases"]), rows[0])
    try:
        resident = {name for engine in snapshot("status")["Engines"] if engine["Healthy"]
                    for name in engine["LoadedModels"]}
    except (OSError, KeyError, ValueError):
        resident = set()
    if model["Id"] not in resident:
        model = next((r for r in rows if r["Id"] in resident), model)
    defaults = [p["MaxContext"] for r in rows if r["Id"] == model["Id"]
                for p in r["Profiles"] if p["Name"] == r["DefaultProfile"]]
    window = min(defaults, default=4096)
    return "air", origin + "/v1", key, model["Id"], window, min(8192, window // 4), {"providers": {}}
