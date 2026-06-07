using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Abstractions.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Runs the <see cref="ConversationStoreContractTests"/> suite against <see cref="FileConversationStore"/>
/// using an in-memory <see cref="MockFileSystem"/> (no real disk I/O).
/// </summary>
public sealed class FileConversationStoreParityTests : ConversationStoreContractTests
{
    private readonly MockFileSystem _fs = new();

    protected override IConversationStore CreateStore()
    {
        var rootPath = "/conversations";
        _fs.Directory.CreateDirectory(rootPath);
        return new FileConversationStore(rootPath, NullLogger<FileConversationStore>.Instance, _fs);
    }
}
