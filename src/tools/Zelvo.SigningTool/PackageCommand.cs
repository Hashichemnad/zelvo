using System.Security.Cryptography;

namespace Zelvo.SigningTool;

/// <summary>Signs update packages (module 13); added with task F4.</summary>
internal static class PackageCommand
{
    public static Task<int> RunAsync(Options options, TextWriter output, Func<string, string?> passphrase,
        Func<Options, Func<string, string?>, Task<(string KeyId, RSA Key)>> loadKey) =>
        throw new ToolException("Update packages come with task F4.");
}
