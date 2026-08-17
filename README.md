# SwaggerDashboard



#requires -Version 5.1
<#
.SYNOPSIS
    Veritabanindaki scriptlerin calismasi icin gereken MINIMUM Windows yetki
    seviyesini tespit eder (batch / admin / interactive / system).

.DESCRIPTION
    1) API'den script listesini ceker (name + scriptbody)
    2) STATIK analiz: scriptbody icinde yetki gerektiren islemleri isaretler
       (calistirmadan, hizli on-filtre)
    3) DINAMIK test: her scripti bir "yetki merdiveninde" zamanlanmis gorev
       olarak calistirir, ILK basarili oldugu seviyeyi minimum yetki kabul eder
    4) name'e gore sirali tablo + CSV uretir

    DIKKAT: 3. adim scriptleri gercekten calistirir. MUTLAKA izole bir test
    VM'inde, snapshot alarak, adanmis test hesaplariyla calistirin. Gercek
    servis hesaplarini KULLANMAYIN. scriptbody'leri once gozden gecirin.
#>

#region ================= CONFIG (kendi ortaminiza gore doldurun) =============
$Config = @{
    # --- API ---
    ApiUrl     = 'https://SUNUCU/internal/scripts'
    ApiHeaders = @{ Authorization = 'Bearer TOKEN' }   # gerekmiyorsa @{}
    NameField  = 'name'          # API cevabindaki isim alani
    BodyField  = 'scriptBody'    # API cevabindaki govde alani

    # --- Calistirma modu ---
    #  'opscli' -> C:\Windows\opscli.ps1 <scriptbody argumanlari>  (senin senaryon)
    #  'inline' -> scriptbody dogrudan bir PowerShell blogu
    ExecMode   = 'opscli'
    OpsCliPath = 'C:\Windows\opscli.ps1'

    # --- Test hesaplari (VM'de onceden olusturun) ---
    # Sifreleri BURAYA yazma. Calisma aninda kimlik bilgisi kaynagi:
    #   'Prompt' -> her calismada Get-Credential ile sorulur (en basiti, dosyada iz kalmaz)
    #   'Vault'  -> once Save-TestCredentials ile bir kez sifreli (DPAPI) kaydedilir, sonra otomatik okunur
    CredentialMode = 'Prompt'
    VaultDir       = 'C:\PrivTest\cred'

    # --- Diger ---
    PerRunTimeoutSec = 120
    WorkDir          = 'C:\PrivTest'
    RunDynamic       = $true      # $false yaparsan sadece statik analiz calisir (guvenli)
    IncludeInteractiveTier = $false  # headless VM'de $false birak (aktif oturum ister)
}
#endregion

#region ================= YETKI MERDIVENI ====================================
# Iki bagimsiz eksen:
#   Oturum turu  : batch (gozetimsiz/zamanlanmis) | interactive (masaustu sart) | system
#   Yukseltme    : standart (Limited token) | admin (Highest/elevated token)
# Sira: once BATCH (asil hedef gozetimsiz calisma), yetmezse INTERACTIVE, en son SYSTEM.
# 'Right' = her tier'in karsilik geldigi Windows kullanici hakki (raporlama icin).
$Ladder = @(
    @{ Tier=1; Label='batch/standart';      Account='Std';    RunLevel='Limited'; Logon='Password';       Right='SeBatchLogonRight (Log on as a batch job)' }
    @{ Tier=2; Label='batch/admin';         Account='Admin';  RunLevel='Highest'; Logon='Password';       Right='SeBatchLogonRight + elevation (Administrators)' }
    @{ Tier=3; Label='interactive/standart';Account='Std';    RunLevel='Limited'; Logon='Interactive';    Right='SeInteractiveLogonRight (Allow log on locally)' }
    @{ Tier=4; Label='interactive/admin';   Account='Admin';  RunLevel='Highest'; Logon='Interactive';    Right='SeInteractiveLogonRight + elevation' }
    @{ Tier=5; Label='system';              Account='System'; RunLevel='Highest'; Logon='ServiceAccount'; Right='LocalSystem' }
)
#endregion

