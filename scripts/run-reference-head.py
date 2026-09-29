#!/usr/bin/env python3
"""Package the running AppHost's API tunnel for a standalone MAUI reference head."""

import argparse
import json
import os
import platform
import re
import shlex
import ssl
import subprocess
import sys
import uuid
import warnings
from http.client import HTTPException
from pathlib import Path
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, HTTPSHandler, Request, build_opener


REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
APPHOST_PROJECT = REPOSITORY_ROOT / "src/SentenceStudio.AppHost/SentenceStudio.AppHost.csproj"
TUNNEL_PORT_RESOURCE = "devtunnel-public-api-http"
PUBLIC_HEALTH_TIMEOUT_SECONDS = 10
ANDROID_SERIAL = re.compile(r"emulator-[0-9]{4,5}\Z")
SIMULATOR_UDID = re.compile(
    r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\Z"
)
HEAD_PROJECTS = {
    "android": REPOSITORY_ROOT / "src/SentenceStudio.Android/SentenceStudio.Android.csproj",
    "ios": REPOSITORY_ROOT / "src/SentenceStudio.iOS/SentenceStudio.iOS.csproj",
    "macos": REPOSITORY_ROOT / "src/SentenceStudio.MacOS/SentenceStudio.MacOS.csproj",
}
NATIVE_BASE_CONFIG = REPOSITORY_ROOT / "src/SentenceStudio.AppLib/appsettings.json"
NATIVE_DEBUG_CONFIG = REPOSITORY_ROOT / "src/SentenceStudio.AppLib/appsettings.Development.json"
NATIVE_CREDENTIAL_KEYS = ("OpenAIKey", "ElevenLabsKey")
PLACEHOLDER_MARKER = re.compile(
    r"(?:^|[-_ ])(?:your|placeholder|not[-_ ]configured|change[-_ ]?me|"
    r"replace[-_ ]?me|insert|enter|example|sample|dummy|fake|todo|test)(?:$|[-_ ])",
    re.IGNORECASE,
)


class ReferenceHeadError(Exception):
    pass


class NoRedirectHandler(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, new_url):
        return None


def require_apphost(value):
    path = Path(value)
    if not path.is_absolute():
        raise ReferenceHeadError("--apphost must be the absolute target-worktree AppHost project path.")
    try:
        actual = path.resolve(strict=True)
    except (OSError, RuntimeError) as exception:
        raise ReferenceHeadError("The target-worktree AppHost project does not exist.") from exception
    if actual != APPHOST_PROJECT.resolve() or not actual.is_file():
        raise ReferenceHeadError(
            "--apphost must point to this script's target-worktree AppHost, not another running stack."
        )
    return actual


def require_output_dir(value):
    path = Path(value)
    if not path.is_absolute():
        raise ReferenceHeadError("--output-dir must be an absolute, existing session-scratch directory.")
    try:
        actual = path.resolve(strict=True)
    except (OSError, RuntimeError) as exception:
        raise ReferenceHeadError("--output-dir must already exist.") from exception
    if not actual.is_dir() or actual == REPOSITORY_ROOT or REPOSITORY_ROOT in actual.parents:
        raise ReferenceHeadError("--output-dir must be a session-scratch directory outside the repository.")
    forbidden_roots = ("/tmp", "/private/tmp", "/var/tmp", "/private/var/tmp",
                       "/var/folders", "/private/var/folders")
    if any(actual == Path(root) or Path(root) in actual.parents for root in forbidden_roots):
        raise ReferenceHeadError("--output-dir must not be an OS temporary directory.")
    if any(token in str(actual) for token in (";", "$(", "@(", "%(", "'", "\n", "\r")):
        raise ReferenceHeadError("--output-dir contains characters unsafe in an MSBuild property.")
    return actual


def validate_tunnel_url(value):
    if not isinstance(value, str) or value != value.strip():
        raise ReferenceHeadError("Tunnel discovery returned an invalid public HTTPS URL.")
    try:
        parsed = urlsplit(value)
        port = parsed.port
    except ValueError as exception:
        raise ReferenceHeadError("Tunnel discovery returned an invalid public HTTPS URL.") from exception
    hostname = parsed.hostname or ""
    if (parsed.scheme != "https" or not hostname.endswith(".devtunnels.ms")
            or hostname.startswith("inspect.")
            or port not in (None, 443) or parsed.path not in ("", "/")
            or parsed.query or parsed.fragment or "@" in parsed.netloc):
        raise ReferenceHeadError(
            "The API tunnel must be a public root HTTPS *.devtunnels.ms URL, never localhost, "
            "10.0.2.2, or a production URL. Android's 10.0.2.2 alias applies only to "
            "direct host loopback, not a public dev tunnel."
        )
    return value


