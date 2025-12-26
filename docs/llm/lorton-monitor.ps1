# WiFi-Aware Network Monitor with Enhanced Diagnostics and Improved Reliability
# Version 2.1 - Complete standalone version with all functions

param(
    [string]$LogFile = "",
    [string]$RouterIP = "192.168.1.1",
    [int]$RouterLatencyThreshold = 100,
    [int]$TestCount = 5,
    [int]$TestTimeout = 3,
    [int]$CheckInterval = 3,
    [bool]$VerboseLogging = $false,
    [string[]]$AdditionalTargets = @(),
    [int]$MaxLogSizeMB = 100,
    [bool]$EnableAlerts = $false,
    [string]$WebhookUrl = "",
    [int]$HistoryWindowSize = 20,
    [bool]$ExportMetrics = $false,
    [string]$MetricsFile = ""
)

# Generate log file name with current date if not specified
if (-not $LogFile) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
    $dateStr = Get-Date -Format "yyyy-MM-dd"
    $LogFile = Join-Path $scriptDir "logs\wifi-diagnostic-$dateStr.log"
}

# Create log directory if it doesn't exist
$logDir = Split-Path $LogFile -Parent
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

# Initialize metrics file if needed
if ($ExportMetrics -and -not $MetricsFile) {
    $MetricsFile = Join-Path $logDir "wifi-metrics-$(Get-Date -Format 'yyyy-MM-dd').csv"
}

# Function to check and rotate log file if needed
function Test-LogRotation {
    param([string]$LogPath)
    
    if (Test-Path $LogPath) {
        $fileInfo = Get-Item $LogPath
        $sizeMB = $fileInfo.Length / 1MB
        
        if ($sizeMB -gt $MaxLogSizeMB) {
            $archiveName = [System.IO.Path]::GetFileNameWithoutExtension($LogPath)
            $archiveName += "_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".log"
            $archivePath = Join-Path (Split-Path $LogPath -Parent) $archiveName
            
            Move-Item -Path $LogPath -Destination $archivePath -Force
            Write-Host "Log rotated to: $archivePath" -ForegroundColor Cyan
            
            if (Get-Command Compress-Archive -ErrorAction SilentlyContinue) {
                $zipPath = $archivePath + ".zip"
                Compress-Archive -Path $archivePath -DestinationPath $zipPath -Force
                Remove-Item $archivePath -Force
                Write-Host "Compressed to: $zipPath" -ForegroundColor Cyan
            }
        }
    }
}

# Function to send alerts
function Send-Alert {
    param(
        [string]$Title,
        [string]$Message,
        [string]$Severity = "Information"
    )
    
    if ($EnableAlerts) {
        try {
            Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
            $notification = New-Object System.Windows.Forms.NotifyIcon
            $notification.Icon = [System.Drawing.SystemIcons]::Warning
            $notification.BalloonTipTitle = $Title
            $notification.BalloonTipText = $Message
            $notification.Visible = $true
            $notification.ShowBalloonTip(5000)
            Start-Sleep -Seconds 1
            $notification.Dispose()
        } catch {
            Write-Log "Failed to show Windows notification: $_" $false "Yellow"
        }
    }
    
    if ($WebhookUrl) {
        try {
            $payload = @{
                title = $Title
                message = $Message
                severity = $Severity
                timestamp = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
                hostname = $env:COMPUTERNAME
            } | ConvertTo-Json
            
            Invoke-RestMethod -Uri $WebhookUrl -Method Post -Body $payload -ContentType "application/json" -TimeoutSec 5
        } catch {
            Write-Log "Failed to send webhook notification: $_" $false "Yellow"
        }
    }
}

# Enhanced logging function with rotation check
function Write-Log {
    param(
        [string]$Message, 
        [bool]$ShowOnScreen = $false, 
        [string]$Color = "White",
        [bool]$ForceLog = $false
    )
    
    if (-not $ShowOnScreen -and -not $ForceLog -and -not $VerboseLogging) {
        return
    }
    
    if ($script:LogCheckCounter++ % 100 -eq 0) {
        Test-LogRotation -LogPath $script:LogFile
    }
    
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $logEntry = "$timestamp : $Message"
    
    $currentDate = Get-Date -Format "yyyy-MM-dd"
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
    $expectedLogFile = Join-Path $scriptDir "logs\wifi-diagnostic-$currentDate.log"
    
    if ($script:LogFile -ne $expectedLogFile) {
        $script:LogFile = $expectedLogFile
        if ($ShowOnScreen) {
            Write-Host "Rolling to new log file: $script:LogFile" -ForegroundColor Cyan
        }
    }
    
    if (-not $script:LogBuffer) {
        $script:LogBuffer = New-Object System.Collections.ArrayList
    }
    
    [void]$script:LogBuffer.Add($logEntry)
    
    if ($script:LogBuffer.Count -ge 10 -or $ShowOnScreen -or $ForceLog) {
        $script:LogBuffer | Out-File -Append -FilePath $script:LogFile
        $script:LogBuffer.Clear()
    }
    
    if ($ShowOnScreen) {
        Write-Host $logEntry -ForegroundColor $Color
    }
}

