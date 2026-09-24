using System.CommandLine;
using System.IO.Abstractions;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Commands;

/// <summary>Defines CLI commands that manage extension repository registration metadata.</summary>
public sealed class ExtensionRepositoryCommand
{
    /// <summary>Builds the extension repository command tree.</summary>
    public Command Build(Option<string?> targetOption)
    {
        var command = new Command("extensions", "Manage extension repository registrations.");
        command.AddCommand(BuildAdd(targetOption));
        command.AddCommand(BuildList(targetOption));
        command.AddCommand(BuildUpdate(targetOption));
        command.AddCommand(BuildSetEnabled("enable", true, targetOption));
        command.AddCommand(BuildSetEnabled("disable", false, targetOption));
        command.AddCommand(BuildRemove(targetOption));
        command.AddCommand(BuildReconcile(targetOption));
        return command;
    }

    private static Command BuildAdd(Option<string?> targetOption)
    {
        var id = RequiredOption("--id", "Stable lowercase repository ID.");
        var url = RequiredOption("--url", "Absolute HTTP, HTTPS, or SSH repository URL.");
        var requestedRef = RequiredOption("--ref", "Branch, tag, or commit to request.");
        var disabled = new Option<bool>("--disabled", "Register the repository disabled.");
        var noUpdates = new Option<bool>("--no-updates", "Disable updates for this repository.");
        var command = new Command("add", "Register an extension repository.")
        {
            id, url, requestedRef, disabled, noUpdates
        };
        command.SetHandler(async context =>
        {
            var service = CreateService(context.ParseResult.GetValueForOption(targetOption));
            await service.AddAsync(
                context.ParseResult.GetValueForOption(id)!,
                context.ParseResult.GetValueForOption(url)!,
                context.ParseResult.GetValueForOption(requestedRef)!,
                !context.ParseResult.GetValueForOption(disabled),
                !context.ParseResult.GetValueForOption(noUpdates));
        });
        return command;
    }

    private static Command BuildList(Option<string?> targetOption)
    {
        var command = new Command("list", "List extension repository registrations.");
        command.SetHandler(async context =>
        {
            var registrations = await CreateService(
                context.ParseResult.GetValueForOption(targetOption)).ListAsync();
            foreach (var item in registrations)
            {
                AnsiConsole.MarkupLine(
                    $"[bold]{CliText.SafeDisplay(item.Id)}[/]  " +
                    $"{CliText.SafeDisplay(item.RepositoryUrl)}  " +
                    $"ref={CliText.SafeDisplay(item.RequestedRef)}  " +
                    $"enabled={item.Enabled.ToString().ToLowerInvariant()}  " +
                    $"updates={item.UpdatesEnabled.ToString().ToLowerInvariant()}");
            }
        });
        return command;
    }

    private static Command BuildUpdate(Option<string?> targetOption)
    {
        var id = RequiredOption("--id", "Stable lowercase repository ID.");
        var url = new Option<string?>("--url", "Replacement absolute repository URL.");
        var requestedRef = new Option<string?>("--ref", "Replacement branch, tag, or commit.");
        var noUpdates = new Option<bool>("--no-updates", "Disable updates for this repository.");
        var updates = new Option<bool>("--updates", "Enable updates for this repository.");
        var command = new Command("update", "Update an extension repository registration.")
        {
            id, url, requestedRef, noUpdates, updates
        };
        command.SetHandler(async context =>
        {
            var disableUpdates = context.ParseResult.GetValueForOption(noUpdates);
            var enableUpdates = context.ParseResult.GetValueForOption(updates);
            if (disableUpdates && enableUpdates)
                throw new ArgumentException("Use either --updates or --no-updates, not both.");

            bool? updatesEnabled = disableUpdates ? false : enableUpdates ? true : null;
            await CreateService(context.ParseResult.GetValueForOption(targetOption)).UpdateAsync(
                context.ParseResult.GetValueForOption(id)!,
                context.ParseResult.GetValueForOption(url),
                context.ParseResult.GetValueForOption(requestedRef),
                updatesEnabled);
        });
        return command;
    }

    private static Command BuildSetEnabled(string name, bool enabled, Option<string?> targetOption)
    {
        var id = RequiredOption("--id", "Stable lowercase repository ID.");
        var command = new Command(name, $"{(enabled ? "Enable" : "Disable")} an extension repository.") { id };
        command.SetHandler(async context =>
        {
            await CreateService(context.ParseResult.GetValueForOption(targetOption)).SetEnabledAsync(
                context.ParseResult.GetValueForOption(id)!, enabled);
        });
        return command;
    }

    private static Command BuildRemove(Option<string?> targetOption)
    {
        var id = RequiredOption("--id", "Stable lowercase repository ID.");
        var command = new Command("remove", "Remove an extension repository registration.") { id };
        command.SetHandler(async context =>
        {
            await CreateService(context.ParseResult.GetValueForOption(targetOption)).RemoveAsync(
                context.ParseResult.GetValueForOption(id)!);
        });
        return command;
    }

    private static Command BuildReconcile(Option<string?> targetOption)
    {
        var command = new Command("reconcile", "Clone or safely update enabled extension repositories.");
        command.SetHandler(async context =>
        {
            var home = CliPaths.ResolveTarget(context.ParseResult.GetValueForOption(targetOption));
            var service = new ExtensionRepositoryRegistryService(Path.Combine(home, "config.json"), new FileSystem());
            var results = await new ExtensionRepositoryCloneReconciler(home, service).ReconcileEnabledAsync();
            foreach (var result in results)
            {
                if (result.Succeeded)
                    AnsiConsole.MarkupLine($"[green]{CliText.SafeDisplay(result.Id)}[/] {CliText.SafeDisplay(result.ResolvedCommit!)}");
                else
                    AnsiConsole.MarkupLine($"[red]{CliText.SafeDisplay(result.Id)}: {CliText.SafeDisplay(result.FailureName!)}[/] {CliText.SafeDisplay(result.Diagnostic!)}");
            }
            context.ExitCode = results.Any(result => !result.Succeeded) ? 1 : 0;
        });
        return command;
    }

    private static Option<string> RequiredOption(string name, string description)
        => new(name, description) { IsRequired = true };

    private static ExtensionRepositoryRegistryService CreateService(string? target)
    {
        var home = CliPaths.ResolveTarget(target);
        return new ExtensionRepositoryRegistryService(Path.Combine(home, "config.json"), new FileSystem());
    }
}