#region ================= 1) STATIK ANALIZ ====================================
function Get-StaticPrivilegeHint {
    param([string]$Body)
    $patterns = [ordered]@{
        'Requires-Admin'      = '#requires\s+-runasadministrator'
        'Elevation(RunAs)'    = '-verb\s+runas'
        'HKLM registry'       = 'hklm:|hkey_local_machine'
        'Servis kontrol'      = '\b(new|set|start|stop|restart|remove)-service\b|sc\.exe|net\s+(start|stop)\b'
        'Program Files yazma' = 'program\s?files'
        'Windows dizini'      = 'c:\\windows\\'
        'Firewall'            = 'netsh\s+advfirewall|-netfirewallrule'
        'Zamanlanmis gorev'   = 'schtasks|register-scheduledtask'
        'EventLog olusturma'  = 'new-eventlog'
        'Makine sertifika'    = 'cert:\\localmachine'
        'IIS'                 = 'webadministration|new-website|appcmd'
        'Guc/boot'            = 'restart-computer|stop-computer|shutdown\.exe|bcdedit'
        'Yerel kullanici/grup'= '(new|set|remove)-localuser|(add|remove)-localgroupmember|net\s+user'
        'Interaktif/GUI'      = 'system\.windows\.forms|read-host|\[console\]::readkey|-windowstyle'
    }
    $hits = foreach ($k in $patterns.Keys) { if ($Body -imatch $patterns[$k]) { $k } }
    [pscustomobject]@{
        LikelyAdmin       = [bool]($hits | Where-Object { $_ -ne 'Interaktif/GUI' })
        LikelyInteractive = [bool]($hits -contains 'Interaktif/GUI')
        Indicators        = ($hits -join '; ')
    }
}
#endregion

#region ================= API'den cekme =======================================
function Get-DbScripts {
    param($Config)
    $resp = Invoke-RestMethod -Uri $Config.ApiUrl -Headers $Config.ApiHeaders -Method Get
    # API dizi yerine {data:[...]} donerse burayi ayarla: $resp = $resp.data
    $resp | ForEach-Object {
        [pscustomobject]@{
            Name = $_.($Config.NameField)
            Body = $_.($Config.BodyField)
        }
    } | Sort-Object Name
}
#endregion

#region ================= KIMLIK BILGISI (sifre dosyada tutulmaz) =============
# Vault modu icin: BIR KEZ calistir, sifreler DPAPI ile sifreli kaydedilir.
# DPAPI anahtari kaydeden kullanici + makineye baglidir -> dosya baska yerde acilmaz.
function Save-TestCredentials {
    param($Config)
    New-Item -ItemType Directory -Path $Config.VaultDir -Force | Out-Null
    (Get-Credential -Message 'STANDART test hesabi (or. .\svctest_std)') |
        Export-Clixml -Path (Join-Path $Config.VaultDir 'std.clixml')
    (Get-Credential -Message 'ADMIN test hesabi (or. .\svctest_admin)') |
        Export-Clixml -Path (Join-Path $Config.VaultDir 'admin.clixml')
    Write-Host 'Kimlik bilgileri sifreli kaydedildi.' -ForegroundColor Green
}

function Resolve-TestCredentials {
    param($Config)
    switch ($Config.CredentialMode) {
        'Prompt' {
            $std   = Get-Credential -Message 'STANDART test hesabi (or. .\svctest_std)'
            $admin = Get-Credential -Message 'ADMIN test hesabi (or. .\svctest_admin)'
        }
        'Vault' {
            $stdFile   = Join-Path $Config.VaultDir 'std.clixml'
            $adminFile = Join-Path $Config.VaultDir 'admin.clixml'
            if (-not (Test-Path $stdFile) -or -not (Test-Path $adminFile)) {
                throw "Vault dosyalari yok. Once: Save-TestCredentials -Config `$Config"
            }
            $std   = Import-Clixml $stdFile
            $admin = Import-Clixml $adminFile
        }
        default { throw "Bilinmeyen CredentialMode: $($Config.CredentialMode)" }
    }
    @{ Std = $std; Admin = $admin }
}
#endregion

