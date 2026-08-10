using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

public sealed record GameEndpoint(string ProcessName,int Pid,string Protocol,string RemoteIp,int RemotePort,string State,bool LikelyGame,int Confidence,string ExecutablePath);

public static class GameScanner
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd,out uint processId);
    static readonly HashSet<string> Ignore=new(StringComparer.OrdinalIgnoreCase){"system","idle","svchost","lsass","services","wininit","spoolsv","explorer","dwm","searchhost","searchindexer","runtimebroker","textinputhost","chrome","msedge","firefox","opera","brave","discord","slack","teams","outlook","onedrive","dropbox","powershell","cmd","conhost","chatgpt","steamwebhelper","steam","epicwebhelper","updater","update"};
    static readonly string[] GameWords={"crossfire","valorant","fortnite","apex","overwatch","warzone","callofduty","cod","cs2","csgo","pubg","dota","leagueoflegends","league","minecraft","roblox","gta","eldenring","battlefield","rainbowsix","r6","destiny","gameclient","game"};
    static readonly string[] GameFolders={"\\games\\","\\steamapps\\common\\","\\epic games\\","\\riot games\\","\\valorant\\","\\crossfire\\","\\garena\\","\\z8games\\","\\blizzard\\","\\ubisoft\\"};

    public static async Task<List<GameEndpoint>> DiscoverAsync()
    {
        var fg=GetForegroundPid(); var text=await RunAsync("netstat.exe","-ano",30000); var cache=new Dictionary<int,(string Name,string Path,string Title)>(); var result=new List<GameEndpoint>();
        foreach(var line in text.Replace('\r','\n').Split('\n'))
        {
            var m=Regex.Match(line,@"^\s*(TCP|UDP)\s+(\S+)\s+(\S+)(?:\s+(\S+))?\s+(\d+)\s*$",RegexOptions.IgnoreCase); if(!m.Success||!int.TryParse(m.Groups[5].Value,out var pid))continue;
            var proto=m.Groups[1].Value.ToUpperInvariant(); var state=m.Groups[4].Value; if(proto=="TCP"&&!state.Equals("ESTABLISHED",StringComparison.OrdinalIgnoreCase))continue;
            var remote=m.Groups[3].Value; var colon=remote.LastIndexOf(':'); var ip=colon>0?remote[..colon].Trim('[',']'):remote.Trim('[',']'); var port=colon>0&&int.TryParse(remote[(colon+1)..],out var pp)?pp:0;
            if(!IPAddress.TryParse(ip,out var addr)||addr.AddressFamily!=AddressFamily.InterNetwork||!IsPublic(ip))continue;
            if(!cache.TryGetValue(pid,out var info))
            {
                try{var p=Process.GetProcessById(pid);var path="";try{path=p.MainModule?.FileName??"";}catch{} info=(p.ProcessName,path,p.MainWindowTitle);cache[pid]=info;}catch{continue;}
            }
            var score=Confidence(info.Name,info.Path,info.Title,pid,fg,port); if(score<25)continue;
            result.Add(new GameEndpoint(info.Name,pid,proto,ip,port,string.IsNullOrWhiteSpace(state)?"ACTIVE":state,score>=45,score,info.Path));
        }
        return result.GroupBy(x=>$"{x.Pid}|{x.Protocol}|{x.RemoteIp}|{x.RemotePort}").Select(g=>g.OrderByDescending(x=>x.Confidence).First()).OrderByDescending(x=>x.Confidence).ToList();
    }
    public static int GetForegroundPid(){try{var h=GetForegroundWindow();GetWindowThreadProcessId(h,out var pid);return (int)pid;}catch{return 0;}}
    public static string GetForegroundProcessName(){try{var pid=GetForegroundPid();return pid>0?Process.GetProcessById(pid).ProcessName:"";}catch{return "";}}
    static int Confidence(string name,string path,string title,int pid,int foreground,int port)
    {
        if(Ignore.Contains(name))return 0; var n=name.ToLowerInvariant(); var all=(n+" "+title+" "+path).ToLowerInvariant(); if(all.Contains("chatgpt")||all.Contains("chrome")||all.Contains("msedge")||all.Contains("firefox")||all.Contains("discord"))return 0;
        int s=0; bool word=GameWords.Any(w=>n.Contains(w)); bool folder=GameFolders.Any(f=>path.Contains(f,StringComparison.OrdinalIgnoreCase)); bool titleGame=GameWords.Any(w=>title.Contains(w,StringComparison.OrdinalIgnoreCase));
        if(word)s+=50; if(folder)s+=28; if(titleGame)s+=15; if(pid==foreground)s+=18; if(port>=10000&&port<=65535)s+=7; if(port is 80 or 443)s-=5; if(n.Contains("launcher"))s-=15; if(n.Contains("helper"))s-=20; return Math.Clamp(s,0,100);
    }
    static bool IsPublic(string ip){if(!IPAddress.TryParse(ip,out var a)||a.AddressFamily!=AddressFamily.InterNetwork)return false;var b=a.GetAddressBytes();if(b[0]==10||b[0]==127||(b[0]==192&&b[1]==168)||(b[0]==169&&b[1]==254))return false;if(b[0]==172&&b[1]>=16&&b[1]<=31)return false;return true;}
    static async Task<string> RunAsync(string file,string args,int timeout){using var p=Process.Start(new ProcessStartInfo(file,args){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true,StandardOutputEncoding=Encoding.UTF8})??throw new InvalidOperationException("Could not start "+file);var o=p.StandardOutput.ReadToEndAsync();var e=p.StandardError.ReadToEndAsync();using var c=new CancellationTokenSource(timeout);try{await p.WaitForExitAsync(c.Token);}catch{try{p.Kill(true);}catch{}}return await o+"\n"+await e;}
}
