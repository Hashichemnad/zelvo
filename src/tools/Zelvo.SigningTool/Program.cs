using Zelvo.SigningTool;

return await SigningTool.RunAsync(args, Console.Out, Console.Error, Passphrase.FromEnvironmentOrPrompt);
