#!/usr/bin/env pwsh
# List all available Copilot models

$ErrorActionPreference = "Stop"

$tokenPath = "$env:USERPROFILE\.botnexus\tokens\copilot.json"

if (-not (Test-Path $tokenPath)) {
    Write-Error "No Copilot token found at $tokenPath"
    exit 1
}

# Load the GitHub OAuth token
$tokenData = Get-Content $tokenPath | ConvertFrom-Json
$githubToken = $tokenData.AccessToken

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

# Get models
$modelsHeaders = @{
    "User-Agent" = "BotNexus/0.1"
}

$request = [System.Net.HttpWebRequest]::Create("https://api.githubcopilot.com/models")
$request.Method = "GET"
$request.Headers.Add("Authorization", "Bearer $copilotToken")
$request.Headers.Add("Editor-Version", "vscode/1.99.0")
$request.Headers.Add("Editor-Plugin-Version", "copilot-chat/0.26.0")

try {
    $result = $request.GetResponse()
    $reader = New-Object System.IO.StreamReader($result.GetResponseStream())
    $responseBody = $reader.ReadToEnd()
    $reader.Close()
    $result.Close()
    
    $models = ($responseBody | ConvertFrom-Json).data
    Write-Host "Available Copilot Models:" -ForegroundColor Cyan
    Write-Host ""
    $models | ForEach-Object {
        Write-Host "  • $($_.id)" -ForegroundColor Green
        if ($_.capabilities) {
            Write-Host "    Capabilities: $($_.capabilities -join ', ')" -ForegroundColor Gray
        }
    }
    Write-Host ""
    Write-Host "Total: $($models.Count) models" -ForegroundColor Yellow
} catch {
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
