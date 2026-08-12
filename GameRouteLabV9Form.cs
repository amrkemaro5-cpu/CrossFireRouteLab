using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

public sealed class GameRouteLabV9Form : Form
{
    static readonly Color BG=Color.FromArgb(3,6,15), CARD=Color.FromArgb(7,12,25), CARD2=Color.FromArgb(10,17,33);
    static readonly Color CYAN=Color.FromArgb(0,231,255), PURPLE=Color.FromArgb(185,74,255), GREEN=Color.FromArgb(37,242,116), BLUE=Color.FromArgb(86,137,255), TEXT=Color.FromArgb(239,246,255), MUTED=Color.FromArgb(133,158,190);
    readonly TableLayoutPanel root=new(), body=new(), center=new(); readonly FlowLayoutPanel memory=new(); readonly RichTextBox console=new();
    readonly TextBox endpointBox=new(); readonly Label gameName=new(),gameMeta=new(),connections=new(),metrics=new(),quality=new(),netText=new(),routerText=new(),guide=new(),status=new();
    readonly PictureBox gameIcon=new(); readonly ProgressBar progress=new(); readonly Radar radar=new(); readonly Spark spark=new(); readonly Telemetry network=new(),router=new();
    readonly Timer animation=new(){Interval=80},scanTimer=new(){Interval=1800},restoreTimer=new(){Interval=500},pingTimer=new(){Interval=1200};
    readonly string dataDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameRouteLab");
    readonly string gamesFile; readonly List<string> customGames=new(); readonly List<double> history=new();
    bool busy,pinging; int gamePid; string game=""; string? endpoint; string protocol="TCP"; double lastPing=-1,jitter; int sent,lost; float phase;
    static readonly string[] Known={"crossfire","crossfire2","crossfire_client","crossfireclient","valorant","cs2","csgo","cod","r5apex","pubg","tslgame","leagueoflegends","dota2","fortniteclient-win64-shipping"};

    public GameRouteLabV9Form()
    {
        Text="Game Route Lab v9";ClientSize=new Size(1500,920);MinimumSize=new Size(1180,760);StartPosition=FormStartPosition.CenterScreen;
        BackColor=BG;ForeColor=TEXT;Font=new Font("Segoe UI",9.5f);DoubleBuffered=true;AutoScaleMode=AutoScaleMode.Dpi;ShowInTaskbar=true;
        gamesFile=Path.Combine(dataDir,"custom-games.txt");Directory.CreateDirectory(dataDir);try{Icon=Brand.CreateIcon();}catch{}
        BuildUi();LoadCustom();RefreshGames(false);Log("GAME ROUTE LAB v9.0");Log("1 Detect Game → 2 Connections → 3 Test Ping → 4 Route → 5 Report");Log("ENDPOINT is automatic; use ADD GAME EXE only if a game is not detected.");
        animation.Tick+=(_,_)=>Animate();scanTimer.Tick+=async(_,_)=>{if(!busy)await Scan(false);};restoreTimer.Tick+=(_,_)=>RestoreIfCrossFireRuns();pingTimer.Tick+=async(_,_)=>await PingOnce();
        animation.Start();scanTimer.Start();restoreTimer.Start();FormClosed+=(_,_)=>{animation.Stop();scanTimer.Stop();restoreTimer.Stop();pingTimer.Stop();};
    }