def port_uri_from_devtunnel_show(output, api_port):
    try:
        snapshot = json.loads(output)
    except (TypeError, ValueError):
        raise ReferenceHeadError("devtunnel show did not return valid JSON; no config was generated.") from None
    tunnel = snapshot.get("tunnel") if isinstance(snapshot, dict) else None
    ports = tunnel.get("ports") if isinstance(tunnel, dict) else None
    if not isinstance(ports, list):
        raise ReferenceHeadError("devtunnel show JSON is missing the tunnel ports array.")
    matches = [
        port for port in ports
        if isinstance(port, dict)
        and type(port.get("portNumber")) is int
        and port["portNumber"] == api_port
        and port.get("protocol") == "http"
    ]
    if len(matches) != 1:
        raise ReferenceHeadError(
            "devtunnel show has no unique HTTP port matching the API's local HTTP port."
        )
    return validate_tunnel_url(matches[0].get("portUri"))


def show_tunnel_port_uri(tunnel_id, api_port):
    try:
        result = subprocess.run(
            ["devtunnel", "show", tunnel_id, "--json"],
            cwd=REPOSITORY_ROOT, check=True, capture_output=True, text=True, timeout=30,
        )
    except FileNotFoundError:
        raise ReferenceHeadError(
            "The devtunnel CLI is required to resolve the API port URL; no config was generated."
        ) from None
    except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired):
        raise ReferenceHeadError(
            "Could not query the running API dev tunnel with devtunnel show. "
            "Check CLI access and authentication; no config was generated."
        ) from None
    return port_uri_from_devtunnel_show(result.stdout, api_port)


def endpoint_from_tunnel_resources(resources, port_resource):
    parents = [
        resource for resource in resources
        if isinstance(resource, dict)
        and resource.get("resourceType") == "DevTunnel"
        and isinstance(resource.get("name"), str)
        and (resource["name"] == "devtunnel-public"
             or resource["name"].startswith("devtunnel-public-"))
    ]
    if len(parents) != 1:
        raise ReferenceHeadError("The public API dev tunnel parent is absent or ambiguous.")
    parent = parents[0]
    if parent.get("state") != "Running":
        raise ReferenceHeadError("The public API dev tunnel parent is not running yet.")
    parent_properties = parent.get("properties")
    tunnel_id = parent_properties.get("TunnelId") if isinstance(parent_properties, dict) else None
    if (not isinstance(tunnel_id, str) or not tunnel_id or tunnel_id != tunnel_id.strip()
            or tunnel_id.startswith("-") or not tunnel_id.isprintable()):
        raise ReferenceHeadError("The public API dev tunnel parent is missing a valid tunnel ID.")

    target = port_resource["properties"]["TargetResource"]
    projects = [
        resource for resource in resources
        if isinstance(resource, dict)
        and resource.get("resourceType") == "Project"
        and resource.get("displayName") == target
    ]
    if len(projects) != 1:
        raise ReferenceHeadError("The API project is absent or ambiguous in aspire describe.")
    project = projects[0]
    if project.get("state") not in (None, "Running") or project.get("healthStatus") not in (None, "Healthy"):
        raise ReferenceHeadError("The API project is not running and healthy yet.")
    urls = project.get("urls")
    target_endpoint = port_resource["properties"]["TargetEndpoint"]
    matches = [
        entry.get("url") for entry in urls
        if isinstance(entry, dict) and entry.get("name") == target_endpoint
    ] if isinstance(urls, list) else []
    if len(matches) != 1 or not isinstance(matches[0], str) or matches[0] != matches[0].strip():
        raise ReferenceHeadError("The API project has no unique local HTTP endpoint.")
    try:
        parsed = urlsplit(matches[0])
        api_port = parsed.port
    except ValueError:
        raise ReferenceHeadError("The API project has an invalid local HTTP endpoint.") from None
    if (parsed.scheme != "http" or parsed.hostname not in ("localhost", "127.0.0.1", "::1")
            or api_port is None or api_port < 1 or parsed.path not in ("", "/")
            or parsed.query or parsed.fragment or "@" in parsed.netloc):
        raise ReferenceHeadError("The API project has an invalid local HTTP endpoint.")
    return show_tunnel_port_uri(tunnel_id, api_port)