# Function to export metrics
function Export-Metrics {
    param($RouterResult, $InternetResult, $WiFiStatus, $Diagnosis)
    
    if (-not $ExportMetrics) { return }
    
    $metrics = [PSCustomObject]@{
        Timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        RouterConnected = $RouterResult.Connected
        RouterLatency = $RouterResult.ResponseTime
        RouterSuccessRate = $RouterResult.SuccessRate
        InternetConnected = $InternetResult.Connected
        InternetLatency = $InternetResult.ResponseTime
        InternetSuccessRate = $InternetResult.SuccessRate
        WiFiSignal = $WiFiStatus.SignalInt
        WiFiSSID = $WiFiStatus.SSID
        WiFiChannel = $WiFiStatus.Channel
        WiFiRxRate = $WiFiStatus.RxRate
        WiFiTxRate = $WiFiStatus.TxRate
        DiagnosisType = $Diagnosis.Type
        DiagnosisSeverity = $Diagnosis.Severity
    }
    
    if (-not (Test-Path $MetricsFile)) {
        $metrics | Export-Csv -Path $MetricsFile -NoTypeInformation
    } else {
        $metrics | Export-Csv -Path $MetricsFile -NoTypeInformation -Append
    }
}

# Enhanced WiFi status with more details
function Get-WiFiStatus {
    try {
        $wifiInterface = netsh wlan show interfaces | Out-String
        
        $patterns = @{
            Signal = 'Signal\s*:\s*(\d+)%'
            State = 'State\s*:\s*(\w+)'
            SSID = 'SSID\s*:\s*(.+)'
            BSSID = 'BSSID\s*:\s*([\w:]+)'
            Channel = 'Channel\s*:\s*(\d+)'
            RxRate = 'Receive rate \(Mbps\)\s*:\s*([\d.]+)'
            TxRate = 'Transmit rate \(Mbps\)\s*:\s*([\d.]+)'
            Authentication = 'Authentication\s*:\s*(.+)'
            Cipher = 'Cipher\s*:\s*(.+)'
            RadioType = 'Radio type\s*:\s*(.+)'
        }
        
        $wifiData = @{}
        foreach ($key in $patterns.Keys) {
            $match = [regex]::Match($wifiInterface, $patterns[$key])
            $wifiData[$key] = if ($match.Success) { 
                $match.Groups[1].Value.Trim() 
            } else { 
                "Unknown" 
            }
        }
        
        $adapter = Get-NetAdapter -Name Wi-Fi* -ErrorAction SilentlyContinue | Select-Object -First 1
        
        $adapterStats = if ($adapter) {
            Get-NetAdapterStatistics -Name $adapter.Name -ErrorAction SilentlyContinue
        } else { $null }
        
        return @{
            Signal = $wifiData.Signal
            SignalInt = if ($wifiData.Signal -ne "Unknown") { [int]$wifiData.Signal } else { -1 }
            State = $wifiData.State
            SSID = $wifiData.SSID
            BSSID = $wifiData.BSSID
            Channel = $wifiData.Channel
            AdapterStatus = if ($adapter) { $adapter.Status } else { "Not Found" }
            LinkSpeed = if ($adapter) { "$($adapter.LinkSpeed)" } else { "Unknown" }
            RxRate = $wifiData.RxRate
            TxRate = $wifiData.TxRate
            Authentication = $wifiData.Authentication
            Cipher = $wifiData.Cipher
            RadioType = $wifiData.RadioType
            ReceivedBytes = if ($adapterStats) { $adapterStats.ReceivedBytes } else { 0 }
            SentBytes = if ($adapterStats) { $adapterStats.SentBytes } else { 0 }
            ReceivedDiscardedPackets = if ($adapterStats) { $adapterStats.ReceivedDiscardedPackets } else { 0 }
            OutboundDiscardedPackets = if ($adapterStats) { $adapterStats.OutboundDiscardedPackets } else { 0 }
        }
    } catch {
        Write-Log "Error getting WiFi status: $_" $false "Red" $true
        return @{
            Signal = "Error"; SignalInt = -1; State = "Error"
            SSID = "Error"; Channel = "Error"; AdapterStatus = "Error"
            LinkSpeed = "Error"; RxRate = "Error"; TxRate = "Error"
        }
    }
}

