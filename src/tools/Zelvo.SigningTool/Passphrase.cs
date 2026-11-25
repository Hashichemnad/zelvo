using System.Text;

namespace Zelvo.SigningTool;

/// <summary>
/// The passphrase that encrypts the private key file: <c>ZELVO_SIGNING_PASSPHRASE</c> when set
/// (for scripted use on the signing computer), else typed without echo.
/// </summary>
internal static class Passphrase
{
    public const string EnvironmentVariable = "ZELVO_SIGNING_PASSPHRASE";

    public static string? FromEnvironmentOrPrompt(string prompt)
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        if (Console.IsInputRedirected)
        {
            return null;
        }

        Console.Error.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }

                continue;
            }

            text.Append(key.KeyChar);
        }

        Console.Error.WriteLine();
        return text.ToString();
    }
}