def endpoint_from_describe(output):
    try:
        snapshot = json.loads(output)
    except (TypeError, ValueError) as exception:
        raise ReferenceHeadError(
            "aspire describe did not return JSON with a resources array; no config was generated."
        ) from exception
    resources = snapshot.get("resources") if isinstance(snapshot, dict) else None
    if not isinstance(resources, list):
        raise ReferenceHeadError("aspire describe JSON is missing the resources array.")
    matches = [
        resource for resource in resources
        if isinstance(resource, dict) and resource.get("name") == TUNNEL_PORT_RESOURCE
    ]
    if len(matches) != 1:
        raise ReferenceHeadError(
            "The API dev tunnel port is absent or ambiguous. Run this AppHost with "
            "LocalDevTunnel__Enabled=true and wait for devtunnel-public-api-http."
        )
    resource = matches[0]
    properties = resource.get("properties")
    if (resource.get("resourceType") != "DevTunnelPort"
            or not isinstance(properties, dict)
            or properties.get("TargetResource") != "api"
            or properties.get("TargetEndpoint") != "http"
            or properties.get("Protocol") != "http"):
        raise ReferenceHeadError(
            "The dev tunnel port does not reference api/http; refusing to package another endpoint."
        )
    if resource.get("state") != "Running" or resource.get("healthStatus") != "Healthy":
        raise ReferenceHeadError("The API dev tunnel port is not running and healthy yet.")

    urls = resource.get("urls")
    tunnel_urls = [
        entry.get("url") for entry in urls
        if isinstance(entry, dict) and entry.get("name") == "tunnel"
    ] if isinstance(urls, list) else []
    if len(tunnel_urls) == 1:
        return validate_tunnel_url(tunnel_urls[0])
    if urls != []:
        raise ReferenceHeadError(
            "The API dev tunnel port has no unique 'tunnel' URL in aspire describe."
        )
    return endpoint_from_tunnel_resources(resources, resource)


def describe_tunnel(apphost):
    command = [
        "aspire", "describe", "--apphost", str(apphost), "--format", "Json",
        "--non-interactive", "--nologo",
    ]
    try:
        result = subprocess.run(
            command, cwd=REPOSITORY_ROOT, check=True, capture_output=True, text=True, timeout=30
        )
    except (FileNotFoundError, subprocess.CalledProcessError, subprocess.TimeoutExpired):
        # aspire describe can include other resources' environment values. Never echo its raw output.
        raise ReferenceHeadError(
            "Could not describe the API tunnel in this running AppHost. "
            "Check that the AppHost is running with LocalDevTunnel__Enabled=true "
            "and the devtunnel-public-api-http port has started."
        ) from None
    return endpoint_from_describe(result.stdout)


def verify_public_api_health(tunnel_url):
    health_url = validate_tunnel_url(tunnel_url).rstrip("/") + "/health"
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error")
            opener = build_opener(
                NoRedirectHandler(), HTTPSHandler(context=ssl.create_default_context())
            )
            with opener.open(
                Request(health_url, method="GET"), timeout=PUBLIC_HEALTH_TIMEOUT_SECONDS
            ) as response:
                if response.status != 200:
                    raise ReferenceHeadError(
                        "The public API dev tunnel /health did not return HTTP 200; "
                        "no config was generated."
                    )
    except (OSError, HTTPException, ValueError, Warning):
        raise ReferenceHeadError(
            "Could not verify the public API dev tunnel /health with valid TLS and HTTP 200; "
            "no config was generated."
        ) from None


