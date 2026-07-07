<#
.SYNOPSIS
    Sends a signed webhook request to a BotNexus inbound webhook endpoint.

.DESCRIPTION
    Computes an HMAC-SHA256 signature over the exact raw JSON body using
    System.Security.Cryptography.HMACSHA256, sends it in the
    X-BotNexus-Signature-256 header, and posts to
    POST api/webhooks/{agentId}/{webhookId}.

    In async mode (the default) the endpoint returns 202 with
    { runId, pollUrl, conversationId }. This function then polls
    GET api/webhooks/runs/{runId} until the agent run completes and returns
    the final run object (including agentResponse).

    The signing secret (format whsec_<64 hex chars>) is read from the
    BOTNEXUS_WEBHOOK_SECRET environment variable unless -Secret is supplied.

.PARAMETER AgentId
    The agent id the webhook is registered against.

.PARAMETER WebhookId
    The webhook registration id.

.PARAMETER Message
    The message payload delivered to the agent.

.PARAMETER BaseUrl
    Base URL of the BotNexus gateway. Default: http://localhost:5000

.PARAMETER ResponseMode
    async | sync | callback. Default: async.

.PARAMETER CallbackUrl
    Required only when ResponseMode is 'callback'.

.PARAMETER Secret
    Overrides the BOTNEXUS_WEBHOOK_SECRET environment variable.

.EXAMPLE
    $env:BOTNEXUS_WEBHOOK_SECRET = 'whsec_...'
    ./Send-BotNexusWebhook.ps1 -AgentId my-agent -WebhookId 1a2b3c -Message 'Hello'

.EXAMPLE
    # Dot-source and call the function directly
    . ./Send-BotNexusWebhook.ps1
    Send-BotNexusWebhook -AgentId my-agent -WebhookId 1a2b3c -Message 'Hi' -ResponseMode sync
#>

function Send-BotNexusWebhook {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$AgentId,

        [Parameter(Mandatory)]
        [string]$WebhookId,

        [Parameter(Mandatory)]
        [string]$Message,

        [string]$BaseUrl = 'http://localhost:5000',

        [ValidateSet('async', 'sync', 'callback')]
        [string]$ResponseMode = 'async',

        [string]$CallbackUrl,

        [string]$Secret = $env:BOTNEXUS_WEBHOOK_SECRET,

        [int]$TimeoutSeconds = 120
    )

    if ([string]::IsNullOrWhiteSpace($Secret)) {
        throw 'No secret provided. Set BOTNEXUS_WEBHOOK_SECRET or pass -Secret (format whsec_<64 hex>).'
    }
    if ($ResponseMode -eq 'callback' -and [string]::IsNullOrWhiteSpace($CallbackUrl)) {
        throw "CallbackUrl is required when ResponseMode is 'callback'."
    }

    $base = $BaseUrl.TrimEnd('/')

    # Build the body object, then serialize ONCE. We must sign the exact bytes
    # we send, so this single JSON string is used for both signing and posting.
    $bodyObject = [ordered]@{
        message      = $Message
        responseMode = $ResponseMode
        agentAction  = $true          # $false = record only, no agent run
        callbackUrl  = if ($ResponseMode -eq 'callback') { $CallbackUrl } else { $null }
    }
    $rawBody = $bodyObject | ConvertTo-Json -Compress -Depth 5

    # Compute HMAC-SHA256 over the raw UTF-8 body bytes using the secret's
    # UTF-8 bytes. Header value is 'sha256=' + lowercase hex.
    $keyBytes  = [System.Text.Encoding]::UTF8.GetBytes($Secret)
    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($rawBody)
    $hmac = [System.Security.Cryptography.HMACSHA256]::new($keyBytes)
    try {
        $hashBytes = $hmac.ComputeHash($bodyBytes)
    }
    finally {
        $hmac.Dispose()
    }
    $hex = -join ($hashBytes | ForEach-Object { $_.ToString('x2') })
    $signature = "sha256=$hex"

    $url = "$base/api/webhooks/$([uri]::EscapeDataString($AgentId))/$([uri]::EscapeDataString($WebhookId))"
    $headers = @{
        'X-BotNexus-Signature-256' = $signature
    }

    Write-Verbose "POST $url"
    Write-Verbose "Signature: $signature"

    try {
        $response = Invoke-WebRequest -Uri $url -Method Post -Body $rawBody `
            -ContentType 'application/json' -Headers $headers -SkipHttpErrorCheck
    }
    catch {
        throw "Request failed: $($_.Exception.Message)"
    }

    if ($response.StatusCode -eq 401) {
        throw "401 Unauthorized - signature rejected. Check the secret matches the registration. Body: $($response.Content)"
    }

    # Sync mode: 200 with the full response inline.
    if ($response.StatusCode -eq 200) {
        return $response.Content | ConvertFrom-Json
    }

    if ($response.StatusCode -ne 202) {
        throw "Unexpected status $($response.StatusCode): $($response.Content)"
    }

    $accepted = $response.Content | ConvertFrom-Json
    Write-Verbose "Accepted: runId=$($accepted.runId) conversationId=$($accepted.conversationId)"

    # callback mode: result is delivered to CallbackUrl; nothing to poll.
    if ($ResponseMode -ne 'async') {
        return $accepted
    }

    # Async mode: poll GET api/webhooks/runs/{runId} until complete.
    # pollUrl may be relative; resolve against the base.
    $pollUrl = [uri]::new([uri]$base, $accepted.pollUrl).AbsoluteUri
    Write-Verbose "Polling $pollUrl"

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $pollResp = Invoke-WebRequest -Uri $pollUrl -Method Get `
            -Headers @{ Accept = 'application/json' } -SkipHttpErrorCheck
        if ($pollResp.StatusCode -ne 200) {
            Write-Verbose "Poll returned $($pollResp.StatusCode); retrying."
            continue
        }
        $run = $pollResp.Content | ConvertFrom-Json
        $status = ("$($run.status)").ToLowerInvariant()
        Write-Verbose "  status=$status"
        if ($status -in @('completed', 'succeeded') -or $run.agentResponse) {
            return $run
        }
        if ($status -in @('failed', 'error')) {
            throw "Run failed: $($pollResp.Content)"
        }
    }

    throw "Timed out after $TimeoutSeconds seconds waiting for the run to complete."
}

# When invoked directly (not dot-sourced), run against the passed arguments.
if ($MyInvocation.InvocationName -ne '.') {
    if ($args.Count -gt 0 -or $PSBoundParameters.Count -gt 0) {
        Send-BotNexusWebhook @args
    }
}
