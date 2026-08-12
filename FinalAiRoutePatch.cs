using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace CrossFireRouteLab;

internal static class FinalAiRoutePatch
{
    static bool installed;
    static int busy;
    static Button? button;
    static Label? status;
    const int Samples = 7;
    const int TimeoutMs = 1200;
    const double MinMs = 3;
    const double MinPct = .05;

    public static void Apply(Form form)
    {
        if (installed || form.IsDisposed) return;
        installed = true;
        var old = Find(form, "AUTO ANALYZE");
        if (old != null)
        {
            var parent = old.Parent;
            old.Visible = false;
            button = new Button { Text = "AI OPTIMIZE ROUTE", Bounds = old.Bounds, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(7,13,27), Font = old.Font };
            button.FlatAppearance.BorderColor = Color.FromArgb(40,242,122);
            button.FlatAppearance.BorderSize = 1;
            button.Click += async (_,_) => await Optimize(form);
            parent?.Controls.Add(button);
            button.BringToFront();
        }
        AddStatus(form);
        Log(form, "[AI ROUTE] READY — one-click TCP route optimizer armed; UDP disabled; no synthetic game packets.");
    }

    static async Task Optimize(Form form)
    {
        if (Interlocked.Exchange(ref busy,1) != 0) return;
        SetButton(false);
        try
        {
            SetStatus("AI ROUTE • TESTING");
            if (!IsCrossFire(form)) { Fail(form,"Start CrossFire and enter an online room first."); return; }
            if (!TryRoom(out var ip,out var port,out var proto) || !proto.Equals("TCP",StringComparison.OrdinalIgnoreCase)) { Fail(form,"No verified CrossFire TCP room endpoint yet."); return; }
            Log(form,$"[AI ROUTE] TARGET {ip}:{port} TCP — live CrossFire room endpoint.");
            var routes = await Defaults();
            if (routes.Count == 0) { Fail(form,"No active IPv4 default route found; nothing was changed."); return; }
            var baseline = await Measure(ip,port,Samples);
            Log(form,Msg("CURRENT",baseline));
            var tests = new List<Result>();
            foreach(var r in routes)
            {
                if(!await AddRoute(ip,r)) { Log(form,$"[AI ROUTE] SKIP {r.Alias} → {r.Gateway}: route test rejected."); continue; }
                Measurement m;
                try { m=await Measure(ip,port,Samples); } finally { await RemoveRoute(ip); }
                tests.Add(new Result(r,m,Score(m)));
                Log(form,$"[AI ROUTE] TEST {r.Alias} → {r.Gateway}: {Msg("",m)} score={Score(m):0.0}.");
            }
            if(tests.Count==0){ Fail(form,"No route candidate could be measured; current route left untouched."); return; }
            var best=tests.OrderBy(x=>x.Score).First();
            var delta=baseline.Median-best.Measurement.Median;
            var pct=baseline.Median>0?delta/baseline.Median:0;
            var better=best.Measurement.Median>=0 && baseline.Median>=0 && Score(best.Measurement)+Math.Max(.5,Score(baseline)*.02)<Score(baseline) && (delta>=MinMs||pct>=MinPct);
            if(!better){ SetStatus("AI ROUTE • CURRENT RETAINED"); Log(form,$"[AI ROUTE] DECISION: current path retained. Best candidate Δ {delta:0.0} ms / {pct:P0} is not material."); return; }
            Log(form,$"[AI ROUTE] WINNER {best.Route.Alias} → {best.Route.Gateway}; estimated gain {delta:0.0} ms ({pct:P0}).");
            Log(form,"[AI ROUTE] APPLYING ONLY A /32 ROUTE FOR THE CURRENT CrossFire ROOM SERVER.");
            if(!await AddRoute(ip,best.Route)){ Fail(form,"Windows refused the selected /32 route; nothing was left changed."); return; }
            var verified=await Measure(ip,port,Samples);
            Log(form,Msg("APPLIED",verified));
            if(!Better(verified,baseline))
            {
                await RemoveSpecificRoute(ip,best.Route);
                SetStatus("AI ROUTE • ROLLED BACK");
                Log(form,"[AI ROUTE] VERIFICATION FAILED — route rolled back automatically.");
                return;
            }
            SetStatus("AI ROUTE • ACTIVE • VERIFIED");
            Log(form,$"[AI ROUTE] SUCCESS — {ip}:{port} now uses {best.Route.Alias} → {best.Route.Gateway}.");
            Log(form,$"[AI ROUTE] VERIFIED: {baseline.Median:0.0} ms → {verified.Median:0.0} ms; jitter {verified.Jitter:0.0} ms; loss {verified.Loss:0.0}%.");
            Log(form,"[AI ROUTE] Rejoin the CrossFire room if the game keeps its existing TCP socket; Windows cannot migrate an established TCP session.");
        }
        catch(Exception ex){ Fail(form,"Optimizer error: "+ex.Message); }
        finally { Interlocked.Exchange(ref busy,0); SetButton(true); }
    }

