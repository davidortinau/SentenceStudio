#if !IOS && !ANDROID && !MACCATALYST && !MACOS
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using SentenceStudio.Application.AppOperations;

namespace SentenceStudio.Data.AppOperations;

public sealed class DataProtectionApplicationOperationContentProtector(
    IDataProtectionProvider dataProtectionProvider)
    : IApplicationOperationContentProtector
{
    public const string RootPurpose = "SentenceStudio.ApplicationOperation.Payload";

    public byte[] Protect(
        ApplicationOperationProtectionContext context,
        ReadOnlyMemory<byte> plaintext)
    {
        Validate(context, plaintext.Length, isCiphertext: false);

        try
        {
            var protectedBytes = CreateProtector(context).Protect(plaintext.ToArray());
            ValidateLength(protectedBytes.Length, isCiphertext: true);
            return protectedBytes;
        }
        catch (ApplicationOperationProtectionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ApplicationOperationProtectionException(
                "Application operation content could not be protected.",
                exception);
        }
    }

    public byte[] Unprotect(
        ApplicationOperationProtectionContext context,
        ReadOnlyMemory<byte> ciphertext,
        int expectedPlaintextLength)
    {
        Validate(context, ciphertext.Length, isCiphertext: true);
        ValidateLength(expectedPlaintextLength, isCiphertext: false);

        try
        {
            var plaintext = CreateProtector(context).Unprotect(ciphertext.ToArray());
            if (plaintext.Length != expectedPlaintextLength)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new ApplicationOperationProtectionException(
                    "Application operation content length validation failed.");
            }

            return plaintext;
        }
        catch (ApplicationOperationProtectionException)
        {
            throw;
        }
        catch (CryptographicException exception)
        {
            throw new ApplicationOperationProtectionException(
                "Application operation content failed purpose or integrity validation.",
                exception);
        }
        catch (Exception exception)
        {
            throw new ApplicationOperationProtectionException(
                "Application operation content could not be unprotected.",
                exception);
        }
    }

    private IDataProtector CreateProtector(ApplicationOperationProtectionContext context)
    {
        context.Validate();

        return dataProtectionProvider
            .CreateProtector(RootPurpose)
            .CreateProtector("v1")
            .CreateProtector(context.Scope.Authority.ToString())
            .CreateProtector(context.ContentKind.ToString())
            .CreateProtector(context.Scope.UserProfileId)
            .CreateProtector($"{context.SubjectKind}:{context.SubjectId}")
            .CreateProtector($"{context.CapabilityCode}@{context.CapabilityVersion}");
    }

    private static void Validate(
        ApplicationOperationProtectionContext context,
        int length,
        bool isCiphertext)
    {
        context.Validate();
        ValidateLength(length, isCiphertext);
    }

    private static void ValidateLength(int length, bool isCiphertext)
    {
        var maximum = isCiphertext
            ? ApplicationOperationLimits.MaximumProtectedCiphertextBytes
            : ApplicationOperationLimits.MaximumProtectedPlaintextBytes;
        if (length <= 0 || length > maximum)
        {
            throw new ApplicationOperationProtectionException(
                "Application operation content length is outside the protected boundary.");
        }
    }
}
#endif
