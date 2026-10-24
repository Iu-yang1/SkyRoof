using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using SkyRoof.NativeRotator;

namespace SkyRoof;

/// <summary>
/// RC-3040S LAN operations. The two TCP ports have independent lifetimes:
/// motor commands stay exclusively in NativeRotatorControlEngine (60020);
/// this dialog opens bounded, on-demand WebSocket/60030 sessions.
/// Dragging either slider does not send AT commands or write EEPROM.
/// </summary>
internal sealed class Rc3040sOperationsDialog : Form
{
  private readonly RotatorWidget rotor;
  private readonly RotatorSettings settings;
  private readonly Label status=new();
  private readonly Label firmware=new();
  private readonly Label actual=new();
  private readonly TrackBar az=new(),el=new();
  private readonly Label azValue=new(),elValue=new();
  private readonly Button read=new(){Text="Read device"},reset=new(){Text="Revert sliders"};
  private readonly Button saveAz=new(){Text="Save AZ..."},saveEl=new(){Text="Save EL..."};
  private readonly Button reboot=new(){Text="Restart..."};
  private readonly Button demo=new(){Text="Start DEMO"},stopDemo=new(){Text="STOP DEMO"};
  private readonly Button motion=new(){Text="Motion Settings..."};
  private readonly System.Windows.Forms.Timer refresh=new(){Interval=500};
  private readonly CancellationTokenSource cancel=new();
  private Rc3040sManagementService.Snapshot? snapshot;
  private bool busy,dirtyAz,dirtyEl,updating;