    static bool Better(Measurement a,Measurement b)=>a.Median>=0&&b.Median>=0&&a.Median+MinMs<b.Median&&a.Loss<=b.Loss+5&&a.Jitter<=Math.Max(b.Jitter+2,b.Jitter*1.25);
    static double Score(Measurement m)=>m.Median<0?1e9:m.Median+m.Jitter*.75+Math.Max(0,m.P95-m.Median)*.35+m.Loss*8;

    static async Task<Measurement> Measure(string ip,int port,int count)
    {
        var v=new List<double>(); var fail=0;
        for(int i=0;i<count;i++)
        {
            var sw=Stopwatch.StartNew();
            try{using var c=new TcpClient{NoDelay=true};var t=c.ConnectAsync(ip,port);if(await Task.WhenAny(t,Task.Delay(TimeoutMs))==t&&c.Connected){sw.Stop();v.Add(sw.Elapsed.TotalMilliseconds);}else fail++;}catch{fail++;}
            await Task.Delay(80);
        }
        if(v.Count==0)return new(-1,-1,-1,100,count,0);
        v.Sort();var med=P(v,.5);var p95=P(v,.95);var mean=v.Average();var jit=Math.Sqrt(v.Sum(x=>Math.Pow(x-mean,2))/v.Count);
        return new(med,p95,jit,fail*100.0/count,count,v.Count);
    }
    static double P(List<double> v,double p){if(v.Count==1)return v[0];var x=(v.Count-1)*p;var a=(int)Math.Floor(x);var b=(int)Math.Ceiling(x);return a==b?v[a]:v[a]+(v[b]-v[a])*(x-a);}

    static async Task<List<Route>> Defaults()
    {
        const string c="Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore ActiveStore | ForEach-Object { $a=Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue; [pscustomobject]@{I=$_.InterfaceIndex;A=$a.Name;G=$_.NextHop;M=$_.RouteMetric;U=($a.Status -eq 'Up')} } | ConvertTo-Json -Compress";
        var text=await PS(c,8000);var list=new List<Route>();
        try{using var d=JsonDocument.Parse(text.Trim());var es=d.RootElement.ValueKind==JsonValueKind.Array?d.RootElement.EnumerateArray().ToList():new(){d.RootElement};foreach(var x in es){var r=new Route(I(x,"I"),S(x,"A"),S(x,"G"),I(x,"M"),x.TryGetProperty("U",out var u)&&u.GetBoolean());if(r.Up&&r.Index>0&&IPAddress.TryParse(r.Gateway,out var g)&&!g.Equals(IPAddress.Any))list.Add(r);}}catch{}
        return list.GroupBy(x=>(x.Index,x.Gateway)).Select(g=>g.OrderBy(x=>x.Metric).First()).ToList();
    }

