using System.Reflection;
using Microsoft.AspNetCore.Identity;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebApp.Auth;

namespace SentenceStudio.UI.Tests;

/// <summary>
/// Assembly-level contract: <c>SentenceStudio.UI</c> must not depend on ASP.NET Core Identity.
/// </summary>
/// <remarks>
/// <para>
/// Found 2026-08-18. The <c>net11.0-macos</c> head built clean and then refused to start:
/// "Could not find Microsoft.Extensions.Identity.Stores, Version=11.0.0.0 referenced by
/// SentenceStudio.UI". <c>monodis --typeref</c> on the bundled assembly showed a direct typeref
/// to <c>[Microsoft.Extensions.Identity.Stores]Microsoft.AspNetCore.Identity.IdentityUser`1</c>,
/// and <c>monodis --memberref</c> narrowed it to a single member access: <c>get_Email</c>.
/// </para>
/// <para>
/// The mechanism is worth stating plainly, because it is invisible in source and the build is
/// silent about it. <c>SentenceStudio.UI</c> is a plain <c>net11.0</c> Razor library that is
/// compiled ONCE and then bundled into every head. It compiles against
/// <c>SentenceStudio.Shared</c>'s <c>net10.0</c> slice, where <see cref="ApplicationUser"/>
/// derives from <see cref="IdentityUser"/>; the native heads load Shared's platform slice, where
/// <c>ApplicationUser</c> is a plain DTO and no Identity assembly is present in the bundle at
/// all. So any member the UI touches that is declared on the Identity base rather than on
/// <c>ApplicationUser</c> itself emits a typeref the native app can never resolve. Auth.razor's
/// profile-deletion block reflected over <c>UserManager</c> to avoid exactly this, then read
/// <c>user.Email</c> directly and reintroduced it — the reflection was doing nothing for the
/// property reads.
/// </para>
/// <para>
/// The fix is NOT to ship <c>Microsoft.Extensions.Identity.Stores</c> in the app. Identity is
/// server-only by project design (see the TFM-conditioned Identity block in
/// <c>SentenceStudio.Shared.csproj</c>); bundling it would put a user store in a client that has
/// no business owning one. The fix is that the UI stops naming it.
/// </para>
/// <para>
/// This is an assembly-metadata test rather than a source scan on purpose. The assembly
/// reference table is precisely what the runtime resolves and precisely what
/// <c>monodis --assemblyref</c> reports, so it fails for the real reason. A source scan would
/// have to guess which expressions bind to the base type, and would pass on the exact bug it is
/// meant to catch: <c>user.Email</c> names neither "Identity" nor "IdentityUser".
/// </para>
/// <para>
/// DO NOT DELETE OR WEAKEN THESE TESTS without an accompanying decision record. Reintroducing the
/// reference does not break the build, or the WebApp, or CI — it breaks the native heads at
/// startup, which is the slowest possible place to find out.
/// </para>
/// </remarks>
public class UiIdentityAssemblyContractTests
{
    private static readonly Assembly UiAssembly = typeof(SentenceStudio.WebUI.Pages.Auth).Assembly;
    private static readonly Assembly WebAppAssembly = typeof(ServerAuthService).Assembly;

    /// <summary>
    /// Every Identity assembly the shared framework splits the feature across.
    /// <c>Microsoft.Extensions.Identity.Stores</c> is the one that actually broke (it declares
    /// <c>IdentityUser</c>), <c>.Core</c> declares <c>UserManager</c>, and
    /// <c>Microsoft.AspNetCore.Identity.*</c> covers the EF and UI packages. None of them belong
    /// in a client assembly, so all of them are checked — catching only the one that happened to
    /// break first is how this comes back through a different door.
    /// </summary>
    private static readonly string[] _forbiddenIdentityAssemblyPrefixes =
    {
        "Microsoft.Extensions.Identity",
        "Microsoft.AspNetCore.Identity",
    };

    // =====================================================================
    // 1. The regression itself
    // =====================================================================

    [Fact]
    public void UiAssembly_DoesNotReferenceMicrosoftExtensionsIdentityStores()
    {
        var referenced = UiAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.Equals("Microsoft.Extensions.Identity.Stores", StringComparison.Ordinal))
            .ToList();

