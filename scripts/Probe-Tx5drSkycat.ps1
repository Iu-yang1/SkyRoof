#Requires -Version 5.1
[CmdletBinding()]
param([ValidateRange(1,65535)][int]$Port=4534,[ValidateRange(250,10000)][int]$TimeoutMs=2500)
$ErrorActionPreference='Stop'
$client=[System.Net.Sockets.TcpClient]::new()
$reader=$null; $writer=$null
try {
  $pending=$client.BeginConnect('127.0.0.1',$Port,$null,$null)
  if(-not $pending.AsyncWaitHandle.WaitOne($TimeoutMs)){
    throw 'SkyCAT loopback proxy connection timed out'
  }
  $client.EndConnect($pending)
  $client.ReceiveTimeout=$TimeoutMs
  $client.SendTimeout=$TimeoutMs
  $stream=$client.GetStream()
  $reader=[System.IO.StreamReader]::new($stream,[System.Text.Encoding]::ASCII,$false,1024,$true)
  $writer=[System.IO.StreamWriter]::new($stream,[System.Text.Encoding]::ASCII,1024,$true)
  $writer.AutoFlush=$true
  $writer.WriteLine('\get_info')
  $info=$reader.ReadLine()
  if($info -ne 'SkyCAT WSJT-X compatibility proxy'){
    throw "Unexpected endpoint identity: $info"
  }
  $writer.WriteLine('f')
  $freq=$reader.ReadLine()
  if($freq -notmatch '^\d+$'){throw "Invalid frequency readback: $freq"}
  $writer.WriteLine('m')
  $mode=$reader.ReadLine()
  $width=$reader.ReadLine()
  if(-not $mode -or $mode.StartsWith('RPRT ') -or $width -notmatch '^-?\d+$'){
    throw "Invalid mode readback: $mode / $width"
  }
  Write-Host "Verified: $info"
  Write-Host "RX $freq Hz; mode $mode; passband $width Hz"
  Write-Warning 'This is a WSJT-X compatibility proxy: frequency/mode/VFO/tone writes return success but are NOT executed. CAT PTT is shared-lease controlled.'
  Write-Warning 'TX-5DR must not rely on this endpoint for independent CAT tuning.'
} finally {
  if($writer){$writer.Dispose()}
  if($reader){$reader.Dispose()}
  $client.Dispose()
}
