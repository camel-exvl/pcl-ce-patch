"""Run Sigstore with all persistent trust metadata confined to this workspace."""
from pathlib import Path
import platformdirs

cache = Path(__file__).resolve().parents[1] / ".tools" / "sigstore"
platformdirs.user_data_dir = lambda *args, **kwargs: str(cache / "data")
platformdirs.user_cache_dir = lambda *args, **kwargs: str(cache / "cache")
platformdirs.user_config_dir = lambda *args, **kwargs: str(cache / "config")

# Patch directory discovery before Sigstore/TUF is imported; no change to installed packages.
from sigstore._cli import main  # noqa: E402

if __name__ == "__main__":
    main()
