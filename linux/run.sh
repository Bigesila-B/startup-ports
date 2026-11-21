#!/bin/sh
set -eu
cd "$(dirname "$0")"
if [ ! -x .venv/bin/python ]; then
    printf '%s\n' 'Run sh setup.sh first. 请先运行 sh setup.sh。' >&2
    exit 1
fi
exec .venv/bin/python startupports.py "$@"