def write_generated_config(output_dir, tunnel_url):
    contents = {"Services": {"api": {"https": [validate_tunnel_url(tunnel_url)]}}}
    for _ in range(3):
        destination = output_dir / ("reference-head-" + uuid.uuid4().hex + ".json")
        try:
            descriptor = os.open(destination, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        except FileExistsError:
            continue
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            json.dump(contents, stream, indent=2)
            stream.write("\n")
        return destination
    raise ReferenceHeadError("Could not allocate a unique reference-head config filename.")


def native_settings_from_file(path):
    def reject_duplicate_keys(pairs):
        result = {}
        seen = set()
        for name, value in pairs:
            normalized = name.casefold()
            if normalized in seen:
                raise ValueError("Duplicate JSON key")
            seen.add(normalized)
            result[name] = value
        return result

    try:
        with path.open(encoding="utf-8") as stream:
            config = json.load(stream, object_pairs_hook=reject_duplicate_keys)
    except (OSError, UnicodeError, ValueError):
        raise ReferenceHeadError(
            f"Cannot validate native credential keys in {path.name}; expected readable JSON."
        ) from None

    if not isinstance(config, dict):
        raise ReferenceHeadError(f"Native {path.name} must contain a JSON object.")
    sections = [value for name, value in config.items() if name.casefold() == "settings"]
    if len(sections) > 1 or (sections and not isinstance(sections[0], dict)):
        raise ReferenceHeadError(f"Native {path.name} has an invalid Settings section.")
    settings = sections[0] if sections else {}
    credentials = {}
    for key in NATIVE_CREDENTIAL_KEYS:
        matches = [value for name, value in settings.items() if name.casefold() == key.casefold()]
        if len(matches) > 1:
            raise ReferenceHeadError(f"Native {path.name} has an ambiguous Settings:{key} key.")
        if matches:
            credentials[key] = matches[0]
    return credentials


def credential_is_configured(value):
    return (
        isinstance(value, str)
        and len(value) >= 16
        and value == value.strip()
        and value.isprintable()
        and not any(character.isspace() for character in value)
        and not any(marker in value for marker in ("...", "<", ">", "$", "{{", "}}"))
        and not PLACEHOLDER_MARKER.search(value)
    )


def require_mobile_credentials():
    # Installed mobile apps cannot rely on inheriting the launcher's shell environment.
    if not NATIVE_BASE_CONFIG.is_file():
        raise ReferenceHeadError(
            "Android/iOS --launch requires the private "
            "src/SentenceStudio.AppLib/appsettings.json with Settings:OpenAIKey and "
            "Settings:ElevenLabsKey. The generated tunnel JSON must remain URL-only."
        )
    base = native_settings_from_file(NATIVE_BASE_CONFIG)
    development = (
        native_settings_from_file(NATIVE_DEBUG_CONFIG) if NATIVE_DEBUG_CONFIG.exists() else {}
    )
    for key in NATIVE_CREDENTIAL_KEYS:
        source = NATIVE_DEBUG_CONFIG if key in development else NATIVE_BASE_CONFIG
        value = development.get(key, base.get(key))
        if not credential_is_configured(value):
            raise ReferenceHeadError(
                f"Settings:{key} in {source.name} is missing or a placeholder. "
                "Supply a non-placeholder credential in private appsettings.json; "
                "remove invalid Development overrides. Never add credentials to "
                "the generated tunnel JSON."
            )


def validate_device_selector(head, device):
    if head == "android" and not ANDROID_SERIAL.fullmatch(device):
        raise ReferenceHeadError("--device must be an explicit Android emulator serial (emulator-5554).")
    if head == "ios" and not SIMULATOR_UDID.fullmatch(device):
        raise ReferenceHeadError("--device must be an explicit iOS simulator UDID.")


def require_running_emulator(serial):
    try:
        result = subprocess.run(
            ["adb", "-s", serial, "shell", "getprop", "ro.kernel.qemu"],
            capture_output=True, text=True, check=True, timeout=15,
        )
    except (FileNotFoundError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as exception:
        raise ReferenceHeadError("The explicitly selected Android emulator is not reachable.") from exception
    if result.stdout.strip() != "1":
        raise ReferenceHeadError("The selected Android target is not a running emulator.")


def require_booted_simulator(udid):
    try:
        result = subprocess.run(
            ["xcrun", "simctl", "list", "devices", "booted", "--json"],
            capture_output=True, text=True, check=True, timeout=15,
        )
        devices = json.loads(result.stdout)["devices"]
    except (FileNotFoundError, subprocess.CalledProcessError, subprocess.TimeoutExpired,
            ValueError, KeyError, TypeError) as exception:
        raise ReferenceHeadError("Could not enumerate booted iOS simulators.") from exception
    if not isinstance(devices, dict) or not any(
        isinstance(device, dict) and device.get("udid", "").lower() == udid.lower()
        and device.get("state") == "Booted"
        for group in devices.values() if isinstance(group, list) for device in group
    ):
        raise ReferenceHeadError("The explicitly selected iOS simulator is not booted.")


def launch_command(head, device, config):
    project = str(HEAD_PROJECTS[head])
    if head == "macos":
        return [
            "dotnet", "run", "--project", project, "-f", "net11.0-macos", "-c", "Debug",
            "--no-launch-profile", f"--property:ReferenceHeadConfig={config}",
        ]
    command = [
        "dotnet", "build", project, "-t:Run", "-f", f"net11.0-{head}", "-c", "Debug",
        f"-p:ReferenceHeadConfig={config}",
    ]
    if head == "android":
        command.append(f"-p:Device={device}")
    else:
        machine = platform.machine().lower()
        if machine not in ("arm64", "aarch64", "x86_64", "amd64"):
            raise ReferenceHeadError("Unknown Mac architecture; cannot select a simulator-only iOS RID.")
        arch = "arm64" if machine in ("arm64", "aarch64") else "x64"
        command.extend([
            f"-p:RuntimeIdentifier=iossimulator-{arch}",
            f"-p:_DeviceName=:v2:udid={device}",
        ])
    return command


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apphost", required=True, help="Absolute target-worktree AppHost .csproj path")
    parser.add_argument("--output-dir", required=True, help="Existing absolute session-scratch directory")
    parser.add_argument("--platform", choices=tuple(HEAD_PROJECTS), help="Standalone head to run")
    parser.add_argument("--device", help="Android emulator serial or booted iOS simulator UDID")
    parser.add_argument("--launch", action="store_true", help="Build and run on the selected emulator/simulator")
    args = parser.parse_args(argv)

    try:
        apphost = require_apphost(args.apphost)
        output_dir = require_output_dir(args.output_dir)
        if args.device and args.platform not in ("android", "ios"):
            raise ReferenceHeadError("--device is only valid for Android or iOS.")
        if args.platform in ("android", "ios") and not args.device:
            raise ReferenceHeadError("--device is required for Android and iOS; no device is auto-selected.")
        if args.launch and args.platform not in ("android", "ios"):
            raise ReferenceHeadError(
                "--launch is emulator/simulator-only. The macOS AppKit head uses Captain's "
                "existing app identity/data; request explicit per-turn authorization before "
                "manually executing its printed command in a safe environment."
            )
        if args.device:
            validate_device_selector(args.platform, args.device)
        if args.launch:
            require_mobile_credentials()
        if args.launch and args.platform == "android":
            require_running_emulator(args.device)
        if args.launch and args.platform == "ios":
            require_booted_simulator(args.device)

        tunnel = describe_tunnel(apphost)
        verify_public_api_health(tunnel)
        config = write_generated_config(output_dir, tunnel)
        print(f"Generated untracked reference-head config: {config}", flush=True)
        if args.platform is None:
            print("Select --platform and an explicit simulator/emulator --device to print or run a command.")
            return 0

        command = launch_command(args.platform, args.device, config)
        print("Build/run command: " + shlex.join(command), flush=True)
        if args.platform == "macos":
            print(
                "NOT executed: macOS uses Captain's existing app identity and data. "
                "Manual launch requires explicit per-turn authorization. Desktop direct clients "
                "need AI__OpenAI__ApiKey and ElevenLabsKey in the app process environment; "
                "AiService also needs Settings__OpenAIKey or a private Settings:OpenAIKey."
            )
            return 0
        if not args.launch:
            print("NOT executed: pass --launch only after verifying the selected emulator/simulator.")
            return 0

        environment = os.environ.copy()
        if args.platform == "android":
            environment["ANDROID_SERIAL"] = args.device
        subprocess.run(command, cwd=REPOSITORY_ROOT, env=environment, check=True)
        print("Launch command returned. Use maui devflow wait and inspect the selected app.")
        return 0
    except subprocess.CalledProcessError:
        print("Reference-head build/launch failed; no other device will be selected.", file=sys.stderr)
        return 1
    except ReferenceHeadError as exception:
        print(f"Reference-head launch refused: {exception}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
