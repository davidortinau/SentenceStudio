import hashlib
import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
import uuid
import xml.etree.ElementTree as ET
import zipfile
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import patch
from xml.sax.saxutils import escape


SCRIPT = Path(__file__).resolve().parents[1] / "validate-platform-neutral-dependencies.py"
SPEC = importlib.util.spec_from_file_location("dependency_closure", SCRIPT)
closure = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = closure
SPEC.loader.exec_module(closure)


class ObservedDependencyGuardTests(unittest.TestCase):
    @staticmethod
    def temporary_parent():
        if sys.platform != "linux":
            return None
        candidate = Path(os.environ.get("RUNNER_TEMP", Path.home())).resolve()
        if candidate.is_relative_to("/tmp") or candidate.is_relative_to("/var/tmp"):
            raise RuntimeError("Linux guard fixture scratch must be outside /tmp")
        return candidate

    @classmethod
    def setUpClass(cls):
        cls.logger_scratch = tempfile.TemporaryDirectory(
            prefix="dependency-guard-logger-", dir=cls.temporary_parent(),
        )
        scratch = Path(cls.logger_scratch.name).resolve()
        source, runtime = scratch / "source", scratch / "runtime"
        runtime.mkdir()
        closure.source_snapshot(closure.ROOT, source)
        cls.addClassCleanup(closure.unlock_snapshot_directories, source)
        cls.addClassCleanup(cls.logger_scratch.cleanup)
        isolation = closure.Isolation(source, runtime, scratch / "write-denied-probe")
        closure.check_isolation(isolation, time.monotonic() + 120)
        config, _ = closure.prepare_restore_sources(source, runtime)
        cls.logger = closure.build_logger(
            config, runtime / "packages", runtime, time.monotonic() + 120,
            source, isolation,
        )

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(
            prefix="dependency-guard-test-", dir=self.temporary_parent(),
        )
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        isolated = tempfile.TemporaryDirectory(
            prefix="dependency-guard-scratch-", dir=self.temporary_parent(),
        )
        self.addCleanup(isolated.cleanup)
        self.scratch = Path(isolated.name).resolve()
        self.feed = self.root / "feed"
        self.feed.mkdir()
        (self.root / "src").mkdir()
        self.package_ids = set()
        self.config = self.root / "src/NuGet.Config"
        self.write_config()

    def write_config(self):
        packages = "".join(
            f'<package pattern="{escape(name)}"/>'
            for name in sorted(self.package_ids or {"Never.Resolve.GuardFixture"})
        )
        self.config.write_text(
            '<configuration><packageSources><clear/>'
            '<add key="fixture" value="../feed"/>'
            '<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>'
            '</packageSources><packageSourceMapping>'
            f'<packageSource key="fixture">{packages}</packageSource>'
            '<packageSource key="nuget.org"><package pattern="*"/></packageSource>'
            '</packageSourceMapping></configuration>',
            encoding="utf-8",
        )

    def package(self, name, *, version="1.0.0", dependencies=()):
        dependencies_xml = "".join(
            f'<dependency id="{escape(package)}" version="{escape(requested)}"/>'
            for package, requested in dependencies
        )
        dependency_group = (
            f"<dependencies>{dependencies_xml}</dependencies>" if dependencies else ""
        )
        nuspec = (
            '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
            "<metadata>"
            f"<id>{escape(name)}</id><version>{escape(version)}</version>"
            "<authors>Fixture</authors><description>Local integration fixture</description>"
            f"{dependency_group}</metadata></package>"
        )
        archive_path = self.feed / f"{name.lower()}.{version}.nupkg"
        with zipfile.ZipFile(archive_path, "w") as archive:
            archive.writestr(f"{name}.nuspec", nuspec)
            archive.writestr("lib/net10.0/_._", "")
            archive.writestr("lib/net11.0/_._", "")
        self.package_ids.add(name)
        self.write_config()
        return archive_path

    def project(self, name, body="", *, frameworks="net10.0"):
        project = self.root / "src" / name / f"{name}.csproj"
        project.parent.mkdir(parents=True, exist_ok=True)
        property_name = "TargetFrameworks" if ";" in frameworks else "TargetFramework"
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk">'
            f"<PropertyGroup><{property_name}>{frameworks}</{property_name}></PropertyGroup>"
            f"{body}</Project>",
            encoding="utf-8",
        )
        return project

    def package_proj(self, project):
        project.parent.mkdir(parents=True, exist_ok=True)
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<TargetFramework>net10.0</TargetFramework>'
            '<LanguageTargets>$(MSBuildToolsPath)/Microsoft.CSharp.targets</LanguageTargets>'
            '</PropertyGroup><ItemGroup>'
            '<PackageReference Include="Comet" Version="1.0.0"/>'
            '</ItemGroup></Project>',
        )
        return project

    def entry(self, project, *, configuration="Release", rid=None, frameworks=("net10.0",),
              properties=(("IncludeMobileTargets", "false"),)):
        return closure.RestoreEntry(
            str(project.relative_to(self.root)), frameworks, configuration, rid, properties,
        )

    def control_environment(self, *, preserve_packages=False):
        runtime = self.scratch / "control-runtime"
        runtime.mkdir(exist_ok=True)
        environment = closure.restore_environment(runtime)
        if preserve_packages:
            packages = Path(os.environ["NUGET_PACKAGES"]).resolve()
            if not packages.is_relative_to(self.scratch):
                raise ValueError("control package cache must remain inside fixture scratch")
            environment["NUGET_PACKAGES"] = str(packages)
        return environment

    def normal_build(self, project, entry, *, restore=True, packages=None):
        packages = packages or self.scratch / "control-packages"
        options = [
            "-p:RestoreForce=true", f"-p:Configuration={entry.configuration}",
            f"-p:RuntimeIdentifier={entry.runtime_identifier or ''}",
            *[f"-p:{name}={value}" for name, value in entry.properties],
        ]
        if restore:
            restored = subprocess.run(
                [
                    "dotnet", "restore", str(project), "--force", "--no-http-cache",
                    "--nologo",
                    "--verbosity", "quiet", "--packages", str(packages),
                    "--configfile", str(self.config), "-m:1", "-nr:false", *options,
                ],
                cwd=project.parent, capture_output=True, text=True, timeout=120,
                check=False, env=self.control_environment(),
            )
            if restored.returncode:
                return restored, None, []
        else:
            restored = None
        trace = self.scratch / f"control-{uuid.uuid4().hex}.jsonl"
        built = subprocess.run(
            [
                "dotnet", "build", str(project), "--no-restore", "--nologo",
                "--verbosity", "quiet", "-m:1", "-nr:false", *options,
                f"-logger:DependencyGuardLogger,{self.logger};{trace}",
            ],
            cwd=project.parent, capture_output=True, text=True, timeout=120,
            check=False, env=self.control_environment(),
        )
        events = [json.loads(line) for line in trace.read_text().splitlines()]
        return restored, built, events

    def guard(self, *entries):
        recorded = []
        metrics = {}
        parse = closure.parse_trace

        def capture(path, *args):
            events = (json.loads(line) for line in path.read_text().splitlines())
            recorded.extend(
                event for event in events if event["kind"] in {"start", "record"}
            )
            return parse(path, *args)

        with patch.object(closure, "parse_trace", side_effect=capture):
            errors, count = closure.validate(
                self.root, entries, scratch_parent=self.scratch, metrics=metrics,
            )
        return errors, count, recorded, metrics

    def assert_forbidden(self, errors, name):
        self.assertTrue(
            any(f"forbidden resolved package {name}/" in error for error in errors),
            errors,
        )

    def actual_child(self, events, project, *, target=""):
        candidates = [
            event for event in events if event["kind"] == "start"
            and self.matches_project(event["project"], project)
            and event.get("targets") == target
        ]
        self.assertTrue(candidates, f"no actual {project} {target} build in logger events")
        return {
            name.casefold(): value for name, value in candidates[-1]["globals"].items()
        }

    def matches_project(self, actual, project):
        path = Path(actual)
        if path == project:
            return True
        if not project.is_relative_to(self.root):
            return False
        parts = project.relative_to(self.root).parts
        return path.parts[-len(parts):] == parts and path.is_relative_to(self.scratch)

    def original_bytes(self):
        return {
            file.relative_to(self.root).as_posix(): hashlib.sha256(file.read_bytes()).hexdigest()
            for file in self.root.rglob("*") if file.is_file()
        }

    def test_matrix_is_explicit_and_guard_contains_no_context_simulator(self):
        self.assertEqual(15, len(closure.ACTIVE_ROOTS))
        self.assertEqual(40, len(closure.ACTIVE_MATRIX))
        for root in closure.ACTIVE_ROOTS:
            contexts = {
                (item.configuration, item.runtime_identifier)
                for item in closure.ACTIVE_MATRIX if item.project == root.path
            }
            expected = {("Debug", None), ("Release", None)}
            if root in closure.SERVER_AND_SHARED_ROOTS:
                expected.add(("Release", "linux-x64"))
            self.assertEqual(expected, contexts)
        script = SCRIPT.read_text()
        for rejected in (
            "child_restore_contexts", "child_restore_entry",
            "preflight_restore_context", "evaluated_project_references",
            "-getItem:ProjectReference",
        ):
            self.assertNotIn(rejected, script)

    def test_root_framework_cannot_drift_from_the_declared_matrix(self):
        root = self.project("Api", frameworks="net11.0")
        entry = self.entry(root, frameworks=("net10.0",))
        _, built, events = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.assertTrue(
            any(event["kind"] == "evaluation" and event["project"] == str(root)
                and event["properties"].get("TargetFramework") == "net11.0"
                for event in events),
            events,
        )

        errors, _, recorded, _ = self.guard(entry)

        self.assertTrue(
            any(event["kind"] == "record" and
                self.matches_project(json.loads(event["data"])["project"], root) and
                json.loads(event["data"])["targetFramework"] == "net11.0"
                for event in recorded),
            errors,
        )
        self.assertTrue(any("root target frameworks" in error for error in errors), errors)

    def test_clean_real_build_observes_every_project_without_repo_assets(self):
        leaf = self.project("Leaf")
        root = self.project(
            "Api", '<ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj"/>'
            "</ItemGroup>",
        )
        entry = self.entry(root)
        _, built, control = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.actual_child(control, leaf)
        sentinel = self.scratch / "preserve"
        sentinel.write_text("do not remove")
        before = {path.name for path in self.scratch.iterdir()}

        errors, count, events, metrics = self.guard(entry)

        self.assertEqual(([], 2), (errors, count))
        self.assertEqual(1, metrics["root_restores"])
        self.assertGreaterEqual(metrics["observed_context_restores"], 2)
        self.actual_child(events, leaf)
        self.assertEqual(before, {path.name for path in self.scratch.iterdir()})
        self.assertEqual("do not remove", sentinel.read_text())

    def test_stripped_hooks_and_output_override_cannot_touch_original_checkout(self):
        leaf = self.project("Leaf")
        root = self.project(
            "Api", '<ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj" '
            'GlobalPropertiesToRemove="CustomAfterDirectoryBuildProps;'
            'CustomAfterMicrosoftCommonTargets;CustomAfterMicrosoftCommonCrossTargetingTargets" '
            f'AdditionalProperties="MSBuildProjectExtensionsPath={escape(str(leaf.parent / "obj"))}/"'
            '/></ItemGroup>',
        )
        before = self.original_bytes()
        self.assertFalse((leaf.parent / "obj").exists())
        errors, _, _, _ = self.guard(self.entry(root))
        self.assertTrue(errors, "the stripped child must fail inside the sandbox")
        self.assertEqual(before, self.original_bytes())
        self.assertFalse((leaf.parent / "obj").exists())
        self.assertFalse((root.parent / "obj").exists())
        self.assertFalse((leaf.parent / "bin").exists())
        self.assertFalse((root.parent / "bin").exists())

    def test_absolute_checkout_write_is_denied_before_creating_a_file(self):
        marker = self.root / "source-write-would-be-here.txt"
        root = self.project(
            "Api", '<Target Name="AttemptOriginalWrite" BeforeTargets="Build">'
            f'<WriteLinesToFile File="{escape(str(marker))}" Lines="unsafe" '
            'Overwrite="true"/></Target>',
        )
        before = self.original_bytes()
        errors, _, _, _ = self.guard(self.entry(root))
        self.assertTrue(errors, "the original checkout must be outside the writable sandbox")
        self.assertEqual(before, self.original_bytes())
        self.assertFalse(marker.exists())
        self.assertFalse((root.parent / "obj").exists())

    def test_snapshot_is_readonly_and_generated_output_stays_in_scratch(self):
        root = self.project("Api")
        before = self.original_bytes()
        observed = []
        snapshot = closure.source_snapshot

        def inspect_source(original, copy):
            snapshot(original, copy)
            observed.append((copy / "src/Api/Api.csproj").stat().st_mode & 0o222)
            observed.append((copy / "src/Api").stat().st_mode & 0o222)

        with patch.object(closure, "source_snapshot", side_effect=inspect_source):
            errors, count, _, _ = self.guard(self.entry(root))
        self.assertEqual(([], 1), (errors, count))
        self.assertEqual([0, 0], observed)
        self.assertEqual(before, self.original_bytes())
        self.assertFalse((root.parent / "obj").exists())
        self.assertFalse((root.parent / "bin").exists())

    def test_linux_sandbox_requires_readonly_mounts_and_never_falls_back(self):
        source, runtime = self.scratch / "linux-source", self.scratch / "linux-runtime"
        source.mkdir()
        runtime.mkdir()
        project = source / "Example.csproj"
        project.write_text("<Project/>")
        isolation = closure.Isolation(source, runtime, self.scratch / "denied")
        with patch.object(closure.sys, "platform", "linux"):
            with patch.object(closure.shutil, "which", return_value=None):
                with self.assertRaisesRegex(closure.GuardFailure, "requires bubblewrap"):
                    closure.isolated_command(["dotnet", "build"], isolation, project)
            with patch.object(closure.shutil, "which", return_value="/usr/bin/bwrap"):
                command = closure.isolated_command(["dotnet", "build"], isolation, project)
                self.assertEqual("/usr/bin/bwrap", command[0])
                self.assertEqual(["dotnet", "build"], command[-2:])
                self.assertIn("--disable-userns", command)
                self.assertIn(["--ro-bind", "/", "/"], [
                    command[index:index + 3] for index in range(len(command) - 2)
                ])
                self.assertIn(
                    ["--bind", str(runtime), str(runtime)],
                    [command[index:index + 3] for index in range(len(command) - 2)],
                )
                self.assertIn(
                    ["--bind", str(runtime / "tmp"), "/tmp"],
                    [command[index:index + 3] for index in range(len(command) - 2)],
                )
                self.assertIn(
                    ["--bind", str(runtime / "var-tmp"), "/var/tmp"],
                    [command[index:index + 3] for index in range(len(command) - 2)],
                )
                temporary_isolation = closure.Isolation(
                    Path("/tmp/guard-source"), Path("/tmp/guard-runtime"),
                    Path("/tmp/guard-denied"),
                )
                with self.assertRaisesRegex(closure.GuardFailure, "outside /tmp"):
                    closure.isolated_command(
                        ["dotnet", "build"], temporary_isolation, project,
                    )

    def test_task_level_global_removal_is_observed_not_predicted(self):
        self.package("Comet")
        leaf = self.project(
            "Leaf", '<ItemGroup><PackageReference Include="Comet" Version="1.0.0" '
            'Condition="\'$(IncludeMobileTargets)\' != \'false\'"/></ItemGroup>',
        )
        root = self.project(
            "Api", "<PropertyGroup><_GlobalPropertiesToRemoveFromProjectReferences>"
            "IncludeMobileTargets</_GlobalPropertiesToRemoveFromProjectReferences>"
            "</PropertyGroup><ItemGroup>"
            '<ProjectReference Include="../Leaf/Leaf.csproj"/></ItemGroup>',
        )
        entry = self.entry(root)
        _, built, control = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.assertNotIn("includemobiletargets", self.actual_child(control, leaf))

        errors, _, observed, _ = self.guard(entry)

        self.assertNotIn("includemobiletargets", self.actual_child(observed, leaf))
        self.assert_forbidden(errors, "Comet")

    def test_prepared_rid_removal_reaches_build_only_child(self):
        self.package("Comet")
        leaf = self.project(
            "Leaf", '<ItemGroup><PackageReference Include="Comet" Version="1.0.0" '
            'Condition="\'$(RuntimeIdentifier)\' == \'\'"/></ItemGroup>',
        )
        root = self.project(
            "Api", '<ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj" '
            'ReferenceOutputAssembly="false"/></ItemGroup>',
        )
        entry = self.entry(root, rid="linux-x64")
        _, built, control = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.assertNotEqual(
            "linux-x64", self.actual_child(control, leaf).get("runtimeidentifier"),
        )

        errors, _, observed, _ = self.guard(entry)

        self.assertNotEqual(
            "linux-x64", self.actual_child(observed, leaf).get("runtimeidentifier"),
        )
        self.assert_forbidden(errors, "Comet")

    def test_prepared_target_framework_removal_checks_query_and_inner_build(self):
        self.package("Comet")
        leaf = self.project(
            "Leaf", '<ItemGroup><PackageReference Include="Comet" Version="1.0.0" '
            'Condition="\'$(TargetFramework)\' == \'net11.0\'"/></ItemGroup>',
            frameworks="net10.0;net11.0",
        )
        root = self.project(
            "Api", '<ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj"/>'
            "</ItemGroup>",
        )
        entry = self.entry(
            root, properties=(("IncludeMobileTargets", "false"),
                              ("TargetFramework", "net10.0"), ("SelfContained", "false")),
        )
        _, built, control = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        queried = self.actual_child(control, leaf, target="GetTargetFrameworks")
        self.assertNotIn("targetframework", queried)
        self.assertNotIn("selfcontained", queried)

        errors, _, observed, _ = self.guard(entry)

        queried = self.actual_child(observed, leaf, target="GetTargetFrameworks")
        self.assertNotIn("targetframework", queried)
        self.assertNotIn("selfcontained", queried)
        self.assert_forbidden(errors, "Comet")

    def test_set_target_framework_metadata_is_seen_after_sdk_preparation(self):
        self.package("Comet")
        leaf = self.project(
            "Leaf", '<ItemGroup><PackageReference Include="Comet" Version="1.0.0" '
            'Condition="\'$(TargetFramework)\' == \'net11.0\'"/></ItemGroup>',
            frameworks="net10.0;net11.0",
        )
        root = self.project(
            "Api", '<ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj" '
            'ReferenceOutputAssembly="false" '
            'SetTargetFramework="TargetFramework=net11.0"/></ItemGroup>',
        )
        entry = self.entry(root)
        _, built, control = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.assertEqual("net11.0", self.actual_child(control, leaf).get("targetframework"))

        errors, _, observed, _ = self.guard(entry)

        self.assertEqual("net11.0", self.actual_child(observed, leaf).get("targetframework"))
        self.assert_forbidden(errors, "Comet")

    def test_nested_build_only_refs_keep_real_additional_properties(self):
        self.package("Comet")
        leaf = self.project(
            "Leaf", '<ItemGroup><PackageReference Include="Comet" Version="1.0.0" '
            'Condition="\'$(EnableComet)\' == \'true\'"/></ItemGroup>',
        )
        self.project(
            "Middle", '<ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj" '
            'ReferenceOutputAssembly="false"/></ItemGroup>',
        )
        root = self.project(
            "Api", '<ItemGroup><ProjectReference Include="../Middle/Middle.csproj" '
            'ReferenceOutputAssembly="false" AdditionalProperties="EnableComet=true"/>'
            "</ItemGroup>",
        )
        entry = self.entry(root)
        _, built, control = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.assertEqual("true", self.actual_child(control, leaf).get("enablecomet"))

        errors, count, observed, _ = self.guard(entry)

        self.assertEqual(3, count)
        self.assertEqual("true", self.actual_child(observed, leaf).get("enablecomet"))
        self.assert_forbidden(errors, "Comet")

    def test_additional_properties_and_configuration_platform_setters(self):
        self.package("Comet")
        for metadata, condition, expected_name, expected_value in (
            ('AdditionalProperties="EnableComet=true"', "EnableComet", "enablecomet", "true"),
            ('SetConfiguration="Configuration=Special"', "Configuration", "configuration", "Special"),
            ('SetPlatform="Platform=Special"', "Platform", "platform", "Special"),
        ):
            with self.subTest(metadata=metadata):
                leaf = self.project(
                    "Leaf", '<ItemGroup><PackageReference Include="Comet" Version="1.0.0" '
                    f'Condition="\'$({condition})\' == \'{expected_value}\'"/></ItemGroup>',
                )
                root = self.project(
                    "Api", '<ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj" '
                    f'{metadata}/></ItemGroup>',
                )
                entry = self.entry(root)
                _, built, control = self.normal_build(root, entry)
                self.assertEqual(0, built.returncode, built.stdout + built.stderr)
                self.assertEqual(
                    expected_value, self.actual_child(control, leaf).get(expected_name),
                )
                errors, _, observed, _ = self.guard(entry)
                self.assertEqual(
                    expected_value, self.actual_child(observed, leaf).get(expected_name),
                )
                self.assert_forbidden(errors, "Comet")

    def test_conditional_imported_package_is_scoped_to_real_configuration(self):
        self.package("Comet")
        imported = self.root / "src/conditional.targets"
        imported.write_text(
            '<Project><ItemGroup><PackageReference Include="Comet" Version="1.0.0" '
            'Condition="\'$(Configuration)\' == \'Release\'"/></ItemGroup></Project>',
        )
        root = self.project("Api", '<Import Project="../conditional.targets"/>')
        debug = self.entry(root, configuration="Debug")
        release = self.entry(root)
        _, built, _ = self.normal_build(root, debug)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.assertEqual([], self.guard(debug)[0])
        _, built, _ = self.normal_build(root, release)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.assert_forbidden(self.guard(release)[0], "Comet")

    def test_stale_repo_assets_cannot_hide_new_imported_package(self):
        self.package("Comet")
        imported = self.root / "src/dependencies.targets"
        imported.write_text("<Project/>")
        root = self.project("Api", '<Import Project="../dependencies.targets"/>')
        entry = self.entry(root)
        _, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        old_assets = root.parent / "obj/project.assets.json"
        original = old_assets.read_bytes()
        imported.write_text(
            '<Project><ItemGroup><PackageReference Include="Comet" '
            'Version="1.0.0"/></ItemGroup></Project>',
        )
        _, stale_build, _ = self.normal_build(root, entry, restore=False)
        self.assertEqual(0, stale_build.returncode, stale_build.stdout + stale_build.stderr)
        self.assertEqual(original, old_assets.read_bytes())

        errors, _, _, _ = self.guard(entry)

        self.assert_forbidden(errors, "Comet")
        self.assertEqual(original, old_assets.read_bytes())

    def test_assets_swapped_after_marker_cannot_hide_actual_sdk_task_input(self):
        self.package("Comet")
        imported = self.root / "src/dependencies.targets"
        imported.write_text(
            '<Project><ItemGroup><PackageReference Include="Comet" '
            'Version="1.0.0"/></ItemGroup></Project>',
        )
        root = self.project("Api", '<Import Project="../dependencies.targets"/>')
        entry = self.entry(root)
        _, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        stale = root.parent / "obj/project.assets.json"
        self.assertIn("Comet/1.0.0", json.loads(stale.read_text())["libraries"])
        shutil.copyfile(stale, self.root / "src/stale-assets.json")
        (self.root / "src/replace-assets.py").write_text(
            "from pathlib import Path\n"
            "import sys\n"
            "Path(sys.argv[2]).write_bytes(Path(sys.argv[1]).read_bytes())\n",
        )
        imported.write_text("<Project/>")
        root.write_text(root.read_text().replace(
            "</Project>",
            '<Target Name="SwapAfterGuard" AfterTargets="_DependencyGuardAssets" '
            'BeforeTargets="ResolvePackageAssets" '
            'Condition="\'$(DependencyGuardScratch)\' != \'\'">'
            f'<Exec Command="&quot;{escape(sys.executable)}&quot; '
            '&quot;$(MSBuildProjectDirectory)/../replace-assets.py&quot; '
            '&quot;$(MSBuildProjectDirectory)/../stale-assets.json&quot; '
            '&quot;$(ProjectAssetsFile)&quot;"/></Target></Project>',
        ))
        _, built, _ = self.normal_build(root, entry, restore=False)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)

        errors, _, _, _ = self.guard(entry)

        self.assertTrue(any("ResolvePackageAssets" in error and "ProjectAssetsFile" in error
                            for error in errors), errors)

    def test_repo_output_override_is_rejected_before_restore_writes(self):
        root = self.project(
            "Api", "<PropertyGroup>"
            "<BaseIntermediateOutputPath>obj/</BaseIntermediateOutputPath>"
            "<MSBuildProjectExtensionsPath>obj/</MSBuildProjectExtensionsPath>"
            "</PropertyGroup>",
        )
        entry = self.entry(root)
        restored, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
        self.assertNotEqual(0, built.returncode)
        self.assertIn("MSB3540", built.stdout + built.stderr)
        old_assets = (root.parent / "obj/project.assets.json").read_bytes()

        errors, _, _, _ = self.guard(entry)

        self.assertTrue(any("unsafe AssetsFile" in error for error in errors), errors)
        self.assertEqual(old_assets, (root.parent / "obj/project.assets.json").read_bytes())

    def test_restore_only_output_override_cannot_escape_scratch(self):
        outside = self.scratch / "escaped-assets"
        root = self.project(
            "Api", "<PropertyGroup Condition=\"'$(MSBuildIsRestoring)' == 'true'\">"
            f"<RestoreOutputPath>{escape(str(outside))}/</RestoreOutputPath>"
            "</PropertyGroup>",
        )
        entry = self.entry(root)
        restored, _, _ = self.normal_build(root, entry)
        self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
        self.assertTrue((outside / "project.assets.json").exists())
        before = (outside / "project.assets.json").read_bytes()

        errors, _, _, _ = self.guard(entry)

        self.assertTrue(any("unsafe RestoreOutputPath" in error for error in errors), errors)
        self.assertEqual(before, (outside / "project.assets.json").read_bytes())

    def test_outdir_override_cannot_write_build_artifacts_outside_scratch(self):
        external = self.scratch / "outside-output"
        root = self.project(
            "Api", f"<PropertyGroup><OutDir>{escape(str(external))}/</OutDir>"
            "</PropertyGroup>",
        )
        entry = self.entry(root)
        _, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.assertTrue((external / "Api.dll").is_file())

        errors, _, _, _ = self.guard(entry)

        self.assertTrue(any("unsafe OutDir" in error for error in errors), errors)

    def test_fallback_folder_can_supply_control_but_never_guard(self):
        archive = self.package("Neutral.Package")
        staging = self.project(
            "Staging", '<ItemGroup><PackageReference Include="Neutral.Package" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        fallback = self.scratch / "fallback"
        staged, _, _ = self.normal_build(staging, self.entry(staging), packages=fallback)
        self.assertEqual(0, staged.returncode, staged.stdout + staged.stderr)
        archive.unlink()
        root = self.project(
            "Api", "<PropertyGroup>"
            f"<RestoreFallbackFolders>{escape(str(fallback))}</RestoreFallbackFolders>"
            "</PropertyGroup><ItemGroup><PackageReference Include=\"Neutral.Package\" "
            'Version="[1.0.0]"/></ItemGroup>',
        )
        entry = self.entry(root)
        restored, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        control_assets = json.loads((root.parent / "obj/project.assets.json").read_text())
        self.assertIn(fallback, {Path(folder).resolve()
                                 for folder in control_assets["packageFolders"]})

        errors, _, _, _ = self.guard(entry)

        self.assertTrue(any("RestoreFallbackFolders" in error for error in errors), errors)

    def test_ambient_global_cache_cannot_supply_missing_source_package(self):
        archive = self.package("Neutral.Package")
        staging = self.project(
            "Staging", '<ItemGroup><PackageReference Include="Neutral.Package" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        global_cache = self.scratch / "ambient-packages"
        staged, _, _ = self.normal_build(staging, self.entry(staging), packages=global_cache)
        self.assertEqual(0, staged.returncode, staged.stdout + staged.stderr)
        archive.unlink()
        root = self.project(
            "Api", '<ItemGroup><PackageReference Include="Neutral.Package" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        entry = self.entry(root)
        with patch.dict(os.environ, {"NUGET_PACKAGES": str(global_cache)}):
            control = subprocess.run(
                [
                    "dotnet", "restore", str(root), "--force", "--nologo",
                    "--no-http-cache", "--verbosity", "quiet", "-m:1",
                    "--configfile", str(self.config), "-p:Configuration=Release",
                ],
                cwd=root.parent, capture_output=True, text=True, timeout=120,
                check=False, env=self.control_environment(preserve_packages=True),
            )
            self.assertEqual(0, control.returncode, control.stdout + control.stderr)
            assets = json.loads((root.parent / "obj/project.assets.json").read_text())
            self.assertEqual(
                {global_cache}, {Path(path).resolve() for path in assets["packageFolders"]},
            )
            errors, _, _, _ = self.guard(entry)
        self.assertTrue(any("NU1101" in error for error in errors), errors)
        self.assertTrue((global_cache / "neutral.package/1.0.0").is_dir())

    def test_http_plugin_and_temp_caches_never_write_outside_guard_scratch(self):
        self.package("Neutral.Package")
        root = self.project(
            "Api", '<ItemGroup><PackageReference Include="Neutral.Package" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        external = self.scratch / "external-cache"
        http, plugin, temporary = (external / name for name in ("http", "plugins", "tmp"))
        for directory in (http, plugin, temporary):
            directory.mkdir(parents=True)
            (directory / "keep").write_bytes(b"unchanged")
        original = self.original_bytes()
        commands = []
        run = closure.run_command

        def capture(command, *args):
            if command[:2] == ["dotnet", "restore"]:
                commands.append(command)
            return run(command, *args)

        with patch.dict(os.environ, {
            "NUGET_HTTP_CACHE_PATH": str(http),
            "NUGET_PLUGINS_CACHE_PATH": str(plugin),
            "TMPDIR": str(temporary),
        }), patch.object(closure, "run_command", side_effect=capture):
            errors, count, events, metrics = self.guard(self.entry(root))

        self.assertEqual(([], 1), (errors, count))
        self.assertEqual(1, metrics["root_restores"])
        self.assertTrue(commands)
        self.assertTrue(all("--no-http-cache" in command for command in commands))
        instance = next(
            json.loads(event["data"]) for event in events
            if event["kind"] == "record"
            and json.loads(event["data"])["phase"] == "instance"
            and self.matches_project(json.loads(event["data"])["project"], root)
        )
        for name in ("NUGET_HTTP_CACHE_PATH", "NUGET_PLUGINS_CACHE_PATH", "TMPDIR"):
            path = Path(instance["cacheEnvironment"][name])
            self.assertTrue(path.is_relative_to(self.scratch), path)
            self.assertFalse(path.is_relative_to(external), path)
        self.assertEqual(original, self.original_bytes())
        for directory in (http, plugin, temporary):
            self.assertEqual(["keep"], [path.name for path in directory.iterdir()])
            self.assertEqual(b"unchanged", (directory / "keep").read_bytes())
        self.assertFalse(list(self.scratch.glob("platform-neutral-build-*")))
        self.assertFalse((root.parent / "obj").exists())
        self.assertFalse((root.parent / "bin").exists())

    def test_child_cannot_strip_the_isolated_package_cache(self):
        leaf = self.project("Leaf")
        root = self.project(
            "Api", '<ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj" '
            'GlobalPropertiesToRemove="RestorePackagesPath;NuGetPackageRoot"/>'
            "</ItemGroup>",
        )
        entry = self.entry(root)
        _, built, control = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.assertNotIn("restorepackagespath", self.actual_child(control, leaf))

        errors, _, observed, _ = self.guard(entry)

        self.assertTrue(
            any(event["kind"] == "start" and self.matches_project(event["project"], leaf)
                for event in observed),
            errors,
        )
        self.assertTrue(any("unsafe RestorePackagesPath" in error
                            and "Leaf.csproj" in error for error in errors), errors)

    def test_hook_removed_by_child_reference_cannot_disappear_from_logger(self):
        leaf = self.project("Leaf")
        root = self.project(
            "Api", '<ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj" '
            'GlobalPropertiesToRemove="CustomAfterMicrosoftCommonTargets;'
            'CustomAfterMicrosoftCommonCrossTargetingTargets"/></ItemGroup>',
        )
        entry = self.entry(root)
        _, built, control = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.actual_child(control, leaf)

        errors, _, recorded, _ = self.guard(entry)

        self.actual_child(recorded, leaf)
        self.assertTrue(any("instrumentation missing from actual child" in error
                            and "Leaf.csproj" in error for error in errors), errors)

    def test_task_level_hook_removal_is_independently_detected(self):
        leaf = self.project("Leaf")
        root = self.project(
            "Api", "<PropertyGroup><_GlobalPropertiesToRemoveFromProjectReferences>"
            "CustomAfterMicrosoftCommonTargets;CustomAfterMicrosoftCommonCrossTargetingTargets"
            "</_GlobalPropertiesToRemoveFromProjectReferences></PropertyGroup>"
            '<ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj"/></ItemGroup>',
        )
        entry = self.entry(root)
        _, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)

        errors, _, recorded, _ = self.guard(entry)

        self.actual_child(recorded, leaf)
        self.assertTrue(any("instrumentation missing from actual child" in error
                            for error in errors), errors)

    def test_repo_local_build_only_proj_cannot_evade_project_inventory(self):
        self.package("Comet")
        hidden = self.package_proj(self.root / "src/Hidden/Hidden.proj")
        restored, built, _ = self.normal_build(hidden, self.entry(hidden))
        self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        root = self.project(
            "Api", '<Target Name="BuildHidden" BeforeTargets="Build">'
            '<MSBuild Projects="../Hidden/Hidden.proj" Targets="Build" '
            'RemoveProperties="CustomAfterDirectoryBuildProps;'
            'CustomAfterMicrosoftCommonTargets;CustomAfterMicrosoftCommonCrossTargetingTargets;'
            'DependencyGuardAssembly;DependencyGuardScratch;DependencyGuardToken;'
            'RestoreConfigFile;RestorePackagesPath;NuGetPackageRoot;RestoreForce"/>'
            "</Target>",
        )
        entry = self.entry(root)
        _, built, control = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.assertTrue(any(event["kind"] == "start" and event["project"] == str(hidden)
                            for event in control), control)

        errors, _, recorded, _ = self.guard(entry)

        self.assertTrue(any(event["kind"] == "start"
                            and self.matches_project(event["project"], hidden)
                            for event in recorded), errors)
        self.assertTrue(any("unsupported active project" in error
                            and "Hidden.proj" in error for error in errors), errors)

    def test_external_build_only_proj_cannot_evade_project_inventory(self):
        self.package("Comet")
        hidden = self.package_proj(self.scratch / "external/Hidden.proj")
        entry = self.entry(self.project("Api"))
        restored, built, _ = self.normal_build(hidden, entry)
        self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        root = self.project(
            "Api", '<Target Name="BuildHidden" BeforeTargets="Build">'
            f'<MSBuild Projects="{escape(str(hidden))}" Targets="Build" '
            'RemoveProperties="CustomAfterDirectoryBuildProps;'
            'CustomAfterMicrosoftCommonTargets;CustomAfterMicrosoftCommonCrossTargetingTargets;'
            'DependencyGuardAssembly;DependencyGuardScratch;DependencyGuardToken;'
            'RestoreConfigFile;RestorePackagesPath;NuGetPackageRoot;RestoreForce"/>'
            "</Target>",
        )
        _, built, control = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        self.assertTrue(any(event["kind"] == "start" and event["project"] == str(hidden)
                            for event in control), control)

        errors, _, recorded, _ = self.guard(entry)

        self.assertTrue(any(event["kind"] == "start" and event["project"] == str(hidden)
                            for event in recorded), recorded)
        self.assertTrue(any("project reference escapes the repository" in error
                            and "Hidden.proj" in error for error in errors), errors)

    def test_local3_provider_and_stable_coresync_resolve_together(self):
        self.package("CoreSync", version="0.1.129")
        provider = self.package(
            "CoreSync.PostgreSQL", version=closure.LOCAL3_VERSION,
            dependencies=(("CoreSync", "[0.1.129-local3,)"),),
        )
        self.package(
            "CoreSync.Sqlite", version="0.1.129",
            dependencies=(("CoreSync", "[0.1.129,)"),),
        )
        root = self.project(
            "Api", '<ItemGroup><PackageReference Include="CoreSync.PostgreSQL" '
            f'Version="[{closure.LOCAL3_VERSION}]"/>'
            '<PackageReference Include="CoreSync.Sqlite" Version="[0.1.129]"/>'
            "</ItemGroup>",
        )
        entry = self.entry(root)
        _, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        assets = json.loads((root.parent / "obj/project.assets.json").read_text())
        self.assertIn("CoreSync/0.1.129", assets["libraries"])
        self.assertIn(
            f"CoreSync.PostgreSQL/{closure.LOCAL3_VERSION}", assets["libraries"],
        )

        reviewed = hashlib.sha256(provider.read_bytes()).hexdigest()
        with patch.object(closure, "PROVIDER_SHA256", reviewed):
            self.assertEqual([], self.guard(entry)[0])
        with patch.object(closure, "PROVIDER_SHA256", "0" * 64):
            errors, _, _, _ = self.guard(entry)
        self.assertTrue(any("SHA-256 differs" in error for error in errors), errors)

    def test_public_provider_version_is_rejected_after_real_build(self):
        self.package("CoreSync.PostgreSQL", version="0.1.129")
        root = self.project(
            "Api", '<ItemGroup><PackageReference Include="CoreSync.PostgreSQL" '
            'Version="[0.1.129]"/></ItemGroup>',
        )
        entry = self.entry(root)
        _, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)

        errors, _, _, _ = self.guard(entry)

        self.assertTrue(any("requires exact 0.1.129-local3" in error for error in errors), errors)

    def test_real_package_download_is_checked_even_without_a_target_library(self):
        self.package("Comet")
        root = self.project(
            "Api", '<ItemGroup><PackageDownload Include="Comet" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        entry = self.entry(root)
        _, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        assets = json.loads((root.parent / "obj/project.assets.json").read_text())
        downloads = assets["project"]["frameworks"]["net10.0"]["downloadDependencies"]
        self.assertTrue(any(item["name"] == "Comet" for item in downloads), downloads)
        self.assertFalse(any(key.startswith("Comet/") for key in assets["libraries"]))

        errors, _, _, _ = self.guard(entry)

        self.assertTrue(any("forbidden PackageDownload Comet" in error
                            for error in errors), errors)

    def test_asset_download_dependency_is_checked_without_an_observed_download_item(self):
        self.package("Neutral.Package")
        root = self.project(
            "Api", '<ItemGroup><PackageDownload Include="Neutral.Package" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        entry = self.entry(root)
        _, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        run = closure.run_observed
        changed = False

        def add_download_to_assets(verb, project, globals_, entry_dir, *args):
            nonlocal changed
            result = run(verb, project, globals_, entry_dir, *args)
            if verb == "restore" and project.name == "Api.csproj" and not changed:
                asset_file = entry_dir / "obj/Api/project.assets.json"
                graph_file = entry_dir / "obj/Api/Api.csproj.nuget.dgspec.json"
                assets = json.loads(asset_file.read_text())
                graph = json.loads(graph_file.read_text())
                dependencies = assets["project"]["frameworks"]["net10.0"][
                    "downloadDependencies"]
                self.assertTrue(any(item["name"] == "Neutral.Package" for item in dependencies))
                self.assertFalse(any(item["name"] == "Comet" for item in dependencies))
                dependencies.append({"name": "Comet", "version": "[1.0.0]"})
                graph["projects"][str(project)]["frameworks"]["net10.0"][
                    "downloadDependencies"].append({"name": "Comet", "version": "[1.0.0]"})
                asset_file.write_text(json.dumps(assets))
                graph_file.write_text(json.dumps(graph))
                changed = True
            return result

        with patch.object(closure, "run_observed", side_effect=add_download_to_assets):
            errors, _, observed, _ = self.guard(entry)

        self.assertTrue(changed)
        self.assertFalse(any(
            event["kind"] == "record" and
            any(item["id"] == "Comet" for item in
                json.loads(event["data"]).get("packageDownloads", []))
            for event in observed
        ))
        self.assertTrue(any("forbidden PackageDownload Comet" in error
                            for error in errors), errors)

    def test_allowed_download_is_resolved_only_in_isolated_package_cache(self):
        self.package("Neutral.Package")
        root = self.project(
            "Api", '<ItemGroup><PackageDownload Include="Neutral.Package" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        entry = self.entry(root)
        _, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        assets = json.loads((root.parent / "obj/project.assets.json").read_text())
        downloads = assets["project"]["frameworks"]["net10.0"]["downloadDependencies"]
        self.assertTrue(
            any(item["name"] == "Neutral.Package" for item in downloads),
            downloads,
        )

        errors, count, _, metrics = self.guard(entry)

        self.assertEqual(([], 1), (errors, count))
        self.assertEqual(1, metrics["root_restores"])
        self.assertEqual(1, metrics["observed_context_restores"])

    def test_nu1603_is_a_guard_error_even_when_control_restores(self):
        self.package("Plain.Package", version="1.0.1")
        self.package(
            "Intermediate.Package",
            dependencies=(("Plain.Package", "[1.0.0,)"),),
        )
        root = self.project(
            "Api", '<ItemGroup><PackageReference Include="Intermediate.Package" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        entry = self.entry(root)
        restored, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)

        errors, _, _, _ = self.guard(entry)

        self.assertTrue(any("NU1603" in error for error in errors), errors)

    def test_nu1605_downgrade_is_a_guard_error(self):
        self.package("Plain.Package", version="1.0.0")
        self.package("Plain.Package", version="2.0.0")
        self.package(
            "Intermediate.Package",
            dependencies=(("Plain.Package", "[2.0.0,)"),),
        )
        root = self.project(
            "Api", '<ItemGroup><PackageReference Include="Plain.Package" '
            'Version="[1.0.0]"/><PackageReference Include="Intermediate.Package" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        entry = self.entry(root)
        restored, _, _ = self.normal_build(root, entry)
        self.assertIn("NU1605", restored.stdout + restored.stderr)

        errors, _, _, _ = self.guard(entry)

        self.assertTrue(any("NU1605" in error for error in errors), errors)

    def test_nu1603_and_nu1605_suppression_at_each_reference_scope(self):
        self.package("Neutral.Package")
        leaf = self.project("Leaf")
        for code in ("NU1603", "NU1605"):
            for scope, body in (
                ("project", f"<PropertyGroup><NoWarn>{code}</NoWarn></PropertyGroup>"),
                ("PackageReference",
                 '<ItemGroup><PackageReference Include="Neutral.Package" '
                 f'Version="[1.0.0]" NoWarn="{code}"/></ItemGroup>'),
                ("PackageDownload",
                 '<ItemGroup><PackageDownload Include="Neutral.Package" '
                 f'Version="[1.0.0]" NoWarn="{code}"/></ItemGroup>'),
                ("ProjectReference",
                 '<ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj" '
                 f'NoWarn="{code}"/></ItemGroup>'),
            ):
                with self.subTest(scope=scope, code=code):
                    root = self.project("Api", body)
                    entry = self.entry(root)
                    _, built, _ = self.normal_build(root, entry)
                    self.assertEqual(0, built.returncode, built.stdout + built.stderr)
                    errors, _, _, _ = self.guard(entry)
                    self.assertTrue(
                        any(code in error and (scope in error or scope == "project")
                            for error in errors),
                        errors,
                    )
        self.assertTrue(leaf.is_file())

    def test_package_reference_suppression_hides_real_nuget_warning(self):
        self.package("Plain.Package", version="1.0.1")
        self.package(
            "Intermediate.Package",
            dependencies=(("Plain.Package", "[1.0.0,)"),),
        )
        root = self.project(
            "Api", '<ItemGroup><PackageReference Include="Intermediate.Package" '
            'Version="[1.0.0]" NoWarn="NU1603"/></ItemGroup>',
        )
        entry = self.entry(root)
        restored, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        assets = json.loads((root.parent / "obj/project.assets.json").read_text())
        metadata = assets["project"]["frameworks"]["net10.0"]["dependencies"]["Intermediate.Package"]
        self.assertIn("NU1603", metadata["noWarn"])
        self.assertTrue(any(key.startswith("Plain.Package/1.0.1")
                            for key in assets["libraries"]))
        errors, _, _, _ = self.guard(entry)
        self.assertTrue(any("NU1603" in error and "PackageReference" in error
                            for error in errors), errors)

    def test_package_reference_suppression_hides_real_nuget_downgrade(self):
        self.package("Plain.Package", version="1.0.0")
        self.package("Plain.Package", version="2.0.0")
        self.package(
            "Intermediate.Package",
            dependencies=(("Plain.Package", "[2.0.0,)"),),
        )
        root = self.project(
            "Api", '<ItemGroup><PackageReference Include="Plain.Package" '
            'Version="[1.0.0]" NoWarn="NU1605"/>'
            '<PackageReference Include="Intermediate.Package" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        entry = self.entry(root)
        restored, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        assets = json.loads((root.parent / "obj/project.assets.json").read_text())
        metadata = assets["project"]["frameworks"]["net10.0"]["dependencies"]["Plain.Package"]
        self.assertIn("NU1605", metadata["noWarn"])
        errors, _, _, _ = self.guard(entry)
        self.assertTrue(any("NU1605" in error and "PackageReference" in error
                            for error in errors), errors)

    def test_resolved_version_is_checked_against_requested_range_independently(self):
        self.package("Plain.Package", version="1.0.0")
        root = self.project(
            "Api", '<ItemGroup><PackageReference Include="Plain.Package" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        entry = self.entry(root)
        _, built, _ = self.normal_build(root, entry)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        changed = False
        run = closure.run_observed

        def change_restored_range(verb, project, globals_, entry_dir, *args):
            nonlocal changed
            result = run(verb, project, globals_, entry_dir, *args)
            if verb == "restore" and project.name == "Api.csproj" and not changed:
                asset = entry_dir / "obj/Api/project.assets.json"
                spec_file = entry_dir / "obj/Api/Api.csproj.nuget.dgspec.json"
                contents = json.loads(asset.read_text())
                graph = json.loads(spec_file.read_text())
                dependencies = contents["project"]["frameworks"]["net10.0"]["dependencies"]
                self.assertIn("1.0.0", dependencies["Plain.Package"]["version"])
                dependencies["Plain.Package"]["version"] = "[2.0.0]"
                graph["projects"][str(project)]["frameworks"]["net10.0"]["dependencies"][
                    "Plain.Package"]["version"] = "[2.0.0]"
                asset.write_text(json.dumps(contents))
                spec_file.write_text(json.dumps(graph))
                changed = True
            return result

        with patch.object(closure, "run_observed", side_effect=change_restored_range):
            errors, _, _, _ = self.guard(entry)

        self.assertTrue(changed)
        self.assertTrue(any("outside requested range" in error
                            and "Plain.Package" in error for error in errors), errors)

    def test_exact_provider_feed_and_source_mapping_preflight(self):
        self.project("SentenceStudio.Shared")
        (self.root / "Directory.Packages.props").write_text(
            '<Project><ItemGroup><PackageVersion Include="CoreSync.PostgreSQL" '
            f'Version="[{closure.LOCAL3_VERSION}]"/></ItemGroup></Project>',
        )
        feed = self.root / "lib/coresync-postgresql-local3"
        feed.mkdir(parents=True)
        package = feed / closure.PROVIDER_ARCHIVE
        shutil.copyfile(
            closure.ROOT / "lib/coresync-postgresql-local3" / closure.PROVIDER_ARCHIVE,
            package,
        )
        self.config.write_text(
            '<configuration><packageSources><clear/>'
            '<add key="coresync-postgresql-local3" value="../lib/coresync-postgresql-local3"/>'
            '<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>'
            '<add key="localnugets" value="/outside/developer-feed"/>'
            '</packageSources><packageSourceMapping>'
            '<packageSource key="coresync-postgresql-local3">'
            '<package pattern="CoreSync.PostgreSQL"/></packageSource>'
            '<packageSource key="nuget.org">'
            '<package pattern="CoreSync"/><package pattern="*"/></packageSource>'
            '<packageSource key="localnugets"><package pattern="*"/></packageSource>'
            '</packageSourceMapping></configuration>',
        )
        with tempfile.TemporaryDirectory(dir=self.scratch) as temporary:
            generated, sources = closure.prepare_restore_sources(self.root, Path(temporary))
            text = generated.read_text()
            self.assertNotIn("localnugets", text.casefold())
            self.assertIn(str(feed), sources)
            original = package.read_bytes()
            package.write_bytes(b"unreviewed")
            with self.assertRaisesRegex(ValueError, "unexpected SHA-256"):
                closure.prepare_restore_sources(self.root, Path(temporary))
            package.write_bytes(original)
            self.config.write_text(
                self.config.read_text().replace(
                    '<package pattern="CoreSync"/>',
                    '<package pattern="CoreSync.PostgreSQL"/><package pattern="CoreSync"/>',
                ),
            )
            with self.assertRaisesRegex(ValueError, "nonlocal exact source mapping"):
                closure.prepare_restore_sources(self.root, Path(temporary))

    def test_scratch_and_cli_do_not_clean_preexisting_files(self):
        root = self.project("Api")
        marker = self.scratch / "keep"
        marker.write_text("preserve")
        with patch.object(closure, "ROOT", self.root), patch.object(
            closure, "ACTIVE_MATRIX", (self.entry(root),)
        ), patch.object(
            sys, "argv", ["guard", "--scratch-parent", str(self.scratch)]
        ), redirect_stdout(io.StringIO()) as output, redirect_stderr(io.StringIO()) as diagnostics:
            exit_code = closure.main()
        self.assertEqual(0, exit_code, diagnostics.getvalue())
        self.assertIn("PASS: 1 active projects; 1 root builds", output.getvalue())
        self.assertIn(
            "1 initial root restores, 1 observed-context forced restores", output.getvalue(),
        )
        self.assertEqual([marker], list(self.scratch.iterdir()))
        self.assertEqual("preserve", marker.read_text())
        self.assertFalse((root.parent / "obj").exists())

    def test_low_disk_evicts_only_guard_owned_packages_between_roots(self):
        self.package("Neutral.Package")
        root = self.project(
            "Api", '<ItemGroup><PackageReference Include="Neutral.Package" '
            'Version="[1.0.0]"/></ItemGroup>',
        )
        keep = self.scratch / "keep"
        keep.write_bytes(b"unrelated scratch file")
        debug, release = self.entry(root, configuration="Debug"), self.entry(root)
        with patch.object(closure, "PACKAGE_CACHE_HEADROOM_BYTES", 1 << 60):
            errors, count, _, metrics = self.guard(debug, release)
        self.assertEqual(([], 1), (errors, count))
        self.assertEqual(2, metrics["root_restores"])
        self.assertEqual(2, metrics["observed_context_restores"])
        self.assertEqual(1, metrics["cache_evictions"])
        self.assertEqual(b"unrelated scratch file", keep.read_bytes())
        self.assertEqual([keep], list(self.scratch.iterdir()))
        self.assertFalse((root.parent / "obj").exists())
        self.assertFalse((root.parent / "bin").exists())

    def test_nonexistent_or_inside_checkout_scratch_fails_before_any_dotnet(self):
        root = self.project("Api")
        for parent, message in (
            (self.root / "does-not-exist", "not an existing directory"),
            (self.root / "src", "must be outside the repository"),
        ):
            with self.subTest(parent=parent), patch.object(closure, "build_logger") as invoked:
                errors, count = closure.validate(
                    self.root, (self.entry(root),), scratch_parent=parent,
                )
                self.assertEqual(0, count)
                self.assertIn(message, errors[0])
                invoked.assert_not_called()


if __name__ == "__main__":
    unittest.main()