    static async Task<bool> AddRoute(string ip,Route r){await RemoveRoute(ip);var c=$"New-NetRoute -DestinationPrefix '{ip}/32' -InterfaceIndex {r.Index} -NextHop '{r.Gateway}' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null; Write-Output 'OK'";return (await PS(c,6000)).Contains("OK",StringComparison.OrdinalIgnoreCase);}
    static async Task RemoveRoute(string ip){await PS($"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {{$_.RouteMetric -eq 1 -and $_.Protocol -eq 3}} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue",6000);}
    static async Task RemoveSpecificRoute(string ip,Route r){await PS($"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {{$_.InterfaceIndex -eq {r.Index} -and $_.NextHop -eq '{r.Gateway}'}} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue",6000);}

    static async Task<string> PS(string command,int timeout)=>await Task.Run(()=>{
        try{using var p=new Process{StartInfo=new ProcessStartInfo{FileName="powershell.exe",Arguments="-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "+Quote(command),UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};p.Start();if(!p.WaitForExit(timeout)){try{p.Kill();}catch{}return "timeout";}return p.StandardOutput.ReadToEnd()+p.StandardError.ReadToEnd();}catch{return "";}
    });
    static string Quote(string s)=>"'"+s.Replace("'","''")+"'";

    static void AddStatus(Form f){var h=f.Controls.Cast<Control>().FirstOrDefault(x=>x.Controls.Cast<Control>().Any(y=>y.Text=="GAME ROUTE LAB"));if(h==null)return;status=new Label{AutoSize=false,TextAlign=ContentAlignment.MiddleCenter,Text="AI ROUTE ENGINE • READY",ForeColor=Color.FromArgb(40,242,122),BackColor=Color.FromArgb(7,13,27),Bounds=new Rectangle(1220,88,240,24),Font=new Font("Segoe UI Semibold",8f)};h.Controls.Add(status);status.BringToFront();}
    static void SetStatus(string s){try{if(status!=null&&!status.IsDisposed)status.BeginInvoke((Action)(()=>status.Text=s));}catch{}}
    static void SetButton(bool e){try{if(button!=null&&!button.IsDisposed)button.BeginInvoke((Action)(()=>button.Enabled=e));}catch{}}
    static void Fail(Form f,string s){SetStatus("AI ROUTE • NO CHANGE");Log(f,"[AI ROUTE] "+s);}
    static bool IsCrossFire(Form f)=>(f.GetType().GetField("gameName",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(f)?.ToString()??"").Contains("crossfire",StringComparison.OrdinalIgnoreCase);
    static bool TryRoom(out string ip,out int port,out string proto){ip="";port=0;proto="";try{return CrossFireRoomTransportProbeV3.TryGetTarget(out ip,out port,out proto);}catch{return false;}}
    static Button? Find(Control r,string t)=>All(r).OfType<Button>().FirstOrDefault(x=>x.Text.Equals(t,StringComparison.OrdinalIgnoreCase));
    static IEnumerable<Control> All(Control r){foreach(Control c in r.Controls){yield return c;foreach(var x in All(c))yield return x;}}
    static void Log(Form f,string s){try{if(!f.IsDisposed)f.BeginInvoke((Action)(()=>f.GetType().GetMethod("Log",BindingFlags.Instance|BindingFlags.NonPublic)?.Invoke(f,new object[]{s})));}catch{}}
    static string Msg(string name,Measurement m)=>m.Median<0?$"[AI ROUTE] {name}: unavailable ({m.Loss:0}% loss).":$"[AI ROUTE] {name}: median {m.Median:0.0} ms, p95 {m.P95:0.0} ms, jitter {m.Jitter:0.0} ms, loss {m.Loss:0.0}% ({m.Successes}/{m.Attempts}).";
    static string S(JsonElement x,string n)=>x.TryGetProperty(n,out var p)&&p.ValueKind!=JsonValueKind.Null?p.ToString():"";
    static int I(JsonElement x,string n)=>x.TryGetProperty(n,out var p)&&p.TryGetInt32(out var v)?v:0;

    readonly record struct Route(int Index,string Alias,string Gateway,int Metric,bool Up);
    readonly record struct Measurement(double Median,double P95,double Jitter,double Loss,int Attempts,int Successes);
    readonly record struct Result(Route Route,Measurement Measurement,double Score);
}