# Function to calculate jitter
function Calculate-Jitter {
    param([array]$Values)
    
    if ($Values.Count -lt 2) { return 0 }
    
    $differences = @()
    for ($i = 1; $i -lt $Values.Count; $i++) {
        $differences += [Math]::Abs($Values[$i] - $Values[$i-1])
    }
    
    return [Math]::Round(($differences | Measure-Object -Average).Average, 1)
}

# Enhanced statistics function
function Get-PingStatistics {
    param([array]$Values)
    
    if ($Values.Count -eq 0) {
        return @{
            Average = $null; Median = $null; StdDev = $null
            Min = $null; Max = $null; CleanAverage = $null
            OutlierDetected = $false; Percentile95 = $null
        }
    }
    
    $sorted = $Values | Sort-Object
    $count = $Values.Count
    
    $p95Index = [Math]::Floor($count * 0.95)
    $percentile95 = if ($p95Index -lt $count) { $sorted[$p95Index] } else { $sorted[-1] }
    
    $median = if ($count % 2 -eq 0) {
        ($sorted[$count/2 - 1] + $sorted[$count/2]) / 2
    } else {
        $sorted[[math]::Floor($count/2)]
    }
    
    $avg = ($Values | Measure-Object -Average).Average
    
    $variance = 0
    if ($count -gt 1) {
        $Values | ForEach-Object { $variance += [math]::Pow($_ - $avg, 2) }
        $variance = $variance / ($count - 1)
        $stdDev = [math]::Sqrt($variance)
    } else {
        $stdDev = 0
    }
    
    $outlierDetected = $false
    $cleanValues = $Values
    
    if ($count -ge 3) {
        $q1 = $sorted[[math]::Floor($count * 0.25)]
        $q3 = $sorted[[math]::Ceiling($count * 0.75) - 1]
        $iqr = $q3 - $q1
        
        if ($iqr -gt 0) {
            $lowerBound = $q1 - (1.5 * $iqr)
            $upperBound = $q3 + (1.5 * $iqr)
            
            $cleanValues = $Values | Where-Object { $_ -ge $lowerBound -and $_ -le $upperBound }
            $outlierDetected = $cleanValues.Count -lt $Values.Count
        }
    }
    
    $cleanAvg = if ($cleanValues.Count -gt 0) {
        ($cleanValues | Measure-Object -Average).Average
    } else { $avg }
    
    return @{
        Average = [math]::Round($avg, 1)
        Median = [math]::Round($median, 1)
        StdDev = [math]::Round($stdDev, 1)
        Min = ($Values | Measure-Object -Minimum).Minimum
        Max = ($Values | Measure-Object -Maximum).Maximum
        CleanAverage = [math]::Round($cleanAvg, 1)
        OutlierDetected = $outlierDetected
        OutlierCount = $Values.Count - $cleanValues.Count
        Percentile95 = [math]::Round($percentile95, 1)
    }
}

# Optimized ping function
function Test-NetworkConnection {
    param(
        [string]$Target, 
        [string]$Description,
        [int]$Count = $script:TestCount
    )
    
    $results = @()
    $successCount = 0
    $attempts = 0
    
    $ping = New-Object System.Net.NetworkInformation.Ping
    $options = New-Object System.Net.NetworkInformation.PingOptions
    $options.DontFragment = $true
    
    try {
        for ($i = 1; $i -le $Count; $i++) {
            $attempts++
            try {
                $reply = $ping.Send($Target, ($TestTimeout * 1000))
                if ($reply.Status -eq 'Success') {
                    $results += $reply.RoundtripTime
                    $successCount++
                }
            } catch {
                Write-Log "Ping error for $Target`: $_" $false "Red"
            }
            
            if ($i -lt $Count) {
                Start-Sleep -Milliseconds 50
            }
        }
        
        if ($successCount -gt 0) {
            $stats = Get-PingStatistics -Values $results
            $successRate = [math]::Round(($successCount / $attempts) * 100, 1)
            
            $reportedLatency = if ($stats.OutlierDetected) {
                $stats.CleanAverage
            } elseif ($successCount -le 3) {
                $stats.Median
            } else {
                $stats.Average
            }
            
            return @{
                Connected = $true
                ResponseTime = $reportedLatency
                Average = $stats.Average
                Median = $stats.Median
                StdDev = $stats.StdDev
                MinResponseTime = $stats.Min
                MaxResponseTime = $stats.Max
                SuccessRate = $successRate
                SuccessCount = $successCount
                AttemptCount = $attempts
                Status = if ($stats.OutlierDetected) { "OK (outliers removed)" } else { "OK" }
                PartialFailure = $successCount -lt $attempts
                OutlierDetected = $stats.OutlierDetected
                OutlierCount = $stats.OutlierCount
                Jitter = if ($results.Count -gt 1) { Calculate-Jitter -Values $results } else { 0 }
            }
        } else {
            return @{
                Connected = $false
                ResponseTime = $null
                SuccessRate = 0
                SuccessCount = 0
                AttemptCount = $attempts
                Status = "No Response"
                PartialFailure = $false
                Jitter = $null
            }
        }
    } finally {
        $ping.Dispose()
    }
}

