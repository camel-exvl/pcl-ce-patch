"""Verify official Sigstore identity before any binary is modified."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--directory", required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
config = json.loads((root / "patch-config.json").read_text(encoding="utf-8"))
for arch in ("x64", "ARM64"):
    path = Path(args.directory) / f"PCL2_CE_Release_{arch}.exe"
    subprocess.run([sys.executable, "-m", "sigstore", "verify", "identity",
                    "--cert-identity", config["upstreamSigstoreIdentity"],
                    "--cert-oidc-issuer", "https://token.actions.githubusercontent.com",
                    "--bundle", str(path) + ".sigstore.json", str(path)], check=True)
