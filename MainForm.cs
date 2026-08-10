using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

public sealed class MainForm : Form
{
    readonly TextBox target = new();
    readonly Label status = new();
    readonly RichTextBox log = new();
    readonly List<Button> buttons = new();
    bool busy;

    public MainForm()
    {
        Text = "CrossFire Route Lab v1.0";
        Width = 1200; Height = 800; MinimumSize = new Size(1000,650);
        StartPosition = FormStartPosition.CenterScreen;

        var head = new Panel { Dock=DockStyle.Top, Height=80, Padding=new Padding(15) };
        head.Controls.Add(new Label { Text="CROSSFIRE ROUTE LAB", AutoSize=true, Font=new Font("Segoe UI",20,FontStyle.Bold), Location=new Point(15,8) });
        head.Controls.Add(new Label { Text="Native Windows diagnostic • WE / TD-W9960 • READ-ONLY", AutoSize=true, ForeColor=Color.DarkCyan, Location=new Point(17,47) });
        Controls.Add(head);

        var bar = new FlowLayoutPanel { Dock=DockStyle.Top, Height=125, Padding=new Padding(12), AutoScroll=true, WrapContents=true };
        bar.Controls.Add(new Label { Text="Live endpoint:", AutoSize=true, Padding=new Padding(0,8,4,0) });
        target.Width=220; target.PlaceholderText="IP or hostname"; bar.Controls.Add(target);
        AddButton(bar,"Detect CrossFire",Detect);
        AddButton(bar,"Find Connections",Connections);
        AddButton(bar,"Network Snapshot",Snapshot);
        AddButton(bar,"DNS Discovery",DnsDiscovery);
        AddButton(bar,"Route Table",Routes);
        AddButton(bar,"Ping 30x",Ping);
        AddButton(bar,"Traceroute",Trace);
        AddButton(bar,"Path Quality",Path);
        AddButton(bar,"Multi Scan",Multi);
        AddButton(bar,"Save Report",SaveReport);
        Controls.Add(bar);

        var sp = new Panel { Dock=DockStyle.Top, Height=40, Padding=new Padding(12,5,12,5) };
        status.Text="READ-ONLY MODE"; status.AutoSize=true; status.ForeColor=Color.DarkGreen; sp.Controls.Add(status); Controls.Add(sp);
        log.Dock=DockStyle.Fill; log.ReadOnly=true; log.WordWrap=false; log.BackColor=Color.FromArgb(18,22,27); log.ForeColor=Color.FromArgb(225,232,240); log.Font=new Font("Consolas",10); Controls.Add(log);

        L("============================================================");
        L("CROSSFIRE ROUTE LAB v1.0");
        L("============================================================");
        L("Discover and measure the CURRENT CrossFire network path.");
        L("This build does NOT change routes, DNS, PPPoE, router settings, or firmware.");
        L("Start CrossFire and enter an actual match before using Find Connections.");
        L("");
    }

    void AddButton(Control c,string text,Func<Task> action)
    {
        var b=new Button { Text=text, AutoSize=true, Height=32, Margin=new Padding(4) };
        b.Click += async (_,_) => await Safe(action); buttons.Add(b); c.Controls.Add(b);
    }

    void L(string s) { if(InvokeRequired){BeginInvoke(()=>L(s));return;} log.AppendText(s+Environment.NewLine); log.SelectionStart=log.TextLength; log.ScrollToCaret(); }

    async Task Safe(Func<Task> f)
    {
        if(busy)return; busy=true; foreach(var b in buttons)b.Enabled=false; status.Text="WORKING..."; status.ForeColor=Color.DarkOrange;
        try{await f();}catch(Exception e){L("[ERROR] "+e.Message);} finally{busy=false;foreach(var b in buttons)b.Enabled=true;status.Text="READ-ONLY MODE";status.ForeColor=Color.DarkGreen;}
    }

    async Task<string> Cmd(string file,string args,int timeout=90000)
    {
        using var p=Process.Start(new ProcessStartInfo(file,args){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true,StandardOutputEncoding=Encoding.UTF8}) ?? throw new Exception("Could not start "+file);
        var o=await p.StandardOutput.ReadToEndAsync(); var e=await p.StandardError.ReadToEndAsync();
        using var c=new CancellationTokenSource(timeout);
        try{await p.WaitForExitAsync(c.Token);}catch{try{p.Kill(true);}catch{}return o+"\nTIMEOUT\n"+e;}
        return o+(string.IsNullOrWhiteSpace(e)?"":"\n"+e);
    }

    string Target(){var t=target.Text.Trim(); if(string.IsNullOrEmpty(t))throw new Exception("Enter a live CrossFire endpoint first."); return t;}

