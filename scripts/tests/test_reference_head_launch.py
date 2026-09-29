"""Offline fixtures for the opt-in standalone MAUI reference-head launcher."""

import contextlib
import importlib.util
import io
import json
import ssl
import stat
import subprocess
import unittest
import uuid
import warnings
import xml.etree.ElementTree as ET
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "run-reference-head.py"
SPEC = importlib.util.spec_from_file_location("run_reference_head", SCRIPT)
launcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(launcher)

TUNNEL_URL = "https://test-tunnel-63449.use.devtunnels.ms/"
TUNNEL_ID = "test-tunnel-id"
API_HTTP_PORT = 63449
IOS_UDID = "A1B2C3D4-1234-5678-90AB-ABCDEF123456"
SYNTHETIC_OPENAI_KEY = "sk-fixture-only-no-real-key-0123456789"
SYNTHETIC_ELEVENLABS_KEY = "el-fixture-only-no-real-key-0123456789"
SYNTHETIC_OTHER_SECRET = "unrelated-fixture-only-no-real-key-0123456789"
VALID_NATIVE_SETTINGS = {
    "OpenAIKey": SYNTHETIC_OPENAI_KEY,
    "ElevenLabsKey": SYNTHETIC_ELEVENLABS_KEY,
    "OtherSecret": SYNTHETIC_OTHER_SECRET,
}


def port_resource(url=TUNNEL_URL):
    return {
        "name": "devtunnel-public-api-http",
        "resourceType": "DevTunnelPort",
        "state": "Running",
        "healthStatus": "Healthy",
        "properties": {"TargetResource": "api", "TargetEndpoint": "http", "Protocol": "http"},
        "urls": [
            {"name": "Inspect", "url": "https://inspect.devtunnels.ms/"},
            {"name": "tunnel", "url": url},
        ],
        "environment": {"SHOULD_NOT_BE_LOGGED": "private-example"},
    }


def describe_response(resource=None):
    return json.dumps({"resources": [resource if resource is not None else port_resource()]})


def live_describe_snapshot():
    port = port_resource()
    port["urls"] = []
    return {
        "resources": [
            port,
            {
                "name": "devtunnel-public-dynamic",
                "resourceType": "DevTunnel",
                "state": "Running",
                "healthStatus": "Unhealthy",
                "properties": {"TunnelId": TUNNEL_ID},
                "urls": [],
            },
            {
                "name": "api-dynamic",
                "displayName": "api",
                "resourceType": "Project",
                "state": "Running",
                "healthStatus": "Healthy",
                "urls": [
                    {"name": "http", "url": f"http://localhost:{API_HTTP_PORT}"},
                    {"name": "https", "url": "https://localhost:63456"},
                ],
            },
        ],
        "environment": {"SHOULD_NOT_BE_LOGGED": "private-example"},
    }


def devtunnel_show_response(ports=None):
    if ports is None:
        ports = [
            {"portNumber": API_HTTP_PORT, "protocol": "http", "portUri": TUNNEL_URL},
            {
                "portNumber": 63456,
                "protocol": "http",
                "portUri": "https://other-test-tunnel-63456.use.devtunnels.ms/",
            },
        ]
    return json.dumps({
        "tunnel": {
            "ports": ports,
            "accessControl": {"SHOULD_NOT_BE_LOGGED": "private-example"},
        }
    })


