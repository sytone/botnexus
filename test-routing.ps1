# Test handler routing - verify anthropic-messages handler is used for claude-opus-4.6
$webSocket = New-Object System.Net.WebSockets.ClientWebSocket
$uri = [System.Uri]::new("ws://localhost:18790/ws?agent=nova")
$cts = New-Object System.Threading.CancellationTokenSource(30000) # 30 second timeout

try {
    Write-Host "=== Testing Provider Architecture Routing ===" -ForegroundColor Cyan
    Write-Host "Expected: claude-opus-4.6 should route to anthropic-messages handler" -ForegroundColor Yellow
    Write-Host ""
    
    Write-Host "Connecting to WebSocket..." -ForegroundColor Cyan
    $webSocket.ConnectAsync($uri, $cts.Token).GetAwaiter().GetResult()
    Write-Host "✅ Connected!" -ForegroundColor Green

    # Send a simple question that should use the model
    $message = @{
        type = "message"
        content = "What is 2+2? Answer in one sentence."
    } | ConvertTo-Json

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($message)
    $segment = [System.ArraySegment[byte]]::new($bytes)
    
    Write-Host ""
    Write-Host "Sending test message..." -ForegroundColor Yellow
    $webSocket.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).GetAwaiter().GetResult()

    # Receive responses
    $buffer = New-Object byte[] 16384
    $segment = [System.ArraySegment[byte]]::new($buffer)
    
    Write-Host ""
    Write-Host "Waiting for responses..." -ForegroundColor Cyan
    
    $receivedContent = @()
    $responseCount = 0
    
    while ($responseCount -lt 50) {
        try {
            $result = $webSocket.ReceiveAsync($segment, $cts.Token).GetAwaiter().GetResult()
            
            if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                Write-Host "Connection closed by server" -ForegroundColor Red
                break
            }
            
            $responseText = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count)
            $response = $responseText | ConvertFrom-Json
            
            $responseCount++
            
            # Log response type
            if ($response.type -eq "connected") {
                Write-Host "  [$responseCount] Connected: connection_id=$($response.connection_id)" -ForegroundColor Gray
            }
            elseif ($response.type -eq "delta") {
                if ($response.content) {
                    $receivedContent += $response.content
                    Write-Host -NoNewline "." -ForegroundColor Green
                }
            }
            elseif ($response.type -eq "response") {
                Write-Host ""
                Write-Host "  [$responseCount] Final response received" -ForegroundColor Green
                if ($response.content) {
                    Write-Host "    Content: $($response.content)" -ForegroundColor White
                }
                break
            }
            else {
                Write-Host "  [$responseCount] $($response.type)" -ForegroundColor Gray
            }
            
            # Safety: break if we get a complete response
            if ($result.EndOfMessage -and $responseCount -gt 2) {
                # Give it a moment to see if there's a final response
                Start-Sleep -Milliseconds 200
            }
        }
        catch {
            Write-Host "Error receiving: $_" -ForegroundColor Red
            break
        }
    }
    
    Write-Host ""
    Write-Host ""
    Write-Host "=== Test Summary ===" -ForegroundColor Cyan
    Write-Host "Total responses: $responseCount" -ForegroundColor White
    Write-Host "Content chunks: $($receivedContent.Count)" -ForegroundColor White
    
    if ($receivedContent.Count -gt 0) {
        Write-Host "Assembled content: $(-join $receivedContent)" -ForegroundColor White
        Write-Host ""
        Write-Host "✅ SUCCESS: Received response from Nova agent" -ForegroundColor Green
        Write-Host "✅ Check gateway logs above for 'Routing to anthropic-messages handler for model claude-opus-4.6'" -ForegroundColor Green
    }
    else {
        Write-Host "❌ FAILED: No content received" -ForegroundColor Red
    }

} catch {
    Write-Host "Exception: $_" -ForegroundColor Red
    Write-Host $_.Exception.GetType().FullName -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
} finally {
    Write-Host ""
    Write-Host "Closing connection..." -ForegroundColor Cyan
    if ($webSocket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        $webSocket.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "Test complete", $cts.Token).GetAwaiter().GetResult()
    }
    $webSocket.Dispose()
    $cts.Dispose()
    Write-Host "Done!" -ForegroundColor Green
}
