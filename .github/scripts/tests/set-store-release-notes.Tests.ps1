$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../set-store-release-notes.ps1'
$notesPath = Join-Path $PSScriptRoot '../../../release-notes/0.1.47.txt'
$parseErrors = $null
$tokens = $null
[Management.Automation.Language.Parser]::ParseFile((Resolve-Path $scriptPath), [ref]$tokens, [ref]$parseErrors) | Out-Null
if ($parseErrors) { throw ($parseErrors | Out-String) }
if ([IO.File]::ReadAllBytes((Resolve-Path $scriptPath)) | Where-Object { $_ -gt 127 }) { throw 'Script is not ASCII' }
function Reset-Fixture {
    $global:fixture = @'
{"status":"PendingCommit","pricing":{"priceId":"Tier1012","marketSpecificPricings":{"CA":"keep"}},"targetPublishMode":"Immediate","applicationPackages":[{"fileName":"ImmichDrive-0.1.47.msixupload","fileStatus":"PendingUpload"}],"listings":{"en-us":{"baseListing":{"title":"Drive for Immich","releaseNotes":"old"},"platformOverrides":{}},"fr-ca":{"baseListing":{"title":"French title","releaseNotes":"French notes"}}}}
'@ | ConvertFrom-Json
    $global:puts = 0
    $global:commits = 0
    $global:storeStatus = 'Certification'
    $global:loseNotes = $false
}
function global:Invoke-RestMethod {
    param($Method, $Uri, $Headers, $Body, $ContentType)
    if ($Uri -like '*oauth2/token') { return @{access_token='test-only'} }
    if ($Uri -match '/commit$') {
        if ($ContentType -ne 'application/json') { throw 'Only JSON content is accepted' }
        $global:commits++
        return @{status='CommitStarted'}
    }
    if ($Uri -match '/status$') { return @{status=$global:storeStatus} }
    if ($Uri -match '/submissions/123$') {
        if ($Method -eq 'Put') {
            $global:puts++
            $global:fixture = $Body | ConvertFrom-Json
            if ($global:loseNotes) { $global:fixture.listings.'en-us'.baseListing.releaseNotes = 'old' }
        }
        return $global:fixture
    }
    return @{pendingApplicationSubmission=@{id='123'}}
}
function Check($condition, $name) {
    if (-not $condition) { throw $name }
    Write-Output "PASS: $name"
}
function Run-Notes([switch]$Commit) {
    & $scriptPath -AppId 9MWC6165N7DH -UploadFileName ImmichDrive-0.1.47.msixupload -NotesPath $notesPath -Commit:$Commit
}
Reset-Fixture
Run-Notes
Check ($global:puts -eq 1 -and $global:commits -eq 0) 'draft mode saves notes without submitting'
Check ($global:fixture.listings.'en-us'.baseListing.releaseNotes -eq (Get-Content $notesPath -Raw).Trim()) 'English notes match release file'
Check ($global:fixture.listings.'fr-ca'.baseListing.releaseNotes -eq 'French notes') 'translated listing preserved'
Check ($global:fixture.pricing.marketSpecificPricings.CA -eq 'keep' -and $global:fixture.targetPublishMode -eq 'Immediate') 'pricing and publication mode preserved'
Reset-Fixture
Run-Notes -Commit
Check ($global:commits -eq 1) 'verified notes submitted once'
Reset-Fixture
try {
    & $scriptPath -AppId 9MWC6165N7DH -ExpectedSubmissionId 999 -UploadFileName ImmichDrive-0.1.47.msixupload -NotesPath $notesPath -Commit
    throw 'Expected rejection'
} catch { Check ($_.Exception.Message -like '*not the current pending submission*') 'resuming rejects a different pending draft' }
Check ($global:puts -eq 0 -and $global:commits -eq 0) 'mismatched resume leaves draft untouched'
Reset-Fixture
$global:fixture.applicationPackages[0].fileName = 'unrelated.msixupload'
try { Run-Notes -Commit; throw 'Expected rejection' } catch { Check ($_.Exception.Message -like '*expected release upload*') 'unrelated draft rejected' }
Check ($global:puts -eq 0 -and $global:commits -eq 0) 'unrelated draft not changed'
Reset-Fixture
$global:loseNotes = $true
try { Run-Notes -Commit; throw 'Expected rejection' } catch { Check ($_.Exception.Message -like '*not retained*') 'failed verification prevents submission' }
Check ($global:commits -eq 0) 'unverified notes never committed'
Reset-Fixture
$global:fixture.status = 'Certification'
try { Run-Notes -Commit; throw 'Expected rejection' } catch { Check ($_.Exception.Message -like '*not editable*') 'non-editable submission rejected' }
Reset-Fixture
$global:storeStatus = 'CommitFailed'
try { Run-Notes -Commit; throw 'Expected rejection' } catch { Check ($_.Exception.Message -like '*Store submission failed*') 'ingestion failure reported' }

