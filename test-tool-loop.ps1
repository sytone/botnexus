# Test tool loop detection and Anthropic protocol compliance
$webSocket = New-Object System.Net.WebSockets.ClientWebSocket
$uri = [System.Uri]::new("ws://localhost:18790/ws?agent=nova")
$cts = New-Object System.Threading.CancellationTokenSource(60000) # 60 second timeout

try {
    Write-Host "=== Testing Tool Loop Detection ===" -ForegroundColor Cyan
    Write-Host "Expected: Nova should call cron tool repeatedly, hit loop detection, get tool_result error" -ForegroundColor Yellow
    Write-Host ""
    
    Write-Host "Connecting to WebSocket..." -ForegroundColor Cyan
    $webSocket.ConnectAsync($uri, $cts.Token).GetAwaiter().GetResult()
    Write-Host "✅ Connected!" -ForegroundColor Green

    # Send a message that should trigger cron tool
    $message = @{
        type = "message"
        content = "Set a reminder for tomorrow at 9am to check email."
    } | ConvertTo-Json

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($message)
    $segment = [System.ArraySegment[byte]]::new($bytes)
    
    Write-Host ""
    Write-Host "Sending test message..." -ForegroundColor Yellow
    $webSocket.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).GetAwaiter().GetResult()

    # Receive responses
    $buffer = New-Object byte[] 32768
    $segment = [System.ArraySegment[byte]]::new($buffer)
    
    Write-Host ""
    Write-Host "Waiting for responses..." -ForegroundColor Cyan
    
    $receivedContent = @()
    $responseCount = 0
    $startTime = Get-Date
    
    while ($true) {
        try {
            # Check timeout
            if (((Get-Date) - $startTime).TotalSeconds -gt 55) {
                Write-Host "Timeout reached" -ForegroundColor Yellow
                break
            }
            
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
                    $fullContent = -join $receivedContent
                    Write-Host "    Content length: $($fullContent.Length) chars" -ForegroundColor White
                    if ($fullContent.Length -lt 500) {
                        Write-Host "    Content: $fullContent" -ForegroundColor White
                    } else {
                        Write-Host "    Content: $($fullContent.Substring(0, 500))..." -ForegroundColor White
                    }
                }
                break
            }
            elseif ($response.type -eq "error") {
                Write-Host ""
                Write-Host "  [$responseCount] Error: $($response.content)" -ForegroundColor Red
                break
            }
            else {
                Write-Host "  [$responseCount] $($response.type)" -ForegroundColor Gray
            }
            
        }
        catch [System.OperationCanceledException] {
            Write-Host "Operation cancelled" -ForegroundColor Yellow
            break
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
    
    # Check gateway logs for the tool loop detection and Anthropic protocol compliance
    Write-Host ""
    Write-Host "=== Recent Gateway Logs ===" -ForegroundColor Cyan
    # Gateway logs to console, so check running process output
    Write-Host "Check console output for:" -ForegroundColor Yellow
    Write-Host "  - 'Blocked repeated tool call'" -ForegroundColor White
    Write-Host "  - 'tool_use ids were found without tool_result' (should NOT appear if fixed)" -ForegroundColor White
    Write-Host "  - 'executing X tool calls'" -ForegroundColor White

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
