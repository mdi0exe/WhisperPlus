$p = 'C:\Users\mahdi\Desktop\WhisperPlus-1.0.0.msi'
$wi = New-Object -ComObject WindowsInstaller.Installer
$db = $wi.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$wi,@($p,0))
$queries = @{
  ProductName   = "SELECT Value FROM Property WHERE Property='ProductName'"
  ProductVersion= "SELECT Value FROM Property WHERE Property='ProductVersion'"
  Manufacturer  = "SELECT Value FROM Property WHERE Property='Manufacturer'"
  UpgradeCode   = "SELECT Value FROM Property WHERE Property='UpgradeCode'"
  ProductCode   = "SELECT Value FROM Property WHERE Property='ProductCode'"
  ALLUSERS      = "SELECT Value FROM Property WHERE Property='ALLUSERS'"
  ProductLanguage = "SELECT Value FROM Property WHERE Property='ProductLanguage'"
}
foreach ($key in $queries.Keys) {
  $vw = $db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@($queries[$key]))
  $vw.GetType().InvokeMember('Execute','InvokeMethod',$null,$vw,@())
  $r = $vw.GetType().InvokeMember('Fetch','InvokeMethod',$null,$vw,@())
  if ($r) {
    $v = $r.GetType().InvokeMember('StringData','GetProperty',$null,$r,@(1))
    Write-Host ("{0,-16} = {1}" -f $key, $v)
  } else {
    Write-Host ("{0,-16} = <not set>" -f $key)
  }
}
# Count components/files
foreach ($pair in @(@('Component','SELECT Count(*) FROM Component'),@('File','SELECT Count(*) FROM File'),@('Feature','SELECT Count(*) FROM Feature'),@('Shortcut','SELECT Shortcut FROM Shortcut'))) {
  $vw = $db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@($pair[1]))
  $vw.GetType().InvokeMember('Execute','InvokeMethod',$null,$vw,@())
  $r = $vw.GetType().InvokeMember('Fetch','InvokeMethod',$null,$vw,@())
  if ($r) {
    $v = $r.GetType().InvokeMember('StringData','GetProperty',$null,$r,@(1))
    Write-Host ("{0,-16} = {1}" -f $pair[0], $v)
  }
}
