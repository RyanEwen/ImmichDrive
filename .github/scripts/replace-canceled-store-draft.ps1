<#
.SYNOPSIS
Removes one explicitly identified canceled Store submission before a corrected publish.
.DESCRIPTION
Partner Center displays canceled certification as a draft, but its API marks the
submission Canceled and will not accept another package upload to it.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9]+$')][string]$AppId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+$')][string]$SubmissionId,
    [Parameter(Mandatory)][ValidatePattern('^Tier[0-9]+$')][string]$PriceId
)

$ErrorActionPreference = 'Stop'
$api = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId"
$token = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$env:AZURE_AD_TENANT_ID/oauth2/token" -Body @{
    grant_type = 'client_credentials'
    client_id = $env:AZURE_AD_APPLICATION_CLIENT_ID
    client_secret = $env:AZURE_AD_APPLICATION_SECRET
    resource = 'https://manage.devcenter.microsoft.com'
}
$headers = @{ Authorization = "Bearer $($token.access_token)" }
$app = Invoke-RestMethod -Uri $api -Headers $headers
if ([string]$app.pendingApplicationSubmission.id -ne $SubmissionId) {
    throw 'The selected canceled submission is not the current pending submission.'
}

$uri = "$api/submissions/$SubmissionId"
$submission = Invoke-RestMethod -Uri $uri -Headers $headers
if ($submission.status -ne 'Canceled' -or $submission.pricing.priceId -ne $PriceId) {
    throw 'The selected submission is not the canceled tier-based release.'
}
if (@($submission.applicationPackages | Where-Object { $_.fileName -like '*.msixupload' }).Count -ne 1) {
    throw 'The canceled submission does not contain the expected Store upload.'
}

$null = Invoke-RestMethod -Method Delete -Uri $uri -Headers $headers
$after = Invoke-RestMethod -Uri $api -Headers $headers
if ($null -ne $after.pendingApplicationSubmission) {
    throw 'A submission is still pending after deletion. Do not publish over it.'
}
Write-Output "Removed canceled submission $SubmissionId; the published app is unchanged."
