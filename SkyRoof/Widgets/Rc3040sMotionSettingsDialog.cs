using System;
using System.Drawing;
using System.Windows.Forms;
namespace SkyRoof;

/// <summary>Pure software planning preferences. Does not send motor commands.</summary>
internal sealed class Rc3040sMotionSettingsDialog:Form
{
  public Rc3040sMotionSettingsDialog(RotatorSettings options,Action save)
  {
    Text="RC-3040S Motion Settings";ClientSize=new Size(410,335);
    FormBorderStyle=FormBorderStyle.FixedDialog;StartPosition=FormStartPosition.CenterParent;
    AutoScaleMode=AutoScaleMode.Font;MaximizeBox=false;MinimizeBox=false;
    var profile=new ComboBox{Left=172,Top=15,Width=216,DropDownStyle=ComboBoxStyle.DropDownList};
    profile.Items.AddRange(Enum.GetNames<Rc3040sMotionProfile>());
    profile.SelectedItem=options.Rc3040sMotionMode.ToString();
    var enabled=new CheckBox{Left=14,Top=52,Width=350,Text="Enable experimental setpoint planner",
      Checked=options.Rc3040sPlannerEnabled};
    var az=Numeric(options.Rc3040sPlanAzSpeed,0.1m,20m,94);
    var el=Numeric(options.Rc3040sPlanElSpeed,0.1m,20m,130);
    var acceleration=Numeric(options.Rc3040sPlanAcceleration,0.05m,8m,166);
    var jerk=Numeric(options.Rc3040sPlanJerk,0.05m,30m,202);
    Controls.AddRange(new Control[]{
      new Label{Text="Motion profile",Left=14,Top=15,Width=145},profile,enabled,
      new Label{Text="AZ planned °/s",Left=14,Top=95,Width=155},az,
      new Label{Text="EL planned °/s",Left=14,Top=131,Width=155},el,
      new Label{Text="Acceleration °/s²",Left=14,Top=167,Width=155},acceleration,
      new Label{Text="Jerk °/s³",Left=14,Top=203,Width=155},jerk,
      new Label{Text="Planning is software-side; physical S-curve execution is unverified.",
        Left=14,Top=246,Width=380,Height=28}
    });
    var apply=new Button{Text="Save settings",Left=190,Top=285,Width=108,Height=30};
    var close=new Button{Text="Cancel",Left=305,Top=285,Width=87,Height=30};
    apply.Click+=(_,_)=>{
      options.Rc3040sMotionMode=Enum.Parse<Rc3040sMotionProfile>((string)profile.SelectedItem!);
      options.Rc3040sPlannerEnabled=enabled.Checked;
      options.Rc3040sPlanAzSpeed=(double)az.Value;
      options.Rc3040sPlanElSpeed=(double)el.Value;
      options.Rc3040sPlanAcceleration=(double)acceleration.Value;
      options.Rc3040sPlanJerk=(double)jerk.Value;
      save();
      DialogResult=DialogResult.OK;Close();
    };
    close.Click+=(_,_)=>{DialogResult=DialogResult.Cancel;Close();};
    Controls.AddRange(new Control[]{apply,close});
  }
  private static NumericUpDown Numeric(double value,decimal min,decimal max,int top)=>
    new(){Left=172,Top=top,Width=216,DecimalPlaces=2,
      Minimum=min,Maximum=max,Increment=0.05m,
      Value=(decimal)Math.Clamp(double.IsFinite(value)?value:(double)min,(double)min,(double)max)};
}