  public Rc3040sOperationsDialog(RotatorWidget widget,RotatorSettings options)
  {
    rotor=widget;settings=options;
    Text="RC-3040S LAN | Motion & AT Management";
    ClientSize=new Size(475,465);
    AutoScaleMode=AutoScaleMode.Font;
    FormBorderStyle=FormBorderStyle.FixedDialog;
    StartPosition=FormStartPosition.CenterParent;
    MaximizeBox=false;
    MinimizeBox=false;
    Controls.Add(Label("GS-232B motor TCP/60020  |  WebSocket management TCP/60030",12,12,451,23));
    firmware.SetBounds(12,42,451,19);firmware.Text="Firmware: not read";Controls.Add(firmware);
    actual.SetBounds(12,66,451,18);actual.Text="AZ/EL raw speeds: unknown";Controls.Add(actual);
    read.SetBounds(12,91,112,29);read.Click+=async(_,_)=>await ReadDeviceAsync();
    reset.SetBounds(135,91,116,29);reset.Click+=(_,_)=>RestoreSliders();
    Controls.Add(read);Controls.Add(reset);

    Controls.Add(Label("AZ raw speed · proposed value",12,137,265,19));
    az.SetBounds(12,160,330,35);az.Minimum=15;az.Maximum=10000;
    az.TickFrequency=1000;az.SmallChange=100;az.LargeChange=500;
    az.Enabled=false;Controls.Add(az);
    azValue.SetBounds(349,158,114,31);Controls.Add(azValue);
    az.Scroll+=(_,_)=>{if(!updating)dirtyAz=true;ShowValues();};

    Controls.Add(Label("EL raw speed · proposed value",12,203,265,19));
    el.SetBounds(12,227,330,35);el.Minimum=15;el.Maximum=10000;
    el.TickFrequency=1000;el.SmallChange=100;el.LargeChange=500;
    el.Enabled=false;Controls.Add(el);
    elValue.SetBounds(349,225,114,31);Controls.Add(elValue);
    el.Scroll+=(_,_)=>{if(!updating)dirtyEl=true;ShowValues();};
    saveAz.SetBounds(12,273,111,29);saveEl.SetBounds(133,273,111,29);
    saveAz.Click+=async(_,_)=>await SaveSpeedAsync(true);
    saveEl.Click+=async(_,_)=>await SaveSpeedAsync(false);
    Controls.Add(saveAz);Controls.Add(saveEl);
    Controls.Add(Label("Sliders are local until Save. EEPROM effects are unverified.",12,313,451,26));

    Controls.Add(Label("LAN motor operations",12,345,260,19));
    demo.SetBounds(12,367,105,29);stopDemo.SetBounds(127,367,105,29);
    reboot.SetBounds(349,367,112,29);motion.SetBounds(242,367,100,29);
    demo.Click+=(_,_)=>{try{rotor.StartRc3040sDemo();}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException)
      {MessageBox.Show(this,ex.Message,"DEMO",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
    stopDemo.Click+=(_,_)=>rotor.StopRc3040sDemo();
    reboot.Click+=async(_,_)=>await RestartAsync();
    motion.Click+=(_,_)=>{
      using var dialog=new Rc3040sMotionSettingsDialog(rotor.ctx.Settings.Rotator,rotor.ctx.Settings.SaveToFile);
      dialog.ShowDialog(this);
    };
    Controls.Add(demo);Controls.Add(stopDemo);Controls.Add(reboot);Controls.Add(motion);
    status.SetBounds(12,412,451,45);status.ForeColor=SystemColors.GrayText;Controls.Add(status);
    refresh.Tick+=(_,_)=>RefreshButtons();refresh.Start();
    RefreshButtons();ShowValues();
    Shown+=async(_,_)=>await ReadDeviceAsync();
  }

  private static Label Label(string text,int x,int y,int w,int h)=>
    new(){Text=text,Location=new Point(x,y),Size=new Size(w,h),
      TextAlign=ContentAlignment.MiddleLeft};

  private bool CanEditHardware =>
    settings.Enabled && settings.NativeProtocol==RotatorProtocolKind.Rc3040sLan &&
    rotor.IsRunning() && !rotor.IsTracking && !rotor.IsParking &&
    !rotor.IsRc3040sDemoRunning && !rotor.PositionSafetyTripped;

  private void RefreshButtons()
  {
    if(IsDisposed)return;
    bool safe=CanEditHardware && !busy;
    read.Enabled=!busy && settings.Rc3040sAtEnabled;
    reset.Enabled=!busy && snapshot!=null;
    az.Enabled=el.Enabled=safe && snapshot?.AzRaw is >=15 and <=10000 &&
      snapshot?.ElRaw is >=15 and <=10000;
    saveAz.Enabled=safe && dirtyAz && az.Enabled;
    saveEl.Enabled=safe && dirtyEl && el.Enabled;
    reboot.Enabled=safe;
    demo.Enabled=safe;stopDemo.Enabled=rotor.IsRc3040sDemoRunning;
    motion.Enabled=!busy;
    // Status is updated by explicit actions. Do not erase network errors and
    // readback/uncertain-write warnings every 500 ms during the UI refresh.
    if(!busy && string.IsNullOrEmpty(status.Text))
      status.Text=safe ? "Ready · no AT is sent while dragging" :
        "AT writes locked: stop TRACK / PARK / DEMO first.";
  }

  private void ShowValues()
  {
    azValue.Text=snapshot==null ? "Unknown" : az.Value.ToString("D5",CultureInfo.InvariantCulture);
    elValue.Text=snapshot==null ? "Unknown" : el.Value.ToString("D5",CultureInfo.InvariantCulture);
    if(snapshot is not null)
      actual.Text=$"Device snapshot: AZ={snapshot.AzRaw?.ToString("D5")??"unknown"}  EL={snapshot.ElRaw?.ToString("D5")??"unknown"}";
    RefreshButtons();
  }
  private void RestoreSliders()
  {
    if(snapshot?.AzRaw is not int a || snapshot.ElRaw is not int e)return;
    if(a is <15 or >10000 || e is <15 or >10000)
    {
      status.Text="Current raw speeds outside slider range (15-10000); read-only view.";
      return;
    }
    updating=true;
    try {az.Value=a;el.Value=e;dirtyAz=dirtyEl=false;}
    finally {updating=false;}
    ShowValues();
  }

  private async Task ReadDeviceAsync()
  {
    if(busy)return;
    busy=true;status.Text="Reading management WebSocket (C2 / diagnostic read-only echo)...";
    RefreshButtons();
    try
    {
      // Detailed read is a confirmed AT+TEST echo, not a speed-setting command.
      var current=await Rc3040sManagementService.ReadAsync(settings.Host,true,cancel.Token);
      if(IsDisposed)return;
      snapshot=current;
      firmware.Text="Firmware: "+(current.Firmware??"unknown");
      RestoreSliders();
      status.Text=current.AzRaw is null || current.ElRaw is null
        ? "Speed fields not received; writing disabled. No automatic retry."
        : "Device raw speed snapshot obtained. No configuration write occurred.";
    }
    catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
    catch(Exception ex){if(!IsDisposed)status.Text="Management read failed: "+ex.Message;}
    finally {busy=false;RefreshButtons();}
  }

  private async Task SaveSpeedAsync(bool azimuth)
  {
    if(busy || !CanEditHardware || snapshot is null || !settings.Rc3040sAtEnabled)return;
    int before=azimuth?(snapshot.AzRaw??-1):(snapshot.ElRaw??-1);
    int proposed=azimuth?az.Value:el.Value;
    if(before<15 || before>10000 || proposed==before)return;
    string axis=azimuth?"AZ":"EL",phrase=$"SAVE-{axis}-{proposed:D5}";
    if(!ConfirmTwice(
      $"{axis}: {before:D5} -> {proposed:D5}\nThis may write EEPROM and change motor speed.\nMotor must be isolated or safely stopped.",
      phrase))return;
    if(!CanEditHardware)return;
    busy=true;status.Text=$"Writing {axis} once; no automatic retries...";
    RefreshButtons();
    try
    {
      var result=await Rc3040sManagementService.SaveSpeedAsync(
        settings.Host,azimuth,proposed,cancel.Token);
      if(IsDisposed)return;
      snapshot=result.After;
      RestoreSliders();
      status.Text=$"{axis} config readback matched {proposed:D5}. EEPROM persistence and actual motor speed remain UNVERIFIED.";
    }
    catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
    catch(Exception ex){if(!IsDisposed)status.Text=$"{axis} result UNKNOWN: {ex.Message}; do not retry blindly.";}
    finally{busy=false;RefreshButtons();}
  }

  private async Task RestartAsync()
  {
    if(busy || !CanEditHardware || !settings.Rc3040sAtEnabled)return;
    if(!ConfirmTwice("Restart may disconnect RC-3040S and reset its control state.\n"
      +"Confirm that the motor is physically stopped and a real emergency stop is available.","RESTART"))
      return;
    if(!CanEditHardware)return;
    rotor.StopRotation(); // serialized STOP on dedicated motor I/O worker
    busy=true;status.Text="Sending one WebSocket restart; reboot is not acknowledged...";
    RefreshButtons();
    try
    {
      await Rc3040sManagementService.RestartAsync(settings.Host,cancel.Token);
      if(!IsDisposed)status.Text="Restart command transmitted, device reboot NOT verified. Recheck position before motion.";
    }
    catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
    catch(Exception ex){if(!IsDisposed)status.Text="Restart result unknown: "+ex.Message;}
    finally{busy=false;RefreshButtons();}
  }

  private bool ConfirmTwice(string warning,string exactPhrase)
  {
    if(MessageBox.Show(this,warning,"RC-3040S hardware operation",
      MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return false;
    using var confirm=new Form{Text="Type exact confirmation",ClientSize=new Size(380,145),
      FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterParent,
      MaximizeBox=false,MinimizeBox=false};
    var prompt=Label("Type: "+exactPhrase,12,12,352,28);
    var entry=new TextBox{Location=new Point(12,49),Width=352};
    var ok=new Button{Text="Confirm",Location=new Point(190,95),Width=84,DialogResult=DialogResult.OK};
    var cancelButton=new Button{Text="Cancel",Location=new Point(280,95),Width=84,DialogResult=DialogResult.Cancel};
    confirm.Controls.AddRange(new Control[]{prompt,entry,ok,cancelButton});
    confirm.AcceptButton=ok;confirm.CancelButton=cancelButton;
    return confirm.ShowDialog(this)==DialogResult.OK &&
      string.Equals(entry.Text.Trim(),exactPhrase,StringComparison.Ordinal);
  }

  protected override void Dispose(bool disposing)
  {
    if(disposing){cancel.Cancel();refresh.Stop();refresh.Dispose();}
    base.Dispose(disposing);
    if(disposing)cancel.Dispose();
  }
}
