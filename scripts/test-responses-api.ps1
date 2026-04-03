#!/usr/bin/env pwsh
# Test script to verify if GitHub Copilot API supports the /responses endpoint

$ErrorActionPreference = "Stop"

$tokenPath = "$env:USERPROFILE\.botnexus\tokens\copilot.json"

if (-not (Test-Path $tokenPath)) {
    Write-Error "No Copilot token found at $tokenPath. Run BotNexus first to authenticate."
    exit 1
}

# Load the GitHub OAuth token
$tokenData = Get-Content $tokenPath | ConvertFrom-Json
$githubToken = $tokenData.AccessToken

Write-Host "Step 1: Exchanging GitHub token for Copilot token..." -ForegroundColor Cyan

# Exchange for Copilot token
$headers = @{
    "Authorization" = "token $githubToken"
    "Accept" = "application/json"
    "User-Agent" = "BotNexus/0.1"
    "Editor-Version" = "vscode/1.99.0"
    "Editor-Plugin-Version" = "copilot-chat/0.26.0"
}

$exchangeResponse = Invoke-RestMethod -Uri "https://api.github.com/copilot_internal/v2/token" -Headers $headers -Method Get
$copilotToken = $exchangeResponse.token

Write-Host "✓ Copilot token acquired (expires at: $($exchangeResponse.expires_at))" -ForegroundColor Green

# Test 1: Try the /responses endpoint
Write-Host "`nStep 2: Testing /responses endpoint..." -ForegroundColor Cyan

$copilotHeaders = @{
    "Content-Type" = "application/json"
}

$responsesPayload = @{
    model = "gpt-4o-realtime-preview-2024-12-17"
    input = @(
        @{
            role = "user"
            content = "Say 'Hello from Responses API' and nothing else."
        }
    )
    stream = $false
} | ConvertTo-Json -Depth 10

try {
    $request = [System.Net.HttpWebRequest]::Create("https://api.githubcopilot.com/responses")
    $request.Method = "POST"
    $request.ContentType = "application/json"
    $request.Headers.Add("Authorization", "Bearer $copilotToken")
    $request.Headers.Add("Editor-Version", "vscode/1.99.0")
    $request.Headers.Add("Editor-Plugin-Version", "copilot-chat/0.26.0")
    
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($responsesPayload)
    $request.ContentLength = $bytes.Length
    
    $stream = $request.GetRequestStream()
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Close()
    
    $result = $request.GetResponse()
    
    $reader = New-Object System.IO.StreamReader($result.GetResponseStream())
    $responseBody = $reader.ReadToEnd()
    $reader.Close()
    $result.Close()
    
    Write-Host "✓ /responses endpoint EXISTS and responded with HTTP $($result.StatusCode)" -ForegroundColor Green
    Write-Host "Response body:" -ForegroundColor Yellow
    Write-Host $responseBody
    Write-Host "`n✓✓✓ RESPONSES API IS SUPPORTED ✓✓✓" -ForegroundColor Green
    exit 0
} catch [System.Net.WebException] {
    $response = $_.Exception.Response
    if ($response -and $response.StatusCode -eq [System.Net.HttpStatusCode]::NotFound) {
        Write-Host "✗ /responses endpoint NOT FOUND (HTTP 404)" -ForegroundColor Red
        Write-Host "`nThe Copilot API does not support the Responses API yet." -ForegroundColor Yellow
        Write-Host "Will continue using Chat Completions API." -ForegroundColor Yellow
        exit 2
    } else {
        Write-Host "✗ Error testing /responses endpoint: $($_.Exception.Message)" -ForegroundColor Red
        if ($response) {
            Write-Host "Status Code: $($response.StatusCode)" -ForegroundColor Red
            $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
            $errorBody = $reader.ReadToEnd()
            Write-Host "Error body: $errorBody" -ForegroundColor Red
        }
        exit 1
    }
}