        referenced.Should().BeEmpty(
            "SentenceStudio.UI is bundled into the native heads, which contain no ASP.NET Identity " +
            "assembly. A reference here is not a build error and not a WebApp error — the macOS/iOS/" +
            "Android app fails to start with \"Could not find Microsoft.Extensions.Identity.Stores ... " +
            "referenced by SentenceStudio.UI\". It is almost always caused by touching a member that " +
            "ApplicationUser inherits from IdentityUser (Email, UserName, Id, PasswordHash, ...) " +
            "rather than one ApplicationUser declares itself (UserProfileId, DisplayName). Read those " +
            "reflectively — see the profile-deletion block in SentenceStudio.UI/Pages/Auth.razor. " +
            "Do NOT fix this by adding the package or bundling the assembly: Identity is server-only.");
    }

    [Fact]
    public void UiAssembly_ReferencesNoAspNetCoreIdentityAssemblyAtAll()
    {
        var referenced = UiAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => _forbiddenIdentityAssemblyPrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        referenced.Should().BeEmpty(
            "no ASP.NET Core Identity assembly may leak into the client UI assembly. " +
            "Found: " + string.Join(", ", referenced));
    }

    // =====================================================================
    // 2. Identity stayed on the server — the reference moved, it did not vanish
    // =====================================================================

    [Fact]
    public void WebAppAssembly_StillReferencesIdentity()
    {
        // The counterweight to the assertions above. "SentenceStudio.UI has no Identity reference"
        // is trivially satisfiable by deleting Identity from the product, which is not the change
        // that was made. Identity belongs to the server head, and this proves it is still there.
        var referenced = WebAppAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => _forbiddenIdentityAssemblyPrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        referenced.Should().NotBeEmpty(
            "SentenceStudio.WebApp owns ASP.NET Identity. If this is empty, Identity was removed " +
            "from the server rather than un-referenced from the client, and sign-in is broken.");
    }

    [Fact]
    public void WebAppDeletionPath_StillExistsOnServerAuthService()
    {
        // The WebApp's own account deletion (ServerAuthService.DeleteAccountAsync, reached from
        // AccountEndpoints) is server-side and may name Identity freely. This guards against
        // "removing the Identity reference from the UI" being mistaken for "removing deletion".
        var deleteAccount = typeof(ServerAuthService).GetMethod(
            nameof(ServerAuthService.DeleteAccountAsync),
            BindingFlags.Public | BindingFlags.Instance);

        deleteAccount.Should().NotBeNull(
            "ServerAuthService.DeleteAccountAsync is the WebApp's server-side account deletion; " +
            "the UI must never take this over.");
        deleteAccount!.ReturnType.Should().Be(typeof(Task<bool>));
    }

    // =====================================================================
    // 3. The reflection contract Auth.razor depends on
    // =====================================================================
    //
    // Reflection buys assembly independence and pays for it with silence: rename a property and
    // the deletion block stops deleting instead of failing to compile, and its catch-and-warn
    // swallows the difference. These pin the exact strings Auth.razor reflects on.

    [Fact]
    public void UserManagerOpenType_ResolvesFromTheAssemblyQualifiedNameAuthRazorUses()
    {
        var userManagerOpenType = Type.GetType(
            "Microsoft.AspNetCore.Identity.UserManager`1, Microsoft.Extensions.Identity.Core");

        userManagerOpenType.Should().NotBeNull(
            "Auth.razor resolves UserManager<> by this exact assembly-qualified name. If Identity " +
            "moves UserManager to another assembly, the lookup returns null and profile deletion " +
            "silently stops removing Identity users.");
        userManagerOpenType.Should().Be(typeof(UserManager<>));
    }

    [Fact]
    public void UserManagerOfApplicationUser_ExposesTheUsersAndDeleteAsyncMembersAuthRazorInvokes()
    {
        var userManagerType = typeof(UserManager<>).MakeGenericType(typeof(ApplicationUser));

        var users = userManagerType.GetProperty("Users");
        users.Should().NotBeNull("Auth.razor reads UserManager<T>.Users to enumerate candidates");
        typeof(System.Collections.IEnumerable).IsAssignableFrom(users!.PropertyType).Should().BeTrue(
            "Auth.razor enumerates Users through the non-generic IEnumerable so it never names " +
            "IQueryable<ApplicationUser> in its own metadata");

        var deleteAsync = userManagerType.GetMethod("DeleteAsync", new[] { typeof(ApplicationUser) });
        deleteAsync.Should().NotBeNull(
            "Auth.razor invokes DeleteAsync(TUser) by reflection with the resolved user type");
        typeof(Task).IsAssignableFrom(deleteAsync!.ReturnType).Should().BeTrue(
            "Auth.razor awaits the result as a plain Task");
    }

    [Theory]
    [InlineData("UserProfileId")]
    [InlineData("Email")]
    public void ApplicationUser_ExposesThePropertiesAuthRazorReadsReflectively(string propertyName)
    {
        var property = typeof(ApplicationUser).GetProperty(propertyName);

        property.Should().NotBeNull(
            $"Auth.razor reads '{propertyName}' via GetProperty(\"{propertyName}\").GetValue(user). " +
            "A rename here does not break the build — it makes profile deletion stop matching " +
            "(UserProfileId) or log an empty email (Email).");
        property!.CanRead.Should().BeTrue();
        property.PropertyType.Should().Be(typeof(string));
    }

    [Fact]
    public void ApplicationUserEmail_IsDeclaredOnTheIdentityBase_WhichIsWhyItMustBeReadReflectively()
    {
        // This is the trap, stated as an assertion so the next reader does not have to take the
        // comment's word for it. UserProfileId is declared on ApplicationUser and is harmless to
        // touch directly; Email is inherited from IdentityUser<string>, and touching it directly
        // is what dragged Microsoft.Extensions.Identity.Stores into SentenceStudio.UI.dll.
        var email = typeof(ApplicationUser).GetProperty("Email");
        var userProfileId = typeof(ApplicationUser).GetProperty("UserProfileId");

        email!.DeclaringType.Should().NotBe(typeof(ApplicationUser),
            "Email is inherited from the Identity base type");
        email.DeclaringType!.Assembly.GetName().Name.Should().Be("Microsoft.Extensions.Identity.Stores",
            "the declaring assembly of Email is exactly the assembly the native heads cannot load, " +
            "so a direct `user.Email` in SentenceStudio.UI breaks app startup");

        userProfileId!.DeclaringType.Should().Be(typeof(ApplicationUser),
            "UserProfileId is ours; it is the server/mobile-safe half of the model");
    }
}
