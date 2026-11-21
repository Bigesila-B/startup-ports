from dataclasses import dataclass
import re


@dataclass(frozen=True)
class Port:
    protocol: str
    host: str
    number: int
    process: str = ""
    pid: int | None = None

    @property
    def address(self) -> str:
        host = f"[{self.host}]" if ":" in self.host else self.host
        return f"{host}:{self.number}"

    @property
    def browser_address(self) -> str | None:
        if self.protocol != "TCP" or self.host not in ("127.0.0.1", "::1", "0.0.0.0", "::", "*"):
            return None
        host = "[::1]" if self.host in ("::", "::1") else "127.0.0.1"
        return f"http://{host}:{self.number}/"


def parse_ss(output: str) -> list[Port]:
    rows: set[Port] = set()
    for line in output.splitlines():
        fields = line.split(maxsplit=6)
        if len(fields) < 6 or fields[0] not in ("tcp", "udp"):
            continue
        protocol = fields[0].upper()
        host, _, number = fields[4].rpartition(":")
        if not number.isdecimal() or not 0 < int(number) <= 65535:
            continue
        host = host.removeprefix("[").removesuffix("]")
        process = fields[6] if len(fields) == 7 else ""
        owners = re.findall(r'\("((?:[^"\\]|\\.)*)",pid=(\d+)(?:,|\))', process)
        rows.update(Port(protocol, host, int(number), name, int(pid)) for name, pid in owners)
        if not owners:
            rows.add(Port(protocol, host, int(number)))
    return sorted(rows, key=lambda row: (row.number, row.protocol, row.host, row.pid or 0))
