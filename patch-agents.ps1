$filePath = 'Q:/repos/botnexus-wt/feat-agent-detail-panel/src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Pages/Agents.razor'
$content = Get-Content $filePath -Raw

# Add AgentId parameter to @code section
$content = $content -replace '(@code \{)', '$1
    [Parameter]
    public string? AgentId { get; set; }
'

# Replace <div class="agents-page"> to add conditional rendering
$oldDiv = '<div class="agents-page">'
$newDiv = '<div class="agents-page">
@if (!string.IsNullOrEmpty(AgentId))
{
    <AgentDetailPanel AgentId="@AgentId" OnDeleted="HandleAgentDeleted" />
}
else
{'
$content = $content -replace [regex]::Escape($oldDiv), $newDiv

# Add closing brace before last </div> that closes agents-page
# Find the last </div> and add a closing brace before it
$lastDivPos = $content.LastIndexOf('</div>')
if ($lastDivPos -ge 0) {
    $content = $content.Substring(0, $lastDivPos) + "}`n" + $content.Substring($lastDivPos)
}

Set-Content $filePath $content
Write-Host "Done"