# Function to determine if an issue is worth reporting
function Should-ReportIssue {
    param($Diagnosis, $LastDiagnosis, $ConsecutiveCount)
    
    if ($Diagnosis.Severity -eq "Critical") { return $true }
    if ($Diagnosis.Severity -eq "High") { return $true }
    if ($Diagnosis.Severity -eq "Medium" -and $ConsecutiveCount -ge 2) { return $true }
    if ($Diagnosis.Severity -eq "Low" -and $ConsecutiveCount -ge 3) { return $true }
    if ($Diagnosis.Confidence -eq "Low" -and $ConsecutiveCount -lt 3) { return $false }
    
    return $false
}

# Enhanced diagnosis with pattern recognition
function Get-NetworkDiagnosis {
    param($RouterResult, $InternetResult, $WiFiStatus, $HistoricalData, $AdditionalResults = @())
    
    $wifiInfo = "WiFi: $($WiFiStatus.Signal)% @ $($WiFiStatus.SSID)"
    
    $minSuccessRate = if ($script:TestCount -le 2) { 50 } else { 60 }
    $criticalSuccessRate = if ($script:TestCount -le 2) { 0 } else { 40 }
    
    # Check for high jitter
    if ($RouterResult.Jitter -gt 60 -or $InternetResult.Jitter -gt 150) {
        return @{
            Type = "HIGH_JITTER"
            Description = "Network instability detected - High jitter (Router: $($RouterResult.Jitter)ms, Internet: $($InternetResult.Jitter)ms)"
            Severity = "Low"
            Color = "Yellow"
            Confidence = "Medium"
        }
    }
    
    # Check for packet errors
    if ($WiFiStatus.ReceivedDiscardedPackets -gt 100 -or $WiFiStatus.OutboundDiscardedPackets -gt 100) {
        return @{
            Type = "PACKET_ERRORS"
            Description = "WiFi adapter dropping packets (Rx dropped: $($WiFiStatus.ReceivedDiscardedPackets), Tx dropped: $($WiFiStatus.OutboundDiscardedPackets))"
            Severity = "Medium"
            Color = "Yellow"
            Confidence = "High"
        }
    }
    
    # Check additional targets
    if ($AdditionalResults.Count -gt 0) {
        $failedTargets = $AdditionalResults | Where-Object { -not $_.Result.Connected }
        if ($failedTargets.Count -eq $AdditionalResults.Count) {
            return @{
                Type = "MULTIPLE_TARGET_FAILURE"
                Description = "Cannot reach any monitored targets - widespread connectivity issue"
                Severity = "Critical"
                Color = "Red"
                Confidence = "High"
            }
        }
    }
    
    # Check WiFi state
    if ($WiFiStatus.State -ne "connected") {
        return @{
            Type = "WIFI_DISCONNECTED"
            Description = "WiFi adapter not connected ($wifiInfo, State=$($WiFiStatus.State))"
            Severity = "Critical"
            Color = "Red"
            Confidence = "High"
        }
    }
    
    # Router connectivity checks
    if (-not $RouterResult.Connected) {
        if ($HistoricalData.LastRouterSuccess -and 
            ((Get-Date) - $HistoricalData.LastRouterSuccess).TotalSeconds -lt 10) {
            return @{
                Type = "ROUTER_TEMPORARY_ISSUE"
                Description = "Temporary router connectivity loss ($wifiInfo)"
                Severity = "Medium"
                Color = "Yellow"
                Confidence = "Low"
            }
        }
        
        return @{
            Type = "LOCAL_NETWORK_FAILURE"
            Description = "Cannot reach router - check local network ($wifiInfo)"
            Severity = "Critical"
            Color = "Red"
            Confidence = "High"
        }
    }
    
    # Router latency issues
    if ($RouterResult.OutlierDetected -and $RouterResult.SuccessRate -ge $minSuccessRate) {
        if ($RouterResult.ResponseTime -le $script:RouterLatencyThreshold) {
            Write-Log "Router latency spike detected (max: $($RouterResult.MaxResponseTime)ms) but average OK ($($RouterResult.ResponseTime)ms)" $false
        } else {
            return @{
                Type = "ROUTER_LATENCY_SPIKES"
                Description = "Router experiencing latency spikes (max: $($RouterResult.MaxResponseTime)ms, typical: $($RouterResult.ResponseTime)ms) ($wifiInfo)"
                Severity = "Low"
                Color = "DarkYellow"
                Confidence = "Medium"
            }
        }
    }
    
    # Router instability
    if ($RouterResult.PartialFailure -and $RouterResult.SuccessRate -lt $minSuccessRate) {
        return @{
            Type = "ROUTER_INSTABILITY"
            Description = "Router connection unstable ($($RouterResult.SuccessRate)% success, $($RouterResult.SuccessCount)/$($RouterResult.AttemptCount) pings) ($wifiInfo)"
            Severity = if ($RouterResult.SuccessRate -lt $criticalSuccessRate) { "High" } else { "Medium" }
            Color = if ($RouterResult.SuccessRate -lt $criticalSuccessRate) { "Red" } else { "Yellow" }
            Confidence = if ($RouterResult.AttemptCount -ge 3) { "High" } else { "Medium" }
        }
    }
    
    # High router latency
    if ($RouterResult.ResponseTime -gt $script:RouterLatencyThreshold -and 
        $RouterResult.StdDev -lt ($RouterResult.ResponseTime * 0.5)) {
        
        if (-not $InternetResult.Connected) {
            return @{
                Type = "ROUTER_AND_INTERNET_ISSUES"
                Description = "Router slow ($($RouterResult.ResponseTime)ms) and internet unreachable ($wifiInfo)"
                Severity = "High"
                Color = "Red"
                Confidence = "High"
            }
        } elseif ($InternetResult.ResponseTime -gt 100) {
            return @{
                Type = "GENERAL_NETWORK_CONGESTION"
                Description = "Network congestion detected - Router: $($RouterResult.ResponseTime)ms, Internet: $($InternetResult.ResponseTime)ms ($wifiInfo)"
                Severity = "Medium"
                Color = "Yellow"
                Confidence = "Medium"
            }
        } else {
            return @{
                Type = "LOCAL_NETWORK_CONGESTION"
                Description = "Local network slow (Router: $($RouterResult.ResponseTime)ms) but internet OK ($wifiInfo)"
                Severity = "Low"
                Color = "DarkYellow"
                Confidence = "Medium"
            }
        }
    }
    
    # Internet connectivity issues
    if (-not $InternetResult.Connected) {
        if ($RouterResult.ResponseTime -le $script:RouterLatencyThreshold -and $RouterResult.SuccessRate -ge 80) {
            return @{
                Type = "ISP_ISSUE"
                Description = "No internet access - Router OK ($($RouterResult.ResponseTime)ms), likely ISP/WAN issue ($wifiInfo)"
                Severity = "High"
                Color = "Red"
                Confidence = "High"
            }
        } else {
            return @{
                Type = "INTERNET_UNREACHABLE"
                Description = "Internet unreachable ($wifiInfo)"
                Severity = "High"
                Color = "Red"
                Confidence = "Medium"
            }
        }
    }
    
    # Internet packet loss
    if ($InternetResult.PartialFailure) {
        if ($InternetResult.AttemptCount -le 3 -and $InternetResult.SuccessRate -ge 33) {
            Write-Log "Minor packet loss to internet ($($InternetResult.SuccessRate)%) - monitoring..." $false
        } elseif ($InternetResult.SuccessRate -lt $minSuccessRate) {
            return @{
                Type = "INTERNET_PACKET_LOSS"
                Description = "Internet packet loss detected ($($InternetResult.SuccessRate)% success, $($InternetResult.SuccessCount)/$($InternetResult.AttemptCount) pings) ($wifiInfo)"
                Severity = if ($InternetResult.SuccessRate -lt 40) { "High" } else { "Medium" }
                Color = if ($InternetResult.SuccessRate -lt 40) { "Red" } else { "Yellow" }
                Confidence = if ($InternetResult.AttemptCount -ge 5) { "High" } else { "Medium" }
            }
        }
    }
    
    # Internet latency spikes
    if ($InternetResult.OutlierDetected -and $InternetResult.MaxResponseTime -gt 500) {
        return @{
            Type = "INTERNET_LATENCY_SPIKES"
            Description = "Internet latency spikes detected (max: $($InternetResult.MaxResponseTime)ms, typical: $($InternetResult.ResponseTime)ms) ($wifiInfo)"
            Severity = "Low"
            Color = "DarkYellow"
            Confidence = "Medium"
        }
    }
    
    # WiFi signal issues
    if ($WiFiStatus.SignalInt -gt 0) {
        if ($WiFiStatus.SignalInt -lt 30) {
            return @{
                Type = "WIFI_WEAK_SIGNAL"
                Description = "WiFi signal very weak ($($WiFiStatus.Signal)%) - expect connectivity issues"
                Severity = "High"
                Color = "Red"
                Confidence = "High"
            }
        } elseif ($WiFiStatus.SignalInt -lt 50) {
            return @{
                Type = "WIFI_POOR_SIGNAL"
                Description = "WiFi signal poor ($($WiFiStatus.Signal)%) - may cause intermittent issues"
                Severity = "Medium"
                Color = "Yellow"
                Confidence = "Medium"
            }
        }
    }
    
    # All systems normal
    return @{
        Type = "ALL_OK"
        Description = "All systems normal"
        Severity = "None"
        Color = "Green"
        Confidence = "High"
    }
}

