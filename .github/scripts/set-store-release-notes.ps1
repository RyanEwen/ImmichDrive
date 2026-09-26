<#
.SYNOPSIS
Verifies a newly uploaded Store draft, updates its English release notes, and optionally submits it.
.DESCRIPTION
Requires the expected versioned upload so an unrelated pending draft cannot be submitted.
Preserves all other listing, pricing, and publication settings. Credentials come from CI.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9]+$')][string]$AppId,
    [Parameter(Mandatory)][string]$UploadFileName,
    [Parameter(Mandatory)][string]$NotesPath,
    [ValidatePattern('^[0-9]+$')][string]$ExpectedSubmissionId,
    [switch]$Commit
)

$ErrorActionPreference = 'Stop'
$notes = (Get-Content -LiteralPath $NotesPath -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($notes) -or $notes.Length -gt 1500) {
    throw 'Release notes must contain between 1 and 1500 characters.'
}

$api = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId"
$token = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$env:AZURE_AD_TENANT_ID/oauth2/token" -Body @{
    grant_type = 'client_credentials'
    client_id = $env:AZURE_AD_APPLICATION_CLIENT_ID
    client_secret = $env:AZURE_AD_APPLICATION_SECRET
    resource = 'https://manage.devcenter.microsoft.com'
}
$headers = @{ Authorization = "Bearer $($token.access_token)" }
$app = Invoke-RestMethod -Uri $api -Headers $headers
$submissionId = [string]$app.pendingApplicationSubmission.id
if ($submissionId -notmatch '^[0-9]+$') { throw 'No pending Store submission was found.' }
if ($ExpectedSubmissionId -and $submissionId -ne $ExpectedSubmissionId) {
    throw 'The selected draft is not the current pending submission.'
}

$uri = "$api/submissions/$submissionId"
$draft = Invoke-RestMethod -Uri $uri -Headers $headers
if ($draft.status -ne 'PendingCommit') { throw "Draft is not editable: $($draft.status)" }
if (@($draft.applicationPackages | Where-Object {
    $_.fileName -eq $UploadFileName -and $_.fileStatus -eq 'PendingUpload'
}).Count -ne 1) {
    throw 'The draft does not contain the expected release upload.'
}

# Only replace English copy; preserve any translated listings and platform overrides.
$englishListings = @($draft.listings.PSObject.Properties | Where-Object Name -Match '^en(-|$)')
if ($englishListings.Count -eq 0) { throw 'No English Store listing was found.' }
foreach ($listing in $englishListings) {
    $listing.Value.baseListing | Add-Member -NotePropertyName releaseNotes -NotePropertyValue $notes -Force
}
Invoke-RestMethod -Method Put -Uri $uri -Headers $headers -ContentType 'application/json' -Body ($draft | ConvertTo-Json -Depth 100) | Out-Null

$verified = Invoke-RestMethod -Uri $uri -Headers $headers
foreach ($listing in $englishListings) {
    if ($verified.listings.($listing.Name).baseListing.releaseNotes -cne $notes) {
        throw "Release notes were not retained for $($listing.Name)."
    }
}
Write-Output "Submission ${submissionId}: verified What's new: $notes"
Write-Output "Price tier: $($verified.pricing.priceId); publishing mode: $($verified.targetPublishMode)"

if ($Commit) {
    Invoke-RestMethod -Method Post -Uri "$uri/commit" -Headers $headers -ContentType 'application/json' | Out-Null
    # Wait for ingestion to accept the submission, not for the full certification process.
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $status = Invoke-RestMethod -Uri "$uri/status" -Headers $headers
        Write-Output "Submission $submissionId status: $($status.status)"
        if ($status.status -match 'Failed$' -or $status.status -eq 'Canceled') {
            throw "Store submission failed: $($status.status). Review Partner Center."
        }
        if ($status.status -in @('Certification', 'PendingPublication', 'Publishing', 'Published', 'Release')) {
            break
        }
        Start-Sleep -Seconds 10
    }
    if ($status.status -notin @('Certification', 'PendingPublication', 'Publishing', 'Published', 'Release')) {
        throw 'Store ingestion is still pending. Inspect this submission before retrying.'
    }
    $submitted = Invoke-RestMethod -Uri $uri -Headers $headers
    $submitted.applicationPackages | Select-Object fileName, version, architecture, fileStatus | Format-Table -AutoSize
}
