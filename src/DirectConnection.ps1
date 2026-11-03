param(
 [ValidateSet('Install','Remove')][string]$Action,
 [int]$InterfaceIndex,
 [string]$TvMac,
 [string]$SourceExecutable,
 [string]$ResultFile
)
$ErrorActionPreference='Stop'
$base=Join-Path $env:ProgramData 'MirakurunDesktopSetup\DirectConnection'
$serviceName='MirakurunDirectDhcp'
$ruleName='MirakurunDirectDhcp-In'
$outRuleName='MirakurunDirectDhcp-Out'
$configPath=Join-Path $base 'config.json'
$changed=$false
$createdService=$false
function Report([string]$text) { if($ResultFile){[IO.File]::WriteAllText($ResultFile,$text,[Text.Encoding]::UTF8)} }
function Wait-Stopped {
 $s=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
 if($s){Stop-Service $serviceName -Force;$s.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(10))}
}
function Remove-OwnedRules {
 Get-NetFirewallRule -Name $ruleName,$outRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
function Overlap([string]$network,[int]$bits,[string]$candidate) {
 if($bits -eq 0){return $false}
 $a=[Net.IPAddress]::Parse($network).GetAddressBytes();$b=[Net.IPAddress]::Parse($candidate).GetAddressBytes()
 $count=[Math]::Min($bits,30)
 for($i=0;$i -lt $count;$i++){
  $mask=1 -shl (7-($i%8));$offset=[int][Math]::Floor($i/8)
  if(($a[$offset] -band $mask) -ne ($b[$offset] -band $mask)){return $false}
 }
 return $true
}
try {
 if($Action -eq 'Remove'){
  if(!(Test-Path -LiteralPath $configPath)){throw 'Direct connection is not installed.'}
  $c=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
  $adapter=Get-NetAdapter -IncludeHidden | Where-Object {$_.InterfaceGuid.ToString() -eq $c.InterfaceId}
  if(!$adapter -or ($adapter.MacAddress -replace '[-:]','') -ne $c.AdapterMac){throw 'The saved adapter is unavailable; no network settings were changed.'}
  $unexpected=Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {$_.IPAddress -ne $c.Server -and $_.IPAddress -notlike '169.254.*'}
  if($unexpected){throw 'The adapter has new manual addresses. Remove them or restore its settings manually before uninstalling.'}
  Wait-Stopped
  & sc.exe delete $serviceName | Out-Null
  if($LASTEXITCODE -ne 0){throw 'Cannot remove DHCP service'}
  Remove-OwnedRules
  Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress $c.Server -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false
  Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -Dhcp Enabled
  Rename-Item -LiteralPath $configPath -NewName ('removed-'+(Get-Date -Format yyyyMMddHHmmss)+'.json')
  Clear-DnsClientCache
  Report 'OK: DHCP service removed. The selected Ethernet adapter is back to automatic addressing.'
  exit 0
 }
 if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){throw 'Already installed. Remove the direct-connection configuration before changing it.'}
 if(Test-Path -LiteralPath $configPath){throw 'A previous configuration exists. Restore/remove it before installing again.'}
 if(!$SourceExecutable -or !(Test-Path -LiteralPath $SourceExecutable -PathType Leaf)){throw 'Setup executable not found.'}
 if((Get-Item -LiteralPath $SourceExecutable).Name -ne 'Mirakurun-Desktop-Setup.exe'){throw 'Unexpected service executable'}
 $adapter=Get-NetAdapter -InterfaceIndex $InterfaceIndex
 if(!$adapter.HardwareInterface -or $adapter.Status -ne 'Up' -or $adapter.NdisPhysicalMedium -ne 14){throw 'Choose a connected physical Ethernet adapter, not Wi-Fi or a virtual adapter.'}
 $ipif=Get-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4
 if($ipif.Dhcp -ne 'Enabled'){throw 'This adapter already has manual configuration. Use an adapter set to automatic addressing.'}
 if($ipif.Forwarding -eq 'Enabled'){throw 'IP forwarding must be disabled on the dedicated TV adapter.'}
 $addresses=@(Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue)
 if($addresses | Where-Object {$_.IPAddress -notlike '169.254.*'}){throw 'This adapter has a DHCP/network address. Connect only the TV directly, not a router or shared LAN.'}
 if(Get-NetUDPEndpoint -LocalPort 67 -ErrorAction SilentlyContinue){throw 'Another DHCP service is already using UDP port 67.'}
 $normalized=$TvMac -replace '[-:]',''
 if($normalized -notmatch '^[0-9a-fA-F]{12}$' -or ([Convert]::ToByte($normalized.Substring(0,2),16) -band 1) -ne 0 -or $normalized -eq '000000000000'){throw 'A valid TV Ethernet MAC address is required.'}
 # Pick a private /30 that does not overlap any existing non-default IPv4 route.
 $routes=@(Get-NetRoute -AddressFamily IPv4)
 $server=$null
 foreach($candidate in @('192.168.250.1','192.168.251.1','172.29.254.1','172.30.254.1','10.253.254.1')){
  $used=$false
  foreach($r in $routes){$parts=$r.DestinationPrefix.Split('/');if(Overlap $parts[0] ([int]$parts[1]) $candidate){$used=$true;break}}
  if(!$used){$server=$candidate;break}
 }
 if(!$server){throw 'No unused dedicated subnet was found. No settings were changed.'}
 $tv=$server.Substring(0,$server.LastIndexOf('.')+1)+([int]$server.Split('.')[3]+1)
 $parent=Split-Path -Parent $base
 foreach($path in @($parent,$base)){
  if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Service directory cannot be a reparse point.'}
  New-Item -ItemType Directory -Path $path -Force | Out-Null
  & icacls.exe $path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
  if($LASTEXITCODE -ne 0){throw 'Could not protect the service directory.'}
  & icacls.exe $path /setowner '*S-1-5-32-544' | Out-Null
  if($LASTEXITCODE -ne 0){throw 'Could not protect the service directory owner.'}
 }
 # Service files are executable as SYSTEM: only administrators/SYSTEM may modify them.
 & icacls.exe $base /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Could not protect the service directory.'}
 $executable=Join-Path $base 'Mirakurun-Desktop-Setup.exe'
 Copy-Item -LiteralPath $SourceExecutable -Destination $executable -Force
 $c=@{InterfaceId=$adapter.InterfaceGuid.ToString();AdapterMac=($adapter.MacAddress -replace '[-:]','');TvMac=$normalized;Server=$server;Address=$tv;Mask='255.255.255.252'}
 $c | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
 & icacls.exe $base /setowner '*S-1-5-32-544' /T | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Could not protect service file ownership.'}
 $changed=$true
 Set-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -Dhcp Disabled
 # Only APIPA addresses can exist here (preflight above).
 Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {$_.IPAddress -like '169.254.*'} | Remove-NetIPAddress -Confirm:$false
 New-NetIPAddress -InterfaceIndex $InterfaceIndex -IPAddress $server -PrefixLength 30 -AddressFamily IPv4 | Out-Null
 New-NetFirewallRule -Name $ruleName -DisplayName 'Mirakurun direct TV DHCP (in)' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 67 -RemotePort 68 -Program $executable -InterfaceAlias $adapter.Name -Profile Any | Out-Null
 New-NetFirewallRule -Name $outRuleName -DisplayName 'Mirakurun direct TV DHCP (out)' -Direction Outbound -Action Allow -Protocol UDP -LocalPort 67 -RemotePort 68 -Program $executable -InterfaceAlias $adapter.Name -Profile Any | Out-Null
 New-Service -Name $serviceName -BinaryPathName ('"'+$executable+'" --direct-service') -DisplayName 'Mirakurun direct TV DHCP' -StartupType Automatic -Description 'Assigns one reserved address to the configured TV MAC on the selected physical Ethernet interface. No router or DNS is advertised.' | Out-Null
 $createdService=$true
 & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
 Start-Service $serviceName
 # Validate startup before reporting installation. Keep configuration if TV is unplugged.
 Start-Sleep -Seconds 3
 $s=Get-Service $serviceName
 if($s.Status -ne 'Running'){throw 'DHCP service did not start.'}
 Clear-DnsClientCache
 Report ('OK: PC='+$server+' TV='+$tv+'; DHCP reservation installed. The TV stays on automatic addressing.')
 exit 0
} catch {
 $errorText=$_.Exception.Message
 if($changed){
  try {
   if($createdService){Wait-Stopped;& sc.exe delete $serviceName | Out-Null}
   Remove-OwnedRules
   Get-NetIPAddress -InterfaceIndex $InterfaceIndex -IPAddress $server -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false
   Set-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -Dhcp Enabled
   Remove-Item -LiteralPath $configPath -ErrorAction SilentlyContinue
  }catch{$errorText+='; automatic rollback failed: '+$_.Exception.Message}
 }
 Report ('ERROR: '+$errorText)
 exit 1
}
