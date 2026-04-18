---
id: improvement-repo-folder-and-namespace-cleanup
title: "Repo Folder and Namespace Cleanup"
type: improvement
priority: high
status: in-progress
created: 2026-04-17
tags: [cleanup, namespaces, repo-structure, refactor]
---

# Repo folder and namespace cleanup

The repo needs a tidy and a bit of a namespace cleanup, I want the following to happen.

Before moving onto the next phase the solution and all projects should build without errors or warnings, and all tests should pass. If you find errors or warnings, fix them before moving on to the next phase.

To make sure you have a clean start run a build and run all tests before starting the refactor, this will ensure that you have a good baseline. If there are existing errors or warnings, please fix them before starting the refactor.

## Phase 1

under the source folder we have the agent folder. This should contain the core agent library and providers.

So under the src/agent folder the BotNexus.Agent.Core project should be renamed to BotNexus.Agent.Core. Then the projects under src\providers for all the providers should be moved to the src/agent folder and be renamed to be BotNexus.Agent.Providers.{ProviderName}. For example, the BotNexus.Agent.Providers.OpenAI project would be moved and renamed to BotNexus.Agent.Providers.OpenAI.

Once this is done the old providers folder can be removed, the slnx file will need to be updated and then all the code needs to be reviewed to make sure the new namespaces are correct and that there are no references to the old namespaces.

So the following would change for namespaces:

- BotNexus.Agent.Core -> BotNexus.Agent.Core
- BotNexus.Agent.Providers.OpenAI -> BotNexus.Agent.Providers.OpenAI
- BotNexus.Agent.Providers.Anthropic -> BotNexus.Agent.Providers.Anthropic
- BotNexus.Agent.Providers.Copilot -> BotNexus.Agent.Providers.Copilot
- BotNexus.Agent.Providers.Core -> BotNexus.Agent.Providers.Core
- BotNexus.Agent.Providers.OpenAICompat -> BotNexus.Agent.Providers.OpenAICompat

## Phase 2

Extensions are currently on a seperate folder off the root, they shouldbe under the source folder.

All the projects under `/extensions` should be moved to under `src/extensions`. The slnx and any project references should be fixed.

Once that is valid then rename all the folders to match the name of the project. So for example `src/extensions/mcp` would be renamed to `extensions\BotNexus.Extensions.Mcp`. Currently the folders are double nested with a simple name and then the name of the project, there is no need to have a simple name for the extensions. All extension projects should have a folder off the `src/extensions` folder with the same name as the project and not be nested any deeper.

Make sure all scripts and projects are updated for the new paths.

## Phase 3

Run a full consistency check across the platform and documentation.

## Phase 4

For Phase 4 we need to rename the files and namespaces/references for channels. Channels are all extensions now. So all projects in the channels folder should be moved to the extensions folder and renamed to be BotNexus.Extensions.Channels.{ChannelName}. For example, BotNexus.Channels.Telegram would be moved and renamed to BotNexus.Extensions.Channels.Telegram.

The Core project in channels should be moved to the gateway folder and renamed to BotNexus.Gateway.Channels. The namespace should be updated to match. This way all the base channel objects and interfaces are in the gateway and then each channel extension can reference that for the base contracts.

## Phase 5

The `src\coding-agent\BotNexus.CodingAgent` project should be moved to `poc\BotNexus.CodingAgent`. This is because the coding agent is a proof of concept and should not be in the main source folder. The namespaces should be updated to match the new location. Nothing should refernce this project, it is just a standalone project for testing and prototyping. Once it is moved and renamed, the old project can be removed and the slnx file should be updated.

Further renames will be done after this work is completed and the base cleanup has happened.