    void BuildUi()
    {
        root.Dock=DockStyle.Fill;root.ColumnCount=1;root.RowCount=4;root.BackColor=BG;root.Margin=Padding.Empty;root.Padding=Padding.Empty;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,120));root.RowStyles.Add(new RowStyle(SizeType.Absolute,70));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,32));
        root.Controls.Add(BuildHeader(),0,0);root.Controls.Add(BuildToolbar(),0,1);root.Controls.Add(BuildBody(),0,2);root.Controls.Add(BuildFooter(),0,3);Controls.Add(root);
    }

    Control BuildHeader()
    {
        var p=new Panel{Dock=DockStyle.Fill,BackColor=BG};p.Controls.Add(new PictureBox{Image=Brand.CreateLogo(96),SizeMode=PictureBoxSizeMode.Zoom,Bounds=new Rectangle(22,12,96,96)});
        p.Controls.Add(Label("GAME ROUTE LAB",132,18,28,TEXT,true,520,42));p.Controls.Add(Label("SMARTER ROUTES.  BETTER PING.",134,59,12,CYAN,true,430,24));p.Controls.Add(Label("LOCAL-FIRST GAME NETWORK ANALYZER  •  v9.0",134,83,8.5f,MUTED,false,450,20));
        var s=new Panel{Bounds=new Rectangle(1210,18,250,72),BackColor=CARD};s.Paint+=(_,e)=>{using var q=new Pen(Color.FromArgb(130,PURPLE));e.Graphics.DrawRectangle(q,0,0,s.Width-1,s.Height-1);};s.Controls.Add(Label("●  ACTIVE • READ-ONLY",18,24,9,GREEN,true,215,24));p.Controls.Add(s);return p;
    }

    Control BuildToolbar()
    {
        var bar=new Panel{Dock=DockStyle.Fill,BackColor=Color.FromArgb(4,8,18),Padding=new Padding(12,8,12,7)};
        var t=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=11,RowCount=1,Margin=Padding.Empty};t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,185));for(int i=1;i<11;i++)t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,1));
        endpointBox.Dock=DockStyle.Fill;endpointBox.ReadOnly=true;endpointBox.BackColor=CARD2;endpointBox.ForeColor=TEXT;endpointBox.BorderStyle=BorderStyle.FixedSingle;endpointBox.PlaceholderText="AUTO-FILLED GAME ENDPOINT";endpointBox.Margin=new Padding(0,8,8,8);t.Controls.Add(endpointBox,0,0);
        string[] n={"AUTO ANALYZE","REFRESH GAMES","ADD GAME EXE","DETECT NETWORK","DETECT ROUTER","FIND CONNECTIONS","PING 30x","TRACEROUTE","PATH QUALITY","SAVE REPORT"};Color[] c={PURPLE,CYAN,BLUE,CYAN,PURPLE,CYAN,GREEN,PURPLE,GREEN,BLUE};
        for(int i=0;i<n.Length;i++){var b=Tool(n[i],c[i]);b.Dock=DockStyle.Fill;b.Margin=new Padding(3,2,3,2);t.Controls.Add(b,i+1,0);}bar.Controls.Add(t);return bar;
    }

    Button Tool(string text,Color accent)
    {
        var b=new Button{Text=text,FlatStyle=FlatStyle.Flat,BackColor=CARD,ForeColor=TEXT,Font=new Font("Segoe UI Semibold",8.2f),Cursor=Cursors.Hand,UseVisualStyleBackColor=false};b.FlatAppearance.BorderColor=accent;b.FlatAppearance.BorderSize=1;
        b.Click+=async(_,_)=>{if(busy&&text!="PING 30x")return;try{switch(text){case"AUTO ANALYZE":await AutoAnalyze();break;case"REFRESH GAMES":RefreshGames(true);break;case"ADD GAME EXE":AddGame();break;case"DETECT NETWORK":DetectNetwork();break;case"DETECT ROUTER":DetectRouter();break;case"FIND CONNECTIONS":await FindConnections();break;case"PING 30x":await Ping30();break;case"TRACEROUTE":await Trace();break;case"PATH QUALITY":await PathQuality();break;case"SAVE REPORT":SaveReport();break;}}catch(Exception ex){Log("[ERROR] "+ex.Message);}};return b;
    }

    Control BuildBody()
    {
        body.Dock=DockStyle.Fill;body.ColumnCount=3;body.RowCount=1;body.BackColor=BG;body.Padding=new Padding(12,8,12,6);body.Margin=Padding.Empty;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,235));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,300));
        body.Controls.Add(BuildLeft(),0,0);body.Controls.Add(BuildCenter(),1,0);body.Controls.Add(BuildRight(),2,0);return body;
    }

    Control BuildLeft()
    {
        var p=new Card(PURPLE){Dock=DockStyle.Fill};p.Controls.Add(Label("GAME MEMORY",16,14,13,PURPLE,true));p.Controls.Add(Label("RUNNING GAMES APPEAR HERE",16,38,8,MUTED,true));
        memory.Location=new Point(10,64);memory.FlowDirection=FlowDirection.TopDown;memory.WrapContents=false;memory.AutoScroll=true;memory.BackColor=Color.Transparent;memory.Margin=Padding.Empty;p.Controls.Add(memory);
        foreach((string,Color,Action) x in new[]{("VIEW ALL GAMES",CYAN,(Action)(()=>RefreshGames(true))), ("ADD GAME EXE",BLUE,(Action)AddGame), ("CLEAR MEMORY",PURPLE,(Action)ClearMemory), ("HOW TO USE",GREEN,(Action)ShowGuide)}){var b=new Button{Text=x.Item1,FlatStyle=FlatStyle.Flat,BackColor=CARD,ForeColor=TEXT,Font=new Font("Segoe UI Semibold",8.5f),Cursor=Cursors.Hand};b.FlatAppearance.BorderColor=x.Item2;b.Click+=(_,_)=>x.Item3();p.Controls.Add(b);}
        p.Resize+=(_,_)=>{int y=Math.Max(90,p.ClientSize.Height-178),i=0;foreach(var b in p.Controls.OfType<Button>()){b.SetBounds(16,y+i++*43,p.ClientSize.Width-32,37);}memory.Bounds=new Rectangle(10,64,p.ClientSize.Width-20,Math.Max(80,y-76));};return p;
    }

    Control BuildCenter()
    {
        center.Dock=DockStyle.Fill;center.ColumnCount=1;center.RowCount=4;center.BackColor=BG;center.Margin=Padding.Empty;center.RowStyles.Add(new RowStyle(SizeType.Absolute,178));center.RowStyles.Add(new RowStyle(SizeType.Absolute,154));center.RowStyles.Add(new RowStyle(SizeType.Absolute,190));center.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var h=new Card(PURPLE){Dock=DockStyle.Fill};radar.Bounds=new Rectangle(16,14,142,142);h.Controls.Add(radar);h.Controls.Add(Label("GUIDED AUTO ANALYSIS",180,18,20,PURPLE,true,650,34));h.Controls.Add(Label("Find the game and endpoints automatically — no endpoint typing required.",180,50,10,MUTED,false,720,24));
        progress.Bounds=new Rectangle(180,84,700,10);progress.Maximum=100;h.Controls.Add(progress);status.Text="READY";status.ForeColor=GREEN;status.Font=new Font("Segoe UI Semibold",9);status.TextAlign=ContentAlignment.MiddleRight;status.Bounds=new Rectangle(890,75,90,28);h.Controls.Add(status);
        string[] steps={"1  DETECT GAME","2  CONNECTIONS","3  TEST PING","4  ROUTE","5  REPORT"};for(int i=0;i<5;i++)h.Controls.Add(Label(steps[i],180+i*160,126,8,i==0?GREEN:MUTED,true,145,24,ContentAlignment.MiddleCenter));center.Controls.Add(h,0,0);
        var s=new Card(CYAN){Dock=DockStyle.Fill};s.Controls.Add(Label("CURRENT ANALYSIS SUMMARY",18,12,12,CYAN,true));gameIcon.Bounds=new Rectangle(18,47,60,60);gameIcon.SizeMode=PictureBoxSizeMode.Zoom;gameIcon.Image=Brand.CreateLogo(56);gameIcon.BackColor=CARD2;s.Controls.Add(gameIcon);
        gameName.Bounds=new Rectangle(92,46,390,32);gameName.Font=new Font("Segoe UI Semibold",17,FontStyle.Bold);gameName.ForeColor=TEXT;s.Controls.Add(gameName);gameMeta.Bounds=new Rectangle(92,82,390,52);gameMeta.Font=new Font("Cascadia Mono",8.5f);gameMeta.ForeColor=MUTED;s.Controls.Add(gameMeta);
        s.Controls.Add(Label("DISCOVERED CONNECTIONS",520,48,10,CYAN,true,470,24));connections.Bounds=new Rectangle(520,75,490,28);connections.Font=new Font("Cascadia Mono",10);connections.ForeColor=TEXT;s.Controls.Add(connections);center.Controls.Add(s,0,1);
        var r=new Card(GREEN){Dock=DockStyle.Fill};r.Controls.Add(Label("BEST ENDPOINT + LIVE PING TRACKER",18,12,12,CYAN,true,420,24));metrics.Bounds=new Rectangle(18,50,320,128);metrics.Font=new Font("Cascadia Mono",10);metrics.ForeColor=GREEN;r.Controls.Add(metrics);quality.Bounds=new Rectangle(690,12,320,28);quality.TextAlign=ContentAlignment.TopRight;quality.Font=new Font("Segoe UI Semibold",10);quality.ForeColor=MUTED;r.Controls.Add(quality);spark.Bounds=new Rectangle(350,52,660,120);spark.BackColor=Color.FromArgb(5,11,22);r.Controls.Add(spark);center.Controls.Add(r,0,2);
        var con=new Card(BLUE){Dock=DockStyle.Fill};con.Controls.Add(Label("LIVE ANALYSIS CONSOLE",14,9,11,CYAN,true,300,22));console.Location=new Point(10,33);console.BackColor=Color.FromArgb(1,3,8);console.ForeColor=TEXT;console.ReadOnly=true;console.WordWrap=false;console.ScrollBars=RichTextBoxScrollBars.Both;console.BorderStyle=BorderStyle.FixedSingle;console.Font=new Font("Cascadia Mono",8.5f);con.Controls.Add(console);con.Resize+=(_,_)=>console.Bounds=new Rectangle(10,33,Math.Max(100,con.ClientSize.Width-20),Math.Max(60,con.ClientSize.Height-43));center.Controls.Add(con,0,3);return center;
    }

    Control BuildRight()
    {
        var r=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,BackColor=BG,Margin=Padding.Empty};r.RowStyles.Add(new RowStyle(SizeType.Percent,36));r.RowStyles.Add(new RowStyle(SizeType.Percent,36));r.RowStyles.Add(new RowStyle(SizeType.Percent,28));
        network.Accent=CYAN;network.Title="NETWORK TELEMETRY";network.Content=netText;network.SetState("WAITING");r.Controls.Add(network,0,0);router.Accent=PURPLE;router.Title="ROUTER INTELLIGENCE";router.Content=routerText;router.SetState("WAITING");r.Controls.Add(router,0,1);
        var g=new Card(GREEN){Dock=DockStyle.Fill};g.Controls.Add(Label("WHAT TO PRESS • IN ORDER",14,12,10.5f,GREEN,true,270,22));guide.Bounds=new Rectangle(14,42,270,140);guide.Font=new Font("Cascadia Mono",8.2f);guide.ForeColor=TEXT;guide.Text="1  Launch the game and enter an online match.\r\n2  Press AUTO ANALYZE.\r\n3  Wait for Best Endpoint to fill.\r\n4  Press PING 30x.\r\n5  Use TRACEROUTE / PATH QUALITY.\r\n6  Press SAVE REPORT.\r\n\r\nYou normally do NOT type an endpoint.";g.Controls.Add(guide);r.Controls.Add(g,0,2);return r;
    }

    Control BuildFooter(){var p=new Panel{Dock=DockStyle.Fill,BackColor=BG};p.Controls.Add(Label("Game Route Lab v9.0  •  READ-ONLY  •  NO ROUTE/DNS CHANGES",16,5,8.5f,GREEN,true,450,20));p.Controls.Add(Label("LOW-OVERHEAD ANIMATION",470,5,8.5f,CYAN,true,220,20));p.Controls.Add(Label("SYSTEM: Windows 64-bit",850,5,8.5f,MUTED,true,200,20));p.Controls.Add(Label("● READY",1360,5,8.5f,GREEN,true,90,20));return p;}
    Label Label(string t,int x,int y,float size,Color c,bool bold=false,int w=120,int h=24,ContentAlignment a=ContentAlignment.TopLeft)=>new(){Text=t,Bounds=new Rectangle(x,y,w,h),Font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular),ForeColor=c,BackColor=Color.Transparent,TextAlign=a,AutoEllipsis=true};

    async Task AutoAnalyze(){if(busy)return;busy=true;try{status.Text="SCANNING";progress.Value=10;radar.Active=true;DetectNetwork();progress.Value=25;DetectRouter();progress.Value=40;var g=await DiscoverGame();if(g==null){NoGame();return;}SetGame(g);progress.Value=55;var e=await GetEndpoints(g.Pid);if(e.Count==0){connections.Text="No public endpoint visible yet — stay in an online match.";quality.Text="GAME FOUND • WAITING";Log("Game detected but no public TCP endpoint is visible.");return;}SetEndpoint(e[0]);progress.Value=75;await PingOnce();progress.Value=100;quality.Text="LIVE • TRACKING";status.Text="LIVE";pingTimer.Start();}finally{busy=false;}}
    Task<GameDef?> DiscoverGame(){var names=new HashSet<string>(Known.Concat(customGames),StringComparer.OrdinalIgnoreCase);foreach(var p in Process.GetProcesses()){try{if(names.Contains(p.ProcessName)){string path="";try{path=p.MainModule?.FileName??"";}catch{}return Task.FromResult<GameDef?>(new GameDef(Pretty(customGames.FirstOrDefault(x=>x.Equals(p.ProcessName,StringComparison.OrdinalIgnoreCase))??p.ProcessName),p.ProcessName,p.Id,path));}}catch{}finally{p.Dispose();}}return Task.FromResult<GameDef?>(null);}
    async Task<List<Endpoint>> GetEndpoints(int owner){var psi=new ProcessStartInfo("netstat.exe","-ano -p tcp"){UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true,StandardOutputEncoding=Encoding.ASCII};using var p=Process.Start(psi);if(p==null)return new();var txt=await p.StandardOutput.ReadToEndAsync();await p.WaitForExitAsync();var list=new List<Endpoint>();foreach(var line in txt.Split('\n')){var m=Regex.Match(line.Trim(),@"^TCP\s+\S+\s+(\S+)\s+ESTABLISHED\s+(\d+)\s*$",RegexOptions.IgnoreCase);if(!m.Success||!int.TryParse(m.Groups[2].Value,out var id)||id!=owner)continue;var rem=m.Groups[1].Value;int k=rem.LastIndexOf(':');if(k<1||!int.TryParse(rem[(k+1)..],out var port))continue;var ip=rem[..k].Trim('[',']');if(IPAddress.TryParse(ip,out var a)&&!Private(a))list.Add(new Endpoint(ip,port));}return list.GroupBy(x=>$"{x.Ip}:{x.Port}").Select(x=>x.First()).Take(16).ToList();}
    static bool Private(IPAddress a){if(a.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)return true;var b=a.GetAddressBytes();return b[0]==10||b[0]==127||(b[0]==172&&b[1]>=16&&b[1]<=31)||(b[0]==192&&b[1]==168)||(b[0]==169&&b[1]==254);}
    void SetGame(GameDef g){gamePid=g.Pid;game=g.Name;gameName.Text=g.Name;gameMeta.Text=$"PID {g.Pid}\r\nRunning: YES\r\nPath: {(string.IsNullOrWhiteSpace(g.Path)?"protected / unavailable":g.Path)}";try{using var i=Icon.ExtractAssociatedIcon(g.Path);gameIcon.Image=i?.ToBitmap()??Brand.CreateLogo(56);}catch{gameIcon.Image=Brand.CreateLogo(56);}Upsert(g);Log($"GAME DETECTED: {g.Name} (PID {g.Pid}).");}
    void NoGame(){gamePid=0;game="";gameName.Text="No game detected";gameMeta.Text="Launch the game and enter an online match.\r\nAutomatic scanning will keep checking.";gameIcon.Image=Brand.CreateLogo(56);connections.Text="Waiting for a game connection.";quality.Text="WAITING FOR A TARGET";status.Text="WAITING";progress.Value=0;}
    void SetEndpoint(Endpoint e){endpoint=$"{e.Ip}:{e.Port}";endpointBox.Text=endpoint;connections.Text=$"Selected automatically: {endpoint}";metrics.Text=$"TARGET     {endpoint}\r\nPROTOCOL   {protocol}\r\nLATENCY    {(lastPing>=0?lastPing.ToString("0"):"—")} ms\r\nLOSS       {(sent>0?(lost*100.0/sent).ToString("0.0"):"—")} %\r\nJITTER     {(lastPing>=0?jitter.ToString("0"):"—")} ms\r\nSTABILITY  {Stability()}";history.Clear();spark.Set(history);Log("ENDPOINT AUTO-FILLED: "+endpoint);}
    async Task PingOnce(){if(pinging||endpoint==null)return;pinging=true;try{using var p=new Ping();var sw=Stopwatch.StartNew();PingReply? r=null;try{r=await p.SendPingAsync(endpoint.Split(':')[0],900);}catch{}sw.Stop();sent++;if(r?.Status==IPStatus.Success){lastPing=r.RoundtripTime>0?r.RoundtripTime:sw.Elapsed.TotalMilliseconds;if(history.Count>0)jitter=Math.Abs(lastPing-history[^1]);history.Add(lastPing);if(history.Count>40)history.RemoveAt(0);spark.Set(history);quality.Text="LIVE • TRACKING";}else lost++;metrics.Text=$"TARGET     {endpoint}\r\nPROTOCOL   {protocol}\r\nLATENCY    {(lastPing>=0?lastPing.ToString("0"):"—")} ms\r\nLOSS       {(lost*100.0/Math.Max(1,sent)):0.0} %\r\nJITTER     {(lastPing>=0?jitter.ToString("0"):"—")} ms\r\nSTABILITY  {Stability()}";}finally{pinging=false;}}
    string Stability()=>lastPing<0?"UNKNOWN":lost>0?"CHECK":jitter<5?"EXCELLENT":jitter<15?"GOOD":"UNSTABLE";
    async Task Ping30(){if(endpoint==null){Log("PING 30x: no endpoint.");return;}sent=lost=0;lastPing=-1;jitter=0;history.Clear();spark.Set(history);status.Text="PINGING";for(int i=0;i<30;i++){await PingOnce();await Task.Delay(100);}status.Text="LIVE";Log($"PING 30x complete: {sent-lost}/{sent} replies.");}
    async Task FindConnections(){if(gamePid==0){var g=await DiscoverGame();if(g!=null)SetGame(g);}if(gamePid==0){Log("FIND CONNECTIONS: no game process found.");return;}var e=await GetEndpoints(gamePid);if(e.Count==0){Log("No public TCP endpoint visible. Stay in an online match and try again.");return;}SetEndpoint(e[0]);await PingOnce();}
    void DetectNetwork(){var n=NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x=>x.OperationalStatus==OperationalStatus.Up&&x.NetworkInterfaceType!=NetworkInterfaceType.Loopback);var p=n?.GetIPProperties();var ip=p?.UnicastAddresses.FirstOrDefault(x=>x.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString()??"scanning";netText.Text=$"LINK         {n?.Name??"unknown"}\r\nLOCAL IP     {ip}\r\nDNS          {string.Join(", ",p?.DnsAddresses.Take(2).Select(x=>x.ToString())??Array.Empty<string>())}\r\nSTATUS       LIVE";network.SetState("LIVE TELEMETRY");}
    void DetectRouter(){var g=NetworkInterface.GetAllNetworkInterfaces().Where(x=>x.OperationalStatus==OperationalStatus.Up).SelectMany(x=>x.GetIPProperties().GatewayAddresses).Select(x=>x.Address).FirstOrDefault(x=>x.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork);routerText.Text=$"GATEWAY      {g?.ToString()??"not found"}\r\nROUTE STATE  MONITORING\r\nMODE         READ-ONLY\r\nCONFIDENCE   {(g!=null?"HIGH":"LOW")}";router.SetState("ROUTER LINK • LIVE");}
    async Task Trace(){if(endpoint==null){Log("TRACEROUTE: no endpoint.");return;}var p=Process.Start(new ProcessStartInfo("tracert.exe","-d -h 16 "+endpoint.Split(':')[0]){UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true});if(p==null)return;var s=await p.StandardOutput.ReadToEndAsync();await p.WaitForExitAsync();Log(s.Length>4500?s[^4500..]:s);}
    async Task PathQuality(){await PingOnce();Log($"PATH QUALITY: {endpoint??"—"} | latency={(lastPing>=0?lastPing.ToString("0"):"—")} ms | loss={(sent>0?(lost*100.0/sent).ToString("0.0"):"—")}% | jitter={jitter:0} ms");}
    void RefreshGames(bool log){_=Scan(log);}
    async Task Scan(bool log){if(busy)return;var g=await DiscoverGame();if(g!=null){SetGame(g);if(log)Log("REFRESH GAMES: running game detected.");}else if(log)Log("REFRESH GAMES: no supported game running.");}
    void AddGame(){using var d=new OpenFileDialog{Filter="Game executable (*.exe)|*.exe",Title="Add a game executable"};if(d.ShowDialog(this)!=DialogResult.OK)return;var n=Path.GetFileNameWithoutExtension(d.FileName);customGames.RemoveAll(x=>x.Equals(n,StringComparison.OrdinalIgnoreCase));customGames.Add(n);File.WriteAllLines(gamesFile,customGames.Distinct(StringComparer.OrdinalIgnoreCase));Log("GAME ADDED: "+d.FileName);RefreshGames(true);}
    void LoadCustom(){try{if(File.Exists(gamesFile))customGames.AddRange(File.ReadAllLines(gamesFile).Where(x=>!string.IsNullOrWhiteSpace(x)));}catch{}}
    void Upsert(GameDef g){var m=memory.Controls.OfType<Memory>().FirstOrDefault(x=>x.Process.Equals(g.Process,StringComparison.OrdinalIgnoreCase));if(m==null){m=new Memory(g.Name,g.Process);memory.Controls.Add(m);}m.Set(g.Pid>0,endpoint);}
    void ClearMemory(){memory.Controls.Clear();customGames.Clear();try{File.Delete(gamesFile);}catch{}Log("GAME MEMORY cleared.");}
    void ShowGuide(){MessageBox.Show(this,"1. Start CrossFire and enter an online match.\r\n2. Press AUTO ANALYZE.\r\n3. Wait for the game icon and Best Endpoint to fill.\r\n4. Press PING 30x.\r\n5. Use TRACEROUTE / PATH QUALITY.\r\n6. Press SAVE REPORT.\r\n\r\nYou normally do NOT type anything into ENDPOINT.\r\nIf CrossFire is not detected, use ADD GAME EXE once and select crossfire.exe.","Game Route Lab — How to Use",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    void SaveReport(){var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),$"GameRouteLab-{DateTime.Now:yyyyMMdd-HHmmss}.txt");File.WriteAllText(path,console.Text+$"\r\nGame: {game}\r\nEndpoint: {endpoint}\r\nLatency: {lastPing:0} ms\r\nLoss: {(sent>0?lost*100.0/sent:0):0.0}%\r\nJitter: {jitter:0} ms");Log("REPORT SAVED: "+path);}
    void Log(string s){console.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n");console.SelectionStart=console.TextLength;console.ScrollToCaret();}
    void Animate(){phase+=.08f;radar.Phase=phase;radar.Active=busy||gamePid>0;radar.Invalidate();spark.Phase=phase;spark.Invalidate();network.Phase=phase;router.Phase=phase;network.Invalidate();router.Invalidate();if(busy)progress.Value=Math.Min(99,progress.Value+1);}
    void RestoreIfCrossFireRuns(){if(IsCrossFireRunning()&&WindowState==FormWindowState.Minimized){WindowState=FormWindowState.Normal;Log("Dashboard restored while CrossFire is running.");}}
    static bool IsCrossFireRunning()=>Process.GetProcessesByName("crossfire").Length>0||Process.GetProcessesByName("crossfire2").Length>0||Process.GetProcessesByName("crossfire_client").Length>0;
    static string Pretty(string s)=>string.IsNullOrWhiteSpace(s)?"Game":char.ToUpperInvariant(s[0])+s[1..].Replace("_"," ").Replace("-"," ");
    record GameDef(string Name,string Process,int Pid,string Path);record Endpoint(string Ip,int Port);

    sealed class Card:Panel{public Color Accent;public Card(Color a){Accent=a;BackColor=CARD;DoubleBuffered=true;}protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using var p=new Pen(Color.FromArgb(120,Accent));e.Graphics.DrawRectangle(p,0,0,Width-1,Height-1);using var q=new Pen(Accent,2);e.Graphics.DrawLine(q,0,1,Math.Min(150,Width),1);}}
    sealed class Memory:Panel{public string Process;readonly Label title=new(),meta=new();public Memory(string n,string p){Process=p;Width=205;Height=76;Margin=new Padding(2,2,2,6);BackColor=CARD2;Controls.Add(new PictureBox{Image=Brand.CreateLogo(48),SizeMode=PictureBoxSizeMode.Zoom,Bounds=new Rectangle(8,12,48,48)});title.Text=n;title.ForeColor=TEXT;title.Font=new Font("Segoe UI Semibold",9);title.Bounds=new Rectangle(62,10,130,22);meta.ForeColor=GREEN;meta.Font=new Font("Cascadia Mono",7.5f);meta.Bounds=new Rectangle(62,34,130,34);Controls.Add(title);Controls.Add(meta);}public void Set(bool run,string? ep){meta.Text=run?"RUNNING\r\n"+(ep??"LIVE"):"SAVED\r\nREADY";}}
    sealed class Telemetry:Panel{public Color Accent=CYAN;public Label Content=new();public string Title="";string state="WAITING";public float Phase;public Telemetry(){BackColor=CARD;DoubleBuffered=true;Content.ForeColor=TEXT;Content.Font=new Font("Cascadia Mono",8.2f);Content.Bounds=new Rectangle(14,42,270,95);Controls.Add(Content);}public void SetState(string s){state=s;Invalidate();}protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using var p=new Pen(Color.FromArgb(120,Accent));e.Graphics.DrawRectangle(p,0,0,Width-1,Height-1);using var q=new Pen(Accent,2);e.Graphics.DrawLine(q,0,1,Math.Min(150,Width),1);e.Graphics.DrawString(Title,new Font("Segoe UI Semibold",11),new SolidBrush(Accent),14,12);float a=(float)((Math.Sin(Phase)+1)/2);using var r=new Pen(Color.FromArgb(70+(int)(110*a),Accent),2);e.Graphics.DrawLine(r,14,Height-18,60+130*a,Height-18);e.Graphics.DrawString(state,new Font("Cascadia Mono",7.5f),new SolidBrush(MUTED),Width-125,14);}}
    sealed class Radar:Control{public float Phase;public bool Active;public Radar(){DoubleBuffered=true;}protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;float x=Width/2f,y=Height/2f,r=Math.Min(Width,Height)*.43f;using var p=new Pen(Color.FromArgb(120,PURPLE));for(int i=1;i<4;i++)e.Graphics.DrawEllipse(p,x-r*i/3,y-r*i/3,r*2*i/3,r*2*i/3);if(Active){using var q=new Pen(Color.FromArgb(190,CYAN),2);e.Graphics.DrawLine(q,x,y,x+(float)Math.Cos(Phase)*r,y+(float)Math.Sin(Phase)*r);using var b=new SolidBrush(CYAN);e.Graphics.FillEllipse(b,x-4,y-4,8,8);}}}
    sealed class Spark:Control{readonly List<double> v=new();public float Phase;public Spark(){DoubleBuffered=true;}public void Set(IEnumerable<double>x){v.Clear();v.AddRange(x);Invalidate();}protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using var g=new Pen(Color.FromArgb(35,90,110));for(int i=1;i<4;i++)e.Graphics.DrawLine(g,0,Height*i/4f,Width,Height*i/4f);if(v.Count<2)return;double lo=Math.Max(0,v.Min()-5),hi=v.Max()+5;var p=new PointF[v.Count];for(int i=0;i<v.Count;i++){float xx=4+i*(Width-8f)/Math.Max(1,v.Count-1),yy=Height-8-(float)((v[i]-lo)/Math.Max(1,hi-lo)*(Height-16));p[i]=new PointF(xx,yy);}using var q=new Pen(GREEN,2);e.Graphics.DrawLines(q,p);using var d=new SolidBrush(GREEN);e.Graphics.FillEllipse(d,p[^1].X-3,p[^1].Y-3,6,6);}}
}
