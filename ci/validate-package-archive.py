#!/usr/bin/env python3
import sys
import zipfile
from pathlib import PurePosixPath


PACKAGE_ROOTS = ("Editor", "Runtime")
ROOT_FILES = ("README.md", "LICENSE.md", "package.json")
FORBIDDEN_ROOTS = (".git", ".github", ".idea", ".agents", ".codex", "Tests", "ci")


def validate_zip_name(name):
    normalized = name.replace("\\", "/")
    parts = PurePosixPath(normalized).parts
    if normalized != name:
        return "uses backslashes"
    if not parts:
        return "is empty"
    if normalized.startswith("/"):
        return "is absolute"
    if any(part in ("", ".", "..") for part in parts):
        return "contains an invalid path segment"
    return None


def is_allowed_package_file(name):
    if name.endswith(".meta"):
        return False

    if name in ROOT_FILES:
        return True

    parts = PurePosixPath(name).parts
    if parts[0] in PACKAGE_ROOTS:
        return True

    return False


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: validate-package-archive.py <package.zip>")

    zip_path = sys.argv[1]
    with zipfile.ZipFile(zip_path) as archive:
        names = sorted(name for name in archive.namelist() if not name.endswith("/"))

    if not names:
        raise SystemExit("Package archive is empty.")

    failures = []
    for name in names:
        invalid_reason = validate_zip_name(name)
        if invalid_reason:
            failures.append(f"{name}: {invalid_reason}")
            continue

        root = PurePosixPath(name).parts[0]
        if root in FORBIDDEN_ROOTS:
            failures.append(f"{name}: forbidden repository/internal path")
            continue

        if not is_allowed_package_file(name):
            failures.append(f"{name}: not in package allowlist")

    required_mixer_files = (
        "Runtime/Orbiters.Mixer.asmdef", "Runtime/MixerInfo.cs",
        "Editor/Orbiters.Mixer.Editor.asmdef",
        "Editor/Core/MeshDocument.cs", "Editor/Core/MixerHistory.cs", "Editor/Core/MixerSession.cs",
        "Editor/Tools/MixerSceneTool.cs",
        "Editor/UI/MixerWindow.cs", "Editor/UI/mixer.uss",
        "Editor/Hierarchy/MixerHierarchyWindow.cs", "Editor/UI/hierarchy.uss",
    )
    for required_file in ROOT_FILES + required_mixer_files:
        if required_file not in names:
            failures.append(f"{required_file}: required root file missing")

    for required_root in PACKAGE_ROOTS:
        if not any(name.startswith(required_root + "/") for name in names):
            failures.append(f"{required_root}/: required package root missing")

    if failures:
        print("Package archive validation failed:")
        for failure in failures:
            print(" - " + failure)
        raise SystemExit(1)

    print(f"Package archive validation passed for {len(names)} files.")


if __name__ == "__main__":
    main()
