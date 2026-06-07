BotNexus.Cli.CliApp.ConfigureOutputEncoding();
return await BotNexus.Cli.CliApp.RunAsync(args, BotNexus.Cli.CliApp.ResolveBannerWriter(Console.Error, Console.IsOutputRedirected));