class ReferenceHeadLauncherTests(unittest.TestCase):
    def setUp(self):
        # Scratch stays in the project, never the OS temporary directory.
        self.scratch = Path(__file__).resolve().parent / (
            ".reference-head-fixture-" + uuid.uuid4().hex
        )
        self.scratch.mkdir()
        self.health_patch = mock.patch.object(launcher, "build_opener")
        self.mock_build_opener = self.health_patch.start()
        self.addCleanup(self.health_patch.stop)
        self.mock_health_opener = self.mock_build_opener.return_value
        self.mock_health_opener.open.return_value.__enter__.return_value.status = 200

    def tearDown(self):
        for created in self.scratch.iterdir():
            self.assertTrue(created.name.startswith("reference-head-"))
            self.assertEqual(created.suffix, ".json")
            created.unlink()
        self.scratch.rmdir()

    @contextlib.contextmanager
    def native_credentials(self, settings=VALID_NATIVE_SETTINGS, development=None):
        base = self.scratch / "native-fixture-appsettings.json"
        debug = self.scratch / "native-fixture-development.json"
        if settings is not None:
            base.write_text(json.dumps({"Settings": settings}), encoding="utf-8")
        if development is not None:
            debug.write_text(json.dumps({"Settings": development}), encoding="utf-8")
        try:
            with mock.patch.object(launcher, "NATIVE_BASE_CONFIG", base), \
                    mock.patch.object(launcher, "NATIVE_DEBUG_CONFIG", debug):
                yield base, debug
        finally:
            base.unlink(missing_ok=True)
            debug.unlink(missing_ok=True)

    def test_aspire_describe_uses_target_apphost_to_read_all_resources(self):
        completed = subprocess.CompletedProcess([], 0, stdout=describe_response())
        with mock.patch.object(launcher.subprocess, "run", return_value=completed) as run:
            endpoint = launcher.describe_tunnel(launcher.APPHOST_PROJECT)
        self.assertEqual(endpoint, TUNNEL_URL)
        self.assertEqual(
            run.call_args.args[0],
            [
                "aspire", "describe", "--apphost", str(launcher.APPHOST_PROJECT),
                "--format", "Json", "--non-interactive", "--nologo",
            ],
        )
        self.assertEqual(run.call_count, 1)
        self.assertTrue(run.call_args.kwargs["capture_output"])

    def test_live_aspire_shape_resolves_exact_public_uri_for_matching_api_http_port(self):
        responses = [
            subprocess.CompletedProcess([], 0, stdout=json.dumps(live_describe_snapshot())),
            subprocess.CompletedProcess([], 0, stdout=devtunnel_show_response()),
        ]
        with mock.patch.object(launcher.subprocess, "run", side_effect=responses) as run:
            endpoint = launcher.describe_tunnel(launcher.APPHOST_PROJECT)
        self.assertEqual(endpoint, TUNNEL_URL)
        self.assertEqual(run.call_count, 2)
        self.assertEqual(
            run.call_args_list[1].args[0],
            ["devtunnel", "show", TUNNEL_ID, "--json"],
        )
        self.assertTrue(run.call_args_list[1].kwargs["capture_output"])

    def test_unhealthy_running_parent_with_public_200_emits_tls_checked_config(self):
        responses = [
            subprocess.CompletedProcess([], 0, stdout=json.dumps(live_describe_snapshot())),
            subprocess.CompletedProcess([], 0, stdout=devtunnel_show_response()),
        ]
        with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(launcher.subprocess, "run", side_effect=responses) as run, \
                contextlib.redirect_stdout(io.StringIO()):
            status = launcher.main([
                "--apphost", str(launcher.APPHOST_PROJECT),
                "--output-dir", str(self.scratch), "--platform", "macos",
            ])
        self.assertEqual(status, 0)
        self.assertEqual(run.call_count, 2)
        self.assertEqual(
            run.call_args_list[1].args[0],
            ["devtunnel", "show", TUNNEL_ID, "--json"],
        )
        self.mock_build_opener.assert_called_once()
        redirect_handler, https_handler = self.mock_build_opener.call_args.args
        self.assertIsInstance(redirect_handler, launcher.NoRedirectHandler)
        self.assertIsNone(
            redirect_handler.redirect_request(
                None, None, 302, "Found", {}, "https://other.example/health"
            )
        )
        self.assertEqual(https_handler._context.verify_mode, ssl.CERT_REQUIRED)
        self.assertTrue(https_handler._context.check_hostname)
        request = self.mock_health_opener.open.call_args.args[0]
        self.assertEqual(request.get_method(), "GET")
        self.assertEqual(request.full_url, TUNNEL_URL + "health")
        self.assertEqual(
            self.mock_health_opener.open.call_args.kwargs["timeout"],
            launcher.PUBLIC_HEALTH_TIMEOUT_SECONDS,
        )
        config = next(self.scratch.glob("reference-head-*.json"))
        self.assertEqual(
            json.loads(config.read_text(encoding="utf-8")),
            {"Services": {"api": {"https": [TUNNEL_URL]}}},
        )

    def test_unhealthy_parent_public_non_200_refuses_config_before_launch(self):
        for http_status in (204, 302, 404, 503):
            with self.subTest(http_status=http_status):
                self.mock_health_opener.open.return_value.__enter__.return_value.status = http_status
                responses = [
                    subprocess.CompletedProcess([], 0, stdout=json.dumps(live_describe_snapshot())),
                    subprocess.CompletedProcess([], 0, stdout=devtunnel_show_response()),
                ]
                stdout, stderr = io.StringIO(), io.StringIO()
                with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                        mock.patch.object(launcher.subprocess, "run", side_effect=responses) as run, \
                        contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                    status = launcher.main([
                        "--apphost", str(launcher.APPHOST_PROJECT),
                        "--output-dir", str(self.scratch), "--platform", "macos",
                    ])
                self.assertEqual(status, 2)
                self.assertEqual(run.call_count, 2)
                self.assertIn("HTTP 200", stderr.getvalue())
                self.assertNotIn("Generated", stdout.getvalue())
                self.assertEqual(list(self.scratch.iterdir()), [])

    def test_stopped_parent_refuses_before_probe_or_config(self):
        snapshot = live_describe_snapshot()
        snapshot["resources"][1]["state"] = "Stopped"
        with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(
                    launcher.subprocess, "run",
                    return_value=subprocess.CompletedProcess([], 0, stdout=json.dumps(snapshot)),
                ) as run, contextlib.redirect_stderr(io.StringIO()):
            status = launcher.main([
                "--apphost", str(launcher.APPHOST_PROJECT),
                "--output-dir", str(self.scratch), "--platform", "macos",
            ])
        self.assertEqual(status, 2)
        self.assertEqual(run.call_count, 1)
        self.mock_build_opener.assert_not_called()
        self.assertEqual(list(self.scratch.iterdir()), [])

    def test_unhealthy_or_mismatched_child_refuses_before_probe_or_config(self):
        for mutate in (
            lambda snapshot: snapshot["resources"][0].update(state="Starting"),
            lambda snapshot: snapshot["resources"][0].update(healthStatus="Unhealthy"),
            lambda snapshot: snapshot["resources"][0].pop("healthStatus"),
            lambda snapshot: snapshot["resources"][0]["properties"].update(TargetResource="web"),
            lambda snapshot: snapshot["resources"][0]["properties"].update(TargetEndpoint="https"),
            lambda snapshot: snapshot["resources"][0]["properties"].update(Protocol="https"),
        ):
            snapshot = live_describe_snapshot()
            mutate(snapshot)
            with self.subTest(mutate=mutate):
                with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                        mock.patch.object(
                            launcher.subprocess, "run",
                            return_value=subprocess.CompletedProcess(
                                [], 0, stdout=json.dumps(snapshot)
                            ),
                        ) as run, contextlib.redirect_stderr(io.StringIO()):
                    status = launcher.main([
                        "--apphost", str(launcher.APPHOST_PROJECT),
                        "--output-dir", str(self.scratch), "--platform", "macos",
                    ])
                self.assertEqual(status, 2)
                self.assertEqual(run.call_count, 1)
                self.mock_build_opener.assert_not_called()
                self.assertEqual(list(self.scratch.iterdir()), [])

    def test_mismatched_cli_port_refuses_before_probe_or_config(self):
        snapshot = live_describe_snapshot()
        snapshot["resources"][2]["urls"][0]["url"] = "http://localhost:63450"
        responses = [
            subprocess.CompletedProcess([], 0, stdout=json.dumps(snapshot)),
            subprocess.CompletedProcess([], 0, stdout=devtunnel_show_response()),
        ]
        with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(launcher.subprocess, "run", side_effect=responses) as run, \
                contextlib.redirect_stderr(io.StringIO()):
            status = launcher.main([
                "--apphost", str(launcher.APPHOST_PROJECT),
                "--output-dir", str(self.scratch), "--platform", "macos",
            ])
        self.assertEqual(status, 2)
        self.assertEqual(run.call_count, 2)
        self.mock_build_opener.assert_not_called()
        self.assertEqual(list(self.scratch.iterdir()), [])

    def test_public_health_tls_timeout_or_warning_refuses_config_without_leaking(self):
        def tls_warning(*_args, **_kwargs):
            warnings.warn("private-example", RuntimeWarning)

        for failure in (
            ssl.SSLCertVerificationError("private-example"),
            TimeoutError("private-example"),
            tls_warning,
        ):
            with self.subTest(failure=failure):
                self.mock_health_opener.open.side_effect = failure
                responses = [
                    subprocess.CompletedProcess([], 0, stdout=json.dumps(live_describe_snapshot())),
                    subprocess.CompletedProcess([], 0, stdout=devtunnel_show_response()),
                ]
                stdout, stderr = io.StringIO(), io.StringIO()
                with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                        mock.patch.object(launcher.subprocess, "run", side_effect=responses) as run, \
                        contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                    status = launcher.main([
                        "--apphost", str(launcher.APPHOST_PROJECT),
                        "--output-dir", str(self.scratch), "--platform", "macos",
                    ])
                self.assertEqual(status, 2)
                self.assertEqual(run.call_count, 2)
                self.assertIn("valid TLS and HTTP 200", stderr.getvalue())
                self.assertNotIn("private-example", stdout.getvalue() + stderr.getvalue())
                self.assertNotIn(TUNNEL_ID, stdout.getvalue() + stderr.getvalue())
                self.assertEqual(list(self.scratch.iterdir()), [])

    def test_legacy_port_url_still_requires_public_health_200(self):
        self.mock_health_opener.open.return_value.__enter__.return_value.status = 404
        with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(
                    launcher.subprocess, "run",
                    return_value=subprocess.CompletedProcess([], 0, stdout=describe_response()),
                ) as run, contextlib.redirect_stderr(io.StringIO()):
            status = launcher.main([
                "--apphost", str(launcher.APPHOST_PROJECT),
                "--output-dir", str(self.scratch), "--platform", "macos",
            ])
        self.assertEqual(status, 2)
        self.assertEqual(run.call_count, 1)
        self.assertEqual(self.mock_health_opener.open.call_count, 1)
        self.assertEqual(list(self.scratch.iterdir()), [])

    def test_probe_failure_never_launches_selected_mobile_device(self):
        self.mock_health_opener.open.return_value.__enter__.return_value.status = 503
        responses = [
            response for _ in range(2) for response in (
                subprocess.CompletedProcess([], 0, stdout=json.dumps(live_describe_snapshot())),
                subprocess.CompletedProcess([], 0, stdout=devtunnel_show_response()),
            )
        ]
        with self.native_credentials(), \
                mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(launcher, "require_running_emulator") as emulator, \
                mock.patch.object(launcher, "require_booted_simulator") as simulator, \
                mock.patch.object(launcher.subprocess, "run", side_effect=responses) as run, \
                contextlib.redirect_stderr(io.StringIO()):
            for platform_name, device in (("android", "emulator-5554"), ("ios", IOS_UDID)):
                status = launcher.main([
                    "--apphost", str(launcher.APPHOST_PROJECT),
                    "--output-dir", str(self.scratch), "--platform", platform_name,
                    "--device", device, "--launch",
                ])
                self.assertEqual(status, 2)
                self.assertEqual(list(self.scratch.glob("reference-head-*.json")), [])
        emulator.assert_called_once_with("emulator-5554")
        simulator.assert_called_once_with(IOS_UDID)
        self.assertEqual(run.call_count, 4)
        self.assertFalse(
            any(call.args[0][0] == "dotnet" for call in run.call_args_list)
        )

    def test_missing_mobile_config_refuses_before_any_device_or_tunnel_process(self):
        with self.native_credentials(settings=None), \
                mock.patch.dict(launcher.os.environ, {
                    "AI__OpenAI__ApiKey": SYNTHETIC_OPENAI_KEY,
                    "Settings__OpenAIKey": SYNTHETIC_OPENAI_KEY,
                    "Settings__ElevenLabsKey": SYNTHETIC_ELEVENLABS_KEY,
                    "ElevenLabsKey": SYNTHETIC_ELEVENLABS_KEY,
                }, clear=True), \
                mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(launcher, "require_running_emulator") as emulator, \
                mock.patch.object(launcher, "require_booted_simulator") as simulator, \
                mock.patch.object(launcher, "describe_tunnel") as describe, \
                mock.patch.object(launcher.subprocess, "run") as run:
            for head, device in (("android", "emulator-5554"), ("ios", IOS_UDID)):
                with self.subTest(head=head):
                    stdout, stderr = io.StringIO(), io.StringIO()
                    with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                        status = launcher.main([
                            "--apphost", str(launcher.APPHOST_PROJECT),
                            "--output-dir", str(self.scratch), "--platform", head,
                            "--device", device, "--launch",
                        ])
                    self.assertEqual(status, 2)
                    self.assertIn("Settings:OpenAIKey", stderr.getvalue())
                    self.assertIn("Settings:ElevenLabsKey", stderr.getvalue())
                    for private_value in VALID_NATIVE_SETTINGS.values():
                        self.assertFalse(private_value in stdout.getvalue() + stderr.getvalue(),
                                         "Credential must not enter launcher output.")
                    self.assertEqual(list(self.scratch.glob("reference-head-*.json")), [])
        emulator.assert_not_called()
        simulator.assert_not_called()
        describe.assert_not_called()
        run.assert_not_called()
        self.mock_build_opener.assert_not_called()

    def test_missing_and_placeholder_mobile_keys_refuse_before_any_process(self):
        cases = (
            ({"ElevenLabsKey": SYNTHETIC_ELEVENLABS_KEY}, "OpenAIKey"),
            ({"OpenAIKey": SYNTHETIC_OPENAI_KEY}, "ElevenLabsKey"),
            ({"OpenAIKey": "your-openai-key-here",
              "ElevenLabsKey": SYNTHETIC_ELEVENLABS_KEY}, "OpenAIKey"),
            ({"OpenAIKey": SYNTHETIC_OPENAI_KEY,
              "ElevenLabsKey": "your-elevenlabs-key-here"}, "ElevenLabsKey"),
            ({"OpenAIKey": "not-configured",
              "ElevenLabsKey": SYNTHETIC_ELEVENLABS_KEY}, "OpenAIKey"),
            ({"OpenAIKey": SYNTHETIC_OPENAI_KEY,
              "ElevenLabsKey": "placeholder-long-enough"}, "ElevenLabsKey"),
        )
        for settings, missing in cases:
            with self.subTest(missing=missing, settings_names=list(settings)):
                with self.native_credentials(settings=settings), \
                        mock.patch.object(launcher, "require_output_dir",
                                          return_value=self.scratch), \
                        mock.patch.object(launcher, "require_running_emulator") as emulator, \
                        mock.patch.object(launcher, "describe_tunnel") as describe, \
                        mock.patch.object(launcher.subprocess, "run") as run, \
                        contextlib.redirect_stderr(io.StringIO()) as stderr:
                    status = launcher.main([
                        "--apphost", str(launcher.APPHOST_PROJECT),
                        "--output-dir", str(self.scratch), "--platform", "android",
                        "--device", "emulator-5554", "--launch",
                    ])
                    self.assertEqual(status, 2)
                    self.assertIn("Settings:" + missing, stderr.getvalue())
                    self.assertNotIn(SYNTHETIC_OPENAI_KEY, stderr.getvalue())
                    self.assertNotIn(SYNTHETIC_ELEVENLABS_KEY, stderr.getvalue())
                    self.assertEqual(list(self.scratch.glob("reference-head-*.json")), [])
                emulator.assert_not_called()
                describe.assert_not_called()
                run.assert_not_called()

    def test_debug_override_and_malformed_config_fail_without_process_or_secret_output(self):
        with self.native_credentials(
            development={"ElevenLabsKey": "your-elevenlabs-key-here"}
        ), mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(launcher, "describe_tunnel") as describe, \
                mock.patch.object(launcher, "require_booted_simulator") as simulator, \
                mock.patch.object(launcher.subprocess, "run") as run, \
                contextlib.redirect_stderr(io.StringIO()) as stderr:
            status = launcher.main([
                "--apphost", str(launcher.APPHOST_PROJECT),
                "--output-dir", str(self.scratch), "--platform", "ios",
                "--device", IOS_UDID, "--launch",
            ])
            self.assertEqual(status, 2)
            self.assertIn("Settings:ElevenLabsKey in native-fixture-development.json",
                          stderr.getvalue())
            self.assertNotIn(SYNTHETIC_ELEVENLABS_KEY, stderr.getvalue())
            self.assertEqual(list(self.scratch.glob("reference-head-*.json")), [])
        describe.assert_not_called()
        simulator.assert_not_called()
        run.assert_not_called()

        with self.native_credentials() as (base, _):
            base.write_text("{invalid JSON", encoding="utf-8")
            with mock.patch.object(launcher, "require_output_dir",
                                   return_value=self.scratch), \
                    mock.patch.object(launcher, "describe_tunnel") as describe, \
                    mock.patch.object(launcher.subprocess, "run") as run, \
                    contextlib.redirect_stderr(io.StringIO()) as stderr:
                status = launcher.main([
                    "--apphost", str(launcher.APPHOST_PROJECT),
                    "--output-dir", str(self.scratch), "--platform", "android",
                    "--device", "emulator-5554", "--launch",
                ])
                self.assertEqual(status, 2)
                self.assertIn("expected readable JSON", stderr.getvalue())
                self.assertNotIn("{invalid JSON", stderr.getvalue())
                self.assertEqual(list(self.scratch.glob("reference-head-*.json")), [])
            describe.assert_not_called()
            run.assert_not_called()

    def test_duplicate_or_mixed_case_credential_keys_fail_closed(self):
        with self.native_credentials() as (base, _):
            base.write_text(
                '{"Settings":{"OpenAIKey":'
                + json.dumps(SYNTHETIC_OPENAI_KEY)
                + ',"openAIKey":null,"ElevenLabsKey":'
                + json.dumps(SYNTHETIC_ELEVENLABS_KEY)
                + "}}",
                encoding="utf-8",
            )
            with mock.patch.object(launcher, "require_output_dir",
                                   return_value=self.scratch), \
                    mock.patch.object(launcher, "describe_tunnel") as describe, \
                    mock.patch.object(launcher.subprocess, "run") as run, \
                    contextlib.redirect_stderr(io.StringIO()) as stderr:
                status = launcher.main([
                    "--apphost", str(launcher.APPHOST_PROJECT),
                    "--output-dir", str(self.scratch), "--platform", "ios",
                    "--device", IOS_UDID, "--launch",
                ])
                self.assertEqual(status, 2)
                self.assertIn("expected readable JSON", stderr.getvalue())
                self.assertNotIn(SYNTHETIC_OPENAI_KEY, stderr.getvalue())
                self.assertEqual(list(self.scratch.glob("reference-head-*.json")), [])
            describe.assert_not_called()
            run.assert_not_called()

    def test_credential_shape_rejects_template_and_environment_placeholders(self):
        for value in (
            None, "", "  ", 42, "short", "your-openai-key-here",
            "your-elevenlabs-key-here", "not-configured", "placeholder-long-enough",
            "sk-...", "<insert-your-key>", "${OPENAI_API_KEY}",
            "sk-fixture key with spaces", "sk-fixture-with-newline\n",
        ):
            with self.subTest(value_type=type(value).__name__):
                self.assertFalse(launcher.credential_is_configured(value))
        self.assertTrue(launcher.credential_is_configured(SYNTHETIC_OPENAI_KEY))
        self.assertTrue(launcher.credential_is_configured(SYNTHETIC_ELEVENLABS_KEY))

    def test_config_only_is_allowed_without_credentials_but_macos_never_auto_launches(self):
        stdout = io.StringIO()
        with self.native_credentials(settings=None), \
                mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(launcher, "describe_tunnel", return_value=TUNNEL_URL), \
                mock.patch.object(launcher, "require_mobile_credentials") as credentials, \
                mock.patch.object(launcher.subprocess, "run") as run, \
                contextlib.redirect_stdout(stdout):
            for head, device in (
                ("android", "emulator-5554"), ("ios", IOS_UDID), ("macos", None),
            ):
                args = [
                    "--apphost", str(launcher.APPHOST_PROJECT),
                    "--output-dir", str(self.scratch), "--platform", head,
                ]
                if device:
                    args += ["--device", device]
                self.assertEqual(launcher.main(args), 0)
        credentials.assert_not_called()
        run.assert_not_called()
        self.assertEqual(len(list(self.scratch.glob("reference-head-*.json"))), 3)
        self.assertIn("AI__OpenAI__ApiKey and ElevenLabsKey", stdout.getvalue())
        self.assertIn("Settings__OpenAIKey", stdout.getvalue())
        self.assertNotIn(SYNTHETIC_OPENAI_KEY, stdout.getvalue())
        self.assertNotIn(SYNTHETIC_ELEVENLABS_KEY, stdout.getvalue())

    def test_live_shape_prints_three_safe_head_commands_with_unique_exact_configs(self):
        responses = [
            response for _ in range(3) for response in (
                subprocess.CompletedProcess([], 0, stdout=json.dumps(live_describe_snapshot())),
                subprocess.CompletedProcess([], 0, stdout=devtunnel_show_response()),
            )
        ]
        stdout, stderr = io.StringIO(), io.StringIO()
        with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(launcher.subprocess, "run", side_effect=responses) as run, \
                mock.patch.object(launcher.platform, "machine", return_value="arm64"), \
                contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            for platform_name, device in (
                ("android", "emulator-5554"), ("ios", IOS_UDID), ("macos", None),
            ):
                arguments = [
                    "--apphost", str(launcher.APPHOST_PROJECT),
                    "--output-dir", str(self.scratch), "--platform", platform_name,
                ]
                if device:
                    arguments.extend(["--device", device])
                self.assertEqual(launcher.main(arguments), 0)
        configs = list(self.scratch.glob("reference-head-*.json"))
        self.assertEqual(len(configs), 3)
        self.assertEqual(len({config.name for config in configs}), 3)
        for config in configs:
            self.assertEqual(
                json.loads(config.read_text(encoding="utf-8")),
                {"Services": {"api": {"https": [TUNNEL_URL]}}},
            )
            self.assertEqual(stat.S_IMODE(config.stat().st_mode), 0o600)
        self.assertEqual(run.call_count, 6)
        self.assertEqual(self.mock_health_opener.open.call_count, 3)
        self.assertEqual(stdout.getvalue().count("Build/run command:"), 3)
        self.assertIn("net11.0-android", stdout.getvalue())
        self.assertIn("net11.0-ios", stdout.getvalue())
        self.assertIn("net11.0-macos", stdout.getvalue())
        for private_value in (TUNNEL_URL, TUNNEL_ID, "private-example"):
            self.assertNotIn(private_value, stdout.getvalue() + stderr.getvalue())

    def test_aspire_failure_does_not_print_other_resources_environment(self):
        failure = subprocess.CalledProcessError(
            1, ["aspire", "describe"], output="other user's sensitive environment value"
        )
        with mock.patch.object(launcher.subprocess, "run", side_effect=failure):
            with self.assertRaises(launcher.ReferenceHeadError) as error:
                launcher.describe_tunnel(launcher.APPHOST_PROJECT)
        self.assertNotIn("sensitive environment value", str(error.exception))

    def test_disabled_or_missing_tunnel_fails_without_fallback(self):
        for response in (
            json.dumps({"resources": []}),
            json.dumps({"resources": [dict(port_resource(), name="devtunnel-public")]}),
        ):
            with self.subTest(response=response):
                with self.assertRaisesRegex(launcher.ReferenceHeadError, "absent or ambiguous"):
                    launcher.endpoint_from_describe(response)

    def test_invalid_describe_json_fails_without_fallback(self):
        for response in ("not JSON", "{}", "[]", '{"resources":"not an array"}'):
            with self.subTest(response=response):
                with self.assertRaises(launcher.ReferenceHeadError):
                    launcher.endpoint_from_describe(response)

    def test_tunnel_must_be_running_healthy_and_owned_by_api_http(self):
        for modification in (
            {"state": "Starting"},
            {"healthStatus": "Unhealthy"},
            {"resourceType": "Project"},
            {"properties": {"TargetResource": "web", "TargetEndpoint": "http"}},
            {"properties": {"TargetResource": "api", "TargetEndpoint": "https"}},
            {"properties": {"TargetResource": "api", "TargetEndpoint": "http",
                            "Protocol": "https"}},
        ):
            with self.subTest(modification=modification):
                resource = port_resource()
                resource.update(modification)
                with self.assertRaises(launcher.ReferenceHeadError):
                    launcher.endpoint_from_describe(describe_response(resource))

    def test_live_shape_requires_one_running_parent_with_valid_tunnel_id(self):
        for mutate in (
            lambda snapshot: snapshot["resources"].pop(1),
            lambda snapshot: snapshot["resources"].append(snapshot["resources"][1].copy()),
            lambda snapshot: snapshot["resources"][1].update(state="Stopped"),
            lambda snapshot: snapshot["resources"][1].update(properties={}),
            lambda snapshot: snapshot["resources"][1].update(
                properties={"TunnelId": "-not-a-positional-id"}
            ),
        ):
            snapshot = live_describe_snapshot()
            mutate(snapshot)
            with self.subTest(resources=len(snapshot["resources"]), mutate=mutate):
                with mock.patch.object(launcher, "show_tunnel_port_uri") as show:
                    with self.assertRaises(launcher.ReferenceHeadError):
                        launcher.endpoint_from_describe(json.dumps(snapshot))
                    show.assert_not_called()

    def test_live_shape_requires_unambiguous_api_project_http_endpoint(self):
        for mutate in (
            lambda snapshot: snapshot["resources"].pop(2),
            lambda snapshot: snapshot["resources"].append(snapshot["resources"][2].copy()),
            lambda snapshot: snapshot["resources"][2].update(displayName="web"),
            lambda snapshot: snapshot["resources"][2].update(resourceType="Container"),
            lambda snapshot: snapshot["resources"][2].update(
                urls=[{"name": "https", "url": "https://localhost:63456"}]
            ),
            lambda snapshot: snapshot["resources"][2]["urls"].append(
                {"name": "http", "url": f"http://localhost:{API_HTTP_PORT}"}
            ),
            lambda snapshot: snapshot["resources"][2].update(
                urls=[{"name": "http", "url": "http://api.production.example:63449"}]
            ),
            lambda snapshot: snapshot["resources"][2].update(
                urls=[{"name": "http", "url": "http://localhost:invalid"}]
            ),
        ):
            snapshot = live_describe_snapshot()
            mutate(snapshot)
            with self.subTest(mutate=mutate):
                with mock.patch.object(launcher, "show_tunnel_port_uri") as show:
                    with self.assertRaises(launcher.ReferenceHeadError):
                        launcher.endpoint_from_describe(json.dumps(snapshot))
                    show.assert_not_called()

    def test_cli_port_must_match_api_http_not_api_https_or_another_port(self):
        for ports in (
            [{"portNumber": 63456, "protocol": "http", "portUri": TUNNEL_URL}],
            [{"portNumber": API_HTTP_PORT, "protocol": "https", "portUri": TUNNEL_URL}],
            [{"portNumber": str(API_HTTP_PORT), "protocol": "http", "portUri": TUNNEL_URL}],
            [{"portNumber": API_HTTP_PORT, "protocol": "http", "portUri": TUNNEL_URL}] * 2,
            [],
        ):
            with self.subTest(ports=ports):
                with self.assertRaisesRegex(launcher.ReferenceHeadError, "no unique HTTP port"):
                    launcher.port_uri_from_devtunnel_show(devtunnel_show_response(ports), API_HTTP_PORT)
        snapshot = live_describe_snapshot()
        snapshot["resources"][2]["urls"][0]["url"] = "http://localhost:63450"
        with mock.patch.object(
            launcher.subprocess, "run",
            return_value=subprocess.CompletedProcess([], 0, stdout=devtunnel_show_response()),
        ):
            with self.assertRaisesRegex(launcher.ReferenceHeadError, "no unique HTTP port"):
                launcher.endpoint_from_describe(json.dumps(snapshot))

    def test_cli_show_requires_schema_and_safe_root_https_port_uri(self):
        for output in (
            "not JSON", "[]", "{}", '{"tunnel":{"ports":"not an array"}}',
            devtunnel_show_response([{"portNumber": API_HTTP_PORT, "protocol": "http"}]),
        ):
            with self.subTest(output=output):
                with self.assertRaises(launcher.ReferenceHeadError):
                    launcher.port_uri_from_devtunnel_show(output, API_HTTP_PORT)
        for unsafe_uri in (
            "http://test-tunnel-63449.use.devtunnels.ms/",
            "https://localhost:63449/",
            "https://10.0.2.2:63449/",
            "https://api.production.example/",
            "https://inspect.devtunnels.ms/",
            "https://test-tunnel-63449.use.devtunnels.ms/path",
            "https://test-tunnel-63449.use.devtunnels.ms/?token=private",
        ):
            with self.subTest(unsafe_uri=unsafe_uri):
                ports = [{"portNumber": API_HTTP_PORT, "protocol": "http", "portUri": unsafe_uri}]
                with self.assertRaises(launcher.ReferenceHeadError):
                    launcher.port_uri_from_devtunnel_show(devtunnel_show_response(ports), API_HTTP_PORT)

    def test_missing_devtunnel_executable_or_auth_failure_writes_no_config_or_secrets(self):
        for failure, expected in (
            (FileNotFoundError("devtunnel"), "CLI is required"),
            (
                subprocess.CalledProcessError(
                    1, ["devtunnel", "show", TUNNEL_ID, "--json"],
                    output="private-example",
                ),
                "Check CLI access and authentication",
            ),
            (subprocess.TimeoutExpired(["devtunnel", "show", TUNNEL_ID], 30), "Check CLI access"),
        ):
            stderr = io.StringIO()
            with self.subTest(failure=type(failure).__name__):
                with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                        mock.patch.object(
                            launcher.subprocess, "run",
                            side_effect=[
                                subprocess.CompletedProcess(
                                    [], 0, stdout=json.dumps(live_describe_snapshot())
                                ),
                                failure,
                            ],
                        ), contextlib.redirect_stderr(stderr), \
                        contextlib.redirect_stdout(io.StringIO()):
                    status = launcher.main([
                        "--apphost", str(launcher.APPHOST_PROJECT),
                        "--output-dir", str(self.scratch), "--platform", "macos",
                    ])
                self.assertEqual(status, 2)
                self.assertIn(expected, stderr.getvalue())
                self.assertNotIn(TUNNEL_ID, stderr.getvalue())
                self.assertNotIn("private-example", stderr.getvalue())
                self.assertEqual(list(self.scratch.iterdir()), [])

    def test_only_tunnel_url_is_accepted_not_inspector_or_missing_urls(self):
        for urls in (
            [],
            [{"name": "Inspect", "url": TUNNEL_URL}],
            [{"name": "tunnel", "url": TUNNEL_URL}] * 2,
        ):
            with self.subTest(urls=urls):
                resource = port_resource()
                resource["urls"] = urls
                with self.assertRaises(launcher.ReferenceHeadError):
                    launcher.endpoint_from_describe(describe_response(resource))

    def test_invalid_public_endpoints_and_android_localhost_alias_are_rejected(self):
        invalid_urls = (
            "http://api-test-1234.usw2.devtunnels.ms/",
            "https://localhost:7071/",
            "https://127.0.0.1:7071/",
            "https://10.0.2.2:7071/",
            "https://api.production.example/",
            "https://inspect.devtunnels.ms/",
            "https://api-test.usw2.devtunnels.ms.evil.example/",
            "https://api-test.usw2.devtunnels.ms:8443/",
            "https://api-test.usw2.devtunnels.ms/path",
            "https://api-test.usw2.devtunnels.ms/?token=value",
            "https://user@api-test.usw2.devtunnels.ms/",
        )
        for url in invalid_urls:
            with self.subTest(url=url):
                with self.assertRaises(launcher.ReferenceHeadError):
                    launcher.endpoint_from_describe(describe_response(port_resource(url)))
        with self.assertRaisesRegex(launcher.ReferenceHeadError, "10.0.2.2"):
            launcher.validate_tunnel_url("https://10.0.2.2:7071")

    def test_generated_config_preserves_tunnel_url_and_never_overwrites(self):
        first = launcher.write_generated_config(self.scratch, TUNNEL_URL)
        original = first.read_bytes()
        second = launcher.write_generated_config(self.scratch, TUNNEL_URL)
        self.assertNotEqual(first, second)
        self.assertEqual(first.read_bytes(), original)
        self.assertEqual(
            json.loads(second.read_text(encoding="utf-8")),
            {"Services": {"api": {"https": [TUNNEL_URL]}}},
        )
        self.assertEqual(stat.S_IMODE(second.stat().st_mode), 0o600)
        self.assertEqual(len(list(self.scratch.iterdir())), 2)

    def test_generated_config_refuses_to_overwrite_an_existing_filename(self):
        existing = self.scratch / "reference-head-fixed.json"
        existing.write_text("keep this data", encoding="utf-8")
        with mock.patch.object(launcher.uuid, "uuid4", return_value=SimpleNamespace(hex="fixed")):
            with self.assertRaisesRegex(launcher.ReferenceHeadError, "unique"):
                launcher.write_generated_config(self.scratch, TUNNEL_URL)
        self.assertEqual(existing.read_text(encoding="utf-8"), "keep this data")
        self.assertEqual(list(self.scratch.iterdir()), [existing])

    def test_output_directory_must_be_absolute_external_existing_and_safe(self):
        with self.assertRaisesRegex(launcher.ReferenceHeadError, "absolute"):
            launcher.require_output_dir("relative/session")
        with self.assertRaisesRegex(launcher.ReferenceHeadError, "outside the repository"):
            launcher.require_output_dir(str(self.scratch))
        with self.assertRaisesRegex(launcher.ReferenceHeadError, "already exist"):
            launcher.require_output_dir(str(self.scratch / "missing"))

    def test_apphost_must_be_explicit_and_this_worktree(self):
        with self.assertRaisesRegex(launcher.ReferenceHeadError, "absolute"):
            launcher.require_apphost("src/SentenceStudio.AppHost/SentenceStudio.AppHost.csproj")
        with self.assertRaisesRegex(launcher.ReferenceHeadError, "not another running stack"):
            launcher.require_apphost(str(SCRIPT))

    def test_emulator_and_simulator_build_commands_pass_embedded_resource(self):
        config = self.scratch / "reference-head-fixture.json"
        android = launcher.launch_command("android", "emulator-5554", config)
        self.assertEqual(android[:3], ["dotnet", "build", str(launcher.HEAD_PROJECTS["android"])])
        self.assertIn("-t:Run", android)
        self.assertIn("-p:Device=emulator-5554", android)
        self.assertIn(f"-p:ReferenceHeadConfig={config}", android)
        self.assertNotIn("maui", android[:1])
        with mock.patch.object(launcher.platform, "machine", return_value="arm64"):
            ios = launcher.launch_command("ios", IOS_UDID, config)
        self.assertIn("-p:RuntimeIdentifier=iossimulator-arm64", ios)
        self.assertIn(f"-p:_DeviceName=:v2:udid={IOS_UDID}", ios)
        self.assertIn(f"-p:ReferenceHeadConfig={config}", ios)
        self.assertNotIn("-p:Device=emulator-5554", ios)

    def test_macos_command_uses_run_with_build_property_but_cannot_auto_launch(self):
        config = self.scratch / "reference-head-fixture.json"
        command = launcher.launch_command("macos", None, config)
        self.assertEqual(command[:3], ["dotnet", "run", "--project"])
        self.assertIn("net11.0-macos", command)
        self.assertIn(f"--property:ReferenceHeadConfig={config}", command)
        self.assertNotIn("--no-build", command)

    def test_device_selector_and_emulator_identity_fail_closed(self):
        for selector in ("physical-device", "ABCD1234", "emulator-5554;adb shell"):
            with self.subTest(selector=selector):
                with self.assertRaises(launcher.ReferenceHeadError):
                    launcher.validate_device_selector("android", selector)
        with self.assertRaises(launcher.ReferenceHeadError):
            launcher.validate_device_selector("ios", "iPhone 15 Pro")
        with mock.patch.object(
            launcher.subprocess, "run",
            return_value=subprocess.CompletedProcess([], 0, stdout="0\n"),
        ):
            with self.assertRaisesRegex(launcher.ReferenceHeadError, "not a running emulator"):
                launcher.require_running_emulator("emulator-5554")
        with mock.patch.object(
            launcher.subprocess, "run",
            return_value=subprocess.CompletedProcess([], 0, stdout="1\n"),
        ) as run:
            launcher.require_running_emulator("emulator-5554")
            self.assertEqual(
                run.call_args.args[0],
                ["adb", "-s", "emulator-5554", "shell", "getprop", "ro.kernel.qemu"],
            )

    def test_ios_udid_must_belong_to_a_booted_simulator(self):
        inventory = {"devices": {"iOS 26.0": [{"udid": IOS_UDID, "state": "Booted"}]}}
        with mock.patch.object(
            launcher.subprocess, "run",
            return_value=subprocess.CompletedProcess([], 0, stdout=json.dumps(inventory)),
        ) as run:
            launcher.require_booted_simulator(IOS_UDID)
            self.assertEqual(
                run.call_args.args[0],
                ["xcrun", "simctl", "list", "devices", "booted", "--json"],
            )
            with self.assertRaisesRegex(launcher.ReferenceHeadError, "not booted"):
                launcher.require_booted_simulator("00000000-0000-0000-0000-000000000000")

    def test_actual_android_launch_route_runs_only_explicit_emulator(self):
        stdout, stderr = io.StringIO(), io.StringIO()
        with self.native_credentials(), \
                mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(launcher, "describe_tunnel", return_value=TUNNEL_URL), \
                mock.patch.object(launcher, "require_running_emulator") as emulator, \
                mock.patch.object(launcher.subprocess, "run") as run, \
                contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            status = launcher.main([
                "--apphost", str(launcher.APPHOST_PROJECT),
                "--output-dir", str(self.scratch),
                "--platform", "android", "--device", "emulator-5554", "--launch",
            ])
        self.assertEqual(status, 0)
        emulator.assert_called_once_with("emulator-5554")
        args = run.call_args.args[0]
        self.assertIn("-p:Device=emulator-5554", args)
        self.assertEqual(run.call_args.kwargs["env"]["ANDROID_SERIAL"], "emulator-5554")
        config = next(self.scratch.glob("reference-head-*.json"))
        self.assertIn(f"-p:ReferenceHeadConfig={config}", args)
        self.assertNotIn(TUNNEL_URL, stdout.getvalue())
        self.assertEqual(
            json.loads(config.read_text(encoding="utf-8")),
            {"Services": {"api": {"https": [TUNNEL_URL]}}},
        )
        self.assertEqual(stat.S_IMODE(config.stat().st_mode), 0o600)
        for private_value in VALID_NATIVE_SETTINGS.values():
            self.assertFalse(private_value.encode() in config.read_bytes(),
                             "Native credential must not enter tunnel JSON.")
            self.assertFalse(private_value in stdout.getvalue() + stderr.getvalue(),
                             "Native credential must not enter launcher output.")
            self.assertFalse(any(private_value in arg for arg in args),
                             "Native credential must not enter a build argument.")

    def test_actual_ios_launch_route_runs_only_booted_simulator(self):
        stdout, stderr = io.StringIO(), io.StringIO()
        with self.native_credentials(), \
                mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(launcher, "describe_tunnel", return_value=TUNNEL_URL), \
                mock.patch.object(launcher, "require_booted_simulator") as simulator, \
                mock.patch.object(launcher.platform, "machine", return_value="arm64"), \
                mock.patch.object(launcher.subprocess, "run") as run, \
                contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            status = launcher.main([
                "--apphost", str(launcher.APPHOST_PROJECT),
                "--output-dir", str(self.scratch),
                "--platform", "ios", "--device", IOS_UDID, "--launch",
            ])
        self.assertEqual(status, 0)
        simulator.assert_called_once_with(IOS_UDID)
        args = run.call_args.args[0]
        self.assertIn(f"-p:_DeviceName=:v2:udid={IOS_UDID}", args)
        self.assertIn("-p:RuntimeIdentifier=iossimulator-arm64", args)
        config = next(self.scratch.glob("reference-head-*.json"))
        self.assertEqual(
            json.loads(config.read_text(encoding="utf-8")),
            {"Services": {"api": {"https": [TUNNEL_URL]}}},
        )
        self.assertEqual(stat.S_IMODE(config.stat().st_mode), 0o600)
        for private_value in VALID_NATIVE_SETTINGS.values():
            self.assertFalse(private_value.encode() in config.read_bytes(),
                             "Native credential must not enter tunnel JSON.")
            self.assertFalse(private_value in stdout.getvalue() + stderr.getvalue(),
                             "Native credential must not enter launcher output.")
            self.assertFalse(any(private_value in arg for arg in args),
                             "Native credential must not enter a build argument.")

    def test_macos_or_unselected_device_never_launches_or_writes_config(self):
        for extra in (
            ["--platform", "macos", "--launch"],
            ["--platform", "android", "--launch"],
            ["--platform", "android", "--device", "physical-device", "--launch"],
        ):
            with self.subTest(extra=extra):
                with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                        mock.patch.object(launcher, "describe_tunnel") as describe, \
                        contextlib.redirect_stderr(io.StringIO()):
                    status = launcher.main([
                        "--apphost", str(launcher.APPHOST_PROJECT),
                        "--output-dir", str(self.scratch), *extra,
                    ])
                self.assertEqual(status, 2)
                describe.assert_not_called()
                self.assertEqual(list(self.scratch.iterdir()), [])

    def test_missing_tunnel_never_writes_config_or_launches(self):
        with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(
                    launcher, "describe_tunnel",
                    side_effect=launcher.ReferenceHeadError("API tunnel is missing")
                ), \
                mock.patch.object(launcher.subprocess, "run") as run, \
                contextlib.redirect_stderr(io.StringIO()):
            status = launcher.main([
                "--apphost", str(launcher.APPHOST_PROJECT),
                "--output-dir", str(self.scratch),
                "--platform", "android", "--device", "emulator-5554",
            ])
        self.assertEqual(status, 2)
        run.assert_not_called()
        self.assertEqual(list(self.scratch.iterdir()), [])

    def test_macos_prints_command_without_running_dotnet(self):
        stdout = io.StringIO()
        with mock.patch.object(launcher, "require_output_dir", return_value=self.scratch), \
                mock.patch.object(launcher, "describe_tunnel", return_value=TUNNEL_URL), \
                mock.patch.object(launcher.subprocess, "run") as run, \
                contextlib.redirect_stdout(stdout):
            status = launcher.main([
                "--apphost", str(launcher.APPHOST_PROJECT),
                "--output-dir", str(self.scratch), "--platform", "macos",
            ])
        self.assertEqual(status, 0)
        run.assert_not_called()
        self.assertIn("NOT executed", stdout.getvalue())
        self.assertIn("net11.0-macos", stdout.getvalue())

    def test_msbuild_resource_and_runtime_configuration_contract(self):
        project = (
            launcher.REPOSITORY_ROOT
            / "src/SentenceStudio.MauiCompatibility/SentenceStudio.MauiCompatibility.csproj"
        )
        root = ET.parse(project).getroot()
        resource = next(
            item for item in root.iter("EmbeddedResource")
            if item.get("Include") == "$(ReferenceHeadConfig)"
        )
        self.assertEqual(
            resource.get("LogicalName"), "SentenceStudio.reference-head.generated.json"
        )
        self.assertIn("$(ReferenceHeadConfig)", resource.get("Condition"))
        validation = next(
            target for target in root.iter("Target")
            if target.get("Name") == "ValidateReferenceHeadConfig"
        )
        self.assertIn("AssignTargetPaths", validation.get("BeforeTargets"))
        self.assertTrue(any("Exists('$(ReferenceHeadConfig)')" in error.get("Condition")
                            for error in validation.iter("Error")))

        source = (
            launcher.REPOSITORY_ROOT
            / "src/SentenceStudio.MauiCompatibility/Setup/ConfigurationExtensions.cs"
        ).read_text(encoding="utf-8")
        self.assertIn("baseStream is null && referenceHeadStream is null", source)
        self.assertIn("ValidateReferenceHeadConfig(json)", source)
        self.assertIn("endpoints.Length != 1", source)
        self.assertLess(
            source.index("configuration.AddJsonStream(baseStream)"),
            source.index("configuration.AddJsonStream(envStream)"),
        )
        self.assertLess(
            source.index("configuration.AddJsonStream(envStream)"),
            source.index("configuration.AddEnvironmentVariables()"),
        )
        self.assertLess(
            source.index("configuration.AddEnvironmentVariables()"),
            source.index("configuration.AddJsonStream(overlay)"),
        )

    def test_direct_native_client_keys_match_mobile_preflight_and_desktop_guidance(self):
        app_builder = (
            launcher.REPOSITORY_ROOT
            / "src/SentenceStudio.MauiCompatibility/Setup/SentenceStudioAppBuilder.cs"
        ).read_text(encoding="utf-8")
        ai_service = (
            launcher.REPOSITORY_ROOT
            / "src/SentenceStudio.Shared/Services/AiService.cs"
        ).read_text(encoding="utf-8")
        core_services = (
            launcher.REPOSITORY_ROOT
            / "src/SentenceStudio.AppLib/Services/CoreServiceExtensions.cs"
        ).read_text(encoding="utf-8")
        self.assertEqual(launcher.NATIVE_CREDENTIAL_KEYS, ("OpenAIKey", "ElevenLabsKey"))
        for contract in (
            'settings.OpenAIKey ?? "not-configured"',
            'settings.ElevenLabsKey ?? "not-configured"',
            'Environment.GetEnvironmentVariable("AI__OpenAI__ApiKey")',
            'Environment.GetEnvironmentVariable("ElevenLabsKey")',
            "builder.Services.AddTieredChatClients(builder.Configuration, openAiApiKey)",
            "new ElevenLabsClient(elevenLabsKey)",
        ):
            self.assertIn(contract, app_builder)
        self.assertIn('GetRequiredSection("Settings").Get<Settings>().OpenAIKey', ai_service)
        self.assertIn("services.AddSingleton<ElevenLabsSpeechService>()", core_services)


if __name__ == "__main__":
    unittest.main()
