#!/usr/bin/env python3
import argparse
from pathlib import Path


PACKAGE_ROOTS = ("Contracts", "Editor", "Runtime")
ROOT_FILES = ("README.md", "LICENSE.md", "package.json")
# The OrbitersContracts submodule also holds its Rust crate, test vectors and .NET tools: not part of the package.
EXCLUDED_DIRS = ("Contracts/Rust~", "Contracts/TestVectors~", "Contracts/Tools~")


def to_posix(path):
    return path.as_posix()


def is_excluded(relative):
    if any(part.startswith(".git") for part in relative.parts):
        return True
    posix = relative.as_posix()
    return any(posix.startswith(excluded + "/") for excluded in EXCLUDED_DIRS)


def collect_package_files(root):
    files = set()

    for root_file in ROOT_FILES:
        path = root / root_file
        if path.is_file() and not path.is_symlink():
            files.add(to_posix(path.relative_to(root)))

    for package_root in PACKAGE_ROOTS:
        package_root_path = root / package_root
        if package_root_path.is_dir() and not package_root_path.is_symlink():
            for path in package_root_path.rglob("*"):
                relative = path.relative_to(root)
                if path.is_file() and not path.is_symlink() and path.suffix.lower() != ".meta" and not is_excluded(relative):
                    files.add(to_posix(relative))

    return sorted(files, key=str.lower)


def main():
    parser = argparse.ArgumentParser(description="Build the explicit Mixer package release file list.")
    parser.add_argument("--root", default=".", help="Package root to scan.")
    parser.add_argument("--output", default="packageFiles", help="Output file path.")
    parser.add_argument("--meta-output", help="Optional output path for the .meta file list used by create-unitypackage.")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    output = Path(args.output)
    if not output.is_absolute():
        output = root / output

    files = collect_package_files(root)
    if not files:
        raise SystemExit("Package allowlist produced no files.")

    output.write_text("\n".join(files) + "\n", encoding="utf-8")
    print(f"Wrote {len(files)} package file entries to {output}")

    if args.meta_output:
        meta_output = Path(args.meta_output)
        if not meta_output.is_absolute():
            meta_output = root / meta_output

        meta_output.write_text("", encoding="utf-8")
        print(f"Wrote 0 package .meta file entries to {meta_output}")


if __name__ == "__main__":
    main()
