# Skill: Aspire.Hosting.Maui bundle-name shim (MSBuild post-build)

## When to use

A .NET MAUI project orchestrated by Aspire silently fails to launch (resource
state goes to Exited / Failed, no console logs, exit code 1) and the project's
`<ApplicationTitle>` differs from `$(MSBuildProjectName)`.

Cause: `Aspire.Hosting.Maui` (≤ 13.3.0-preview) computes the bundle path as
`bin/$(Configuration)/$(TargetFramework)/<rid>/$(MSBuildProjectName).app` and
calls `open -a` on it. The MAUI build actually produces
`$(ApplicationTitle).app`, so the file isn't found.

## Fix (Mac Catalyst)

Add this target to the MAUI project's csproj:

```xml
<Target Name="_CreateAspireBundleNameSymlink"
        AfterTargets="Build"
        Condition="'$(TargetFramework)' == 'net10.0-maccatalyst' And '$(_AppBundleName)' != '' And '$(_AppBundleName)' != '$(MSBuildProjectName)'">
  <PropertyGroup>
    <!-- $(OutputPath) already includes the RID for Mac Catalyst. Do NOT append $(RuntimeIdentifier). -->
    <_AspireBundleDir>$(OutputPath)</_AspireBundleDir>
    <_AspireBundleSourceApp>$(_AspireBundleDir)$(_AppBundleName).app</_AspireBundleSourceApp>
    <_AspireBundleAliasApp>$(_AspireBundleDir)$(MSBuildProjectName).app</_AspireBundleAliasApp>
  </PropertyGroup>
  <Exec Condition="Exists('$(_AspireBundleSourceApp)')"
        Command="ln -sfn '$(_AppBundleName).app' '$(_AspireBundleAliasApp)'"
        WorkingDirectory="$(MSBuildProjectDirectory)" />
</Target>
```

## Key facts

- `$(_AppBundleName)` is set by the Xamarin/MAUI Shared SDK target
  `_GenerateBundleName` and reflects the actual produced bundle name.
- For Mac Catalyst, `$(OutputPath)` is already
  `bin/$(Config)/net10.0-maccatalyst/$(RuntimeIdentifier)/`. Appending
  `$(RuntimeIdentifier)` again produces a doubled-RID path and the
  `Exists()` guard silently skips the `Exec`. Always check with
  `dotnet build -v:diag` and grep for the target name.
- `ln -sfn` is idempotent — safe on rebuild.
- Survives `dotnet clean` because the target is part of the normal Build chain.

## iOS / other Apple platforms

Same pattern applies; gate the target on the appropriate TFM. iOS device
builds may need the symlink in the `Payload/` IPA directory instead of the
plain `.app` directory — verify per-platform path before extending blindly.

## Remove when

Aspire.Hosting.Maui exposes a bundle-name override API. Track upstream;
delete the target and its XML comment when available.
