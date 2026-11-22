"""Install the AIR extension beside an existing Pi CLM launcher."""
import argparse
import ast
import os
from pathlib import Path
import tempfile

HERE = Path(__file__).resolve().parent


def replace(source, anchor, change):
    if source.count(anchor) != 1:
        raise ValueError(f"Unsupported launcher layout near {anchor.splitlines()[0]!r}")
    return source.replace(anchor, change)


def patch(source):
    if "from pi_air_launcher import router_provider" in source:
        return source
    start = source.index("def router_provider(path):")
    end = source.index("def main():", start)
    source = source[:start] + "from pi_air_launcher import router_provider\n\n\n" + source[end:]
    source = replace(source, "    subagents = data.setdefault", """    if backend == 'air':
        data.setdefault('retry', {})['enabled'] = False
        data['retry'].setdefault('provider', {})['maxRetries'] = 0
        data['defaultProvider'] = 'air'
        data['defaultModel'] = model_id
    subagents = data.setdefault""")
    source = replace(source, "    if key is not None:", """    if backend == 'air':
        env['PI_AIR_BASE_URL'] = base
    if key is not None:""")
    source = replace(source, "    os.execve(command[0], command, env)", """    if backend == 'air':
        # Pi's native session restoration takes precedence over defaults in settings.
        del command[command.index('--provider'):command.index('--model') + 2]
        command[1:1] = ['-e', str(ROOT / 'pi-air' / 'index.mjs')]
    os.execve(command[0], command, env)""")
    source = replace(source, "    with tempfile.NamedTemporaryFile(mode='w', dir=agent, delete=False) as handle:\n        json.dump(data,", """    if backend == 'air':
        ext = str(ROOT / 'pi-air' / 'index.mjs')
        if ext not in subagents['defaultExtensions']:
            subagents['defaultExtensions'].append(ext)
    with tempfile.NamedTemporaryFile(mode='w', dir=agent, delete=False) as handle:
        json.dump(data,""")
    ast.parse(source)
    return source


def install(path, data, mode):
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as handle:
        handle.write(data)
    os.chmod(handle.name, mode)
    os.replace(handle.name, path)


def extension_source():
    # Pi reloads the entry but can retain imported local modules in its process cache.
    # Embed the helper in the published entry so a single rename changes both.
    entry = (HERE / "index.mjs").read_text()
    lines = [line for line in entry.splitlines() if line.endswith('from "./catalog.mjs";')]
    if len(lines) != 1:
        raise ValueError("Expected one catalog import in the AIR extension")
    return replace(entry, lines[0], (HERE / "catalog.mjs").read_text()).encode()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("launcher", type=Path)
    args = parser.parse_args()
    launcher = args.launcher.resolve()
    source = patch(launcher.read_text())
    dest = launcher.parent / "pi-air"
    dest.mkdir(exist_ok=True)
    install(dest / "index.mjs", extension_source(), 0o644)
    install(launcher.parent / "pi_air_launcher.py", (HERE / "pi_air_launcher.py").read_bytes(), 0o644)
    install(launcher, source.encode(), launcher.stat().st_mode & 0o777)


if __name__ == "__main__":
    main()