    async Task Detect()
    {
        L("\n=== CROSSFIRE PROCESS DETECTION ===");
        var p=Process.GetProcesses().Where(x=>Regex.IsMatch(x.ProcessName,"(?i)crossfire|cflauncher|cfloader")).ToArray();
        if(p.Length==0)L("No obvious CrossFire process found. Start the game first."); else foreach(var x in p)L($"PID {x.Id}  {x.ProcessName}");
    }

    async Task Connections()
    {
        L("\n=== LIVE CONNECTION DISCOVERY ===");
        var pids=Process.GetProcesses().Where(x=>Regex.IsMatch(x.ProcessName,"(?i)crossfire|cflauncher|cfloader")).Select(x=>x.Id).ToHashSet();
        if(pids.Count==0){L("No CrossFire process found. Start an actual match.");return;}
        var s=await Cmd("netstat.exe","-ano",30000); var found=new HashSet<string>();
        foreach(var line in s.Replace("\r","\n").Split('\n'))
        {
            var m=Regex.Match(line,@"\s(?:TCP|UDP)\s+\S+\s+(?<ip>\d{1,3}(?:\.\d{1,3}){3}):(?<port>\d+).*\s(?<pid>\d+)\s*$");
            if(!m.Success||!int.TryParse(m.Groups["pid"].Value,out var pid)||!pids.Contains(pid))continue;
            var ip=m.Groups["ip"].Value; if(Public(ip)){found.Add(ip);L(line.Trim());}
        }
        if(found.Count==0)L("No public IPv4 candidate found. UDP traffic may be represented differently.");
        else{L("\nCandidate endpoints:");foreach(var x in found)L("  "+x);target.Text=found.First();L("\nFirst candidate placed in endpoint box. Verify it while playing.");}
    }

    static bool Public(string ip)
    {
        if(!IPAddress.TryParse(ip,out var a)||a.AddressFamily!=AddressFamily.InterNetwork)return false; var b=a.GetAddressBytes();
        if(b[0]==10||b[0]==127||b[0]==192&&b[1]==168||b[0]==169&&b[1]==254)return false; if(b[0]==172&&b[1]>=16&&b[1]<=31)return false; return true;
    }

    async Task Snapshot(){L("\n=== NETWORK / PPPoE SNAPSHOT ===");L("Time: "+DateTime.Now);L("\n--- IPCONFIG /ALL ---");L(await Cmd("ipconfig.exe","/all",30000));L("\n--- ROUTE PRINT ---");L(await Cmd("route.exe","print",30000));L("\n--- INTERFACES ---");L(await Cmd("netsh.exe","interface ipv4 show interfaces",30000));L("\nNo network configuration was changed.");}

    async Task DnsDiscovery(){L("\n=== CURRENT DNS DISCOVERY ===");foreach(var h in new[]{"crossfire.z8games.com","z8games.com","cfpatch.z8games.com"}){try{var a=await Dns.GetHostAddressesAsync(h);L($"{h}: {string.Join(", ",a.Where(x=>x.AddressFamily==AddressFamily.InterNetwork))}");}catch{L(h+": resolution failed");}}L("DNS/CDN results are candidates only, not assumed game servers.");}
    async Task Routes(){L("\n=== WINDOWS ROUTE TABLE ===");L(await Cmd("route.exe","print",30000));}
    async Task Ping(){var t=Target();L($"\n=== PING 30x {t} ===");L(await Cmd("ping.exe",$"-n 30 -w 1000 {t}",60000));}
    async Task Trace(){var t=Target();L($"\n=== TRACEROUTE {t} ===");L(await Cmd("tracert.exe",$"-d -h 30 -w 800 {t}",90000));}
    async Task Path(){var t=Target();L($"\n=== PATH QUALITY {t} ===");L("This can take several minutes.");L(await Cmd("pathping.exe",$"-n -q 20 -w 500 {t}",300000));}
    async Task Multi(){var t=Target();string[] ips;try{ips=(await Dns.GetHostAddressesAsync(t)).Where(x=>x.AddressFamily==AddressFamily.InterNetwork).Select(x=>x.ToString()).Distinct().Take(8).ToArray();}catch{ips=new[]{t};}L("\n=== MULTI-ENDPOINT QUICK SCAN ===");foreach(var ip in ips){L("\n--- "+ip+" ---");L(await Cmd("ping.exe",$"-n 12 -w 800 {ip}",35000));}}
    async Task SaveReport(){using var d=new SaveFileDialog{Filter="Text report (*.txt)|*.txt|All files (*.*)|*.*",FileName=$"CrossFire_Route_Lab_{DateTime.Now:yyyyMMdd_HHmmss}.txt"};if(d.ShowDialog(this)!=DialogResult.OK)return;await File.WriteAllTextAsync(d.FileName,log.Text,Encoding.UTF8);MessageBox.Show(this,"Report saved.","CrossFire Route Lab",MessageBoxButtons.OK,MessageBoxIcon.Information);}
}
