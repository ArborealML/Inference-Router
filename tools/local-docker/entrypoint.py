#!/usr/bin/env python3
"""Prepare writable runtime paths, then run the configured Python serving module."""
import argparse
import json
import os
from pathlib import Path
import sys


def prepare(config_path, environment):
    with open(config_path, encoding="utf-8") as source:
        config = json.load(source)
    directories = []
    if config.get("log"):
        directories.append(Path(config["log"]).parent)
    runtime_environment = dict(environment)
    runtime_environment.update(config.get("env") or {})
    for name in ("TMPDIR", "MAYA_DS4_SLOT_DIR"):
        if runtime_environment.get(name):
            directories.append(Path(runtime_environment[name]))
    for directory in directories:
        directory.mkdir(mode=0o700, parents=True, exist_ok=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--config", required=True)
    arguments, _ = parser.parse_known_args()
    os.umask(0o077)
    prepare(arguments.config, os.environ)
    os.execv(sys.executable, [sys.executable, "-m", "serve.server", *sys.argv[1:]])
