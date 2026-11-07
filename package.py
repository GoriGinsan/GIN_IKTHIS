from pathlib import Path
import argparse
import hashlib
import zipfile

VERSION = "1.0.0"
INSTALL_PATH = "BepInEx/plugins/GINLifeHacks/GIN IKTHIS.dll"

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Package only the IKTHIS runtime DLL.")
    parser.add_argument("--dll", type=Path, default=Path("bin/GIN IKTHIS.dll"))
    parser.add_argument("--output", type=Path, default=Path("dist"))
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    destination = args.output / ("GIN_IKTHIS_v" + VERSION + ".zip")
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.write(args.dll, INSTALL_PATH)
    with zipfile.ZipFile(destination) as archive:
        assert archive.namelist() == [INSTALL_PATH]
        assert archive.read(INSTALL_PATH) == args.dll.read_bytes()
    print(str(destination))
    print("DLL SHA256: " + hashlib.sha256(args.dll.read_bytes()).hexdigest())
