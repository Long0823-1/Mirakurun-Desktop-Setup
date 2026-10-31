using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MirakurunDesktop {
 static class Program {
  [STAThread] static int Main(string[] args) {
   if(args.Contains("--epgtimer")){LaunchEpg(Arg(args,"--destination")).GetAwaiter().GetResult();return 0;}
   if(args.Contains("--headless"))return Headless(args).GetAwaiter().GetResult();
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new SetupForm());return 0;
  }
  public static async Task LaunchEpg(string root) {
   string dir=Path.Combine(root,"EDCB");
   if(!Process.GetProcessesByName("EpgTimerSrv").Any())Process.Start(new ProcessStartInfo(Path.Combine(dir,"EpgTimerSrv.exe")){WorkingDirectory=dir});
   // Wait for the server to parse initial EPG before the client caches an empty table.
   for(int attempt=0;attempt<30;attempt++) {
    try {
     using(var pipe=new NamedPipeClientStream(".","EpgTimerSrvPipe",PipeDirection.InOut,PipeOptions.Asynchronous)) {
      await Task.Run(()=>pipe.Connect(1000));
      byte[] command=new byte[8];Array.Copy(BitConverter.GetBytes(1026),command,4);await pipe.WriteAsync(command,0,8);
      byte[] header=new byte[8];await ReadPipe(pipe,header);
      int status=BitConverter.ToInt32(header,0),size=BitConverter.ToInt32(header,4);
      if(size<0||size>64*1024*1024)throw new InvalidDataException();
      byte[] body=new byte[size];await ReadPipe(pipe,body);
      if(status==1&&size>8)break;
     }
    }catch(IOException){}catch(TimeoutException){}
    await Task.Delay(1000);
   }
   Process.Start(new ProcessStartInfo(Path.Combine(dir,"EpgTimer.exe")){WorkingDirectory=dir});
  }
  static async Task ReadPipe(Stream pipe,byte[] buffer) {
   int offset=0;while(offset<buffer.Length){var task=pipe.ReadAsync(buffer,offset,buffer.Length-offset);if(await Task.WhenAny(task,Task.Delay(3000))!=task)throw new TimeoutException();int n=await task;if(n==0)throw new IOException();offset+=n;}
  }
  static string Arg(string[] args,string name,string fallback=null){int index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:fallback;}
  static async Task<int> Headless(string[] args) {
   string report=Arg(args,"--report",Path.Combine(Environment.CurrentDirectory,"setup-test.log"));
   var logLock=new object();
   Action<string> log=text=>{lock(logLock)File.AppendAllText(report,DateTime.Now.ToString("HH:mm:ss ")+text+Environment.NewLine);};
   try {
    var options=new Options {Server=Arg(args,"--server","http://169.254.95.200:40772/"),Destination=Arg(args,"--destination"),CacheDirectory=Arg(args,"--cache"),CaptureSeconds=int.Parse(Arg(args,"--seconds","8")),TunerCount=int.Parse(Arg(args,"--tuners","2")),Shortcuts=false,Startup=false,InstallRuntime=false};
    await new SetupEngine(log).InstallAsync(options,CancellationToken.None);return 0;
   }catch(Exception error){log(error.ToString());return 1;}
  }
 }
 sealed class SetupForm:Form {
  readonly TextBox server=new TextBox(),destination=new TextBox(),recording=new TextBox(),log=new TextBox();
  readonly NumericUpDown tuners=new NumericUpDown(),seconds=new NumericUpDown();
  readonly CheckBox startup=new CheckBox(),runtime=new CheckBox();
  readonly Button install=new Button(),cancel=new Button(),check=new Button(),view=new Button(),timer=new Button(),folder=new Button();
  readonly ProgressBar progress=new ProgressBar();CancellationTokenSource cancellation;bool busy;string completed;
  public SetupForm() {
   AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;Text="Mirakurun Desktop Setup 0.1.1";ClientSize=new Size(960,820);MinimumSize=new Size(900,780);Font=new Font("Yu Gothic UI",10);BackColor=Color.FromArgb(246,248,249);
   var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(26),ColumnCount=3,RowCount=13};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,165));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,115));Controls.Add(layout);
   var title=new Label {Text="Mirakurunから、視聴・録画環境をまとめて構築",Font=new Font(Font.FontFamily,19,FontStyle.Bold),AutoSize=true,Margin=new Padding(0,0,0,10)};layout.Controls.Add(title,0,0);layout.SetColumnSpan(title,3);
   var description=new Label {Text="BonDriver_Mirakurun / TVTest / EDCB・EPGTimer（すべてx64）\n稼働済みのMirakurunに接続して、局設定・初回EPG・TVTest連携まで自動設定します。",AutoSize=true,Margin=new Padding(0,0,0,18)};layout.Controls.Add(description,0,1);layout.SetColumnSpan(description,3);
   server.Text="http://169.254.95.200:40772/";destination.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MirakurunDesktop");
   Row(layout,2,"Mirakurun接続先",server);check.Text="接続確認";check.Dock=DockStyle.Fill;layout.Controls.Add(check,2,2);
   Row(layout,3,"構築先（空）",destination);var browse=new Button {Text="参照…",Dock=DockStyle.Fill};layout.Controls.Add(browse,2,3);browse.Click+=(s,e)=>{using(var dialog=new FolderBrowserDialog {Description="空の専用フォルダーを選択してください",SelectedPath=destination.Text})if(dialog.ShowDialog(this)==DialogResult.OK)destination.Text=dialog.SelectedPath;};
   recording.PlaceholderTextCompat("空欄なら構築先のRecordings");Row(layout,4,"録画保存先",recording);var recBrowse=new Button {Text="参照…",Dock=DockStyle.Fill};layout.Controls.Add(recBrowse,2,4);recBrowse.Click+=(s,e)=>{using(var d=new FolderBrowserDialog {Description="録画の保存先"})if(d.ShowDialog(this)==DialogResult.OK)recording.Text=d.SelectedPath;};
   var settings=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};settings.Controls.Add(new Label {Text="録画チューナー数",AutoSize=true,Padding=new Padding(0,5,0,0)});tuners.Minimum=1;tuners.Maximum=16;tuners.Value=2;tuners.Width=55;settings.Controls.Add(tuners);settings.Controls.Add(new Label {Text="初回EPG：1多重あたり（秒）",AutoSize=true,Padding=new Padding(18,5,0,0)});seconds.Minimum=3;seconds.Maximum=120;seconds.Value=15;seconds.Width=55;settings.Controls.Add(seconds);layout.Controls.Add(settings,0,5);layout.SetColumnSpan(settings,3);
   startup.Text="Windowsログイン時に録画サーバーを自動起動する";startup.Checked=true;startup.AutoSize=true;layout.Controls.Add(startup,0,6);layout.SetColumnSpan(startup,3);
   runtime.Text="必要な場合はMicrosoft Visual C++ x64ランタイムを導入（UACあり）";runtime.Checked=true;runtime.AutoSize=true;layout.Controls.Add(runtime,0,7);layout.SetColumnSpan(runtime,3);
   var warning=new Label {Text="配布元から約170MBを取得します。初回の局・EPG確認はチューナーを使います。\n既存TVTest・録画設定は上書きしません。Windowsの起動・サーバー常駐が予約録画に必要です。",ForeColor=Color.FromArgb(81,99,106),AutoSize=true,Margin=new Padding(0,10,0,12)};layout.Controls.Add(warning,0,8);layout.SetColumnSpan(warning,3);
   var actions=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};install.Text="まとめて構築";install.Size=new Size(190,43);install.BackColor=Color.FromArgb(21,112,105);install.ForeColor=Color.White;install.FlatStyle=FlatStyle.Flat;cancel.Text="中止";cancel.Size=new Size(90,43);cancel.Enabled=false;actions.Controls.Add(install);actions.Controls.Add(cancel);layout.Controls.Add(actions,0,9);layout.SetColumnSpan(actions,3);
   progress.Dock=DockStyle.Fill;progress.Height=7;progress.Style=ProgressBarStyle.Continuous;layout.Controls.Add(progress,0,10);layout.SetColumnSpan(progress,3);
   log.Multiline=true;log.ScrollBars=ScrollBars.Vertical;log.ReadOnly=true;log.BackColor=Color.White;log.Dock=DockStyle.Fill;layout.Controls.Add(log,0,11);layout.SetColumnSpan(log,3);layout.RowStyles.Add(new RowStyle());for(int i=1;i<11;i++)layout.RowStyles.Add(new RowStyle());layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle());
   var finished=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};view.Text="TVTestを開く";timer.Text="EPGTimerを開く";folder.Text="構築先を開く";foreach(var b in new[]{view,timer,folder}){b.AutoSize=true;b.Enabled=false;finished.Controls.Add(b);}var support=new LinkLabel {Text="Ko-fiで開発を支援",AutoSize=true,Margin=new Padding(20,8,0,0)};support.LinkClicked+=(s,e)=>Process.Start("https://ko-fi.com/long0823");finished.Controls.Add(support);layout.Controls.Add(finished,0,12);layout.SetColumnSpan(finished,3);
   check.Click+=async(s,e)=>{if(busy)return;SetBusy(true);try{var info=await new SetupEngine(Log).InspectAsync(server.Text,CancellationToken.None);tuners.Value=Math.Min(16,info.Item2);Log("接続できました："+info.Item1.Count+"多重 / "+info.Item2+"チューナー");}catch(Exception ex){Log("接続失敗："+ex.Message);}finally{SetBusy(false);}};
   install.Click+=async(s,e)=>await Install();cancel.Click+=(s,e)=>cancellation?.Cancel();
   view.Click+=(s,e)=>Launch(Path.Combine(completed,"TVTest","TVTest.exe"),"/d BonDriver_Mirakurun.dll");timer.Click+=async(s,e)=>{timer.Enabled=false;try{await Program.LaunchEpg(completed);}catch(Exception ex){Log(ex.Message);}finally{timer.Enabled=true;}};folder.Click+=(s,e)=>Process.Start(completed);
   FormClosing+=(s,e)=>{if(busy){e.Cancel=true;Log("処理中です。先に「中止」を選んでください。");}};
   Log("接続先と空の構築先を指定し、「まとめて構築」を押してください。");
  }
  static void Row(TableLayoutPanel panel,int row,string label,TextBox text){panel.Controls.Add(new Label {Text=label,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(0,7,5,7)},0,row);text.Dock=DockStyle.Fill;text.Margin=new Padding(0,4,8,4);panel.Controls.Add(text,1,row);}
  void SetBusy(bool value){busy=value;foreach(Control c in new Control[]{server,destination,recording,tuners,seconds,startup,runtime,install,check})c.Enabled=!value;cancel.Enabled=value;progress.Style=value?ProgressBarStyle.Marquee:ProgressBarStyle.Continuous;}
  void Log(string text){if(IsDisposed)return;if(InvokeRequired){BeginInvoke(new Action<string>(Log),text);return;}log.AppendText(DateTime.Now.ToString("HH:mm:ss ")+text+Environment.NewLine);}
  async Task Install(){
   var options=new Options {Server=server.Text,Destination=destination.Text,RecordDirectory=recording.Text,TunerCount=(int)tuners.Value,CaptureSeconds=(int)seconds.Value,Startup=startup.Checked,InstallRuntime=runtime.Checked};
   cancellation=new CancellationTokenSource();SetBusy(true);
   try {
    var result=await Task.Run(()=>new SetupEngine(Log).InstallAsync(options,cancellation.Token));completed=result.destination;
    foreach(var b in new[]{view,timer,folder})b.Enabled=true;
    Log("TVTestとEPGTimerを開けます。録画を使うときはEPGTimerを起動してください。");
    if(result.warnings.Any())MessageBox.Show(this,"構築しました。一部の局は受信できませんでした。ログとsetup-result.jsonを確認してください。","構築完了（注意あり）",MessageBoxButtons.OK,MessageBoxIcon.Information);
   }catch(OperationCanceledException){Log("構築を中止しました。既存環境は変更していません。");}catch(Exception error){Log("構築失敗："+error.Message);Log("設定前の既存環境を上書きしないため、未完了フォルダーは診断用に保持します。");}
   finally{SetBusy(false);cancellation.Dispose();cancellation=null;}
  }
  void Launch(string path,string args){try{Process.Start(new ProcessStartInfo(path,args){WorkingDirectory=Path.GetDirectoryName(path),UseShellExecute=true});}catch(Exception e){Log(e.Message);}}
 }
 static class TextBoxExtensions {
  [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,string l);
  public static void PlaceholderTextCompat(this TextBox box,string text){box.HandleCreated+=(s,e)=>SendMessage(box.Handle,0x1501,new IntPtr(1),text);}
 }
}