# Initialize global variables
$script:LogCheckCounter = 0
$script:LogBuffer = New-Object System.Collections.ArrayList

# Main monitoring loop
Write-Log "=== Enhanced WiFi Network Monitor v2.1 Started ===" $true "Green"
Write-Log "Configuration: Router=$RouterIP, Threshold=${RouterLatencyThreshold}ms, Tests=$TestCount, Interval=${CheckInterval}s" $true "Cyan"

if ($AdditionalTargets.Count -gt 0) {
    Write-Log "Additional targets: $($AdditionalTargets -join ', ')" $true "Cyan"
}

if ($EnableAlerts) {
    Write-Log "Alerts enabled (Webhook: $(if ($WebhookUrl) { 'Yes' } else { 'No' }))" $true "Cyan"
}

# Initial WiFi status
$initialWiFi = Get-WiFiStatus
Write-Log "Initial WiFi: Signal=$($initialWiFi.Signal)%, SSID=$($initialWiFi.SSID), Channel=$($initialWiFi.Channel)" $true "Cyan"
Write-Log "  Authentication: $($initialWiFi.Authentication), Cipher: $($initialWiFi.Cipher)" $true "Cyan"
Write-Log "  Radio Type: $($initialWiFi.RadioType), Speed: $($initialWiFi.LinkSpeed)" $true "Cyan"

