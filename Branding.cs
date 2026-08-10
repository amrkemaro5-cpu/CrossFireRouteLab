using System.Drawing.Drawing2D;

namespace CrossFireRouteLab;

public static class Brand
{
    public static Bitmap CreateLogo(int size)
    {
        var bmp=new Bitmap(size,size); using var g=Graphics.FromImage(bmp); g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(Color.FromArgb(3,6,14));
        var c=size/2f; var pts=new[]{new PointF(c,size*.04f),new PointF(size*.82f,size*.22f),new PointF(size*.72f,size*.78f),new PointF(c,size*.96f),new PointF(size*.28f,size*.78f),new PointF(size*.18f,size*.22f)};
        using var outer=new LinearGradientBrush(new RectangleF(0,0,size,size),Color.FromArgb(214,55,255),Color.FromArgb(0,220,255),LinearGradientMode.ForwardDiagonal); using var inner=new SolidBrush(Color.FromArgb(7,13,27)); g.FillPolygon(outer,pts); var innerPts=pts.Select(p=>new PointF(c+(p.X-c)*.82f,c+(p.Y-c)*.82f)).ToArray(); g.FillPolygon(inner,innerPts);
        using var glow=new Pen(Color.FromArgb(0,220,255),Math.Max(2,size*.035f)); g.DrawEllipse(glow,size*.19f,size*.13f,size*.62f,size*.62f);
        using var f=new Font("Arial",size*.25f,FontStyle.Bold,GraphicsUnit.Pixel); using var sf=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center}; using var textBrush=new LinearGradientBrush(new RectangleF(0,size*.30f,size,size*.30f),Color.White,Color.FromArgb(150,235,255),LinearGradientMode.Vertical); g.DrawString("GRL",f,textBrush,new RectangleF(0,size*.30f,size,size*.30f),sf);
        using var accent=new Pen(Color.FromArgb(177,77,255),Math.Max(1,size*.018f)); g.DrawLine(accent,size*.22f,size*.78f,size*.78f,size*.78f); return bmp;
    }
    public static Icon CreateIcon(){ using var bmp=CreateLogo(64); return Icon.FromHandle(bmp.GetHicon()); }
}
