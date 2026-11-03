"""Build the single-file launcher into dist/ with its SHA-256.

usage: python tools/publish.py [version]      (default: the csproj <Version>)
Output: dist/Mass Effect Archipelago Launcher.exe (self-contained, win-x64) and dist/SHA256SUMS. GitHub replaces spaces in
asset names, so the release uploads it as Mass-Effect-Archipelago-Launcher.exe.
The exe records the GitHub repository it is built from (the remote the current branch tracks, else origin): it checks
that repository for launcher updates, and uses its owner's fork of a game when that has a release.
"""
import hashlib
import pathlib
import re
import shutil
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "MassEffectAPLauncher" / "MassEffectAPLauncher.csproj"
EXE = "Mass Effect Archipelago Launcher.exe"


def github_repo() -> str | None:
    """owner/name of the GitHub remote the current branch tracks (else origin), or None."""
    def git(*args: str) -> str:
        return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True).stdout.strip()
    upstream = git("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}")
    remote = upstream.split("/", 1)[0] if "/" in upstream else "origin"
    match = re.search(r"github\.com[:/]([^/]+)/([^/]+?)(?:\.git)?/?$", git("remote", "get-url", remote))
    return f"{match.group(1)}/{match.group(2)}" if match else None


def main() -> int:
    version = sys.argv[1] if len(sys.argv) > 1 else re.search(r"<Version>([^<]+)</Version>", PROJECT.read_text()).group(1)
    dist = ROOT / "dist"
    if dist.exists():
        shutil.rmtree(dist)
    repo = github_repo()
    subprocess.run(["dotnet", "publish", str(PROJECT), "-c", "Release", "-r", "win-x64", "--self-contained", "true",
                    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
                    f"-p:Version={version}", *([f"-p:LauncherRepository={repo}"] if repo else []), "-o", str(dist / "publish")],
                   check=True)
    shutil.copy(dist / "publish" / EXE, dist / EXE)
    shutil.rmtree(dist / "publish")
    digest = hashlib.sha256((dist / EXE).read_bytes()).hexdigest()
    (dist / "SHA256SUMS").write_text(f"{digest}  {EXE}\n", newline="\n")
    print(f"{dist / EXE}  {version}  {repo or 'Mass-Effect-LE-Archipelago/ME-Archipelago-Launcher (default)'}  sha256 {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