# State tracking
$lastDiagnosis = @{ Type = "INIT"; Description = "Initializing"; Severity = "None" }
$issueStartTime = $null
$lastWiFiStatus = $initialWiFi
$consecutiveIssues = @{}
$reportedIssue = $null

# Historical data
$historicalData = @{
    LastRouterSuccess = Get-Date
    LastInternetSuccess = Get-Date
    RecentRouterLatencies = New-Object System.Collections.Generic.Queue[double]
    RecentInternetLatencies = New-Object System.Collections.Generic.Queue[double]
    MaxHistorySize = $HistoryWindowSize
}

# Statistics tracking
$stats = @{
    StartTime = Get-Date
    TotalChecks = 0
    RouterFailures = 0
    InternetFailures = 0
    WiFiDrops = 0
    SignalDips = 0
    FalsePositives = 0
    IssuesReported = 0
    LastStatsDisplay = Get-Date
    TotalBytesReceived = $initialWiFi.ReceivedBytes
    TotalBytesSent = $initialWiFi.SentBytes
}

# Set up Ctrl+C handler
$null = [Console]::TreatControlCAsInput = $true

try {
    while ($true) {
        # Check for Ctrl+C
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            if ($key.Key -eq "C" -and $key.Modifiers -eq "Control") {
                Write-Log "Shutdown requested..." $true "Yellow"
                break
            }
        }
        
        $stats.TotalChecks++
        
        # Get current WiFi status
        $currentWiFi = Get-WiFiStatus
        
        # Test router and internet
        $routerResult = Test-NetworkConnection -Target $RouterIP -Description "Router"
        $internetResult = Test-NetworkConnection -Target "8.8.8.8" -Description "Internet"
        
        # Test additional targets
        $additionalResults = @()
        foreach ($target in $AdditionalTargets) {
            $result = Test-NetworkConnection -Target $target -Description "Additional: $target"
            $additionalResults += @{ Target = $target; Result = $result }
        }
        
        # Update historical data
        if ($routerResult.Connected) {
            $historicalData.LastRouterSuccess = Get-Date
            if ($historicalData.RecentRouterLatencies.Count -ge $historicalData.MaxHistorySize) {
                [void]$historicalData.RecentRouterLatencies.Dequeue()
            }
            $historicalData.RecentRouterLatencies.Enqueue($routerResult.ResponseTime)
        }
        
        if ($internetResult.Connected) {
            $historicalData.LastInternetSuccess = Get-Date
            if ($historicalData.RecentInternetLatencies.Count -ge $historicalData.MaxHistorySize) {
                [void]$historicalData.RecentInternetLatencies.Dequeue()
            }
            $historicalData.RecentInternetLatencies.Enqueue($internetResult.ResponseTime)
        }
        
        # Get diagnosis
        $diagnosis = Get-NetworkDiagnosis -RouterResult $routerResult -InternetResult $internetResult `
                                          -WiFiStatus $currentWiFi -HistoricalData $historicalData `
                                          -AdditionalResults $additionalResults
        
        # Export metrics
        Export-Metrics -RouterResult $routerResult -InternetResult $internetResult `
                      -WiFiStatus $currentWiFi -Diagnosis $diagnosis
        
        # Track consecutive issues
        if ($diagnosis.Type -ne "ALL_OK") {
            if ($consecutiveIssues.ContainsKey($diagnosis.Type)) {
                $consecutiveIssues[$diagnosis.Type]++
            } else {
                $consecutiveIssues[$diagnosis.Type] = 1
            }
        } else {
            $consecutiveIssues.Clear()
        }
        
        # Determine if we should report
        $shouldReport = Should-ReportIssue -Diagnosis $diagnosis -LastDiagnosis $lastDiagnosis `
                                           -ConsecutiveCount $consecutiveIssues[$diagnosis.Type]
        
        # Update statistics
        if (-not $routerResult.Connected) { $stats.RouterFailures++ }
        if (-not $internetResult.Connected) { $stats.InternetFailures++ }
        
        # Calculate bandwidth
        $bytesReceivedDelta = $currentWiFi.ReceivedBytes - $stats.TotalBytesReceived
        $bytesSentDelta = $currentWiFi.SentBytes - $stats.TotalBytesSent
        $stats.TotalBytesReceived = $currentWiFi.ReceivedBytes
        $stats.TotalBytesSent = $currentWiFi.SentBytes
        
        # Check for WiFi drops
        if ($currentWiFi.State -ne "connected" -and $lastWiFiStatus.State -eq "connected") {
            $stats.WiFiDrops++
            Write-Log "WiFi DROP! State: $($lastWiFiStatus.State) -> $($currentWiFi.State)" $true "Magenta"
            Send-Alert -Title "WiFi Disconnected" -Message "WiFi connection lost at $(Get-Date -Format 'HH:mm:ss')" -Severity "High"
        }
        
        if ($currentWiFi.SignalInt -gt 0 -and $currentWiFi.SignalInt -lt 50) {
            $stats.SignalDips++
        }
        
        # Handle issue reporting
        if ($diagnosis.Type -ne "ALL_OK") {
            if ($shouldReport -and ($reportedIssue -eq $null -or $reportedIssue.Type -ne $diagnosis.Type)) {
                Write-Log "ISSUE: $($diagnosis.Description)" $true $diagnosis.Color
                Write-Log "  Router: Success=$($routerResult.SuccessRate)%, Latency=$($routerResult.ResponseTime)ms, Jitter=$($routerResult.Jitter)ms" $true "Yellow"
                Write-Log "  Internet: Success=$($internetResult.SuccessRate)%, Latency=$($internetResult.ResponseTime)ms, Jitter=$($internetResult.Jitter)ms" $true "Yellow"
                Write-Log "  WiFi: Signal=$($currentWiFi.Signal)%, Rx=$($currentWiFi.RxRate)Mbps, Tx=$($currentWiFi.TxRate)Mbps" $true "Yellow"
                
                if ($routerResult.OutlierDetected -or $internetResult.OutlierDetected) {
                    Write-Log "  Note: Outliers detected and removed from averages" $true "Cyan"
                }
                
                if ($diagnosis.Severity -in @("Critical", "High")) {
                    Send-Alert -Title "Network Issue Detected" -Message $diagnosis.Description -Severity $diagnosis.Severity
                }
                
                $reportedIssue = $diagnosis
                $issueStartTime = Get-Date
                $stats.IssuesReported++
            }
        } else {
            # All OK
            if ($reportedIssue -ne $null) {
                $duration = if ($issueStartTime) { 
                    (Get-Date) - $issueStartTime
                } else { 
                    $null 
                }
                
                if ($duration -and $duration.TotalSeconds -lt 10 -and $reportedIssue.Severity -ne "Critical") {
                    $stats.FalsePositives++
                    Write-Log "RESOLVED: $($reportedIssue.Description) (Duration: $($duration.ToString('mm\:ss'))s - possible false positive)" $true "Green"
                } else {
                    $durationStr = if ($duration) { " (Duration: $($duration.ToString('mm\:ss')))" } else { "" }
                    Write-Log "RESOLVED: $($reportedIssue.Description)$durationStr" $true "Green"
                    
                    if ($reportedIssue.Severity -in @("Critical", "High")) {
                        Send-Alert -Title "Network Issue Resolved" -Message "Issue resolved: $($reportedIssue.Description)" -Severity "Information"
                    }
                }
                
                Write-Log "  Current: Router=$($routerResult.ResponseTime)ms, Internet=$($internetResult.ResponseTime)ms, WiFi=$($currentWiFi.Signal)%" $true "Green"
                
                $reportedIssue = $null
                $issueStartTime = $null
            }
            
            # Log normal operation periodically
            if ($stats.TotalChecks % 20 -eq 0) {
                $bandwidthInfo = if ($bytesReceivedDelta -gt 0 -or $bytesSentDelta -gt 0) {
                    ", Bandwidth: Rx=$([Math]::Round($bytesReceivedDelta/1KB, 1))KB Tx=$([Math]::Round($bytesSentDelta/1KB, 1))KB"
                } else { "" }
                
                Write-Log "OK - Router: $($routerResult.ResponseTime)ms, Internet: $($internetResult.ResponseTime)ms, WiFi: $($currentWiFi.Signal)%$bandwidthInfo" $false "Green" $true
            }
        }
        
        # Check for significant WiFi changes
        if ($lastWiFiStatus.SignalInt -gt 0 -and $currentWiFi.SignalInt -gt 0) {
            $signalDiff = [Math]::Abs($currentWiFi.SignalInt - $lastWiFiStatus.SignalInt)
            if ($signalDiff -ge 30) {
                Write-Log "Significant WiFi signal change: $($lastWiFiStatus.Signal)% -> $($currentWiFi.Signal)%" $true "Yellow"
            }
        }
        
        # Display statistics periodically
        if ((Get-Date) - $stats.LastStatsDisplay -gt [TimeSpan]::FromSeconds(120)) {
            $uptime = (Get-Date) - $stats.StartTime
            $routerFailRate = if ($stats.TotalChecks -gt 0) { 
                [math]::Round(($stats.RouterFailures / $stats.TotalChecks) * 100, 2) 
            } else { 0 }
            
            $falsePositiveRate = if ($stats.IssuesReported -gt 0) {
                [math]::Round(($stats.FalsePositives / $stats.IssuesReported) * 100, 1)
            } else { 0 }
            
            $avgRouterLatency = if ($historicalData.RecentRouterLatencies.Count -gt 0) {
                [math]::Round(($historicalData.RecentRouterLatencies | Measure-Object -Average).Average, 1)
            } else { "N/A" }
            
            $avgInternetLatency = if ($historicalData.RecentInternetLatencies.Count -gt 0) {
                [math]::Round(($historicalData.RecentInternetLatencies | Measure-Object -Average).Average, 1)
            } else { "N/A" }
            
            Write-Log "--- STATS: Uptime: $($uptime.ToString('dd\.hh\:mm\:ss')) | Checks: $($stats.TotalChecks) | Router Fails: $routerFailRate% | Issues: $($stats.IssuesReported) | False Positives: $falsePositiveRate% ---" $true "Cyan"
            Write-Log "    Avg Latency - Router: ${avgRouterLatency}ms, Internet: ${avgInternetLatency}ms | WiFi Drops: $($stats.WiFiDrops) | Signal Dips: $($stats.SignalDips)" $true "Cyan"
            $stats.LastStatsDisplay = Get-Date
        }
        
        $lastDiagnosis = $diagnosis
        $lastWiFiStatus = $currentWiFi
        Start-Sleep -Seconds $CheckInterval
    }
}
catch {
    Write-Log "Monitor error: $($_.Exception.Message)" $true "Red"
    Write-Log "Stack trace: $($_.ScriptStackTrace)" $false "Red" $true
}
finally {
    # Flush log buffer
    if ($script:LogBuffer -and $script:LogBuffer.Count -gt 0) {
        $script:LogBuffer | Out-File -Append -FilePath $script:LogFile
        $script:LogBuffer.Clear()
    }
    
    Write-Log "=== Monitor Stopped ===" $true "Red"
    
    # Final statistics
    $uptime = (Get-Date) - $stats.StartTime
    Write-Log "Final Stats:" $true "Yellow"
    Write-Log "  Total runtime: $($uptime.ToString('dd\.hh\:mm\:ss'))" $true "Yellow"
    Write-Log "  Total checks: $($stats.TotalChecks)" $true "Yellow"
    Write-Log "  Issues reported: $($stats.IssuesReported)" $true "Yellow"
    Write-Log "  False positives: $($stats.FalsePositives)" $true "Yellow"
    Write-Log "  WiFi drops: $($stats.WiFiDrops)" $true "Yellow"
    
    if ($ExportMetrics) {
        Write-Log "Metrics exported to: $MetricsFile" $true "Green"
    }
}
