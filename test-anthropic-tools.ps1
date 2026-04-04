# Test Anthropic Tool Calls
# Tests the fix for tool call support in AnthropicProvider

$gatewayUrl = "http://localhost:18790"
$sessionKey = "tool-test-$(Get-Date -Format 'HHmmss')"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Testing Anthropic Tool Call Support" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Session: $sessionKey" -ForegroundColor Yellow
Write-Host ""

# Test 1: Send a message that requires tool use
Write-Host "[TEST 1] Sending message that requires tool use..." -ForegroundColor Green

$payload = @{
    content = "What's the current time? Use the get_current_time tool to find out."
    sessionKey = $sessionKey
    channel = "rest"
    chatId = "test-chat"
    timestamp = [DateTimeOffset]::UtcNow.ToString("o")
    metadata = @{
        model = "claude-3-5-sonnet-20241022"
    }
} | ConvertTo-Json -Depth 10

$response = Invoke-RestMethod `
    -Uri "$gatewayUrl/api/messages" `
    -Method Post `
    -Body $payload `
    -ContentType "application/json"

Write-Host ""
Write-Host "Response:" -ForegroundColor Cyan
Write-Host $response -ForegroundColor White
Write-Host ""

# Test 2: Check session history for tool calls
Write-Host "[TEST 2] Checking session history for tool calls..." -ForegroundColor Green

$history = Invoke-RestMethod `
    -Uri "$gatewayUrl/api/sessions/$sessionKey/history" `
    -Method Get

$toolMessages = $history | Where-Object { $_.role -eq "Tool" }

Write-Host ""
Write-Host "Session history summary:" -ForegroundColor Cyan
Write-Host "  Total messages: $($history.Count)" -ForegroundColor White
Write-Host "  Tool messages: $($toolMessages.Count)" -ForegroundColor White
Write-Host ""

if ($toolMessages.Count -gt 0) {
    Write-Host "✅ SUCCESS: Tool calls were executed!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Tool calls found:" -ForegroundColor Cyan
    foreach ($tool in $toolMessages) {
        Write-Host "  - $($tool.toolName): $($tool.content.Substring(0, [Math]::Min(100, $tool.content.Length)))..." -ForegroundColor White
    }
} else {
    Write-Host "❌ FAILURE: No tool calls were executed" -ForegroundColor Red
    Write-Host ""
    Write-Host "Full history:" -ForegroundColor Cyan
    $history | ForEach-Object {
        Write-Host "  [$($_.role)] $($_.content.Substring(0, [Math]::Min(100, $_.content.Length)))..." -ForegroundColor White
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
