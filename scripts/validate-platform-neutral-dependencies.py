#!/usr/bin/env python3
"""Check the actual MSBuild restore and build instances of the active .NET roots.

All dotnet processes run against a guard-owned, read-only source copy inside
an OS-enforced scratch-only write boundary. An independent MSBuild logger
inventories actual project requests, downloads and package assets. Each
observed child context is force-restored with the properties MSBuild reported,
not a simulation of ProjectReference property propagation.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import uuid
import xml.etree.ElementTree as ET
from collections import deque
from dataclasses import dataclass, field
from pathlib import Path
from xml.sax.saxutils import escape


GUARD_ORIGIN = Path(__file__).resolve().parents[1]
ROOT = GUARD_ORIGIN
GUARD_TOOL = Path("scripts/platform-neutral-guard")
SOURCE_DIRECTORIES = ("src", "tests", "lib", "tools", "scripts", "docs", "feed")
GENERATED_DIRECTORIES = frozenset({
    "obj", "bin", "node_modules", "__pycache__", ".git", ".vs",
    "TestResults", "test-results", "artifacts",
})
ROOT_INPUTS = frozenset({
    "Directory.Packages.props", "Directory.Build.props", "Directory.Build.targets",
    "NuGet.config", "NuGet.Config", "global.json", ".editorconfig", ".globalconfig",
})
SOURCE_GENERATED_FILES = frozenset({
    Path("src/SentenceStudio.Shared/Resources/Strings/AppResources.Designer.cs"),
})
SEATBELT_PROFILE = (
    '(version 1)(allow default)(deny file-write* file-link file-clone)'
    '(allow file-write* (subpath (param "SCRATCH")))'
    '(allow file-clone (subpath (param "SCRATCH")))'
    '(allow file-clone (subpath (param "SOURCE")))'
    '(allow file-write* (literal "/dev/null"))'
)


@dataclass(frozen=True)
class RootProject:
    path: str
    framework: str


SERVER_AND_SHARED_ROOTS = (
    RootProject("src/SentenceStudio.AppHost/SentenceStudio.AppHost.csproj", "net10.0"),
    RootProject("src/SentenceStudio.WebApp/SentenceStudio.WebApp.csproj", "net11.0"),
    RootProject("src/SentenceStudio.Api/SentenceStudio.Api.csproj", "net10.0"),
    RootProject("src/SentenceStudio.Workers/SentenceStudio.Workers.csproj", "net10.0"),
    RootProject("src/SentenceStudio.Marketing/SentenceStudio.Marketing.csproj", "net10.0"),
    RootProject("src/SentenceStudio.WebServiceDefaults/SentenceStudio.WebServiceDefaults.csproj", "net11.0"),
    RootProject("src/SentenceStudio.AppLib/SentenceStudio.AppLib.csproj", "net11.0"),
    RootProject("src/SentenceStudio.UI/SentenceStudio.UI.csproj", "net11.0"),
    RootProject("src/SentenceStudio.Shared/SentenceStudio.Shared.csproj", "net10.0"),
    RootProject("src/SentenceStudio.Infrastructure/SentenceStudio.Infrastructure.csproj", "net10.0"),
)
TEST_ROOTS = (
    RootProject("tests/SentenceStudio.Api.Tests/SentenceStudio.Api.Tests.csproj", "net10.0"),
    RootProject("tests/SentenceStudio.AppLib.Tests/SentenceStudio.AppLib.Tests.csproj", "net11.0"),
    RootProject("tests/SentenceStudio.UI.Tests/SentenceStudio.UI.Tests.csproj", "net11.0"),
    RootProject("tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj", "net10.0"),
    RootProject("tests/SentenceStudio.WebApp.Tests/SentenceStudio.WebApp.Tests.csproj", "net11.0"),
)
ACTIVE_ROOTS = SERVER_AND_SHARED_ROOTS + TEST_ROOTS


@dataclass(frozen=True)
class RestoreEntry:
    project: str
    frameworks: tuple[str, ...]
    configuration: str = "Release"
    runtime_identifier: str | None = None
    properties: tuple[tuple[str, str], ...] = (("IncludeMobileTargets", "false"),)

    def label(self) -> str:
        return (f"{self.project} [{','.join(self.frameworks)}, {self.configuration}, "
                f"{self.runtime_identifier or 'no RID'}]")


# Do not pass TargetFramework globally: the net10 AppHost builds a net11 WebApp.
ACTIVE_MATRIX = (
    tuple(RestoreEntry(root.path, (root.framework,), configuration)
          for root in ACTIVE_ROOTS for configuration in ("Debug", "Release"))
    + tuple(RestoreEntry(root.path, (root.framework,), "Release", "linux-x64")
            for root in SERVER_AND_SHARED_ROOTS)
)
TEMPORARY_HEADS = frozenset({
    "SentenceStudio.Android", "SentenceStudio.iOS", "SentenceStudio.MacOS",
    "SentenceStudio.MacCatalyst", "SentenceStudio.Windows",
    "SentenceStudio.ShareExtension", "SentenceStudio.MauiCompatibility",
    "SentenceStudio.MauiServiceDefaults",
})
FORBIDDEN_PACKAGE = re.compile(r"(^|[._-])(maui|comet)([._-]|$)|^mauireactor", re.I)
LOCAL3_VERSION = "0.1.129-local3"
PROVIDER_ID = "coresync.postgresql"
PROVIDER_ARCHIVE = "CoreSync.PostgreSQL.0.1.129-local3.nupkg"
PROVIDER_SHA256 = "c301f31bb4c7415242b545835230d566499f6805fd3bc74cd51098e10c0a05f9"
MIN_FREE_BYTES = 512 * 1024 * 1024
PACKAGE_CACHE_HEADROOM_BYTES = 3 * 1024 * 1024 * 1024
COMMAND_TIMEOUT_SECONDS = 300
GUARD_TIMEOUT_SECONDS = 5400
MAX_CONTEXTS_PER_ROOT = 160

ContextKey = tuple[Path, tuple[tuple[str, str], ...]]


class GuardFailure(ValueError):
    pass


@dataclass(frozen=True)
class Isolation:
    source: Path
    runtime: Path
    denied_probe: Path


def source_snapshot(root: Path, snapshot: Path) -> None:
    def exclude(directory: str, names: list[str]) -> set[str]:
        rejected = set()
        for name in names:
            path = Path(directory) / name
            if path.is_symlink():
                raise GuardFailure(f"snapshot refuses source symlink: {path}")
            if path.is_dir() and name in GENERATED_DIRECTORIES:
                rejected.add(name)
            elif (path.is_file() and path.is_relative_to(root)
                  and path.relative_to(root) in SOURCE_GENERATED_FILES):
                rejected.add(name)
            elif path.is_file() and (
                name.startswith(".env") or name.endswith((".user", ".pfx", ".pem"))
            ):
                rejected.add(name)
        return rejected

    snapshot.mkdir()
    for directory in SOURCE_DIRECTORIES:
        original = root / directory
        if original.is_dir():
            shutil.copytree(original, snapshot / directory, ignore=exclude)
    tools = snapshot / GUARD_TOOL
    if not tools.exists():
        tools.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(GUARD_ORIGIN / GUARD_TOOL, tools, ignore=exclude)
    for name in ROOT_INPUTS:
        original = root / name
        if original.is_symlink():
            raise GuardFailure(f"snapshot refuses source symlink: {original}")
        if original.is_file():
            shutil.copy2(original, snapshot / name)
    for directory, names, files in os.walk(snapshot, topdown=False):
        for name in files:
            file = Path(directory) / name
            if not file.is_file() or file.is_symlink():
                raise GuardFailure(f"snapshot refuses nonregular source file: {file}")
            file.chmod(stat.S_IMODE(file.stat().st_mode) & ~0o222)
        for name in names:
            path = Path(directory) / name
            path.chmod(stat.S_IMODE(path.stat().st_mode) & ~0o222)
    snapshot.chmod(stat.S_IMODE(snapshot.stat().st_mode) & ~0o222)


def unlock_snapshot_directories(snapshot: Path) -> None:
    if snapshot.exists():
        for directory, names, _ in os.walk(snapshot, topdown=False):
            for name in names:
                path = Path(directory) / name
                path.chmod(stat.S_IMODE(path.stat().st_mode) | stat.S_IWUSR)
        snapshot.chmod(stat.S_IMODE(snapshot.stat().st_mode) | stat.S_IWUSR)


def isolated_command(
    command: list[str], isolation: Isolation, project: Path,
) -> list[str]:
    if sys.platform == "darwin":
        sandbox = Path("/usr/bin/sandbox-exec")
        if not sandbox.is_file():
            raise GuardFailure("macOS write sandbox is unavailable; refusing unsandboxed build")
        return [
            str(sandbox), "-D", f"SCRATCH={isolation.runtime}",
            "-D", f"SOURCE={isolation.source}", "-p",
            SEATBELT_PROFILE, *command,
        ]
    if sys.platform == "linux":
        bwrap = shutil.which("bwrap")
        if bwrap is None:
            raise GuardFailure("Linux read-only mount sandbox requires bubblewrap; refusing build")
        if isolation.runtime.is_relative_to(Path("/tmp")) or isolation.runtime.is_relative_to(
            Path("/var/tmp")
        ):
            raise GuardFailure("Linux scratch parent must be outside /tmp and /var/tmp")
        (isolation.runtime / "var-tmp").mkdir(exist_ok=True)
        return [
            bwrap, "--unshare-user", "--disable-userns", "--unshare-pid",
            "--unshare-ipc", "--share-net", "--new-session", "--die-with-parent",
            "--ro-bind", "/", "/",
            "--bind", str(isolation.runtime), str(isolation.runtime),
            "--bind", str(isolation.runtime / "tmp"), "/tmp",
            "--bind", str(isolation.runtime / "var-tmp"), "/var/tmp",
            "--dev", "/dev", "--proc", "/proc",
            "--chdir", str(project.parent), "--", *command,
        ]
    raise GuardFailure(f"no enforced scratch-only write sandbox for {sys.platform}")


def check_isolation(isolation: Isolation, deadline: float) -> None:
    isolation.denied_probe.write_bytes(b"unchanged")
    isolation.denied_probe.chmod(0o644)
    initial_mtime = isolation.denied_probe.stat().st_mtime_ns
    probe = (
        "import ctypes,errno,os,pathlib,sys\n"
        "denied,allowed,source,source_output = map(pathlib.Path,sys.argv[1:])\n"
        "if not denied.is_file() or denied.read_bytes() != b'unchanged':\n"
        "    sys.exit('sandbox probe cannot read a host-writable outside file')\n"
        "def deny(action,operation):\n"
        "    try:\n"
        "        action()\n"
        "    except OSError as error:\n"
        "        if error.errno in (errno.EPERM,errno.EACCES,errno.EROFS):\n"
        "            return\n"
        "        raise\n"
        "    sys.exit('write sandbox allowed ' + operation)\n"
        "deny(lambda: denied.write_bytes(b'unsafe'),'content write outside runtime')\n"
        "deny(lambda: denied.chmod(0o600),'chmod outside runtime')\n"
        "deny(lambda: os.utime(denied,(1,1)),'timestamp write outside runtime')\n"
        "if hasattr(os,'setxattr'):\n"
        "    deny(lambda: os.setxattr(denied,'user.sentence_guard',b'unsafe'),\n"
        "         'xattr write outside runtime')\n"
        "allowed.write_bytes(b'allowed')\n"
        "if sys.platform == 'darwin':\n"
        "    library = ctypes.CDLL(None, use_errno=True)\n"
        "    library.clonefile.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int]\n"
        "    library.clonefile.restype = ctypes.c_int\n"
        "    def clone(start, end):\n"
        "        return library.clonefile(bytes(start), bytes(end), 0)\n"
        "    if clone(source, allowed.with_name('clone-allowed')) != 0:\n"
        "        sys.exit('cannot clone read-only source into isolated scratch')\n"
        "    if clone(allowed, source_output) == 0:\n"
        "        sys.exit('write sandbox allowed a clone into read-only source')\n"
    )
    code, output = run_command(
        [sys.executable, "-c", probe, str(isolation.denied_probe),
         str(isolation.runtime / "sandbox-probe"),
         str(isolation.source / GUARD_TOOL / "DependencyGuard.csproj"),
         str(isolation.source / "clone-write-denied")],
        isolation.source / GUARD_TOOL / "DependencyGuard.csproj",
        isolation.runtime, deadline, isolation,
    )
    if (code or isolation.denied_probe.read_bytes() != b"unchanged"
            or isolation.denied_probe.stat().st_mtime_ns != initial_mtime
            or stat.S_IMODE(isolation.denied_probe.stat().st_mode) != 0o644
            or not (isolation.runtime / "sandbox-probe").is_file()
            or (isolation.source / "clone-write-denied").exists()):
        raise GuardFailure(f"scratch-only process write boundary failed: {output.strip()}")


@dataclass
class Observation:
    project: Path
    global_properties: dict[str, str]
    evaluated: dict
    evaluation_id: int
    instance_id: int
    targets: set[str] = field(default_factory=set)
    executed_targets: set[str] = field(default_factory=set)
    snapshots: list[Path] = field(default_factory=list)
    asset_tasks: dict[int, str] = field(default_factory=dict)
    asset_inputs: dict[int, dict] = field(default_factory=dict)
    consumed_snapshots: list[Path] = field(default_factory=list)
    asset_record_path: str | None = None

    @property
    def key(self) -> ContextKey:
        return self.project, tuple(sorted(
            (name.casefold(), value) for name, value in self.global_properties.items()
        ))

    def label(self, root: Path) -> str:
        properties = self.global_properties
        selected = {name: properties.get(name, "") for name in
                    ("TargetFramework", "Configuration", "RuntimeIdentifier")}
        return f"{self.project.relative_to(root)} {selected}"


@dataclass
class BuildTrace:
    observations: list[Observation]
    succeeded: bool
    diagnostics: list[str]


def read_object(path: Path) -> dict:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (ValueError, OSError) as error:
        raise GuardFailure(f"missing or invalid {path}: {error}") from error
    if not isinstance(data, dict):
        raise GuardFailure(f"invalid {path}: expected a JSON object")
    return data


def project_in_root(root: Path, name: str | Path) -> Path:
    path = Path(name)
    project = (path if path.is_absolute() else root / path).resolve()
    if not project.is_relative_to(root):
        raise GuardFailure(f"project reference escapes the repository: {project}")
    if any(part in TEMPORARY_HEADS for part in project.relative_to(root).parts):
        raise GuardFailure(f"active graph references temporary MAUI head {project.relative_to(root)}")
    if not project.is_file() or project.suffix != ".csproj":
        raise GuardFailure(f"missing or unsupported project {project}")
    return project


def prepare_restore_sources(root: Path, scratch: Path) -> tuple[Path, set[str]]:
    source = next((path for path in (root / "src/NuGet.config", root / "src/NuGet.Config")
                   if path.is_file()), None)
    if source is None:
        raise GuardFailure("missing src/NuGet.config; refusing implicit user package sources")
    try:
        tree = ET.parse(source)
    except (ET.ParseError, OSError) as error:
        raise GuardFailure(f"cannot read repository NuGet config: {error}") from error
    config = tree.getroot()
    sources, mapping = config.find("packageSources"), config.find("packageSourceMapping")
    if sources is None or mapping is None:
        raise GuardFailure("NuGet config must define package sources and source mapping")

    for section in list(config):
        if section.tag.casefold() == "fallbackpackagefolders":
            config.remove(section)
        elif section.tag.casefold() == "config":
            for item in list(section):
                key = item.get("key", "").casefold()
                if item.tag.casefold() == "add" and (
                    "fallback" in key or key in {"globalpackagesfolder", "repositorypath"}
                ):
                    section.remove(item)
    ET.SubElement(config, "fallbackPackageFolders").append(ET.Element("clear"))

    retained: set[str] = set()
    allowed_sources: set[str] = set()
    for item in list(sources):
        if item.tag == "clear":
            continue
        if item.tag != "add" or not item.get("key") or not item.get("value"):
            raise GuardFailure("cannot verify a NuGet package source in src/NuGet.config")
        key, value = item.attrib["key"], item.attrib["value"]
        if key.casefold() == "localnugets":
            sources.remove(item)
            continue
        if not value.startswith(("https://", "http://")):
            location = Path(value)
            location = (location if location.is_absolute() else source.parent / location).resolve()
            if not location.is_relative_to(root) or not location.is_dir():
                raise GuardFailure(f"NuGet source {key} is not an available repository-local feed")
            value = str(location)
            item.set("value", value)
        allowed_sources.add(value.rstrip("/"))
        retained.add(key)
    for item in list(mapping):
        if item.tag != "packageSource" or not item.get("key"):
            raise GuardFailure("cannot verify NuGet package source mapping")
        if item.attrib["key"] not in retained:
            mapping.remove(item)
    disabled = config.find("disabledPackageSources")
    if disabled is not None:
        for item in list(disabled):
            if item.get("key", "").casefold() == "localnugets":
                disabled.remove(item)
    credentials = config.find("packageSourceCredentials")
    if credentials is not None:
        for item in list(credentials):
            if item.tag.casefold() == "localnugets":
                credentials.remove(item)

    if (root / "src/SentenceStudio.Shared/SentenceStudio.Shared.csproj").is_file():
        validate_local3_feed(root, sources, mapping)
    output = scratch / "nuget.guard.config"
    tree.write(output, encoding="utf-8", xml_declaration=True)
    return output, allowed_sources


def validate_local3_feed(root: Path, sources: ET.Element, mapping: ET.Element) -> None:
    try:
        versions = ET.parse(root / "Directory.Packages.props").getroot()
    except (ET.ParseError, OSError) as error:
        raise GuardFailure(f"cannot read central package versions: {error}") from error
    pins = [
        item.get("Version") for item in versions.iter("PackageVersion")
        if item.get("Include", "").casefold() == PROVIDER_ID
    ]
    if pins != [f"[{LOCAL3_VERSION}]"]:
        raise GuardFailure(f"CoreSync.PostgreSQL must be pinned to [{LOCAL3_VERSION}]")

    feed = (root / "lib/coresync-postgresql-local3").resolve()
    if not feed.is_relative_to(root):
        raise GuardFailure("reviewed CoreSync.PostgreSQL feed must be repository-local")
    local = [item for item in sources.findall("add")
             if item.get("key") == "coresync-postgresql-local3"]
    if len(local) != 1 or Path(local[0].get("value", "")).resolve() != feed:
        raise GuardFailure("coresync-postgresql-local3 must use the repository-local feed")
    mapped = [item for item in mapping.findall("packageSource")
              if item.get("key") == "coresync-postgresql-local3"]
    if len(mapped) != 1 or {
        item.get("pattern", "").casefold() for item in mapped[0].findall("package")
    } != {PROVIDER_ID}:
        raise GuardFailure("reviewed feed must map only the exact CoreSync.PostgreSQL ID")

    public = [item for item in sources.findall("add") if item.get("key") == "nuget.org"]
    public_mapping = [item for item in mapping.findall("packageSource")
                      if item.get("key") == "nuget.org"]
    if (len(public) != 1 or len(public_mapping) != 1
            or public[0].get("value") != "https://api.nuget.org/v3/index.json"):
        raise GuardFailure("stable CoreSync must have the nuget.org source mapping")
    for item in mapping.findall("packageSource"):
        patterns = {package.get("pattern", "").casefold()
                    for package in item.findall("package")}
        if item is not mapped[0] and PROVIDER_ID in patterns:
            raise GuardFailure("CoreSync.PostgreSQL also has a nonlocal exact source mapping")
        if (item is public_mapping[0]) != ("coresync" in patterns):
            raise GuardFailure("stable CoreSync must map only to nuget.org by exact ID")

    package = feed / PROVIDER_ARCHIVE
    if not package.is_file() or package.is_symlink():
        raise GuardFailure(f"missing reviewed repository-local package {PROVIDER_ARCHIVE}")
    if set(feed.rglob("*.nupkg")) != {package}:
        raise GuardFailure("reviewed feed must contain only the CoreSync.PostgreSQL archive")
    if hashlib.sha256(package.read_bytes()).hexdigest() != PROVIDER_SHA256:
        raise GuardFailure(f"reviewed repo-local package {PROVIDER_ARCHIVE} has unexpected SHA-256")


def restore_environment(runtime: Path) -> dict[str, str]:
    forbidden = {
        "restoresources", "restoreadditionalprojectsources", "restoreconfigfile",
        "restorepackagespath", "nuget_packages", "nugetpackageroot",
        "restorefallbackfolders", "restoreadditionalprojectfallbackfolders",
        "nuget_fallback_packages", "nuget_http_cache_path",
        "nuget_plugins_cache_path", "nuget_scratch", "nuget_plugin_paths",
        "dotnet_shared_memory_application_group_id",
        "customaftermicrosoftcommontargets",
        "customaftermicrosoftcommoncrosstargetingtargets",
        "customafterdirectorybuildprops", "dependencyguardassembly",
        "dependencyguardscratch", "dependencyguardtoken",
    }
    environment = {key: value for key, value in os.environ.items()
                   if key.casefold() not in forbidden}
    paths = {
        "HOME": "home", "DOTNET_CLI_HOME": "dotnet-home",
        "TMPDIR": "tmp", "TMP": "tmp", "TEMP": "tmp",
        "XDG_CACHE_HOME": "xdg-cache", "XDG_CONFIG_HOME": "xdg-config",
        "XDG_DATA_HOME": "xdg-data", "XDG_RUNTIME_DIR": "xdg-run",
        "NUGET_PACKAGES": "packages", "NUGET_HTTP_CACHE_PATH": "http-cache",
        "NUGET_PLUGINS_CACHE_PATH": "plugins-cache",
        "NUGET_SCRATCH": "nuget-scratch", "MSBUILDDEBUGPATH": "msbuild-debug",
    }
    for name, child in paths.items():
        directory = runtime / child
        directory.mkdir(exist_ok=True)
        environment[name] = str(directory)
    sdk_root = Path(shutil.which("dotnet") or "").resolve().parent / "sdk"
    if not sdk_root.is_dir():
        raise GuardFailure("cannot locate installed .NET SDKs for isolated CLI initialization")
    # A scratch-local first-use sentinel avoids the CLR's hard-coded /tmp/.dotnet/shm mutex.
    profile = runtime / "dotnet-home/.dotnet"
    profile.mkdir(exist_ok=True)
    installed = [path for path in sdk_root.iterdir() if path.is_dir()
                 and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.]+)?", path.name)]
    if not installed:
        raise GuardFailure("no SDK version found for isolated CLI initialization")
    for path in installed:
        (profile / f"{path.name}.dotnetFirstUseSentinel").touch(exist_ok=True)
    # NuGet's scratch-only profile has no legacy user config to migrate.
    migration = runtime / "xdg-data/NuGet/Migrations/1"
    migration.parent.mkdir(parents=True, exist_ok=True)
    migration.touch(exist_ok=True)
    environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false"
    environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false"
    if sys.platform == "darwin":
        # Apple CoreCLR otherwise creates named mutex files in /tmp/.dotnet/shm.
        shared = runtime / "shared-memory"
        shared.mkdir(exist_ok=True)
        environment["DOTNET_SHARED_MEMORY_APPLICATION_GROUP_ID"] = str(shared)
    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true"
    environment["MSBUILDDISABLENODEREUSE"] = "1"
    return environment


def free_bytes(scratch: Path) -> int:
    try:
        return shutil.disk_usage(scratch).free
    except OSError as error:
        raise GuardFailure(f"cannot measure guard scratch space: {error}") from error


def run_command(
    command: list[str], project: Path, scratch: Path, deadline: float,
    isolation: Isolation,
) -> tuple[int, str]:
    if free_bytes(scratch) < MIN_FREE_BYTES:
        raise GuardFailure(f"scratch has less than {MIN_FREE_BYTES // (1024 * 1024)} MiB free")
    remaining = min(COMMAND_TIMEOUT_SECONDS, deadline - time.monotonic())
    if remaining <= 0:
        raise GuardFailure(f"restore/build budget exceeded ({GUARD_TIMEOUT_SECONDS}s)")
    try:
        confined = isolated_command(command, isolation, project)
        process = subprocess.Popen(
            confined, cwd=project.parent, env=restore_environment(isolation.runtime),
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
        )
    except (OSError, subprocess.SubprocessError) as error:
        raise GuardFailure(f"cannot start sandboxed process: {error}") from error
    started = time.monotonic()
    try:
        while True:
            left = remaining - (time.monotonic() - started)
            if left <= 0:
                raise GuardFailure(f"dotnet command exceeded {remaining:.0f}s: {command[:3]}")
            try:
                output, _ = process.communicate(timeout=min(1, left))
                break
            except subprocess.TimeoutExpired:
                if free_bytes(scratch) < MIN_FREE_BYTES:
                    raise GuardFailure(
                        f"dotnet stopped: scratch has less than "
                        f"{MIN_FREE_BYTES // (1024 * 1024)} MiB free"
                    )
        if free_bytes(scratch) < MIN_FREE_BYTES:
            raise GuardFailure("dotnet command exhausted the minimum scratch reserve")
        return process.returncode, output
    except (GuardFailure, OSError) as error:
        diagnostic = ""
        if process.poll() is None:
            process.terminate()
            try:
                diagnostic, _ = process.communicate(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                diagnostic, _ = process.communicate(timeout=10)
        if isinstance(error, OSError):
            raise GuardFailure(f"dotnet process I/O failed: {error}") from error
        if diagnostic:
            detail = "\n".join(diagnostic.strip().splitlines()[-12:])
            raise GuardFailure(f"{error}:\n{detail}") from error
        raise


def command_failure(verb: str, project: Path, code: int, output: str) -> GuardFailure:
    detail = "\n".join(output.strip().splitlines()[-14:])
    return GuardFailure(f"dotnet {verb} exited {code} for {project}:\n{detail}")


def build_logger(
    config: Path, packages: Path, scratch: Path, deadline: float,
    source: Path, isolation: Isolation,
) -> Path:
    tool = scratch / "tool"
    obj, bin_path = tool / "obj", tool / "bin"
    guard_project = source / GUARD_TOOL / "DependencyGuard.csproj"
    properties = [
        f"-p:BaseIntermediateOutputPath={obj}/",
        f"-p:MSBuildProjectExtensionsPath={obj}/",
        f"-p:OutputPath={bin_path}/",
        f"-p:RestorePackagesPath={packages}",
    ]
    for verb, extras in (
        ("restore", ["--force", "--no-http-cache", "--packages", str(packages),
                     "--configfile", str(config), "-p:RestoreForce=true"]),
        ("build", ["--no-restore"]),
    ):
        command = [
            "dotnet", verb, str(guard_project), "--nologo", "--verbosity", "quiet",
            "-m:1", "-nr:false", "-warnaserror:NU1603,NU1605", *extras, *properties,
        ]
        code, output = run_command(command, guard_project, scratch, deadline, isolation)
        if code:
            raise command_failure(verb, guard_project, code, output)
    assembly = bin_path / "DependencyGuard.dll"
    if not assembly.is_file():
        raise GuardFailure(f"MSBuild logger was not built at {assembly}")
    return assembly


def isolated_props(entry_dir: Path) -> Path:
    props = entry_dir / "isolate.props"
    base = escape(str(entry_dir / "obj"))
    props.write_text(
        "<Project><PropertyGroup>"
        f"<BaseIntermediateOutputPath>{base}/$(MSBuildProjectName)/</BaseIntermediateOutputPath>"
        "<MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>"
        f"<BaseOutputPath>{base}/$(MSBuildProjectName)/bin/</BaseOutputPath>"
        "<DefaultItemExcludes>$(DefaultItemExcludes);"
        "$(MSBuildProjectDirectory)/obj/**;$(MSBuildProjectDirectory)/bin/**"
        "</DefaultItemExcludes>"
        "</PropertyGroup></Project>",
        encoding="utf-8",
    )
    return props


def root_globals(
    entry: RestoreEntry, entry_dir: Path, assembly: Path, config: Path, packages: Path,
    token: str, source: Path,
) -> dict[str, str]:
    properties = {
        "Configuration": entry.configuration,
        "RuntimeIdentifier": entry.runtime_identifier or "",
        "RestoreForce": "true",
        "RestoreNoCache": "true",
        "RestoreConfigFile": str(config),
        "RestorePackagesPath": str(packages),
        "NuGetPackageRoot": str(packages),
        "CustomAfterDirectoryBuildProps": str(isolated_props(entry_dir)),
        "CustomAfterMicrosoftCommonTargets": str(source / GUARD_TOOL / "Observe.targets"),
        "CustomAfterMicrosoftCommonCrossTargetingTargets": str(source / GUARD_TOOL / "Observe.targets"),
        "DependencyGuardAssembly": str(assembly),
        "DependencyGuardScratch": str(entry_dir),
        "DependencyGuardToken": token,
    }
    for name, value in entry.properties:
        if name.casefold() in {key.casefold() for key in properties}:
            raise GuardFailure(f"matrix overrides guarded property {name}")
        properties[name] = value
    return properties


def property_switches(properties: dict[str, str]) -> list[str]:
    switches = []
    for name, value in properties.items():
        if (not re.fullmatch(r"[A-Za-z_][A-Za-z_0-9]*", name)
                or any(character in value for character in ";\r\n")):
            raise GuardFailure(f"cannot pass observed MSBuild global property {name!r}")
        switches.append(f"-p:{name}={value}")
    return switches


def reject_warning_suppression(value: object, label: str) -> None:
    if isinstance(value, list):
        if not all(isinstance(code, str) for code in value):
            raise GuardFailure(f"{label}: invalid NoWarn metadata")
        codes = value
    elif isinstance(value, str):
        codes = re.split(r"[;,\s]+", value)
    else:
        raise GuardFailure(f"{label}: invalid NoWarn metadata")
    if {"NU1603", "NU1605"} & {code.upper() for code in codes}:
        raise GuardFailure(f"{label}: NU1603/NU1605 suppression is forbidden")


def reject_asset_suppression(value: object, label: str) -> None:
    if isinstance(value, dict):
        for key, item in value.items():
            if key.casefold() == "nowarn":
                reject_warning_suppression(item, label)
            else:
                reject_asset_suppression(item, label)
    elif isinstance(value, list):
        for item in value:
            reject_asset_suppression(item, label)


def validate_observation(
    observation: Observation, root: Path, entry_dir: Path, packages: Path, config: Path,
    token: str,
) -> None:
    data = observation.evaluated
    if data.get("token") != token or data.get("project") != str(observation.project):
        raise GuardFailure(f"{observation.project}: MSBuild observation token/project mismatch")
    if data.get("restoreForce", "").casefold() != "true":
        raise GuardFailure(f"{observation.project}: RestoreForce was removed in a child")
    if data.get("restoreNoCache", "").casefold() != "true":
        raise GuardFailure(f"{observation.project}: RestoreNoCache was removed in a child")
    runtime = packages.parent
    cache_paths = {
        "NUGET_PACKAGES": packages,
        "NUGET_HTTP_CACHE_PATH": runtime / "http-cache",
        "NUGET_PLUGINS_CACHE_PATH": runtime / "plugins-cache",
        "NUGET_SCRATCH": runtime / "nuget-scratch",
        "TMPDIR": runtime / "tmp",
        "DOTNET_CLI_HOME": runtime / "dotnet-home",
    }
    if sys.platform == "darwin":
        cache_paths["DOTNET_SHARED_MEMORY_APPLICATION_GROUP_ID"] = runtime / "shared-memory"
    cache_environment = data.get("cacheEnvironment")
    if not isinstance(cache_environment, dict):
        raise GuardFailure(f"{observation.project}: missing isolated NuGet process cache paths")
    for name, expected in cache_paths.items():
        actual = cache_environment.get(name)
        if (not isinstance(actual, str) or not Path(actual).is_absolute()
                or Path(actual).resolve() != expected):
            raise GuardFailure(f"{observation.project}: unsafe {name}={actual!r}")
    if data.get("restoreRecursive", "").casefold() == "false":
        raise GuardFailure(f"{observation.project}: recursive restore was disabled")
    if data.get("buildProjectReferences", "").casefold() == "false":
        raise GuardFailure(f"{observation.project}: build project references were disabled")
    reject_warning_suppression(data.get("noWarn", ""), f"{observation.project} project")
    for kind in ("packageReferences", "packageDownloads", "projectReferences"):
        references = data.get(kind)
        if not isinstance(references, list):
            raise GuardFailure(f"{observation.project}: missing observed {kind}")
        for reference in references:
            if not isinstance(reference, dict) or not isinstance(reference.get("id"), str):
                raise GuardFailure(f"{observation.project}: malformed observed {kind}")
            reject_warning_suppression(
                reference.get("noWarn", ""),
                f"{observation.project} {kind} {reference['id']}",
            )
            if kind == "packageDownloads" and FORBIDDEN_PACKAGE.search(reference["id"]):
                raise GuardFailure(
                    f"{observation.project}: forbidden PackageDownload {reference['id']}"
                )
    for name in ("restoreFallbackFolders", "restoreAdditionalProjectFallbackFolders"):
        if data.get(name):
            raise GuardFailure(f"{observation.project}: unsafe {name}={data[name]}")
    sdk_path = data.get("msbuildToolsPath")
    if not isinstance(sdk_path, str) or not Path(sdk_path).is_absolute():
        raise GuardFailure(f"{observation.project}: missing MSBuild SDK path")
    sdk_packs = (Path(sdk_path).resolve().parent.parent / "library-packs").resolve()
    for source in data.get("restoreAdditionalProjectSources", "").split(";"):
        if source.strip() and (
            not sdk_packs.is_dir() or Path(source.strip()).resolve() != sdk_packs
        ):
            raise GuardFailure(f"{observation.project}: unsafe RestoreAdditionalProjectSources={source}")
    for name, expected in (("restorePackagesPath", packages),
                           ("nugetPackageRoot", packages),
                           ("restoreConfigFile", config)):
        value = data.get(name)
        if not isinstance(value, str) or not value or Path(value).resolve() != expected:
            raise GuardFailure(f"{observation.project}: unsafe {name}={value!r}")
    for name in ("assetsFile", "baseIntermediateOutputPath", "msbuildProjectExtensionsPath",
                 "restoreOutputPath", "baseOutputPath", "outputPath", "outDir",
                 "targetDir", "intermediateOutputPath"):
        value = data.get(name)
        if not isinstance(value, str):
            raise GuardFailure(f"{observation.project}: missing evaluated {name}")
        if not value:
            if name in {"baseIntermediateOutputPath", "msbuildProjectExtensionsPath"}:
                raise GuardFailure(f"{observation.project}: missing evaluated {name}")
            continue
        resolved = (observation.project.parent / value).resolve()
        if not resolved.is_relative_to(entry_dir):
            raise GuardFailure(f"{observation.project}: unsafe {name}={value!r}")
    for name in ("targetFramework", "runtimeIdentifier", "configuration", "platform",
                 "selfContained"):
        passed = observation.global_properties.get(name.casefold())
        if passed is not None and data.get(name, "").casefold() != passed.casefold():
            raise GuardFailure(f"{observation.project}: effective {name} differs from its MSBuild request")


def parse_trace(
    path: Path, root: Path, entry_dir: Path, packages: Path, config: Path, token: str,
) -> BuildTrace:
    if not path.is_file():
        raise GuardFailure(f"MSBuild logger did not produce {path}")
    active: dict[str, Observation] = {}
    observations: list[Observation] = []
    external_projects: set[Path] = set()
    evaluations: dict[int, tuple[Path, dict[str, str]]] = {}
    diagnostics: list[str] = []
    guard_errors: list[str] = []
    finished: bool | None = None
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
        for line in lines:
            event = json.loads(line)
            kind, context = event["kind"], event.get("context", "")
            if kind == "evaluation":
                evaluation_id = event.get("evaluation")
                properties = event.get("properties")
                if isinstance(evaluation_id, int) and isinstance(properties, dict):
                    evaluations[evaluation_id] = (
                        Path(event["project"]).resolve(),
                        {name.casefold(): value for name, value in properties.items()},
                    )
            elif kind == "start":
                source = event.get("project")
                if not isinstance(source, str):
                    raise GuardFailure(f"{path}: missing ProjectStarted path")
                project = Path(source).resolve()
                if project.is_relative_to(root) and project.suffix != ".csproj":
                    raise GuardFailure(f"unsupported active project instance: {project}")
                if not project.is_relative_to(root):
                    if project.suffix == ".csproj":
                        project_in_root(root, project)
                    external_projects.add(project)
                    continue
                if project.suffix not in {".csproj", ".fsproj", ".vbproj"}:
                    continue
                project_in_root(root, project)
                if project.suffix != ".csproj":
                    raise GuardFailure(f"unsupported active project type: {project}")
                raw = event.get("globals")
                if not isinstance(raw, dict) or not all(
                    isinstance(key, str) and isinstance(value, str)
                    for key, value in raw.items()
                ):
                    raise GuardFailure(f"{project}: MSBuild did not report global properties")
                properties = {key.casefold(): value for key, value in raw.items()}
                if len(properties) != len(raw) or not context or context in active:
                    raise GuardFailure(f"{project}: ambiguous MSBuild project instance")
                evaluation_id, instance_id = event.get("evaluation"), event.get("instance")
                if not isinstance(evaluation_id, int) or not isinstance(instance_id, int):
                    raise GuardFailure(f"{project}: MSBuild did not report its evaluation and instance IDs")
                observation = Observation(project, properties, {}, evaluation_id, instance_id)
                observation.targets.add(event.get("targets", ""))
                observations.append(observation)
                active[context] = observation
            elif kind == "record" and context in active:
                record = json.loads(event["data"])
                observation = active[context]
                if not isinstance(record, dict) or record.get("project") != str(observation.project):
                    raise GuardFailure(f"{observation.project}: untrusted MSBuild target record")
                if record.get("phase") == "instance":
                    if observation.evaluated and observation.evaluated != record:
                        raise GuardFailure(f"{observation.project}: conflicting instance observations")
                    observation.evaluated = record
                elif record.get("phase") == "assets":
                    asset_file = record.get("assetsFile")
                    if (observation.asset_record_path is not None
                            and observation.asset_record_path != asset_file):
                        raise GuardFailure(f"{observation.project}: ProjectAssetsFile changed during build")
                    observation.asset_record_path = asset_file
                    snapshot = record.get("snapshot")
                    if snapshot is not None:
                        snapshot_path = Path(snapshot).resolve()
                        if not snapshot_path.is_relative_to(entry_dir) or not snapshot_path.is_file():
                            raise GuardFailure(f"{observation.project}: asset snapshot escaped scratch")
                        observation.snapshots.append(snapshot_path)
                else:
                    raise GuardFailure(f"{observation.project}: unknown MSBuild observation phase")
            elif kind == "target" and context in active:
                active[context].executed_targets.add(event["name"])
            elif kind == "asset-task" and context in active:
                task_id, task_file = event.get("task"), event.get("file")
                if (not isinstance(task_id, int) or not isinstance(task_file, str)
                        or task_id in active[context].asset_tasks):
                    raise GuardFailure(f"{active[context].project}: ambiguous ResolvePackageAssets task")
                active[context].asset_tasks[task_id] = task_file
            elif kind == "asset-input" and context in active:
                task_id = event.get("task")
                if not isinstance(task_id, int) or task_id in active[context].asset_inputs:
                    raise GuardFailure(f"{active[context].project}: ambiguous ProjectAssetsFile task input")
                active[context].asset_inputs[task_id] = event
            elif kind == "finish" and context in active:
                active.pop(context)
            elif kind in {"warning", "error"}:
                code = event.get("code", "")
                if code in {"NU1603", "NU1605"}:
                    diagnostics.append(f"{code}: {event.get('message', '')}")
                if kind == "error" and event.get("message", "").startswith("Dependency guard:"):
                    guard_errors.append(event["message"])
            elif kind == "build-finished":
                finished = event.get("succeeded")
    except (OSError, ValueError, KeyError, TypeError) as error:
        if isinstance(error, GuardFailure):
            raise
        raise GuardFailure(f"invalid MSBuild trace {path}: {error}") from error
    if not lines or finished is None:
        raise GuardFailure(f"MSBuild logger did not finish trace {path}")
    if active:
        raise GuardFailure(f"MSBuild trace ended with active project instances: {path}")
    if not observations:
        raise GuardFailure(f"no active project instances recorded in {path}")
    if guard_errors:
        raise GuardFailure("; ".join(guard_errors))
    marked = {
        (item.project, item.evaluation_id, item.instance_id, item.key): item
        for item in observations if item.evaluated
    }
    for observation in observations:
        if not observation.evaluated:
            donor = marked.get((
                observation.project, observation.evaluation_id, observation.instance_id,
                observation.key,
            ))
            if donor is None:
                raise GuardFailure(
                    f"{observation.project}: instrumentation missing from actual child project "
                    f"{observation.global_properties}"
                )
            observation.evaluated = donor.evaluated
        if (observation.asset_record_path is not None
                and observation.evaluated["assetsFile"] != observation.asset_record_path):
            raise GuardFailure(f"{observation.project}: ProjectAssetsFile changed during build")
        evaluation = evaluations.get(observation.evaluation_id)
        if evaluation is not None and evaluation[0] == observation.project:
            evaluated = evaluation[1]
            for name, record_name in (
                ("projectassetsfile", "assetsFile"),
                ("msbuildprojectextensionspath", "msbuildProjectExtensionsPath"),
                ("restorepackagespath", "restorePackagesPath"),
                ("nugetpackageroot", "nugetPackageRoot"),
                ("targetframework", "targetFramework"),
                ("runtimeidentifier", "runtimeIdentifier"),
            ):
                current = evaluated.get(name, "")
                recorded = observation.evaluated.get(record_name, "")
                if current and isinstance(recorded, str) and current != recorded:
                    raise GuardFailure(
                        f"{observation.project}: evaluated {name} differs from recorded instance"
                    )
        elif observation not in marked.values():
            raise GuardFailure(
                f"{observation.project}: reused instance has no evaluation for its asset path"
            )
        validate_observation(observation, root, entry_dir, packages, config, token)
        sdk_task = (
            Path(observation.evaluated["msbuildToolsPath"]).resolve()
            / "Sdks/Microsoft.NET.Sdk/targets/Microsoft.PackageDependencyResolution.targets"
        )
        for task_id, task_file in observation.asset_tasks.items():
            if Path(task_file).resolve() != sdk_task:
                raise GuardFailure(
                    f"{observation.project}: ResolvePackageAssets came from an unexpected target"
                )
            actual_input = observation.asset_inputs.get(task_id)
            if actual_input is None:
                raise GuardFailure(
                    f"{observation.project}: ResolvePackageAssets did not report ProjectAssetsFile"
                )
            values = actual_input.get("values")
            if (not isinstance(values, list) or len(values) != 1
                    or not isinstance(values[0], str)
                    or not Path(values[0]).is_absolute()):
                raise GuardFailure(
                    f"{observation.project}: invalid ResolvePackageAssets ProjectAssetsFile input"
                )
            expected = observation.evaluated["assetsFile"]
            if not expected or Path(values[0]).resolve() != Path(expected).resolve():
                raise GuardFailure(
                    f"{observation.project}: ResolvePackageAssets ProjectAssetsFile "
                    "differs from the observed project instance"
                )
            snapshot = actual_input.get("snapshot")
            if (not isinstance(snapshot, str) or
                    not Path(snapshot).resolve().is_relative_to(entry_dir) or
                    not Path(snapshot).is_file()):
                raise GuardFailure(
                    f"{observation.project}: ResolvePackageAssets ProjectAssetsFile "
                    "was not captured inside guard scratch"
                )
            observation.consumed_snapshots.append(Path(snapshot).resolve())
    sdk_roots = {
        Path(item.evaluated["msbuildToolsPath"]).resolve() for item in observations
    }
    for project in sorted(external_projects):
        if not any(project.is_relative_to(sdk) for sdk in sdk_roots):
            raise GuardFailure(f"project reference escapes the repository: {project}")
    if diagnostics:
        raise GuardFailure(f"NU1603/NU1605 in real build: {'; '.join(diagnostics)}")
    return BuildTrace(observations, finished, diagnostics)


def check_package(dependency: str, project: Path, target: str) -> None:
    package_id, separator, version = dependency.partition("/")
    if FORBIDDEN_PACKAGE.search(package_id):
        raise GuardFailure(f"{project} ({target}): forbidden resolved package {dependency}")
    if package_id.casefold() == PROVIDER_ID and (
        not separator or version.casefold() != LOCAL3_VERSION
    ):
        raise GuardFailure(
            f"{project} ({target}): CoreSync.PostgreSQL resolved {version or '<none>'}; "
            f"requires exact {LOCAL3_VERSION}"
        )


def package_closure_digest(assets: dict, framework: str, rid: str) -> str:
    all_targets = assets["targets"]
    selected = f"{framework}/{rid}" if framework and rid else framework
    target_names = [selected] if selected else sorted(all_targets)
    package_targets = {
        name: {
            dependency: metadata
            for dependency, metadata in all_targets[name].items()
            if metadata.get("type") == "package"
        }
        for name in target_names
    }
    used = {dependency for dependencies in package_targets.values()
            for dependency in dependencies}
    libraries = {
        dependency: assets["libraries"][dependency]
        for dependency in sorted(used)
    }
    frameworks = assets["project"]["frameworks"]
    direct = {
        name: frameworks[name].get("dependencies", {})
        for name in (framework,) if name and name in frameworks
    }
    downloads = {
        name: frameworks[name].get("downloadDependencies", [])
        for name in (framework,) if name and name in frameworks
    }
    closure = {
        "targets": package_targets, "libraries": libraries,
        "direct": direct, "downloads": downloads,
    }
    return hashlib.sha256(json.dumps(
        closure, sort_keys=True, separators=(",", ":"),
    ).encode("utf-8")).hexdigest()


def inspect_assets(
    project: Path, observation: Observation, entry: RestoreEntry, root: Path,
    packages: Path, allowed_sources: set[str], *, snapshot: Path | None = None,
) -> tuple[Path, str]:
    data = observation.evaluated
    output = Path(data["msbuildProjectExtensionsPath"]).resolve()
    asset_path = Path(data["assetsFile"]).resolve() if data["assetsFile"] else output / "project.assets.json"
    if snapshot is not None and not snapshot.is_relative_to(Path(data["scratch"]).resolve()):
        raise GuardFailure(f"{project}: asset snapshot escaped the guard scratch directory")
    assets = read_object(snapshot or asset_path)
    spec = assets.get("project")
    if not isinstance(spec, dict) or not isinstance(spec.get("restore"), dict):
        raise GuardFailure(f"{project}: missing restored package specification")
    reject_asset_suppression(spec, str(project))
    restored = spec["restore"]
    if (Path(restored.get("projectPath", "")).resolve() != project
            or Path(restored.get("outputPath", "")).resolve() != output
            or Path(restored.get("packagesPath", "")).resolve() != packages):
        raise GuardFailure(f"{project}: assets are not for the observed project/output/package cache")
    if asset_path.parent != output:
        raise GuardFailure(f"{project}: ProjectAssetsFile differs from NuGet RestoreOutputPath")
    folders = assets.get("packageFolders")
    if not isinstance(folders, dict) or {
        Path(folder).resolve() for folder in folders
    } != {packages}:
        raise GuardFailure(f"{project}: assets contain a fallback folder or global package cache")
    if restored.get("fallbackFolders"):
        raise GuardFailure(f"{project}: assets contain fallback folders")
    sources = restored.get("sources", {})
    sdk_packs = (Path(data["msbuildToolsPath"]).resolve().parent.parent / "library-packs").resolve()
    permitted = allowed_sources | ({str(sdk_packs)} if sdk_packs.is_dir() else set())
    if not isinstance(sources, dict) or {
        name.rstrip("/") for name in sources
    } - permitted:
        raise GuardFailure(f"{project}: assets used a source outside the CI-equivalent NuGet config")

    targets, libraries = assets.get("targets"), assets.get("libraries")
    if not isinstance(targets, dict) or not targets or not isinstance(libraries, dict):
        raise GuardFailure(f"{project}: missing restored framework targets or libraries")
    framework = data["targetFramework"]
    rid = data["runtimeIdentifier"]
    frameworks = spec.get("frameworks")
    if not isinstance(frameworks, dict) or not frameworks:
        raise GuardFailure(f"{project}: missing restored package frameworks")
    for tfm, details in frameworks.items():
        if not isinstance(details, dict):
            raise GuardFailure(f"{project}: invalid restored framework {tfm}")
        downloads = details.get("downloadDependencies", [])
        if not isinstance(downloads, list):
            raise GuardFailure(f"{project}: malformed downloadDependencies for {tfm}")
        for download in downloads:
            if (not isinstance(download, dict)
                    or not isinstance(download.get("name"), str)
                    or not isinstance(download.get("version"), str)):
                raise GuardFailure(f"{project}: malformed downloadDependencies for {tfm}")
            name, version = download["name"], download["version"]
            if FORBIDDEN_PACKAGE.search(name):
                raise GuardFailure(
                    f"{project} ({tfm}): forbidden PackageDownload {name}/{version}"
                )
            if name.casefold() == PROVIDER_ID and version.casefold() != f"[{LOCAL3_VERSION}]":
                raise GuardFailure(
                    f"{project} ({tfm}): CoreSync.PostgreSQL PackageDownload "
                    f"requires exact [{LOCAL3_VERSION}]"
                )
            for exact_version in version.split(";"):
                exact = re.fullmatch(
                    r"\[\s*(?P<version>[A-Za-z0-9][A-Za-z0-9.+-]*)"
                    r"\s*(?:,\s*(?P=version)\s*)?\]",
                    exact_version, re.IGNORECASE,
                )
                if exact is None:
                    raise GuardFailure(
                        f"{project} ({tfm}): non-exact PackageDownload "
                        f"{name}/{exact_version}"
                    )
                version_number = exact.group("version").lower()
                archive = (
                    packages / name.casefold() / version_number
                    / f"{name.casefold()}.{version_number}.nupkg"
                )
                if not archive.is_file():
                    raise GuardFailure(
                        f"{project} ({tfm}): missing isolated PackageDownload "
                        f"{name}/{version_number}"
                    )
                if (name.casefold() == PROVIDER_ID
                        and hashlib.sha256(archive.read_bytes()).hexdigest() != PROVIDER_SHA256):
                    raise GuardFailure(f"{project}: downloaded provider SHA-256 differs")
    selected = f"{framework}/{rid}" if framework and rid else framework
    if selected and selected not in targets:
        raise GuardFailure(f"{project}: assets lack observed target {selected}")
    if project == project_in_root(root, entry.project) and not framework:
        for selected in entry.frameworks:
            if not any(key == selected or key.startswith(selected + "/")
                       for key in targets):
                raise GuardFailure(f"{project}: root assets lack matrix target {selected}")
    for target, dependencies in targets.items():
        if not isinstance(target, str) or not isinstance(dependencies, dict):
            raise GuardFailure(f"{project}: malformed restored target")
        for dependency in dependencies:
            if not isinstance(dependency, str):
                raise GuardFailure(f"{project}: malformed resolved package")
            check_package(dependency, project, target)
            if dependency not in libraries or not isinstance(dependencies[dependency], dict):
                raise GuardFailure(f"{project}: resolved target lacks a package library")
    resolved_ids: set[str] = set()
    for dependency, metadata in libraries.items():
        if not isinstance(dependency, str) or not isinstance(metadata, dict):
            raise GuardFailure(f"{project}: malformed resolved package library")
        if metadata.get("type") != "package":
            continue
        check_package(dependency, project, "libraries")
        resolved_ids.add(dependency.partition("/")[0].casefold())
        if dependency.casefold() == f"{PROVIDER_ID}/{LOCAL3_VERSION}":
            expected_path = f"{PROVIDER_ID}/{LOCAL3_VERSION}"
            if metadata.get("path") != expected_path:
                raise GuardFailure(f"{project}: unexpected CoreSync.PostgreSQL cache path")
            archive = packages / expected_path / PROVIDER_ARCHIVE.lower()
            try:
                actual_sha = hashlib.sha256(archive.read_bytes()).hexdigest()
            except OSError as error:
                raise GuardFailure(f"{project}: missing restored provider archive: {error}") from error
            if actual_sha != PROVIDER_SHA256:
                raise GuardFailure(f"{project}: restored CoreSync.PostgreSQL SHA-256 differs")
    if (project == root / "src/SentenceStudio.Shared/SentenceStudio.Shared.csproj"
            and ("net10.0" in targets or "net10.0/linux-x64" in targets)
            and (PROVIDER_ID not in resolved_ids or "coresync" not in resolved_ids)):
        raise GuardFailure(f"{project}: missing local3 provider or stable CoreSync base package")
    for diagnostic in assets.get("logs", []):
        if isinstance(diagnostic, dict) and diagnostic.get("code") in {"NU1603", "NU1605"}:
            raise GuardFailure(f"{project}: {diagnostic['code']} in freshly restored assets")
    if not isinstance(spec.get("frameworks"), dict):
        raise GuardFailure(f"{project}: missing restored package frameworks")
    return asset_path, package_closure_digest(assets, framework, rid)


def verify_fresh_restore(
    observation: Observation, trace: BuildTrace, root: Path, entry: RestoreEntry,
    entry_dir: Path, packages: Path, allowed_sources: set[str],
    visited: set[Path],
) -> tuple[Path, str]:
    if not trace.succeeded:
        raise GuardFailure(f"{observation.project}: observed restore did not succeed")
    project = observation.project
    output = Path(observation.evaluated["msbuildProjectExtensionsPath"]).resolve()
    graph_file = output / (project.name + ".nuget.dgspec.json")
    graph = read_object(graph_file)
    projects = graph.get("projects")
    if (not isinstance(projects, dict) or not projects
            or str(project) not in graph.get("restore", {})
            or str(project) not in projects):
        raise GuardFailure(f"{project}: forced restore did not produce a full NuGet graph")
    observations_by_project: dict[Path, list[Observation]] = {}
    for item in trace.observations:
        observations_by_project.setdefault(item.project, []).append(item)
        visited.add(item.project)
    for name, spec in projects.items():
        restored_project = project_in_root(root, name)
        inspected = observations_by_project.get(restored_project)
        if not inspected:
            raise GuardFailure(f"{restored_project}: NuGet graph project had no MSBuild instance")
        assets_path: Path | None = None
        for item in inspected:
            candidate, _ = inspect_assets(
                restored_project, item, entry, root, packages, allowed_sources,
            )
            if assets_path is not None and candidate != assets_path:
                raise GuardFailure(f"{restored_project}: conflicting restored assets paths")
            assets_path = candidate
        if read_object(assets_path)["project"] != spec:
            raise GuardFailure(f"{restored_project}: assets differ from the forced NuGet graph")
        cache = read_object(assets_path.parent / "project.nuget.cache")
        if cache.get("success") is not True or cache.get("projectFilePath") != str(restored_project):
            raise GuardFailure(f"{restored_project}: no successful fresh NuGet restore cache")
    asset_file, digest = inspect_assets(
        project, observation, entry, root, packages, allowed_sources,
    )
    return asset_file, digest


def run_observed(
    verb: str, project: Path, globals_: dict[str, str], entry_dir: Path,
    root: Path, packages: Path, config: Path, token: str, deadline: float,
    serial: int, isolation: Isolation,
) -> tuple[BuildTrace, int, str]:
    trace_path = entry_dir / f"{serial:04d}-{verb}.jsonl"
    assembly = globals_.get("DependencyGuardAssembly") or globals_.get("dependencyguardassembly")
    if not assembly:
        raise GuardFailure(f"{project}: logger assembly property removed from child")
    command = [
        "dotnet", verb, str(project), "--nologo", "--verbosity", "diagnostic",
        "-clp:ErrorsOnly", "-m:1", "-nr:false", "-warnaserror:NU1603,NU1605",
    ]
    if verb == "restore":
        command += [
            "--force", "--no-http-cache", "--packages", str(packages),
            "--configfile", str(config),
        ]
    else:
        command.append("--no-restore")
    command += property_switches(globals_)
    command.append(f"-logger:DependencyGuardLogger,{assembly};{trace_path}")
    code, output = run_command(command, project, entry_dir, deadline, isolation)
    trace = parse_trace(trace_path, root, entry_dir, packages, config, token)
    if trace.succeeded != (code == 0):
        raise GuardFailure(f"{project}: process status disagrees with independent MSBuild logger")
    return trace, code, output


def validate_entry(
    root: Path, entry: RestoreEntry, entry_dir: Path, assembly: Path, config: Path,
    packages: Path, allowed_sources: set[str], deadline: float,
    visited: set[Path], metrics: dict[str, int], isolation: Isolation,
) -> None:
    project = project_in_root(root, entry.project)
    token = uuid.uuid4().hex
    globals_ = root_globals(entry, entry_dir, assembly, config, packages, token, root)
    observed: dict[ContextKey, Observation] = {}
    pending: deque[Observation] = deque()
    closures: dict[ContextKey, tuple[Path, str]] = {}
    serial = 0

    def queue(trace: BuildTrace) -> None:
        for observation in trace.observations:
            visited.add(observation.project)
            key = observation.key
            previous = observed.get(key)
            if previous is not None and (
                previous.evaluated["assetsFile"] != observation.evaluated["assetsFile"]
                or previous.evaluated["msbuildProjectExtensionsPath"]
                   != observation.evaluated["msbuildProjectExtensionsPath"]
            ):
                raise GuardFailure(f"{observation.project}: one MSBuild context has conflicting assets paths")
            if previous is None:
                if len(observed) >= MAX_CONTEXTS_PER_ROOT:
                    raise GuardFailure(
                        f"too many actual project contexts for one root ({MAX_CONTEXTS_PER_ROOT})"
                    )
                observed[key] = observation
                pending.append(observation)

    def execute(verb: str, path: Path, properties: dict[str, str]) -> tuple[BuildTrace, int, str]:
        nonlocal serial
        serial += 1
        metric = ("root_restores" if serial == 1 else "observed_context_restores"
                  ) if verb == "restore" else "builds"
        metrics[metric] = metrics.get(metric, 0) + 1
        return run_observed(
            verb, path, properties, entry_dir, root, packages, config,
            token, deadline, serial, isolation,
        )

    restore, code, output = execute("restore", project, globals_)
    if code:
        raise command_failure("restore", project, code, output)
    root_context = next((item for item in restore.observations if item.project == project
                         and item.global_properties.get("configuration") == entry.configuration),
                        None)
    if root_context is None:
        raise GuardFailure(f"{project}: explicit matrix root was not observed in its restore")
    evaluated = root_context.evaluated
    selected = evaluated["targetFramework"] or evaluated["targetFrameworks"]
    actual_frameworks = {framework for framework in selected.split(";") if framework}
    if actual_frameworks != set(entry.frameworks):
        raise GuardFailure(
            f"{project}: root target frameworks {sorted(actual_frameworks)} "
            f"differ from matrix {sorted(entry.frameworks)}"
        )
    verify_fresh_restore(
        root_context, restore, root, entry, entry_dir, packages, allowed_sources, visited,
    )
    root_assets = Path(evaluated["msbuildProjectExtensionsPath"]) / "project.assets.json"
    restored_frameworks = read_object(root_assets)["project"]["restore"].get("originalTargetFrameworks")
    if (not isinstance(restored_frameworks, list)
            or set(restored_frameworks) != set(entry.frameworks)):
        raise GuardFailure(
            f"{project}: root assets target frameworks {restored_frameworks} "
            f"differ from matrix {list(entry.frameworks)}"
        )

    # Query-only NuGet project starts are covered by the recursive restore's
    # actual graph. Build starts supply the child globals used below.
    while True:
        build, build_code, build_output = execute("build", project, globals_)
        queue(build)
        restored_new_context = False
        while pending:
            context = pending.popleft()
            if context.key in closures:
                continue
            restored_new_context = True
            restored, restore_code, restore_output = execute(
                "restore", context.project, context.global_properties,
            )
            if restore_code:
                raise command_failure("restore", context.project, restore_code, restore_output)
            exact = next((item for item in restored.observations
                          if item.project == context.project and all(
                              item.global_properties.get(name) == value
                              for name, value in context.global_properties.items()
                          )), None)
            if exact is None:
                raise GuardFailure(
                    f"{context.project}: forced restore changed actual MSBuild global properties"
                )
            if exact.evaluated["packageReferences"] != context.evaluated["packageReferences"]:
                raise GuardFailure(
                    f"{context.project}: restore/build evaluated different PackageReferences"
                )
            closures[context.key] = verify_fresh_restore(
                exact, restored, root, entry, entry_dir, packages, allowed_sources, visited,
            )

        if build_code and restored_new_context:
            continue
        if build_code:
            raise command_failure("build", project, build_code, build_output)

        for instance in build.observations:
            if instance.key not in closures:
                raise GuardFailure(f"{instance.project}: built without a recorded package closure")
            asset_path, expected_hash = closures[instance.key]
            if (instance.evaluated["assetsFile"]
                    and Path(instance.evaluated["assetsFile"]).resolve() != asset_path):
                raise GuardFailure(f"{instance.project}: build used a different ProjectAssetsFile")
            needs_assets = (
                instance.evaluated["isCrossTargetingBuild"].casefold() != "true"
                and "Build" in instance.executed_targets
            )
            if needs_assets and not instance.consumed_snapshots:
                raise GuardFailure(
                    f"{instance.project}: built without an observed ResolvePackageAssets "
                    "ProjectAssetsFile closure"
                )
            for snapshot in instance.consumed_snapshots:
                try:
                    _, consumed_hash = inspect_assets(
                        instance.project, instance, entry, root, packages, allowed_sources,
                        snapshot=snapshot,
                    )
                except GuardFailure as error:
                    raise GuardFailure(
                        f"{instance.project}: invalid ResolvePackageAssets "
                        f"ProjectAssetsFile closure: {error}"
                    ) from error
                if consumed_hash != expected_hash:
                    raise GuardFailure(
                        f"{instance.project}: ResolvePackageAssets ProjectAssetsFile "
                        "contains a different package closure"
                    )
            for snapshot in instance.snapshots:
                _, marked_hash = inspect_assets(
                    instance.project, instance, entry, root, packages, allowed_sources,
                    snapshot=snapshot,
                )
                if marked_hash != expected_hash:
                    raise GuardFailure(
                        f"{instance.project}: imported target observed a different package closure"
                    )
        return


def validate(
    root: Path = ROOT, entries: tuple[RestoreEntry, ...] = ACTIVE_MATRIX,
    *, scratch_parent: Path | None = None, progress: bool = False,
    metrics: dict[str, int] | None = None,
) -> tuple[list[str], int]:
    root = root.resolve()
    scratch_parent = (scratch_parent or Path(tempfile.gettempdir())).resolve()
    if not scratch_parent.is_dir():
        return [f"scratch parent is not an existing directory: {scratch_parent}"], 0
    if scratch_parent == root or scratch_parent.is_relative_to(root):
        return [f"scratch parent must be outside the repository: {scratch_parent}"], 0
    if not entries:
        return ["active root matrix is empty"], 0

    visited: set[Path] = set()
    counts = metrics if metrics is not None else {}
    deadline = time.monotonic() + GUARD_TIMEOUT_SECONDS
    with tempfile.TemporaryDirectory(prefix="platform-neutral-build-", dir=scratch_parent) as temporary:
        scratch = Path(temporary)
        source, runtime = scratch / "source", scratch / "runtime"
        runtime.mkdir()
        isolation = Isolation(source, runtime, scratch / "write-denied-probe")
        try:
            source_snapshot(root, source)
            check_isolation(isolation, deadline)
            config, allowed_sources = prepare_restore_sources(source, runtime)
            packages = runtime / "packages"
            assembly = build_logger(config, packages, runtime, deadline, source, isolation)
            for index, entry in enumerate(entries, start=1):
                with tempfile.TemporaryDirectory(prefix=f"entry-{index:03d}-", dir=runtime) as directory:
                    started = time.monotonic()
                    validate_entry(
                        source, entry, Path(directory), assembly, config, packages,
                        allowed_sources, deadline, visited, counts, isolation,
                    )
                    if progress:
                        print(
                            f"BUILD {index}/{len(entries)}: {entry.label()} "
                            f"({time.monotonic() - started:.1f}s, "
                            f"{counts.get('root_restores', 0)} root restores, "
                            f"{counts.get('observed_context_restores', 0)} "
                            "observed-context restores)",
                            file=sys.stderr, flush=True,
                        )
                if (index < len(entries) and packages.is_dir()
                        and free_bytes(scratch) < PACKAGE_CACHE_HEADROOM_BYTES):
                    if packages.is_symlink() or packages.resolve() != runtime / "packages":
                        raise GuardFailure("isolated package cache path changed before eviction")
                    shutil.rmtree(packages)
                    counts["cache_evictions"] = counts.get("cache_evictions", 0) + 1
                    if progress:
                        print(
                            f"CACHE: released guard-owned packages after root {index} "
                            f"to retain the {MIN_FREE_BYTES // (1024 * 1024)} MiB "
                            "scratch reserve",
                            file=sys.stderr, flush=True,
                        )
        except (GuardFailure, OSError) as error:
            return [str(error)], len(visited)
        finally:
            unlock_snapshot_directories(source)
    return [], len(visited)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--scratch-parent", type=Path,
        help="Existing directory outside the checkout; only a guard-owned child is removed",
    )
    parser.add_argument("--list-matrix", action="store_true", help="Print the explicit active roots")
    args = parser.parse_args()
    if args.list_matrix:
        for entry in ACTIVE_MATRIX:
            print(f"{entry.label()} properties={dict(entry.properties)}")
        return 0
    started = time.monotonic()
    metrics: dict[str, int] = {}
    errors, count = validate(
        ROOT, ACTIVE_MATRIX, scratch_parent=args.scratch_parent,
        progress=True, metrics=metrics,
    )
    if errors:
        for error in errors:
            print(f"FAIL: {error}", file=sys.stderr)
        return 1
    print(
        f"PASS: {count} active projects; {len(ACTIVE_MATRIX)} root builds, "
        f"{metrics.get('root_restores', 0)} initial root restores, "
        f"{metrics.get('observed_context_restores', 0)} observed-context forced restores; "
        f"zero MAUI/Comet packages and exact CoreSync.PostgreSQL local3 "
        f"({time.monotonic() - started:.1f}s)."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
