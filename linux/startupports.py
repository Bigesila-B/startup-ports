from pathlib import Path
import sys

from startup_ports.model import Store, login_launch, source_command


def main() -> int:
    command = source_command(Path(__file__))
    store = Store(command)
    args = sys.argv[1:]
    if args == ["--autostart"] or len(args) == 2 and args[0] == "--run-entry":
        try:
            return login_launch(store, args[1] if len(args) == 2 else "")
        except (OSError, ValueError) as exc:
            print(f"StartupPorts: {exc}", file=sys.stderr)
            return 1
    if args:
        print("Usage: StartupPorts", file=sys.stderr)
        return 2

    from startup_ports.ui import run

    return run(store)


if __name__ == "__main__":
    raise SystemExit(main())