#region ================= KULLANICI HAKKI VERME (secedit, harici arac yok) ====
# Test hesaplarina batch/interactive logon hakkini verir. Yerel guvenlik
# politikasini degistirir -> admin gerekir, SADECE test VM'inde calistir.
# Ornek:
#   Grant-UserRight -Account 'svctest_std'   -Right SeBatchLogonRight
#   Grant-UserRight -Account 'svctest_admin' -Right SeBatchLogonRight
#   Grant-UserRight -Account 'svctest_std'   -Right SeInteractiveLogonRight   # interactive tier icin
function Grant-UserRight {
    param(
        [Parameter(Mandatory)][string]$Account,
        [ValidateSet('SeBatchLogonRight','SeInteractiveLogonRight','SeServiceLogonRight')]
        [string]$Right = 'SeBatchLogonRight'
    )
    $sid = (New-Object System.Security.Principal.NTAccount($Account)
           ).Translate([System.Security.Principal.SecurityIdentifier]).Value
    $inf = Join-Path $env:TEMP "ur_$([guid]::NewGuid().ToString('N')).inf"
    $sdb = Join-Path $env:TEMP "ur_$([guid]::NewGuid().ToString('N')).sdb"

    secedit /export /cfg $inf /areas USER_RIGHTS | Out-Null
    $content = Get-Content $inf
    $line = $content | Where-Object { $_ -match "^\s*$Right\s*=" } | Select-Object -First 1
    if ($line) {
        if ($line -notmatch [regex]::Escape($sid)) {
            $content = $content -replace [regex]::Escape($line), ($line.TrimEnd() + ",*$sid")
        } else { Write-Host "$Account zaten $Right hakkina sahip." -ForegroundColor DarkGray; return }
    } else {
        $content = $content -replace '(\[Privilege Rights\])', "`$1`r`n$Right = *$sid"
    }
    Set-Content -Path $inf -Value $content -Encoding Unicode   # secedit UNICODE ister
    secedit /import /db $sdb /cfg $inf /areas USER_RIGHTS | Out-Null
    secedit /configure /db $sdb /areas USER_RIGHTS | Out-Null
    Remove-Item $inf, $sdb -ErrorAction SilentlyContinue
    Write-Host "$Account -> $Right verildi." -ForegroundColor Green
}
#endregion

#region ================= 3) DINAMIK: tek tier testi ==========================
function Invoke-TierTest {
    param($Script, $TierDef, $Config, $Cred)

    $runId   = [guid]::NewGuid().ToString('N')
    $resPath = Join-Path $Config.WorkDir "res_$runId.json"
    $tgtPath = Join-Path $Config.WorkDir "tgt_$runId.ps1"

    # Hedef cagriyi kur
    if ($Config.ExecMode -eq 'opscli') {
        $invocation = "& `"$($Config.OpsCliPath)`" $($Script.Body)"
    } else {
        $invocation = $Script.Body
    }

    # Wrapper: hedefi calistir, hata + erisim reddini yakala, sonucu JSON yaz
    # (Sadece $invocation ve $resPath dis degiskendir; digerleri backtick ile literaldir)
    $wrapper = @"
`$ErrorActionPreference = 'Continue'
`$out = New-Object System.Text.StringBuilder
`$exit = 0
try {
    `$global:LASTEXITCODE = 0
    `$e = & { $invocation } 2>&1
    `$exit = if (`$null -ne `$LASTEXITCODE) { `$LASTEXITCODE } else { 0 }
    foreach (`$line in `$e) { [void]`$out.AppendLine(`$line.ToString()) }
} catch {
    `$exit = 1
    [void]`$out.AppendLine(`$_.Exception.GetType().FullName)
    [void]`$out.AppendLine(`$_.Exception.Message)
}
`$txt = `$out.ToString()
`$denied = `$txt -imatch 'access is denied|erisim engellendi|erisim reddedildi|unauthorizedaccess|requires elevation|yukseltme gerekli|elevation|denied|0x80070005|win32 5\b'
@{ exit = `$exit; denied = `$denied; output = `$txt } | ConvertTo-Json -Compress | Set-Content -Path '$resPath' -Encoding UTF8
"@
    Set-Content -Path $tgtPath -Value $wrapper -Encoding UTF8

    $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$tgtPath`""

    $principal = switch ($TierDef.Account) {
        'System' { New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel $TierDef.RunLevel }
        default  {
            $acct = if ($TierDef.Account -eq 'Admin') { $Cred.Admin } else { $Cred.Std }
            New-ScheduledTaskPrincipal -UserId $acct.UserName -LogonType $TierDef.Logon -RunLevel $TierDef.RunLevel
        }
    }

    $taskName = "PrivTest_$runId"
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::FromSeconds($Config.PerRunTimeoutSec))
    $task = New-ScheduledTask -Action $action -Principal $principal -Settings $settings

    try {
        if ($TierDef.Account -eq 'System') {
            Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
        } else {
            $acct = if ($TierDef.Account -eq 'Admin') { $Cred.Admin } else { $Cred.Std }
            # Sifre yalnizca kayit aninda, bellekte duz metne cevrilir; diske/dosyaya yazilmaz
            try {
                Register-ScheduledTask -TaskName $taskName -InputObject $task `
                    -User $acct.UserName -Password $acct.GetNetworkCredential().Password -Force | Out-Null
            } catch {
                # 1385 = logon type not granted -> hesap bu logon turu hakkina sahip degil (SETUP sorunu)
                if ($_.Exception.Message -match '1385|logon type|batch job|log on as') {
                    return [pscustomobject]@{
                        Success=$false; Denied=$false; Exit=$null; TaskRc=$null
                        Output="SETUP: '$($acct.UserName)' hesabi '$($TierDef.Logon)' logon hakkina sahip degil (SeBatchLogonRight/SeInteractiveLogonRight). Grant-UserRight ile ver."
                        LogonRightMissing=$true
                    }
                }
                throw
            }
        }

        Start-ScheduledTask -TaskName $taskName
        $deadline = (Get-Date).AddSeconds($Config.PerRunTimeoutSec + 15)
        do {
            Start-Sleep -Milliseconds 500
            $info = Get-ScheduledTaskInfo -TaskName $taskName
        } while ($info.LastTaskResult -eq 267009 -and (Get-Date) -lt $deadline)  # 267009 = calisiyor

        $result = if (Test-Path $resPath) { Get-Content $resPath -Raw | ConvertFrom-Json } else { $null }
        [pscustomobject]@{
            Success = ($result -and $result.exit -eq 0 -and -not $result.denied)
            Denied  = [bool]($result.denied)
            Exit    = $result.exit
            Output  = ($result.output -as [string])
            TaskRc  = $info.LastTaskResult
            LogonRightMissing = $false
        }
    } finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Remove-Item $tgtPath, $resPath -ErrorAction SilentlyContinue
    }
}

