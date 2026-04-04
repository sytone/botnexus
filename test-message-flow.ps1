# Test script to capture exact message flow for multi-turn tool calling
# This will help us compare BotNexus message flow vs Pi

Write-Host "Starting BotNexus to capture message flow..." -ForegroundColor Cyan

# Start the API server in background
$apiProcess = Start-Process -FilePath "dotnet" `
    -ArgumentList "run --project Q:\repos\botnexus\src\BotNexus.Api\BotNexus.Api.csproj" `
    -PassThru `
    -WindowStyle Hidden

Write-Host "API server started (PID: $($apiProcess.Id))" -ForegroundColor Green
Write-Host "Waiting 5 seconds for server to start..." -ForegroundColor Yellow
Start-Sleep -Seconds 5

try {
    # Test a simple tool call that requires multiple turns
    $testMessage = @{
        content = "list the files in the current directory"
        sessionKey = "test-message-flow-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        channel = "console"
        chatId = "test-flow"
    } | ConvertTo-Json

    Write-Host "`nSending test message to trigger tool call..." -ForegroundColor Cyan
    Write-Host "Message: $testMessage" -ForegroundColor Gray

    $response = Invoke-RestMethod `
        -Uri "http://localhost:5000/api/chat" `
        -Method Post `
        -Body $testMessage `
        -ContentType "application/json"

    Write-Host "`nResponse received:" -ForegroundColor Green
    Write-Host ($response | ConvertTo-Json -Depth 10) -ForegroundColor Gray

    Write-Host "`n✅ Test completed! Check logs for Anthropic API request details." -ForegroundColor Green
    Write-Host "Look for: 'Anthropic Messages API Request' in the logs" -ForegroundColor Yellow

} finally {
    Write-Host "`nStopping API server..." -ForegroundColor Yellow
    Stop-Process -Id $apiProcess.Id -Force
    Write-Host "API server stopped." -ForegroundColor Green
}
