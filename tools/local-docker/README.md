# Python backend startup

Mount `entrypoint.py` read-only into a Python backend container and pass it to
that image's Python interpreter in place of `-m serve.server`. Preserve the
remaining server arguments, including `--config` and `--engine`.

Before serving, it creates the configured log parent, temporary directory and
Maya slot directory as the container's existing user. Fresh writable mounts need
no manual nested-directory setup. Repeated startup preserves existing files.
The configuration and credentials are unchanged. Directory failures stop startup.

Run `python3 tools/local-docker/test_entrypoint.py` for the file-only check.
