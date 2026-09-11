#!/usr/bin/env python3
"""compone el instalador: [stub.exe] + [payload.zip] + [marca] + [longitud].

Formato de extracción (SelfExtractor):
  <exe><zip>ALARMINO_PAYLOAD<longitud: little-endian u64>
"""
import io
import os
import struct
import sys
import zipfile

MARKER = b"ALARMINO_PAYLOAD"


def build_payload(app_dir: str) -> bytes:
    blob = io.BytesIO()
    with zipfile.ZipFile(blob, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        exe = os.path.join(app_dir, "Alarmino.exe")
        if not os.path.exists(exe):
            raise SystemExit(f"No existe {exe} (lanza scripts/publish.{'sh' if os.name != 'nt' else 'ps1'} primero)")
        zf.write(exe, "Alarmino.exe")
        pdb = os.path.join(app_dir, "Alarmino.pdb")
        if os.path.exists(pdb):
            zf.write(pdb, "Alarmino.pdb")
    return blob.getvalue()


def compose(stub_exe: str, app_dir: str, out_exe: str) -> None:
    if not os.path.exists(stub_exe):
        raise SystemExit(f"No existe el stub: {stub_exe}")

    payload = build_payload(app_dir)
    with open(stub_exe, "rb") as f:
        stub = f.read()

    with open(out_exe, "wb") as f:
        f.write(stub)
        f.write(payload)
        f.write(MARKER)
        f.write(struct.pack("<Q", len(payload)))

    print(f"OK: {out_exe} ({os.path.getsize(out_exe)} bytes)")


if __name__ == "__main__":
    if len(sys.argv) < 4:
        raise SystemExit("uso: compose-installer.py <stub.exe> <app_dir> <out.exe>")
    compose(sys.argv[1], sys.argv[2], sys.argv[3])