# Merdiveni yuru: ilk basarili tier = minimum yetki
function Resolve-ScriptPrivilege {
    param($Script, $Ladder, $Config, $Cred)
    $trace = @(); $lastOut = ''
    foreach ($tier in $Ladder) {
        if ($tier.Label -like 'interactive*' -and -not $Config.IncludeInteractiveTier) { continue }
        $r = Invoke-TierTest -Script $Script -TierDef $tier -Config $Config -Cred $Cred
        $lastOut = $r.Output
        $mark = if ($r.Success) { 'OK' } elseif ($r.LogonRightMissing) { 'SETUP!' } elseif ($r.Denied) { 'DENIED' } else { 'ERR' }
        $trace += "$($tier.Label):$mark"
        if ($r.LogonRightMissing) {
            return [pscustomobject]@{ MinPrivilege='(setup eksik)'; Right=''; Status='SETUP-EKSIK'; Trace=($trace -join ' > '); LastOutput=$lastOut }
        }
        if ($r.Success) {
            return [pscustomobject]@{ MinPrivilege=$tier.Label; Right=$tier.Right; Status='OK'; Trace=($trace -join ' > '); LastOutput=$lastOut }
        }
    }
    [pscustomobject]@{ MinPrivilege='BULUNAMADI'; Right=''; Status='MANUEL-INCELE'; Trace=($trace -join ' > '); LastOutput=$lastOut }
}
#endregion

#region ================= MAIN ================================================
New-Item -ItemType Directory -Path $Config.WorkDir -Force | Out-Null
# Test hesaplarinin WorkDir'e yazabilmesi icin (ilk kurulumda bir kez):
#   icacls C:\PrivTest /grant "svctest_std:(OI)(CI)M" "svctest_admin:(OI)(CI)M"

# Kimlik bilgileri (dinamik test icin) - sifreler dosyada tutulmaz
$cred = if ($Config.RunDynamic) { Resolve-TestCredentials -Config $Config } else { $null }

Write-Host "API'den scriptler cekiliyor..." -ForegroundColor Cyan
$scripts = Get-DbScripts -Config $Config
Write-Host ("{0} script bulundu (beklenen ~40)." -f $scripts.Count) -ForegroundColor Cyan

$report = foreach ($s in $scripts) {
    $static = Get-StaticPrivilegeHint -Body $s.Body
    if ($Config.RunDynamic) {
        Write-Host ("Test ediliyor: {0}" -f $s.Name) -ForegroundColor DarkGray
        $dyn = Resolve-ScriptPrivilege -Script $s -Ladder $Ladder -Config $Config -Cred $cred
    } else {
        $dyn = [pscustomobject]@{ MinPrivilege = '(statik-only)'; Right = ''; Status = 'SKIP'; Trace = '' }
    }
    [pscustomobject]@{
        Name              = $s.Name
        MinPrivilege      = $dyn.MinPrivilege
        WindowsRight      = $dyn.Right
        Status            = $dyn.Status
        StaticLikelyAdmin = $static.LikelyAdmin
        StaticInteractive = $static.LikelyInteractive
        Indicators        = $static.Indicators
        Trace             = $dyn.Trace
    }
}

$report = $report | Sort-Object Name
$report | Format-Table Name, MinPrivilege, WindowsRight, Status, StaticLikelyAdmin -AutoSize
$csv = Join-Path $Config.WorkDir 'privilege_report.csv'
$report | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
Write-Host ("`nRapor: {0}" -f $csv) -ForegroundColor Green
#endregion
