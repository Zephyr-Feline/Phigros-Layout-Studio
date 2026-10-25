using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

namespace PhigrosEnhancer
{
    public class FreeLayer
    {
        public string Id = Guid.NewGuid().ToString("N"), Group = "", Name = "组件", Kind = "Text";
        public float X, Y, Width = 400, Height = 70, FontSize = 40;
        public string FontName = "Microsoft YaHei UI";
        public bool Bold, Italic;
        public float LineSpacing = 1;
        public string VerticalAlign = "Center";
        public string Text = "文字", Align = "Left", Difficulty = "IN";
        public int Level = 15, ColorArgb = Color.White.ToArgb();
        public bool Visible = true, Locked, Slanted, KeepAspect = true;
        public byte[] Image;
        public RectangleF Bounds { get { return new RectangleF(X, Y, Width, Height); } }
        public FreeLayer Copy() { return (FreeLayer)MemberwiseClone(); }
    }

    public class FreeProject
    {
        // .phifree format version. Independent from the .phipreview version.
        internal const int CurrentVersion = 1;
        public int Version = CurrentVersion, Width = 2740, Height = 1080;
        public int BackgroundColor = Color.FromArgb(35, 59, 71).ToArgb();
        public bool SoftBackground = true;
        public byte[] Background;
        public List<FreeLayer> Layers = new List<FreeLayer>();
        public FreeProject Copy()
        {
            return new FreeProject { Version=Version, Width=Width, Height=Height, BackgroundColor=BackgroundColor,
                SoftBackground=SoftBackground, Background=Background, Layers=Layers.Select(x=>x.Copy()).ToList() };
        }
        public void Validate()
        {
            if(Version!=CurrentVersion || Width<320 || Width>7680 || Height<180 || Height>4320) throw new InvalidDataException("自由排版工程版本或画布尺寸无效。");
            if(Layers==null || Layers.Count>200) throw new InvalidDataException("组件数量不能超过 200。");
            var ids=new HashSet<string>();
            foreach(var x in Layers)
            {
                if(x==null || string.IsNullOrEmpty(x.Id) || !ids.Add(x.Id) || !new[]{"Text","Image","Shape","Badge","Tag"}.Contains(x.Kind)) throw new InvalidDataException("组件数据无效。");
                if(new[]{x.X,x.Y,x.Width,x.Height,x.FontSize,x.LineSpacing}.Any(v=>float.IsNaN(v)||float.IsInfinity(v)) || x.Width<1 || x.Height<(x.Kind=="Badge"?FreeBadgeGeometry.MinimumHeight:1f) || x.Width>20000 || x.Height>20000 || Math.Abs(x.X)>20000 || Math.Abs(x.Y)>20000 || x.FontSize<1 || x.FontSize>1000 || x.LineSpacing<.5f || x.LineSpacing>4) throw new InvalidDataException("组件位置、尺寸或行距无效。");
                if(!Renderer.Colors.ContainsKey(x.Difficulty??"") || x.Level<1) throw new InvalidDataException("难度或等级无效。");
                if((x.Text??"").Length>4000 || (x.Name??"").Length>200 || string.IsNullOrWhiteSpace(x.FontName) || x.FontName.Length>200 || !new[]{"Left","Center","Right"}.Contains(x.Align) || !new[]{"Top","Center","Bottom"}.Contains(x.VerticalAlign)) throw new InvalidDataException("组件文字无效。");
            }
        }
        public void Save(string path)
        {
            Validate();
            // V1 readers require both container dimensions >= 1. A tiny badge is written with
            // its height raised to 1 and its canonical 128:108 width kept, so the stored box
            // stays readable by old V1 readers; the painted size is unchanged because the
            // painter already fits the badge inside the box. The editor's canonical box and
            // the file format stay as they are.
            var stored=Copy();
            foreach(var layer in stored.Layers)if(layer.Kind=="Badge"&&layer.Height<1)
            {layer.Width=FreeBadgeGeometry.CanonicalSize(layer.Width,layer.Height).Width;layer.Height=1;}
            AtomicFile.Write(path,delegate(string temp)
            {
                using(var w=XmlWriter.Create(temp,new XmlWriterSettings{Indent=true}))new XmlSerializer(typeof(FreeProject)).Serialize(w,stored);
            });
        }
        public static FreeProject Read(string path)
        {
            if(new FileInfo(path).Length>300L*1024*1024)throw new InvalidDataException("工程超过 300 MB。");
            using(var r=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))
            {var p=(FreeProject)new XmlSerializer(typeof(FreeProject)).Deserialize(r);Migrate(p);p.Validate();return p;}
        }
        // In-memory only: Read must never write the project back to disk. A migrated
        // project is written in the current format only when the user saves it.
        static void Migrate(FreeProject p)
        {
            if(p.Version==CurrentVersion)return;
            if(p.Version>CurrentVersion)
                throw new InvalidDataException("该工程由更新版本的软件创建（工程格式 V"+p.Version+
                    "，本程序支持到 V"+CurrentVersion+"）。请使用更新版本的 Phigros 自由排版打开。");
            // No pre-V1 .phifree was ever written; add explicit V1 -> Vn steps here.
            throw new InvalidDataException("不支持的旧工程格式（V"+p.Version+"，本程序支持 V"+CurrentVersion+"）。");
        }
        public static List<FreeLayer> Song(float x,float y,string title,int index)
        {
            string group=Guid.NewGuid().ToString("N");
            return new List<FreeLayer>{
                new FreeLayer{Group=group,Name=title+" · 信息底栏",Kind="Shape",X=x-15,Y=y+240,Width=520,Height=125,ColorArgb=Color.FromArgb(190,10,18,26).ToArgb(),Text="",Slanted=true,KeepAspect=false},
                new FreeLayer{Group=group,Name=title+" · 曲绘",Kind="Image",X=x,Y=y,Width=520,Height=250,Slanted=true,Text="导入曲绘"},
                new FreeLayer{Group=group,Name=title+" · 曲名",X=x+12,Y=y+250,Width=420,Height=49,FontSize=34,Text=title},
                new FreeLayer{Group=group,Name=title+" · 曲师",X=x+12,Y=y+295,Width=420,Height=35,FontSize=23,Text="音乐作者"},
                new FreeLayer{Group=group,Name=title+" · 谱师",X=x+12,Y=y+329,Width=420,Height=32,FontSize=21,Text="谱面作者",ColorArgb=Color.FromArgb(210,225,235).ToArgb()},
                new FreeLayer{Group=group,Name=title+" · 难度",Kind="Badge",X=x+419,Y=y+184,Width=104,Height=87.75f,Text="",Difficulty=index==2?"AT":"IN",Level=index==2?16:15}
            };
        }
        public static FreeProject FivePoint()
        {
            var p=new FreeProject();
            p.Layers.Add(new FreeLayer{Kind="Shape",Name="左侧装饰块",X=0,Y=0,Width=640,Height=1080,Slanted=true,Locked=true,ColorArgb=Color.FromArgb(80,10,20,30).ToArgb(),Text=""});
            p.Layers.Add(new FreeLayer{Name="预选标题",X=455,Y=56,Width=720,Height=85,FontSize=55,Text="预选曲组"});
            string logo=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"IMAGE","Phigros.png");
            if(File.Exists(logo))p.Layers.Add(new FreeLayer{Kind="Image",Name="Phigros 标志",X=95,Y=57,Width=310,Height=85,Image=File.ReadAllBytes(logo),Text=""});
            else p.Layers.Add(new FreeLayer{Name="Phigros 标志",X=95,Y=57,Width=310,Height=85,FontSize=55,Text="Phigros"});
            float[] xs={170,170,1110,2050,2050},ys={190,635,410,190,635};
            for(int i=0;i<5;i++)p.Layers.AddRange(Song(xs[i],ys[i],i==2?"自制曲目":"曲目 "+(i<2?i+1:i),i));
            return p;
        }
    }

    static class FreeFonts
    {
        static readonly PrivateFontCollection local = new PrivateFontCollection();
        static readonly Dictionary<string,FontFamily> families = new Dictionary<string,FontFamily>(StringComparer.OrdinalIgnoreCase);
        static FreeFonts()
        {
            foreach(var family in FontFamily.Families)families[family.Name]=family;
            string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"FONT");
            if(Directory.Exists(directory))foreach(var path in Directory.GetFiles(directory).Where(p=>new[]{".ttf",".otf"}.Contains(Path.GetExtension(p).ToLowerInvariant())))
                try{local.AddFontFile(path);}catch(ArgumentException){}
            RegisterLocal();
        }
        static void RegisterLocal(){foreach(var family in local.Families)families[family.Name]=family;}
        public static string[] Names {get{return families.Keys.OrderBy(n=>n,StringComparer.CurrentCultureIgnoreCase).ToArray();}}
        public static bool Available(string name){return !string.IsNullOrEmpty(name)&&families.ContainsKey(name);}
        public static Font Create(FreeLayer layer)
        {
            FontFamily family;
            if(!families.TryGetValue(layer.FontName??"",out family)&&!families.TryGetValue("Microsoft YaHei UI",out family))family=FontFamily.GenericSansSerif;
            FontStyle style=(layer.Bold?FontStyle.Bold:FontStyle.Regular)|(layer.Italic?FontStyle.Italic:FontStyle.Regular);
            if(!family.IsStyleAvailable(style))style=new[]{FontStyle.Regular,FontStyle.Bold,FontStyle.Italic,FontStyle.Bold|FontStyle.Italic}.First(s=>family.IsStyleAvailable(s));
            return new Font(family,layer.FontSize,style,GraphicsUnit.Pixel);
        }
        public static string Import(string path)
        {
            string name;
            using(var probe=new PrivateFontCollection()){probe.AddFontFile(path);if(probe.Families.Length==0)throw new InvalidDataException("无法读取此字体。");name=probe.Families[0].Name;}
            string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"FONT");Directory.CreateDirectory(directory);
            string destination=Path.Combine(directory,Path.GetFileName(path));
            if(!string.Equals(Path.GetFullPath(path),Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase))
            {if(File.Exists(destination))destination=Path.Combine(directory,Path.GetFileNameWithoutExtension(path)+"_"+Guid.NewGuid().ToString("N")+Path.GetExtension(path));File.Copy(path,destination);}
            local.AddFontFile(destination);RegisterLocal();return name;
        }
    }

    sealed class FreePainter : IDisposable
    {
        readonly Dictionary<byte[],Bitmap> images=new Dictionary<byte[],Bitmap>();
        byte[] softSource; Bitmap softImage, blendedImage;
        string blendKey="";
        public bool HoldBackground;
        public Bitmap Get(byte[] data)
        {if(data==null||data.Length==0)return null;Bitmap b;if(!images.TryGetValue(data,out b)){b=Renderer.Decode(data);images.Add(data,b);}return b;}
        public void Prepare(FreeProject p)
        {
            var wanted=new HashSet<byte[]>(p.Layers.Where(x=>x.Image!=null).Select(x=>x.Image));if(p.Background!=null)wanted.Add(p.Background);
            foreach(var key in images.Keys.Where(k=>!wanted.Contains(k)).ToArray()){images[key].Dispose();images.Remove(key);}
            foreach(var key in wanted)Get(key);
            if(softSource!=p.Background){if(softImage!=null)softImage.Dispose();softImage=null;softSource=p.Background;}
        }
        // Blend before blurring: cards never carry hard-edged background rectangles.
        Bitmap BlendedBackground(FreeProject p)
        {
            var artwork=p.Layers.Where(l=>l.Visible&&l.Kind=="Image"&&l.Slanted&&l.Image!=null).ToArray();
            string key=p.Width+":"+p.Height+":"+p.BackgroundColor+":"+string.Join(";",artwork.Select(l=>l.Id+":"+System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l.Image)+":"+l.X+":"+l.Y+":"+l.Width+":"+l.Height));
            if(blendedImage!=null&&(key==blendKey||HoldBackground))return blendedImage;
            if(blendedImage!=null)blendedImage.Dispose();blendKey=key;
            int width=128,height=Math.Max(16,Math.Min(192,(int)(128f*p.Height/p.Width)));
            blendedImage=new Bitmap(width,height);var samples=new List<Bitmap>();
            try
            {
                foreach(var art in artwork){var small=new Bitmap(12,12);using(var g=Graphics.FromImage(small))Cover(g,Get(art.Image),new RectangleF(0,0,12,12));samples.Add(small);}
                Color baseColor=Color.FromArgb(p.BackgroundColor);
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                {
                    float px=(x+.5f)*p.Width/width,py=(y+.5f)*p.Height/height;
                    double total=.16,r=baseColor.R*total,green=baseColor.G*total,b=baseColor.B*total;
                    for(int i=0;i<artwork.Length;i++)
                    {
                        var art=artwork[i];double dx=(px-art.X-art.Width/2)/Math.Max(p.Width*.24,art.Width),dy=(py-art.Y-art.Height/2)/Math.Max(p.Height*.48,art.Height);
                        double weight=Math.Exp(-(dx*dx+dy*dy)*1.5);
                        int sx=(int)Math.Max(0,Math.Min(11,6+dx*4)),sy=(int)Math.Max(0,Math.Min(11,6+dy*4));
                        Color sample=samples[i].GetPixel(sx,sy);r+=sample.R*weight;green+=sample.G*weight;b+=sample.B*weight;total+=weight;
                    }
                    blendedImage.SetPixel(x,y,Color.FromArgb((int)(r/total),(int)(green/total),(int)(b/total)));
                }
                Blur(blendedImage);
            }
            finally{foreach(var sample in samples)sample.Dispose();}
            return blendedImage;
        }
        static void Blur(Bitmap b)
        {
            int w=b.Width,h=b.Height;var source=new Color[w*h];var temp=new Color[w*h];
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)source[y*w+x]=b.GetPixel(x,y);
            double[] weights=Enumerable.Range(-6,13).Select(i=>Math.Exp(-i*i/18.0)).ToArray();double sum=weights.Sum();
            for(int axis=0;axis<2;axis++)
            {
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                {
                    double r=0,g=0,blue=0;
                    for(int j=-6;j<=6;j++){int sx=axis==0?Math.Max(0,Math.Min(w-1,x+j)):x,sy=axis==1?Math.Max(0,Math.Min(h-1,y+j)):y;Color c=source[sy*w+sx];double f=weights[j+6];r+=c.R*f;g+=c.G*f;blue+=c.B*f;}
                    temp[y*w+x]=Color.FromArgb((int)(r/sum),(int)(g/sum),(int)(blue/sum));
                }
                var swap=source;source=temp;temp=swap;
            }
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)b.SetPixel(x,y,source[y*w+x]);
        }
        public static PointF[] Polygon(RectangleF r,bool slanted)
        {
            float dx=slanted?Math.Min(r.Height*.268f,r.Width*.4f):0;
            return new[]{new PointF(r.Left+dx,r.Top),new PointF(r.Right,r.Top),new PointF(r.Right-dx,r.Bottom),new PointF(r.Left,r.Bottom)};
        }
        static void Cover(Graphics g,Image img,RectangleF r)
        {
            float scale=Math.Max(r.Width/img.Width,r.Height/img.Height),sw=r.Width/scale,sh=r.Height/scale;
            using(var attributes=new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(img,new[]{new PointF(r.Left,r.Top),new PointF(r.Right,r.Top),new PointF(r.Left,r.Bottom)},new RectangleF((img.Width-sw)/2,(img.Height-sh)/2,sw,sh),GraphicsUnit.Pixel,attributes);
            }
        }
        static void Text(Graphics g,FreeLayer c)
        {
            using(var font=FreeFonts.Create(c))
            using(var brush=new SolidBrush(Color.FromArgb(c.ColorArgb)))
            using(var format=new StringFormat(StringFormat.GenericTypographic){FormatFlags=StringFormatFlags.NoWrap|StringFormatFlags.MeasureTrailingSpaces})
            {
                var lines=WrapLines(g,c.Text??"",font,c.Width,format);
                float lineHeight=font.GetHeight(g),step=lineHeight*c.LineSpacing,total=lineHeight+Math.Max(0,lines.Count-1)*step;
                float top=c.Y+(c.VerticalAlign=="Bottom"?Math.Max(0,c.Height-total):c.VerticalAlign=="Center"?Math.Max(0,(c.Height-total)/2):0);
                var saved=g.Save();g.SetClip(c.Bounds,CombineMode.Intersect);
                foreach(var line in lines)
                {
                    float width=g.MeasureString(line,font,new PointF(0,0),format).Width;
                    float left=c.X+(c.Align=="Right"?Math.Max(0,c.Width-width):c.Align=="Center"?Math.Max(0,(c.Width-width)/2):0);
                    g.DrawString(line,font,brush,new PointF(left,top),format);top+=step;
                    if(top>=c.Y+c.Height)break;
                }
                g.Restore(saved);
            }
        }
        internal static List<string> WrapLines(Graphics g,string text,Font font,float width,StringFormat format)
        {
            var result=new List<string>();
            foreach(string paragraph in text.Replace("\r\n","\n").Replace('\r','\n').Split('\n'))
            {
                if(paragraph.Length==0){result.Add("");continue;}
                int[] starts=StringInfo.ParseCombiningCharacters(paragraph);int offset=0;
                while(offset<starts.Length)
                {
                    int low=1,high=starts.Length-offset,best=1;
                    while(low<=high)
                    {
                        int take=(low+high)/2,end=offset+take==starts.Length?paragraph.Length:starts[offset+take];
                        string candidate=paragraph.Substring(starts[offset],end-starts[offset]);
                        if(g.MeasureString(candidate,font,new PointF(0,0),format).Width<=width){best=take;low=take+1;}else high=take-1;
                    }
                    int finish=offset+best==starts.Length?paragraph.Length:starts[offset+best];
                    result.Add(paragraph.Substring(starts[offset],finish-starts[offset]));offset+=best;
                }
            }
            return result;
        }
        public void Draw(Graphics g,FreeProject p,bool transparent)
        {
            g.SmoothingMode=SmoothingMode.None;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;g.InterpolationMode=InterpolationMode.HighQualityBicubic;
            if(!transparent)
            {
                using(var brush=new LinearGradientBrush(new Rectangle(0,0,p.Width,p.Height),Color.FromArgb(p.BackgroundColor),Color.FromArgb(9,20,31),25))g.FillRectangle(brush,0,0,p.Width,p.Height);
                var bg=Get(p.Background);
                if(bg!=null)
                {
                    if(p.SoftBackground&&softImage==null)
                    {
                        softImage=new Bitmap(128,Math.Max(16,Math.Min(192,(int)(128f*bg.Height/bg.Width))));
                        using(var sg=Graphics.FromImage(softImage)){sg.InterpolationMode=InterpolationMode.HighQualityBicubic;sg.DrawImage(bg,0,0,softImage.Width,softImage.Height);}Blur(softImage);
                    }
                    Cover(g,p.SoftBackground?softImage:bg,new RectangleF(0,0,p.Width,p.Height));
                    if(p.SoftBackground)using(var shade=new SolidBrush(Color.FromArgb(105,4,12,20)))g.FillRectangle(shade,0,0,p.Width,p.Height);
                }
                else if(p.SoftBackground)
                {
                    Cover(g,BlendedBackground(p),new RectangleF(0,0,p.Width,p.Height));
                    using(var shade=new SolidBrush(Color.FromArgb(105,4,12,20)))g.FillRectangle(shade,0,0,p.Width,p.Height);
                }
            }
            g.SmoothingMode=SmoothingMode.AntiAlias;
            foreach(var c in p.Layers.Where(l=>l.Visible))
            {
                if(c.Kind=="Text")Text(g,c);
                else if(c.Kind=="Tag")
                {
                    g.FillPolygon(Brushes.White,Polygon(c.Bounds,true));
                    var caption=c.Copy();float padding=Math.Min(c.Width*.2f,c.Height*.268f+4);
                    caption.X+=padding;caption.Width=Math.Max(1,c.Width-2*padding);Text(g,caption);
                }
                else if(c.Kind=="Shape")using(var brush=new SolidBrush(Color.FromArgb(c.ColorArgb)))g.FillPolygon(brush,Polygon(c.Bounds,c.Slanted));
                else if(c.Kind=="Image")
                {
                    var state=g.Save();using(var path=new GraphicsPath()){path.AddPolygon(Polygon(c.Bounds,c.Slanted));g.SetClip(path,CombineMode.Intersect);}
                    var img=Get(c.Image);
                    if(img!=null)
                    {
                        if(c.Slanted)Cover(g,img,c.Bounds);
                        else {float fit=Math.Min(c.Width/img.Width,c.Height/img.Height);g.DrawImage(img,c.X+(c.Width-img.Width*fit)/2,c.Y+(c.Height-img.Height*fit)/2,img.Width*fit,img.Height*fit);}
                    }
                    else
                    {
                        using(var fill=new LinearGradientBrush(c.Bounds,Color.FromArgb(69,99,116),Color.FromArgb(27,49,71),35))g.FillRectangle(fill,c.Bounds);
                        var caption=c.Copy();caption.Kind="Text";caption.Text="导入曲绘";caption.Align="Center";caption.FontSize=Math.Min(c.Height/6,40);Text(g,caption);
                    }
                    g.Restore(state);
                }
                else if(c.Kind=="Badge")
                {
                    var state=g.Save();float fit=FreeBadgeGeometry.FitScale(c.Width,c.Height);
                    g.TranslateTransform(c.X,c.Y);g.ScaleTransform(fit,fit);
                    Renderer.Draw(g,new Project{Labels=new List<ChartLabel>{new ChartLabel{BadgeX=0,BadgeY=0,Difficulty=c.Difficulty,Level=c.Level,ShowAuthor=false}}});g.Restore(state);
                }
            }
        }
        public Bitmap Render(FreeProject p,bool transparent)
        {Prepare(p);var b=new Bitmap(p.Width,p.Height,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(b)){g.Clear(Color.Transparent);Draw(g,p,transparent);}return b;}
        public void Dispose(){foreach(var b in images.Values)b.Dispose();images.Clear();if(softImage!=null)softImage.Dispose();if(blendedImage!=null)blendedImage.Dispose();}
    }

    // Milestone 10A/10B: pure move-snapping math for the free-layout guides.
    internal struct SnapResult
    {
        public float DeltaX, DeltaY;
        public bool HasVerticalGuide; public float VerticalGuideX;
        public bool HasHorizontalGuide; public float HorizontalGuideY;
    }
    internal static class FreeSnap
    {
        internal const float ThresholdScreenPixels = 8f;
        // Snaps the moved bounds against an explicit guide list per axis. Candidate order is the
        // caller's guide order and then features start -> centre -> end; ties keep the first
        // candidate (replacement is strictly smaller), so the caller owns the priority and the
        // result is deterministic. Canvas guides are passed first, stationary layers after.
        internal static SnapResult SnapMoveToGuides(RectangleF bounds,float dx,float dy,
            IList<float> guidesX,IList<float> guidesY,float thresholdX,float thresholdY)
        {
            var result=new SnapResult{DeltaX=dx,DeltaY=dy};
            float[] featuresX={bounds.X+dx,bounds.X+dx+bounds.Width/2f,bounds.X+dx+bounds.Width};
            float bestX=float.MaxValue,correctionX=0f,guideX=0f;
            if(guidesX!=null)for(int gi=0;gi<guidesX.Count;gi++)for(int fi=0;fi<featuresX.Length;fi++)
            {float c=guidesX[gi]-featuresX[fi],a=Math.Abs(c);if(a<=thresholdX&&a<bestX){bestX=a;correctionX=c;guideX=guidesX[gi];}}
            if(bestX<float.MaxValue){result.DeltaX=dx+correctionX;result.HasVerticalGuide=true;result.VerticalGuideX=guideX;}
            float[] featuresY={bounds.Y+dy,bounds.Y+dy+bounds.Height/2f,bounds.Y+dy+bounds.Height};
            float bestY=float.MaxValue,correctionY=0f,guideY=0f;
            if(guidesY!=null)for(int gi=0;gi<guidesY.Count;gi++)for(int fi=0;fi<featuresY.Length;fi++)
            {float c=guidesY[gi]-featuresY[fi],a=Math.Abs(c);if(a<=thresholdY&&a<bestY){bestY=a;correctionY=c;guideY=guidesY[gi];}}
            if(bestY<float.MaxValue){result.DeltaY=dy+correctionY;result.HasHorizontalGuide=true;result.HorizontalGuideY=guideY;}
            return result;
        }
        // Milestone 10A wrapper: canvas guides only (0 / centre / edge on each axis), unchanged.
        internal static SnapResult SnapMove(RectangleF bounds,float dx,float dy,float canvasWidth,float canvasHeight,float thresholdX,float thresholdY)
        {
            return SnapMoveToGuides(bounds,dx,dy,
                new float[]{0f,canvasWidth/2f,canvasWidth},new float[]{0f,canvasHeight/2f,canvasHeight},
                thresholdX,thresholdY);
        }
    }

    // Milestone 8B: single source of truth for badge (128 x 108) geometry.
    internal static class FreeBadgeGeometry
    {
        internal const float ReferenceWidth=128f;
        internal const float ReferenceHeight=108f;
        internal const float MinimumHeight=108f/128f;
        internal static SizeF BoundedSize(float scale)
        {
            scale=Math.Max(1f/ReferenceWidth,Math.Min(20000f/ReferenceWidth,scale));
            return new SizeF(ReferenceWidth*scale,ReferenceHeight*scale);
        }
        // Aspect-fit scale shared by the badge painter and by canonicalisation.
        internal static float FitScale(float width,float height)
        {
            return Math.Min(width/ReferenceWidth,height/ReferenceHeight);
        }
        // Canonical badge size for a container box: the limiting axis is kept, the other shrinks.
        internal static SizeF CanonicalSize(float width,float height)
        {
            float fit=FitScale(width,height);
            return new SizeF(ReferenceWidth*fit,ReferenceHeight*fit);
        }
    }

    class FreeLayoutForm : Form
    {
        FreeProject project=FreeProject.FivePoint();readonly FreePainter painter=new FreePainter();
        readonly Stack<FreeProject> undo=new Stack<FreeProject>(),redo=new Stack<FreeProject>();
        readonly PreviewPanel canvas=new PreviewPanel();readonly ListBox layers=new ListBox();readonly Label status=new Label();
        readonly TextBox name=new TextBox(),text=new TextBox();
        readonly NumericUpDown x=Number(-20000,20000),y=Number(-20000,20000),w=Number(1,20000),h=Number(1,20000),fontSize=Number(1,1000),level=Number(1,int.MaxValue),cw=Number(320,7680),ch=Number(180,4320);
        readonly CheckBox visible=new CheckBox{Text="显示",AutoSize=true},locked=new CheckBox{Text="锁定",AutoSize=true},slanted=new CheckBox{Text="斜切",AutoSize=true},aspect=new CheckBox{Text="等比缩放",AutoSize=true},groupMove=new CheckBox{Text="整组移动 / 缩放",Checked=true,AutoSize=true},soft=new CheckBox{Text="背景柔化",AutoSize=true};
        readonly ComboBox diff=new ComboBox(),align=new ComboBox(),fontChoice=new ComboBox(),verticalAlign=new ComboBox();
        readonly NumericUpDown lineSpacing=Number(.5m,4m);
        readonly CheckBox bold=new CheckBox{Text="粗体",AutoSize=true},italic=new CheckBox{Text="斜体",AutoSize=true};
        readonly Label fontNotice=new Label{Height=46,ForeColor=Color.DimGray};
        readonly Button color=new Button{Text="文字 / 色块颜色",Height=31};
        bool filling,dirty,dragging,resizing,dragRecorded;RectangleF view;LogicalViewportTransform previewTransform;PointF dragStart;List<FreeLayer> dragOriginal;RectangleF dragBounds;string projectPath="";
        float? activeVerticalGuide,activeHorizontalGuide;
        readonly List<float> snapGuidesX=new List<float>(),snapGuidesY=new List<float>();
        // Milestone 10B.1: Alt temporarily bypasses every move guide. The live state comes from
        // WinForms; the delegate keeps that query and lets the self test drive it, because the
        // test cannot press a real key.
        Func<bool> altHeld=delegate{return (Control.ModifierKeys&Keys.Alt)==Keys.Alt;};
        bool SnapBypassed(){return altHeld!=null&&altHeld();}
        string root=AppDomain.CurrentDomain.BaseDirectory;
        FreeLayer Current {get {return layers.SelectedIndex>=0?project.Layers[project.Layers.Count-1-layers.SelectedIndex]:null;}}
        public FreeLayoutForm(string path=null)
        {
            Text="Phigros · 自由排版";Size=new Size(1450,920);MinimumSize=new Size(1060,720);StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",9);KeyPreview=true;
            // 96 DPI is the design baseline of the hand-written pixel layout below.
            AutoScaleDimensions=new SizeF(96F,96F);AutoScaleMode=AutoScaleMode.Dpi;
            level.DecimalPlaces=cw.DecimalPlaces=ch.DecimalPlaces=0;
            var toolbar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=94,Padding=new Padding(8),BackColor=Color.White};
            AddButton(toolbar,"新建五点布局",NewFive);AddButton(toolbar,"打开工程",Open);AddButton(toolbar,"保存工程",Save);AddButton(toolbar,"导出 PNG",()=>Export(false));AddButton(toolbar,"透明 PNG",()=>Export(true));
            AddButton(toolbar,"添加曲目",AddSong);AddButton(toolbar,"添加图片",()=>Add("Image"));AddButton(toolbar,"添加文字",()=>Add("Text"));AddButton(toolbar,"添加色块",()=>Add("Shape"));AddButton(toolbar,"添加难度",()=>Add("Badge"));
            AddButton(toolbar,"Original 标签",AddOriginal);
            AddButton(toolbar,"设置背景",SetBackground);AddButton(toolbar,"背景颜色",BackgroundColor);AddButton(toolbar,"清除背景图",()=>{Remember();project.Background=null;Changed();});
            var right=new Panel{Dock=DockStyle.Right,Width=316,Padding=new Padding(12),AutoScroll=true,BackColor=Color.White};
            var props=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1};props.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));right.Controls.Add(props);
            Row(props,new Label{Text="组件 / 图层（上方显示在前）",Height=28,Font=new Font(Font,FontStyle.Bold)});
            layers.Height=220;layers.SelectionMode=SelectionMode.MultiExtended;layers.IntegralHeight=false;layers.SelectedIndexChanged+=delegate{Fill();canvas.Invalidate();};Row(props,layers);
            Buttons(props,new[]{"上移一层","下移一层","复制"},new Action[]{()=>Reorder(1),()=>Reorder(-1),Duplicate});
            Buttons(props,new[]{"组合","取消组合","删除"},new Action[]{Group,Ungroup,Delete});Row(props,groupMove);
            Buttons(props,new[]{"隐藏整组","显示整组"},new Action[]{()=>SetGroupVisible(false),()=>SetGroupVisible(true)});
            Field(props,"组件名称",name);name.MaxLength=200;
            var flags=new FlowLayoutPanel{Height=30};flags.Controls.Add(visible);flags.Controls.Add(locked);flags.Controls.Add(slanted);Row(props,flags);Row(props,aspect);
            Coordinates(props,"位置 X / Y",x,y);Coordinates(props,"宽度 / 高度",w,h);
            text.Multiline=true;text.Height=62;text.MaxLength=4000;Field(props,"文字内容",text);Field(props,"文字大小",fontSize);
            fontChoice.DropDownStyle=ComboBoxStyle.DropDownList;fontChoice.MaxDropDownItems=16;fontChoice.DropDownWidth=350;fontChoice.Items.AddRange(FreeFonts.Names);Field(props,"字体（系统字体及 FONT 目录）",fontChoice);
            var styles=new FlowLayoutPanel{Height=30};styles.Controls.Add(bold);styles.Controls.Add(italic);Row(props,styles);
            var importFont=new Button{Text="载入本地字体（TTF / OTF）",Height=32};importFont.Click+=delegate{ImportFont();};Row(props,importFont);Row(props,fontNotice);
            align.DropDownStyle=ComboBoxStyle.DropDownList;align.Items.AddRange(new object[]{"左对齐","居中","右对齐"});Field(props,"水平对齐",align);
            verticalAlign.DropDownStyle=ComboBoxStyle.DropDownList;verticalAlign.Items.AddRange(new object[]{"顶部对齐","垂直居中","底部对齐"});Field(props,"垂直对齐",verticalAlign);
            lineSpacing.DecimalPlaces=2;lineSpacing.Increment=.1m;lineSpacing.Value=1;Field(props,"行间距（倍数，1 为标准行距）",lineSpacing);
            color.Click+=delegate{ChooseColor();};Row(props,color);
            diff.DropDownStyle=ComboBoxStyle.DropDownList;diff.Items.AddRange(new object[]{"EZ","HD","IN","AT"});Field(props,"难度类型",diff);Field(props,"难度等级",level);
            var replace=new Button{Text="替换曲绘 / 图片（双击图片也可）",Height=34};replace.Click+=delegate{ReplaceImage();};Row(props,replace);
            Coordinates(props,"画布宽 / 高（像素）",cw,ch);var apply=new Button{Text="应用画布尺寸（内容等比适配）",Height=34};apply.Click+=delegate{ResizeCanvas((int)cw.Value,(int)ch.Value);};Row(props,apply);
            soft.Text="统一模糊背景 / 自动融合曲绘";
            Row(props,soft);soft.CheckedChanged+=delegate{if(!filling){Remember();project.SoftBackground=soft.Checked;Changed();}};
            Row(props,new Label{Text="指定背景图优先；未指定时自动融合曲绘。\n整张画布统一模糊，拖动结束后更新融合。",Height=46,ForeColor=Color.DimGray});
            Row(props,new Label{Text="拖动移动；选框右下角缩放。\n取消「整组移动」可单独调整文字、曲绘。\n图层列表 Ctrl 多选后可组合；锁定项不随组移动。\nCtrl+Z 撤销，Ctrl+Y 重做，Ctrl+D 复制。\n方向键微调，Shift 加速；Ctrl+S 保存工程。",Height=115,ForeColor=Color.DimGray});
            canvas.Dock=DockStyle.Fill;canvas.BackColor=Color.FromArgb(23,28,37);canvas.Paint+=PaintCanvas;canvas.MouseDown+=Down;canvas.MouseMove+=MovePointer;canvas.MouseUp+=Up;
            canvas.MouseCaptureChanged+=delegate{dragging=false;painter.HoldBackground=false;ClearSnapGuides();canvas.Invalidate();};canvas.MouseDoubleClick+=delegate{ReplaceImage();};canvas.KeyDown+=CanvasKey;
            status.Dock=DockStyle.Bottom;status.Height=32;status.Padding=new Padding(10,6,0,0);
            Controls.Add(canvas);Controls.Add(right);Controls.Add(status);Controls.Add(toolbar);
            foreach(var box in new[]{name,text})box.TextChanged+=Edit;
            foreach(var n in new[]{x,y,w,h,fontSize,level})n.ValueChanged+=Edit;
            foreach(var b in new[]{visible,locked,slanted,aspect})b.CheckedChanged+=Edit;
            diff.SelectedIndexChanged+=Edit;align.SelectedIndexChanged+=Edit;
            fontChoice.SelectedIndexChanged+=Edit;bold.CheckedChanged+=Edit;italic.CheckedChanged+=Edit;
            verticalAlign.SelectedIndexChanged+=Edit;lineSpacing.ValueChanged+=Edit;
            KeyDown+=KeysPressed;FormClosing+=delegate(object s,FormClosingEventArgs e){if(!Discard())e.Cancel=true;};
            AllowDrop=true;DragEnter+=delegate(object s,DragEventArgs e){if(e.Data.GetDataPresent(DataFormats.FileDrop))e.Effect=DragDropEffects.Copy;};
            DragDrop+=delegate(object s,DragEventArgs e){var paths=(string[])e.Data.GetData(DataFormats.FileDrop);foreach(string f in paths){if(f.EndsWith(".phifree",StringComparison.OrdinalIgnoreCase)){LoadProject(f);break;}ImportImage(f);}};
            RefreshLayers(null);status.Text="五点布局：左二、中一、右二。双击曲绘导入图片，拖动整首曲目调整位置。";
            if(path!=null)Shown+=delegate{LoadProject(path);};
        }
        static NumericUpDown Number(decimal min,decimal max){return new NumericUpDown{Minimum=min,Maximum=max,DecimalPlaces=1,Width=130};}
        static void AddButton(Control p,string title,Action action){var b=new Button{Text=title,Width=118,Height=32,Margin=new Padding(0,0,6,5)};b.Click+=delegate{action();};p.Controls.Add(b);}
        static void Row(TableLayoutPanel p,Control c){c.Dock=DockStyle.Top;c.Margin=new Padding(0,0,0,7);p.RowCount++;p.RowStyles.Add(new RowStyle(SizeType.AutoSize));p.Controls.Add(c,0,p.RowCount-1);}
        static void Field(TableLayoutPanel p,string label,Control c){Row(p,new Label{Text=label,Height=22});Row(p,c);}
        static void Coordinates(TableLayoutPanel p,string label,Control a,Control b){var row=new FlowLayoutPanel{Height=33,WrapContents=false};row.Controls.Add(a);row.Controls.Add(b);Field(p,label,row);}
        static void Buttons(TableLayoutPanel p,string[] labels,Action[] actions){var row=new FlowLayoutPanel{Height=37,WrapContents=false};for(int i=0;i<labels.Length;i++){var action=actions[i];var b=new Button{Text=labels[i],Width=labels.Length==3?88:132,Height=31};b.Click+=delegate{action();};row.Controls.Add(b);}Row(p,row);}
        void Error(Exception e){MessageBox.Show(this,e.GetBaseException().Message,"自由排版",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        void Remember(){undo.Push(project.Copy());redo.Clear();if(undo.Count>60){var keep=undo.Take(50).Reverse().ToArray();undo.Clear();foreach(var p in keep)undo.Push(p);}}
        void Changed(){dirty=true;Text="Phigros · 自由排版 *";painter.Prepare(project);canvas.Invalidate();}
        List<FreeLayer> Selected(){return layers.SelectedIndices.Cast<int>().Select(i=>project.Layers[project.Layers.Count-1-i]).ToList();}
        List<FreeLayer> Targets()
        {
            var selected=Selected();var groups=new HashSet<string>(selected.Where(l=>!string.IsNullOrEmpty(l.Group)).Select(l=>l.Group));
            return project.Layers.Where(l=>!l.Locked&&(selected.Contains(l)||(groupMove.Checked&&groups.Contains(l.Group)))).ToList();
        }
        static RectangleF SelectionBounds(IEnumerable<FreeLayer> items)
        {var all=items.ToArray();if(all.Length==0)return RectangleF.Empty;return RectangleF.FromLTRB(all.Min(l=>l.X),all.Min(l=>l.Y),all.Max(l=>l.X+l.Width),all.Max(l=>l.Y+l.Height));}
        // Milestone 10B: canvas guides first (so 10A keeps its tie priority), then every visible
        // stationary layer in project.Layers order. A locked layer is still a valid reference; a
        // hidden layer never contributes; the moving set is excluded by real identity (Id), never
        // by comparing bounds, so a drag can never snap to itself.
        internal static void CollectSnapGuides(FreeProject p,IEnumerable<FreeLayer> moving,List<float> guidesX,List<float> guidesY)
        {
            guidesX.Clear();guidesY.Clear();
            guidesX.Add(0f);guidesX.Add(p.Width/2f);guidesX.Add(p.Width);
            guidesY.Add(0f);guidesY.Add(p.Height/2f);guidesY.Add(p.Height);
            var movingIds=new HashSet<string>();
            if(moving!=null)foreach(var layer in moving)if(layer!=null)movingIds.Add(layer.Id);
            foreach(var layer in p.Layers)
            {
                if(layer==null||!layer.Visible||movingIds.Contains(layer.Id))continue;
                guidesX.Add(layer.X);guidesX.Add(layer.X+layer.Width/2f);guidesX.Add(layer.X+layer.Width);
                guidesY.Add(layer.Y);guidesY.Add(layer.Y+layer.Height/2f);guidesY.Add(layer.Y+layer.Height);
            }
        }
        void RefreshLayers(string id)
        {
            filling=true;layers.BeginUpdate();layers.Items.Clear();foreach(var c in project.Layers.AsEnumerable().Reverse())layers.Items.Add((c.Visible?"":"[隐藏] ")+(c.Locked?"[锁] ":"")+c.Name);layers.EndUpdate();
            int index=project.Layers.FindIndex(l=>l.Id==id);if(index>=0)layers.SelectedIndex=project.Layers.Count-1-index;
            cw.Value=project.Width;ch.Value=project.Height;soft.Checked=project.SoftBackground;filling=false;Fill();canvas.Invalidate();
        }
        void Fill()
        {
            if(filling)return;filling=true;var c=Current;
            foreach(Control control in new Control[]{name,text,x,y,w,h,fontSize,fontChoice,bold,italic,verticalAlign,lineSpacing,visible,locked,slanted,aspect,align,diff,level,color})control.Enabled=c!=null;
            if(c!=null)
            {
                h.Minimum=c.Kind=="Badge"?(decimal)FreeBadgeGeometry.MinimumHeight:1m;
                w.DecimalPlaces=h.DecimalPlaces=c.Kind=="Badge"?5:1;
                name.Text=c.Name;text.Text=c.Text;x.Value=(decimal)c.X;y.Value=(decimal)c.Y;w.Value=(decimal)c.Width;h.Value=(decimal)c.Height;fontSize.Value=(decimal)c.FontSize;
                visible.Checked=c.Visible;locked.Checked=c.Locked;slanted.Checked=c.Slanted;aspect.Checked=c.KeepAspect;align.SelectedIndex=Array.IndexOf(new[]{"Left","Center","Right"},c.Align);diff.SelectedItem=c.Difficulty;level.Value=c.Level;
                verticalAlign.SelectedIndex=Array.IndexOf(new[]{"Top","Center","Bottom"},c.VerticalAlign);lineSpacing.Value=(decimal)c.LineSpacing;
                if(!fontChoice.Items.Contains(c.FontName))fontChoice.Items.Add(c.FontName);fontChoice.SelectedItem=c.FontName;bold.Checked=c.Bold;italic.Checked=c.Italic;
                fontChoice.Enabled=bold.Enabled=italic.Enabled=c.Kind=="Text"||c.Kind=="Tag";
                verticalAlign.Enabled=lineSpacing.Enabled=c.Kind=="Text"||c.Kind=="Tag";
                fontNotice.Text=FreeFonts.Available(c.FontName)?"预览与 PNG 导出使用相同字体。\n字体不支持的粗斜体将使用可用字形。":"缺少字体："+c.FontName+"\n暂用微软雅黑；可载入原字体恢复。";
                text.Enabled=fontSize.Enabled=align.Enabled=c.Kind=="Text"||c.Kind=="Tag";diff.Enabled=level.Enabled=c.Kind=="Badge";slanted.Enabled=c.Kind=="Image"||c.Kind=="Shape";color.Enabled=c.Kind=="Text"||c.Kind=="Shape"||c.Kind=="Tag";
                // Milestone 8B: a badge is always 128:108, so the aspect toggle would be misleading.
                aspect.Checked=c.Kind=="Badge"||c.KeepAspect;aspect.Enabled=c.Kind!="Badge";
            }
            filling=false;
        }
        void Edit(object sender,EventArgs e)
        {
            if(filling||Current==null)return;Remember();var c=Current;float oldW=c.Width,oldH=c.Height;
            c.Name=name.Text;c.Text=text.Text;c.X=(float)x.Value;c.Y=(float)y.Value;c.Width=(float)w.Value;c.Height=(float)h.Value;c.FontSize=(float)fontSize.Value;
            c.FontName=fontChoice.SelectedItem as string??"Microsoft YaHei UI";c.Bold=bold.Checked;c.Italic=italic.Checked;
            c.LineSpacing=(float)lineSpacing.Value;c.VerticalAlign=new[]{"Top","Center","Bottom"}[Math.Max(0,verticalAlign.SelectedIndex)];
            c.Visible=visible.Checked;c.Locked=locked.Checked;c.Slanted=slanted.Checked;c.KeepAspect=aspect.Checked;c.Align=new[]{"Left","Center","Right"}[Math.Max(0,align.SelectedIndex)];c.Difficulty=diff.SelectedItem as string??"IN";c.Level=(int)level.Value;
            // Milestone 8B: a badge keeps the fixed 128:108 ratio in every editor path.
            if(c.Kind=="Badge")
            {
                float badgeScale=sender==w?c.Width/FreeBadgeGeometry.ReferenceWidth:sender==h?c.Height/FreeBadgeGeometry.ReferenceHeight:FreeBadgeGeometry.FitScale(c.Width,c.Height);
                var size=FreeBadgeGeometry.BoundedSize(badgeScale);c.Width=size.Width;c.Height=size.Height;
                c.KeepAspect=true;
            }
            else
            {
                if(c.KeepAspect&&sender==w)c.Height=Math.Max(1,Math.Min(20000,oldH*c.Width/oldW));
                if(c.KeepAspect&&sender==h)c.Width=Math.Max(1,Math.Min(20000,oldW*c.Height/oldH));
            }
            filling=true;layers.Items[layers.SelectedIndex]=(c.Visible?"":"[隐藏] ")+(c.Locked?"[锁] ":"")+c.Name;filling=false;
            if(sender is NumericUpDown||sender==fontChoice)Fill();Changed();
        }
        void ImportFont()
        {
            using(var dialog=new OpenFileDialog{Filter="字体文件|*.ttf;*.otf",Title="载入字体（复制到 FONT，不安装到系统）"})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try
                {
                    string chosen=FreeFonts.Import(dialog.FileName);bool select=Current!=null&&(Current.Kind=="Text"||Current.Kind=="Tag");
                    if(select){Remember();Current.FontName=chosen;}
                    filling=true;fontChoice.Items.Clear();fontChoice.Items.AddRange(FreeFonts.Names);filling=false;Fill();
                    if(select)Changed();status.Text="已载入字体："+chosen+"；与工程一起转移时请带上 FONT 文件夹。";
                }
                catch(Exception error){Error(error);}
            }
        }
        void SelectLayer(FreeLayer c){ClearSnapGuides();filling=true;layers.ClearSelected();if(c!=null)layers.SelectedIndex=project.Layers.Count-1-project.Layers.IndexOf(c);filling=false;Fill();canvas.Invalidate();}
        void PaintCanvas(object sender,PaintEventArgs e)
        {
            float fit=Math.Max(.001f,Math.Min((canvas.Width-32f)/project.Width,(canvas.Height-32f)/project.Height));
            view=new RectangleF((canvas.Width-project.Width*fit)/2,(canvas.Height-project.Height*fit)/2,project.Width*fit,project.Height*fit);
            previewTransform=new LogicalViewportTransform(project.Width,project.Height,view);
            var g=e.Graphics;var state=g.Save();g.TranslateTransform(view.X,view.Y);g.ScaleTransform(previewTransform.HorizontalScale,previewTransform.VerticalScale);g.SetClip(new RectangleF(0,0,project.Width,project.Height));painter.Draw(g,project,false);g.Restore(state);
            var selected=Targets();if(selected.Count==0&&Current!=null)selected.Add(Current);
            if(selected.Count>0)
            {
                var b=SelectionBounds(selected);var r=new RectangleF(view.X+b.X*fit,view.Y+b.Y*fit,b.Width*fit,b.Height*fit);
                using(var pen=new Pen(Color.FromArgb(140,222,255),1.5f)){pen.DashStyle=DashStyle.Dash;g.DrawRectangle(pen,r.X,r.Y,r.Width,r.Height);}
                if(selected.Any(l=>!l.Locked))g.FillRectangle(Brushes.White,r.Right-5,r.Bottom-5,10,10);
            }
            if(activeVerticalGuide.HasValue||activeHorizontalGuide.HasValue)
            {
                using(var pen=new Pen(Color.FromArgb(200,140,222,255),1.5f))
                {
                    if(activeVerticalGuide.HasValue){float gx=view.X+activeVerticalGuide.Value*fit;g.DrawLine(pen,gx,view.Y,gx,view.Y+project.Height*fit);}
                    if(activeHorizontalGuide.HasValue){float gy=view.Y+activeHorizontalGuide.Value*fit;g.DrawLine(pen,view.X,gy,view.X+project.Width*fit,gy);}
                }
            }
        }
        LogicalViewportTransform InputTransform()
        {
            // Retain the legacy uniform input scale until paint rebuilds the viewport.
            // The old view height may belong to a different canvas aspect ratio.
            return previewTransform??new LogicalViewportTransform(project.Width,project.Height,
                new RectangleF(view.X,view.Y,view.Width,view.Width*project.Height/project.Width));
        }
        PointF Logical(Point point){return InputTransform().ScreenToLogical(new PointF(point.X,point.Y));}
        void Down(object sender,MouseEventArgs e)
        {
            canvas.Focus();if(e.Button!=MouseButtons.Left||view.Width<=0)return;var p=Logical(e.Location);var target=Targets();var box=SelectionBounds(target);float tolerance=8*project.Width/view.Width;
            resizing=target.Count>0&&Math.Abs(p.X-box.Right)<tolerance&&Math.Abs(p.Y-box.Bottom)<tolerance;
            if(!resizing)
            {
                var hit=project.Layers.LastOrDefault(l=>l.Visible&&!l.Locked&&l.Bounds.Contains(p));
                if(hit==null){SelectLayer(null);return;}SelectLayer(hit);target=Targets();box=SelectionBounds(target);
            }
            if(target.Count==0)return;ClearSnapGuides();dragging=true;painter.HoldBackground=true;dragRecorded=false;dragStart=p;dragBounds=box;dragOriginal=target.Select(l=>l.Copy()).ToList();canvas.Capture=true;
        }
        void MovePointer(object sender,MouseEventArgs e)
        {
            if(!dragging)return;var p=Logical(e.Location);float dx=p.X-dragStart.X,dy=p.Y-dragStart.Y;
            if(!dragRecorded){if(Math.Abs(dx)+Math.Abs(dy)<1)return;Remember();dragRecorded=true;}
            if(resizing)
            {
                float sx=Math.Max(.05f,(dragBounds.Width+dx)/dragBounds.Width),sy=Math.Max(.05f,(dragBounds.Height+dy)/dragBounds.Height);
                bool uniform=dragOriginal.Count>1||dragOriginal[0].KeepAspect||dragOriginal[0].Kind=="Badge";
                if(uniform)
                {
                    float minimum=dragOriginal.Max(l=>Math.Max(1f/l.Width,(l.Kind=="Badge"?FreeBadgeGeometry.MinimumHeight:1f)/l.Height));
                    float maximum=dragOriginal.Min(l=>Math.Min(20000f/l.Width,20000f/l.Height));
                    sx=sy=Math.Max(minimum,Math.Min(maximum,Math.Max(sx,sy)));
                }
                else {sx=Math.Min(sx,dragOriginal.Min(l=>20000/l.Width));sy=Math.Min(sy,dragOriginal.Min(l=>20000/l.Height));}
                foreach(var old in dragOriginal)
                {var c=project.Layers.First(l=>l.Id==old.Id);c.X=dragBounds.X+(old.X-dragBounds.X)*sx;c.Y=dragBounds.Y+(old.Y-dragBounds.Y)*sy;c.Width=Math.Max(1,old.Width*sx);c.Height=Math.Max(old.Kind=="Badge"?FreeBadgeGeometry.MinimumHeight:1f,old.Height*sy);c.FontSize=Math.Max(1,Math.Min(1000,old.FontSize*Math.Min(sx,sy)));}
            }
            else
            {
                // Milestone 10B.1: holding Alt bypasses canvas AND component snapping for this
                // move and shows no guides; releasing Alt restores snapping on the next move.
                if(SnapBypassed())
                {
                    MoveLayers(dragOriginal,dx,dy);
                    ClearSnapGuides();
                }
                else
                {
                    // Move branch only: snap the moving set's whole bounds to the canvas guides and
                    // to the visible stationary layers. All candidates compete in one pass, so the
                    // closest correction wins and canvas guides keep their 10A tie priority.
                    // previewTransform can still be null between a canvas resize / project swap and
                    // the next paint, so fall back to the view-based threshold (same as Logical()).
                    SizeF tolerance=InputTransform().ScreenDeltaToLogical(new SizeF(FreeSnap.ThresholdScreenPixels,FreeSnap.ThresholdScreenPixels));
                    CollectSnapGuides(project,dragOriginal,snapGuidesX,snapGuidesY);
                    var snap=FreeSnap.SnapMoveToGuides(dragBounds,dx,dy,snapGuidesX,snapGuidesY,tolerance.Width,tolerance.Height);
                    MoveLayers(dragOriginal,snap.DeltaX,snap.DeltaY);
                    activeVerticalGuide=snap.HasVerticalGuide?(float?)snap.VerticalGuideX:null;
                    activeHorizontalGuide=snap.HasHorizontalGuide?(float?)snap.HorizontalGuideY:null;
                }
            }
            LimitCoordinates();Fill();Changed();
        }
        void MoveLayers(List<FreeLayer> originals,float dx,float dy)
        {foreach(var old in originals){var c=project.Layers.First(l=>l.Id==old.Id);c.X=old.X+dx;c.Y=old.Y+dy;}}
        void LimitCoordinates(){foreach(var c in project.Layers){c.X=Math.Max(-20000,Math.Min(20000,c.X));c.Y=Math.Max(-20000,Math.Min(20000,c.Y));}}
        void Up(object sender,MouseEventArgs e){dragging=false;painter.HoldBackground=false;canvas.Capture=false;ClearSnapGuides();canvas.Invalidate();}
        void ClearSnapGuides(){activeVerticalGuide=null;activeHorizontalGuide=null;}
        void CanvasKey(object sender,KeyEventArgs e)
        {
            if(!new[]{Keys.Left,Keys.Right,Keys.Up,Keys.Down,Keys.Delete}.Contains(e.KeyCode))return;
            if(e.KeyCode==Keys.Delete)Delete();else
            {var target=Targets();if(target.Count>0){Remember();float step=e.Shift?10:1;MoveLayers(target.Select(l=>l.Copy()).ToList(),e.KeyCode==Keys.Left?-step:e.KeyCode==Keys.Right?step:0,e.KeyCode==Keys.Up?-step:e.KeyCode==Keys.Down?step:0);LimitCoordinates();Fill();Changed();}}
            e.Handled=true;e.SuppressKeyPress=true;
        }
        void KeysPressed(object sender,KeyEventArgs e)
        {
            if(!e.Control)return;
            if(e.KeyCode==Keys.S){Save();e.SuppressKeyPress=true;}
            if(e.KeyCode==Keys.Z||e.KeyCode==Keys.Y)
            {var from=e.KeyCode==Keys.Z?undo:redo;var to=e.KeyCode==Keys.Z?redo:undo;if(from.Count>0){string id=Current==null?null:Current.Id;to.Push(project.Copy());project=from.Pop();RefreshLayers(id);Changed();}e.SuppressKeyPress=true;}
            if(e.KeyCode==Keys.D){Duplicate();e.SuppressKeyPress=true;}
        }
        void AddSong()
        {if(project.Layers.Count>194)return;Remember();var added=FreeProject.Song(project.Width/2f-260,project.Height/2f-175,"新曲目",0);project.Layers.AddRange(added);RefreshLayers(added[1].Id);Changed();}
        void AddOriginal()
        {
            var selected=Current;
            string group=selected==null?"":selected.Group;
            var existing=selected!=null&&selected.Kind=="Tag"?selected:string.IsNullOrEmpty(group)?null:project.Layers.FirstOrDefault(l=>l.Group==group&&l.Kind=="Tag");
            if(existing!=null){if(!existing.Visible){Remember();existing.Visible=true;Changed();}RefreshLayers(existing.Id);status.Text="已选中标签：右侧修改文字，取消「显示」可隐藏。";return;}
            if(project.Layers.Count>=200){status.Text="组件数量已达 200，请先移除不需要的组件。";return;}
            var art=string.IsNullOrEmpty(group)?null:project.Layers.FirstOrDefault(l=>l.Group==group&&l.Kind=="Image");
            float scale=art==null?1:Math.Max(.1f,Math.Min(10,art.Width/520));
            var tag=new FreeLayer{Kind="Tag",Name="Original 标签",Group=group,Text="Phigros Original",Width=240*scale,Height=36*scale,FontSize=22*scale,ColorArgb=Color.Black.ToArgb(),Align="Center",Slanted=true,
                X=art==null?project.Width/2f-120:art.X+art.Width-240*scale,Y=art==null?project.Height/2f-18:art.Y+art.Height+115*scale};
            Remember();project.Layers.Add(tag);LimitCoordinates();RefreshLayers(tag.Id);Changed();
            status.Text=string.IsNullOrEmpty(group)?"已添加独立标签，可改字、拖动和隐藏。":"已添加标签并加入曲目组合；取消整组移动可单独调整标签。";
        }
        void Add(string kind)
        {
            if(kind=="Image"){var path=PickImage();if(path!=null)ImportImage(path);return;}if(project.Layers.Count>=200)return;
            Remember();var c=new FreeLayer{Kind=kind,Name=kind=="Text"?"新文字":kind=="Shape"?"装饰色块":"难度牌",X=project.Width/2f-200,Y=project.Height/2f-35};
            if(kind=="Shape"){c.Height=300;c.ColorArgb=Color.FromArgb(150,20,40,55).ToArgb();c.Slanted=true;c.KeepAspect=false;}
            if(kind=="Badge"){c.Width=128;c.Height=108;c.Text="";}project.Layers.Add(c);RefreshLayers(c.Id);Changed();
        }
        string PickImage(){using(var d=new OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",InitialDirectory=root})return d.ShowDialog(this)==DialogResult.OK?d.FileName:null;}
        void ImportImage(string path)
        {
            try{if(project.Layers.Count>=200)return;byte[] data=File.ReadAllBytes(path);using(var img=Renderer.Decode(data)){Remember();float fit=Math.Min(520f/img.Width,380f/img.Height);var c=new FreeLayer{Kind="Image",Name=Path.GetFileNameWithoutExtension(path),X=project.Width/2f-260,Y=project.Height/2f-180,Width=img.Width*fit,Height=img.Height*fit,Image=data,Text=""};project.Layers.Add(c);RefreshLayers(c.Id);Changed();}}
            catch(Exception e){Error(e);}
        }
        void ReplaceImage()
        {
            var c=Current;if(c==null)return;
            if(c.Kind!="Image"&&!string.IsNullOrEmpty(c.Group))c=project.Layers.FirstOrDefault(l=>l.Group==c.Group&&l.Kind=="Image");
            if(c==null||c.Kind!="Image"){status.Text="请先选择图片组件或某首曲目的组件。";return;}
            var path=PickImage();if(path==null)return;
            try{byte[] data=File.ReadAllBytes(path);using(var check=Renderer.Decode(data)){}Remember();c.Image=data;SelectLayer(c);Changed();}catch(Exception e){Error(e);}
        }
        void SetBackground(){string path=PickImage();if(path==null)return;try{var data=File.ReadAllBytes(path);using(var b=Renderer.Decode(data)){}Remember();project.Background=data;Changed();}catch(Exception e){Error(e);}}
        void BackgroundColor(){using(var d=new ColorDialog{Color=Color.FromArgb(project.BackgroundColor)})if(d.ShowDialog(this)==DialogResult.OK){Remember();project.BackgroundColor=d.Color.ToArgb();Changed();}}
        void ChooseColor(){var c=Current;if(c==null)return;using(var d=new ColorDialog{Color=Color.FromArgb(c.ColorArgb)})if(d.ShowDialog(this)==DialogResult.OK){Remember();c.ColorArgb=Color.FromArgb(Color.FromArgb(c.ColorArgb).A,d.Color).ToArgb();Changed();}}
        void Duplicate()
        {
            var target=Targets();if(target.Count==0||project.Layers.Count+target.Count>200)return;Remember();var groups=new Dictionary<string,string>();var copies=new List<FreeLayer>();
            foreach(var c in target){var copy=c.Copy();copy.Id=Guid.NewGuid().ToString("N");copy.X+=30;copy.Y+=30;copy.Name+=" 副本";if(!string.IsNullOrEmpty(c.Group)){if(!groups.ContainsKey(c.Group))groups[c.Group]=Guid.NewGuid().ToString("N");copy.Group=groups[c.Group];}copies.Add(copy);}project.Layers.AddRange(copies);LimitCoordinates();RefreshLayers(copies.Last().Id);Changed();
        }
        void Delete(){var target=Targets();if(target.Count==0)return;Remember();project.Layers.RemoveAll(l=>target.Contains(l));RefreshLayers(null);Changed();}
        void Reorder(int direction)
        {
            var target=Targets();if(target.Count==0)return;string id=Current.Id;Remember();
            var indexes=target.Select(l=>project.Layers.IndexOf(l)).OrderBy(i=>direction>0?-i:i).ToArray();
            foreach(int i in indexes){int next=i+direction;if(next<0||next>=project.Layers.Count||target.Contains(project.Layers[next]))continue;var swap=project.Layers[next];project.Layers[next]=project.Layers[i];project.Layers[i]=swap;}
            RefreshLayers(id);Changed();
        }
        void Group(){var target=Targets();if(target.Count<2){status.Text="在图层列表中按住 Ctrl 选择多个组件，再点击组合。";return;}Remember();string group=Guid.NewGuid().ToString("N");foreach(var c in target)c.Group=group;Changed();}
        void Ungroup(){var target=Selected();if(target.Count==0)return;var groups=new HashSet<string>(target.Where(l=>!string.IsNullOrEmpty(l.Group)).Select(l=>l.Group));Remember();foreach(var c in project.Layers.Where(l=>target.Contains(l)||groups.Contains(l.Group)))c.Group="";Changed();}
        void SetGroupVisible(bool value){if(Current==null)return;var c=Current;var target=string.IsNullOrEmpty(c.Group)?new[]{c}:project.Layers.Where(l=>l.Group==c.Group).ToArray();Remember();foreach(var item in target)item.Visible=value;RefreshLayers(c.Id);Changed();}
        void ResizeCanvas(int width,int height)
        {
            if(width==project.Width&&height==project.Height)return;Remember();float fit=Math.Min((float)width/project.Width,(float)height/project.Height),left=(width-project.Width*fit)/2,top=(height-project.Height*fit)/2;
            foreach(var c in project.Layers){c.X=left+c.X*fit;c.Y=top+c.Y*fit;
                if(c.Kind=="Badge"){var size=FreeBadgeGeometry.BoundedSize(FreeBadgeGeometry.FitScale(c.Width,c.Height)*fit);c.Width=size.Width;c.Height=size.Height;}
                else {c.Width=Math.Max(1,c.Width*fit);c.Height=Math.Max(1,c.Height*fit);}
                c.FontSize=Math.Max(1,Math.Min(1000,c.FontSize*fit));}
            project.Width=width;project.Height=height;CanonicalizeBadges(project);previewTransform=null;RefreshLayers(Current==null?null:Current.Id);Changed();
        }
        bool Discard(){if(!dirty)return true;var result=MessageBox.Show(this,"是否先保存当前自由排版工程？","保存修改",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question);if(result==DialogResult.Cancel)return false;if(result==DialogResult.Yes)return SaveCore();return true;}
        void NewFive(){if(!Discard())return;project=FreeProject.FivePoint();previewTransform=null;ClearSnapGuides();projectPath="";undo.Clear();redo.Clear();RefreshLayers(null);Changed();}
        void Open(){using(var d=new OpenFileDialog{Filter="自由排版工程|*.phifree",InitialDirectory=root})if(d.ShowDialog(this)==DialogResult.OK)LoadProject(d.FileName);}
        // Milestone 8B: the editor only ever holds canonical badges, so the declared
        // Bounds is exactly the painted badge box. This runs after FreeProject.Read
        // (deserialize -> Migrate -> Validate) and never rewrites the file on disk.
        internal static void CanonicalizeBadges(FreeProject p)
        {
            if(p==null||p.Layers==null)return;
            foreach(var c in p.Layers)
            {
                if(c==null||c.Kind!="Badge")continue;
                var size=FreeBadgeGeometry.CanonicalSize(c.Width,c.Height);
                c.Width=size.Width;c.Height=size.Height;c.KeepAspect=true;
            }
        }
        void LoadProject(string path)
        {
            try{var next=FreeProject.Read(path);using(var checker=new FreePainter())checker.Prepare(next);if(!Discard())return;project=next;CanonicalizeBadges(project);previewTransform=null;ClearSnapGuides();projectPath=path;undo.Clear();redo.Clear();dirty=false;painter.Prepare(project);RefreshLayers(null);Text="Phigros · 自由排版";status.Text="已打开："+path;}
            catch(Exception e){Error(e);}
        }
        void Save(){SaveCore();}
        bool SaveCore()
        {
            using(var d=new SaveFileDialog{Filter="自由排版工程|*.phifree",FileName=string.IsNullOrEmpty(projectPath)?"自由排版.phifree":Path.GetFileName(projectPath),InitialDirectory=string.IsNullOrEmpty(projectPath)?root:Path.GetDirectoryName(projectPath)})
            {if(d.ShowDialog(this)!=DialogResult.OK)return false;try{project.Save(d.FileName);projectPath=d.FileName;dirty=false;Text="Phigros · 自由排版";status.Text="已保存工程（含所有导入图片）："+d.FileName;return true;}catch(Exception e){Error(e);return false;}}
        }
        void Export(bool transparent)
        {
            try{string folder=Path.Combine(root,"增强版输出");Directory.CreateDirectory(folder);using(var d=new SaveFileDialog{Filter="PNG 图片|*.png",FileName=transparent?"自由排版_透明.png":"自由排版.png",InitialDirectory=folder})
            {if(d.ShowDialog(this)!=DialogResult.OK)return;project.Validate();using(var b=painter.Render(project,transparent))Renderer.SavePng(b,d.FileName);status.Text="已导出 "+project.Width+" × "+project.Height+"："+d.FileName;}}
            catch(Exception e){Error(e);}
        }
        protected override void Dispose(bool disposing){if(disposing)painter.Dispose();base.Dispose(disposing);}
        internal int VerifyAuditRegressions()
        {
            var savedAlt=altHeld;
            try {altHeld=delegate{return false;};return VerifyAuditRegressionsCore();}
            finally {altHeld=savedAlt;}
        }
        int VerifyAuditRegressionsCore()
        {
            int checks=0;Action<bool,string> check=delegate(bool ok,string message){if(!ok)throw new Exception("Audit regression: "+message);checks++;};
            groupMove.Checked=false;Add("Badge");var badge=Current;
            // A badge box can carry the canonical 0.84375 height, so the size controls must not
            // round it to one decimal place (87.75 must not drift to 87.8).
            check(w.DecimalPlaces==5&&h.DecimalPlaces==5,"badge size controls keep five decimals");
            w.Value=1;
            check(badge.Width==1&&badge.Height==.84375f,"minimum width keeps the badge ratio");
            h.Value=20000;
            check(badge.Width==20000&&badge.Height==16875,"maximum height clamps the common scale");
            h.Value=.84375m;
            check(badge.Width==1&&badge.Height==.84375f,"minimum height keeps the badge ratio");
            SelectLayer(project.Layers.First(l=>l.Kind=="Shape"));
            check(h.Minimum==1m&&w.DecimalPlaces==1&&h.DecimalPlaces==1,"ordinary layer restores normal size controls");
            SelectLayer(badge);
            // Exercise the real Down/Move/Up handlers with an identity viewport and an
            // isolated scene; the large synthetic pointer models dragging past the limit.
            project=new FreeProject{Width=1000,Height=600,SoftBackground=false};
            badge.X=100;badge.Y=100;badge.Width=128;badge.Height=108;project.Layers.Add(badge);RefreshLayers(badge.Id);
            view=new RectangleF(0,0,1000,600);previewTransform=null;
            Down(canvas,new MouseEventArgs(MouseButtons.Left,1,228,208,0));
            MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,30100,30100,0));
            Up(canvas,new MouseEventArgs(MouseButtons.Left,1,30100,30100,0));
            check(badge.Width==20000&&badge.Height==16875,"resize at upper limit keeps one common scale");
            KeysPressed(this,new KeyEventArgs(Keys.Control|Keys.Z));badge=Current;
            check(badge.Width==128&&badge.Height==108,"undo restores the pre-limit badge dimensions");
            KeysPressed(this,new KeyEventArgs(Keys.Control|Keys.Y));badge=Current;
            check(badge.Width==20000&&badge.Height==16875,"redo restores the canonical upper-limit dimensions");
            badge.Width=128;badge.Height=108;badge.Group="limits";
            var companion=new FreeLayer{Kind="Shape",Group="limits",X=300,Y=100,Width=400,Height=100};
            project.Layers.Add(companion);RefreshLayers(badge.Id);groupMove.Checked=true;
            Down(canvas,new MouseEventArgs(MouseButtons.Left,1,700,208,0));
            MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,40700,40208,0));
            Up(canvas,new MouseEventArgs(MouseButtons.Left,1,40700,40208,0));
            check(badge.Width==6400&&badge.Height==5400&&companion.Width==20000&&companion.Height==5000,"group upper limit applies one scale to every dimension");
            check(Math.Abs((companion.X-badge.X)-10000)<.001f,"group resize limit keeps scaled relative positions");
            project.Layers.Remove(companion);badge.Group="";badge.X=100;badge.Y=100;groupMove.Checked=false;RefreshLayers(badge.Id);
            badge.Width=1;badge.Height=.84375f;Fill();
            Down(canvas,new MouseEventArgs(MouseButtons.Left,1,101,101,0));
            MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,81,81,0));
            Up(canvas,new MouseEventArgs(MouseButtons.Left,1,81,81,0));
            check(badge.Width==1&&badge.Height==.84375f,"resize at lower limit preserves tiny badge geometry");
            ResizeCanvas(1440,1080);project.Validate();
            check(previewTransform==null,"canvas aspect change leaves input in the pre-paint lifecycle");
            var origin=Logical(new Point(0,0));var eight=Logical(new Point(8,8));
            var tolerance=InputTransform().ScreenDeltaToLogical(new SizeF(8,8));
            check(Math.Abs(eight.X-origin.X-tolerance.Width)<.00001f&&Math.Abs(eight.Y-origin.Y-tolerance.Height)<.00001f,
                "pre-paint input and threshold agree on both axes after aspect change");
            check(Math.Abs(tolerance.Width-11.52f)<.00001f&&Math.Abs(tolerance.Height-11.52f)<.00001f,
                "stale viewport uses the uniform legacy 8-pixel input scale");
            var bounds=new RectangleF(100,100,100,100);
            var outside=FreeSnap.SnapMoveToGuides(bounds,0,9*1.44f,new float[0],new float[]{100},tolerance.Width,tolerance.Height);
            check(!outside.HasHorizontalGuide,"a guide over eight screen pixels away does not snap before paint");
            var inside=FreeSnap.SnapMoveToGuides(bounds,0,7*1.44f,new float[0],new float[]{100},tolerance.Width,tolerance.Height);
            check(inside.HasHorizontalGuide&&Math.Abs(inside.DeltaY)<.00001f,"a guide within eight screen pixels snaps before paint");
            badge.X=100;badge.Y=100;badge.Width=128;badge.Height=108;Fill();
            Down(canvas,new MouseEventArgs(MouseButtons.Left,1,114,107,0));
            MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,114,47,0));
            check(Math.Abs(badge.Y-13.6f)<.001f&&!activeHorizontalGuide.HasValue,"real move outside eight pixels stays raw before repaint");
            Up(canvas,new MouseEventArgs(MouseButtons.Left,1,114,47,0));
            badge.X=100;badge.Y=100;Fill();
            Down(canvas,new MouseEventArgs(MouseButtons.Left,1,114,107,0));
            MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,114,45,0));
            check(Math.Abs(badge.Y)<.001f&&activeHorizontalGuide==0f,"real move within eight pixels snaps before repaint");
            Up(canvas,new MouseEventArgs(MouseButtons.Left,1,114,45,0));
            badge.Width=1;badge.Height=.84375f;Fill();
            ResizeCanvas(320,180);project.Validate();
            check(badge.Width==1&&badge.Height==.84375f,"canvas shrinking preserves the legal minimum badge size");
            // The canvas fit must clamp a badge through the same common-scale limit as the
            // property box and the resize handle; growing the canvas may not push a large
            // badge past the persisted 20000 maximum that Validate() enforces. The selection
            // is cleared first so that a broken implementation is reported by this assertion
            // instead of by the property panel rejecting the out-of-range value.
            project=new FreeProject{Width=640,Height=360,SoftBackground=false};
            var large=new FreeLayer{Kind="Badge",X=100,Y=100,Width=16000,Height=13500};
            project.Layers.Add(large);RefreshLayers(large.Id);layers.ClearSelected();
            ResizeCanvas(1280,720);
            bool largeValid=true;try{project.Validate();}catch(InvalidDataException){largeValid=false;}
            check(largeValid&&large.Width==20000&&large.Height==16875,"growing the canvas clamps a large badge to the common-scale maximum");
            // Fail before the large integration test's first assertion; the wrapper must
            // restore the exact delegate even when its body throws.
            using(var failure=new FreeLayoutForm())
            {
                Func<bool> original=delegate{return true;};failure.altHeld=original;failure.groupMove.Checked=false;
                bool threw=false;try{failure.VerifyInteractions();}catch(Exception ex){if(ex.Message!="Free UI: song grouping")throw;threw=true;}
                check(threw&&ReferenceEquals(failure.altHeld,original)&&failure.SnapBypassed(),"failed interaction tests restore the original Alt delegate");
            }
            return checks;
        }
        internal int VerifyInteractions()
        {
            var savedAlt=altHeld;
            try {altHeld=delegate{return false;};return VerifyInteractionsCore();}
            finally {altHeld=savedAlt;}
        }
        int VerifyInteractionsCore()
        {
            int checks=0;Action<bool,string> check=delegate(bool ok,string message){if(!ok)throw new Exception("Free UI: "+message);checks++;};
            // VerifyInteractions owns the Alt override and restores it even on failure.
            var art=project.Layers.First(l=>l.Kind=="Image"&&l.Slanted);SelectLayer(art);
            var group=Targets();check(group.Count==6,"song grouping");
            using(var bitmap=new Bitmap(Math.Max(1,canvas.Width),Math.Max(1,canvas.Height)))using(var g=Graphics.FromImage(bitmap))PaintCanvas(canvas,new PaintEventArgs(g,canvas.ClientRectangle));
            check(previewTransform!=null,"PaintCanvas initializes the preview transform");
            check(previewTransform.HorizontalScale==view.Width/project.Width&&previewTransform.VerticalScale==view.Height/project.Height,
                "preview transform scales match the free viewport");
            PointF probeScreen=new PointF(view.X+view.Width/3f,view.Y+view.Height/4f);
            float legacyFit=view.Width/project.Width;
            PointF legacyProbe=new PointF((probeScreen.X-view.X)/legacyFit,(probeScreen.Y-view.Y)/legacyFit);
            PointF helperProbe=previewTransform.ScreenToLogical(probeScreen);
            check(Math.Abs(helperProbe.X-legacyProbe.X)<.001f&&Math.Abs(helperProbe.Y-legacyProbe.Y)<.001f,
                "preview transform inverse matches the legacy formula");
            float fit=view.Width/project.Width;int px=(int)(view.X+(art.X+art.Width/2)*fit),py=(int)(view.Y+(art.Y+100)*fit);
            var before=group.Select(l=>l.Copy()).ToList();
            Down(canvas,new MouseEventArgs(MouseButtons.Left,1,px,py,0));MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,px+30,py+15,0));Up(canvas,new MouseEventArgs(MouseButtons.Left,1,px+30,py+15,0));
            check(art.X>before.First(l=>l.Id==art.Id).X,"mouse moves art");float dx=art.X-before.First(l=>l.Id==art.Id).X;
            check(group.All(l=>Math.Abs(l.X-before.First(b=>b.Id==l.Id).X-dx)<.01f),"all song components move together");
            KeysPressed(this,new KeyEventArgs(Keys.Control|Keys.Z));art=project.Layers.First(l=>l.Id==art.Id);check(Math.Abs(art.X-before.First(l=>l.Id==art.Id).X)<.01f,"undo movement");
            KeysPressed(this,new KeyEventArgs(Keys.Control|Keys.Y));art=project.Layers.First(l=>l.Id==art.Id);check(art.X>before.First(l=>l.Id==art.Id).X,"redo movement");
            SelectLayer(art);groupMove.Checked=false;float oldTitleX=project.Layers.First(l=>l.Group==art.Group&&l.Kind=="Text").X;
            CanvasKey(canvas,new KeyEventArgs(Keys.Right|Keys.Shift));check(project.Layers.First(l=>l.Group==art.Group&&l.Kind=="Text").X==oldTitleX,"independent movement");
            groupMove.Checked=true;int oldCount=project.Layers.Count;Duplicate();check(project.Layers.Count==oldCount+6&&Current.Group!=art.Group,"duplicate creates independent group");
            Delete();check(project.Layers.Count==oldCount,"delete group");
            SelectLayer(art);SetGroupVisible(false);check(project.Layers.Where(l=>l.Group==art.Group).All(l=>!l.Visible),"hide group");SetGroupVisible(true);
            var title=project.Layers.First(l=>l.Group==art.Group&&l.Kind=="Text");SelectLayer(title);text.Text="测试标题";check(title.Text=="测试标题","editable text");
            fontChoice.SelectedItem="Arial";bold.Checked=true;italic.Checked=true;lineSpacing.Value=1.6m;align.SelectedIndex=2;verticalAlign.SelectedIndex=0;
            check(title.FontName=="Arial"&&title.Bold&&title.Italic&&Math.Abs(title.LineSpacing-1.6f)<.01f&&title.Align=="Right"&&title.VerticalAlign=="Top","typography property controls update selected layer");
            groupMove.Checked=false;SelectLayer(art);int index=project.Layers.IndexOf(art);Reorder(1);check(project.Layers.IndexOf(art)==index+1,"z order");
            art.Locked=true;check(Targets().Count==0,"locked layer not moved");art.Locked=false;groupMove.Checked=true;SelectLayer(art);
            var box=SelectionBounds(Targets());before=Targets().Select(l=>l.Copy()).ToList();px=(int)(view.X+box.Right*fit);py=(int)(view.Y+box.Bottom*fit);
            Down(canvas,new MouseEventArgs(MouseButtons.Left,1,px,py,0));MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,px+25,py+20,0));Up(canvas,new MouseEventArgs(MouseButtons.Left,1,px+25,py+20,0));
            float factor=art.Width/before.First(l=>l.Id==art.Id).Width;
            check(factor>1&&Targets().All(l=>Math.Abs(l.Width/before.First(b=>b.Id==l.Id).Width-factor)<.001f&&Math.Abs(l.Height/before.First(b=>b.Id==l.Id).Height-factor)<.001f),"uniform group resize");
            float ratio=art.Width/art.Height;ResizeCanvas(1440,1080);check(project.Width==1440&&Math.Abs(art.Width/art.Height-ratio)<.001f,"4:3 canvas preserves component ratio");
            check(previewTransform==null,"resize canvas invalidates the cached preview transform");
            SelectLayer(art);Ungroup();check(string.IsNullOrEmpty(art.Group),"ungroup");
            AddSong();var taggedArt=Current;AddOriginal();var tag=Current;
            check(tag.Kind=="Tag"&&tag.Group==taggedArt.Group&&tag.Text=="Phigros Original"&&tag.ColorArgb==Color.Black.ToArgb(),"optional original label joins song");
            text.Text="Fanmade Chart";fontSize.Value=26;check(tag.Text=="Fanmade Chart"&&tag.FontSize==26,"original label text and font editable");
            visible.Checked=false;check(!tag.Visible,"original label visibility toggle");int taggedCount=project.Layers.Count;SelectLayer(taggedArt);AddOriginal();
            check(tag.Visible&&project.Layers.Count==taggedCount&&Current.Id==tag.Id,"original button restores existing label without duplication");
            float oldTagX=tag.X;CanvasKey(canvas,new KeyEventArgs(Keys.Right|Keys.Shift));check(tag.X==oldTagX+10,"original label follows group movement");
            SelectLayer(null);AddOriginal();check(Current.Kind=="Tag"&&Current.Group=="","standalone original label");
            // Milestone 10A: move snapping, guide lifetime and one gesture = one undo entry.
            this.ClientSize=new Size(1200,800);this.PerformLayout();
            using(var snapBitmap=new Bitmap(Math.Max(1,canvas.Width),Math.Max(1,canvas.Height)))
            using(var snapGraphics=Graphics.FromImage(snapBitmap))PaintCanvas(canvas,new PaintEventArgs(snapGraphics,canvas.ClientRectangle));
            float snapFit=view.Width/project.Width;
            check(snapFit>.05f,"snap test needs a usable preview scale (got "+snapFit+")");
            var snapLayer=project.Layers.First(l=>l.Kind=="Image"&&l.Slanted);
            SelectLayer(snapLayer);groupMove.Checked=false;
            snapLayer.X=40f;snapLayer.Y=40f;
            // Milestone 10B adds stationary component guides to the same candidate set, and the
            // 10A blocks below check canvas-only behaviour. Park the other layers (restoring their
            // exact visibility afterwards) so these keep testing the canvas guides themselves.
            var parked=project.Layers.Where(l=>l.Id!=snapLayer.Id).Select(l=>new{l,visible=l.Visible}).ToList();
            Action<bool> parkOthers=delegate(bool restore){foreach(var o in parked)o.l.Visible=restore?o.visible:false;};
            parkOthers(false);
            try
            {
            int snapUndo=undo.Count;
            int snapX=(int)(view.X+(snapLayer.X+snapLayer.Width/2f)*snapFit),snapY=(int)(view.Y+(snapLayer.Y+snapLayer.Height/2f)*snapFit);
            Down(canvas,new MouseEventArgs(MouseButtons.Left,1,snapX,snapY,0));
            MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(snapX-30*snapFit),(int)(snapY-20*snapFit),0));
            check(Math.Abs(snapLayer.X)<1.5f,"move snapping pulls the layer onto the canvas left edge");
            check(activeVerticalGuide.HasValue&&activeVerticalGuide.Value==0f,"left edge snap activates the x=0 guide");
            MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(snapX-45*snapFit),(int)(snapY-30*snapFit),0));
            check(Math.Abs(snapLayer.X)<1.5f,"a later move inside the same gesture keeps snapping to the left edge");
            MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(snapX+60*snapFit),snapY,0));
            check(snapLayer.X>50f&&!activeVerticalGuide.HasValue,"leaving the guide range keeps the raw position and clears the guide");
            check(undo.Count==snapUndo+1,"guide enter/leave never adds extra undo entries");
            Up(canvas,new MouseEventArgs(MouseButtons.Left,1,snapX,snapY,0));
            check(!activeVerticalGuide.HasValue&&!activeHorizontalGuide.HasValue,"mouse up clears the active snap guides");
            }
            finally {parkOthers(true);}
            SelectLayer(snapLayer);
            var resizeBox=SelectionBounds(Targets());
            int resizeX=(int)(view.X+resizeBox.Right*snapFit),resizeY=(int)(view.Y+resizeBox.Bottom*snapFit);
            Down(canvas,new MouseEventArgs(MouseButtons.Left,1,resizeX,resizeY,0));
            MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,resizeX+20,resizeY+15,0));
            check(!activeVerticalGuide.HasValue&&!activeHorizontalGuide.HasValue,"resizing never activates snap guides");
            Up(canvas,new MouseEventArgs(MouseButtons.Left,1,resizeX+20,resizeY+15,0));
            var groupArt=project.Layers.First(l=>l.Kind=="Image"&&l.Slanted&&!string.IsNullOrEmpty(l.Group));
            SelectLayer(groupArt);groupMove.Checked=true;
            var groupBefore=Targets().Select(l=>l.Copy()).ToList();
            float groupLeft=SelectionBounds(groupBefore).Left;
            int groupX=(int)(view.X+(groupArt.X+groupArt.Width/2f)*snapFit),groupY=(int)(view.Y+(groupArt.Y+groupArt.Height/2f)*snapFit);
            Down(canvas,new MouseEventArgs(MouseButtons.Left,1,groupX,groupY,0));
            MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(groupX+(groupLeft==5f?0f:-groupLeft+5f)*snapFit),groupY,0));
            check(Math.Abs(SelectionBounds(Targets()).Left)<1.5f,"group move snaps the whole selection bounds onto the canvas left edge");
            check(Targets().All(l=>Math.Abs((l.X-groupBefore.First(b=>b.Id==l.Id).X)-(groupArt.X-groupBefore.First(b=>b.Id==groupArt.Id).X))<.001f),
                "group move keeps the relative layout inside the group");
            Up(canvas,new MouseEventArgs(MouseButtons.Left,1,groupX,groupY,0));
            ClearSnapGuides();
            // previewTransform may be null between a canvas resize / project swap and the next paint.
            using(var nullBitmap=new Bitmap(Math.Max(1,canvas.Width),Math.Max(1,canvas.Height)))
            using(var nullGraphics=Graphics.FromImage(nullBitmap))PaintCanvas(canvas,new PaintEventArgs(nullGraphics,canvas.ClientRectangle));
            float nullFit=view.Width/project.Width;
            var cachedTransform=new LogicalViewportTransform(project.Width,project.Height,view);
            SizeF cachedTolerance=cachedTransform.ScreenDeltaToLogical(new SizeF(FreeSnap.ThresholdScreenPixels,FreeSnap.ThresholdScreenPixels));
            SizeF fallbackTolerance=new SizeF(FreeSnap.ThresholdScreenPixels*project.Width/view.Width,FreeSnap.ThresholdScreenPixels*project.Height/view.Height);
            check(Math.Abs(fallbackTolerance.Width-cachedTolerance.Width)<.001f&&Math.Abs(fallbackTolerance.Height-cachedTolerance.Height)<.001f,
                "the legacy fallback threshold matches the cached transform threshold");
            var nullLayer=project.Layers.First(l=>l.Kind=="Image"&&l.Slanted);
            SelectLayer(nullLayer);groupMove.Checked=false;
            nullLayer.X=40f;nullLayer.Y=40f;
            parkOthers(false);
            try
            {
            int nullX=(int)(view.X+(nullLayer.X+nullLayer.Width/2f)*nullFit),nullY=(int)(view.Y+(nullLayer.Y+nullLayer.Height/2f)*nullFit);
            previewTransform=null;
            bool nullMoveSafe=true;
            try
            {
                Down(canvas,new MouseEventArgs(MouseButtons.Left,1,nullX,nullY,0));
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(nullX-30*nullFit),(int)(nullY-20*nullFit),0));
            }
            catch(NullReferenceException){nullMoveSafe=false;}
            check(nullMoveSafe,"a move with a not-yet-rebuilt preview transform does not throw");
            check(Math.Abs(nullLayer.X)<1.5f,"the legacy fallback still snaps at the 8 screen pixel threshold");
            Up(canvas,new MouseEventArgs(MouseButtons.Left,1,nullX,nullY,0));
            }
            finally {parkOthers(true);}
            check(previewTransform==null,"the fallback does not fabricate a cached preview transform");
            using(var rebuildBitmap=new Bitmap(Math.Max(1,canvas.Width),Math.Max(1,canvas.Height)))
            using(var rebuildGraphics=Graphics.FromImage(rebuildBitmap))PaintCanvas(canvas,new PaintEventArgs(rebuildGraphics,canvas.ClientRectangle));
            check(previewTransform!=null,"the next paint rebuilds the cached preview transform");
            // Milestone 8B: a legacy non-canonical badge is normalised when the editor takes
            // the project over, so Bounds == the painted badge box for painting, hit testing,
            // selection, the resize handle and canvas snapping.
            string legacyPath=Path.Combine(Path.GetTempPath(),"phifree-badge-"+Guid.NewGuid().ToString("N")+".phifree");
            try
            {
                var legacy=new FreeProject{Width=800,Height=400,SoftBackground=false};
                legacy.Layers.Add(new FreeLayer{Kind="Shape",Name="backdrop",X=100f,Y=60f,Width=320f,Height=160f,Slanted=false,ColorArgb=Color.FromArgb(255,40,90,140).ToArgb(),Text=""});
                legacy.Layers.Add(new FreeLayer{Kind="Badge",Name="legacy badge",X=100f,Y=60f,Width=256f,Height=108f,KeepAspect=false,Difficulty="IN",Level=15,Text=""});
                legacy.Save(legacyPath);
                var disk=FreeProject.Read(legacyPath);
                check(disk.Layers.Any(l=>l.Kind=="Badge"&&l.Width==256f&&l.Height==108f),"a legacy non-canonical badge is still a valid project file");
                check(FreeProject.CurrentVersion==1&&disk.Version==1,"badge canonicalisation needs no project format bump");
                using(var probe=new FreePainter())probe.Prepare(disk);
                dirty=false;LoadProject(legacyPath);
                check(project.Width==800&&project.Height==400&&project.Layers.Count==2,"the legacy project becomes the editor project");
                var badge=project.Layers.FirstOrDefault(l=>l.Kind=="Badge");
                check(badge!=null&&Math.Abs(badge.Width-128f)<.001f&&Math.Abs(badge.Height-108f)<.001f,"loading a legacy badge canonicalises its box");
                check(badge.X==100f&&badge.Y==60f,"badge canonicalisation keeps X / Y");
                check(badge.KeepAspect,"the loaded badge is marked aspect-true");
                using(var bitmap=new Bitmap(Math.Max(1,canvas.Width),Math.Max(1,canvas.Height)))using(var g=Graphics.FromImage(bitmap))PaintCanvas(canvas,new PaintEventArgs(g,canvas.ClientRectangle));
                float badgeFit=view.Width/project.Width;
                check(badgeFit>.05f,"badge load test needs a usable preview scale (got "+badgeFit+")");
                groupMove.Checked=false;SelectLayer(badge);
                var badgeBox=SelectionBounds(Targets());
                check(Math.Abs(badgeBox.Width-badge.Width)<.001f&&Math.Abs(badgeBox.Height-badge.Height)<.001f,"badge selection bounds equal its declared box");
                check(aspect.Checked&&!aspect.Enabled,"a badge pins the aspect toggle on and disabled");
                int blankX=(int)(view.X+(badge.X+200f)*badgeFit),blankY=(int)(view.Y+(badge.Y+50f)*badgeFit);
                Down(canvas,new MouseEventArgs(MouseButtons.Left,1,blankX,blankY,0));
                check(Current!=null&&Current.Kind=="Shape","the retired blank badge area no longer blocks the layer behind it");
                Up(canvas,new MouseEventArgs(MouseButtons.Left,1,blankX,blankY,0));
                SelectLayer(badge);badge.X=300f;badge.Y=60f;
                int snapFromX=(int)(view.X+(badge.X+badge.Width/2f)*badgeFit),snapFromY=(int)(view.Y+(badge.Y+badge.Height/2f)*badgeFit);
                Down(canvas,new MouseEventArgs(MouseButtons.Left,1,snapFromX,snapFromY,0));
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(view.X+(project.Width-badge.Width/2f)*badgeFit),snapFromY,0));
                check(Math.Abs(SelectionBounds(Targets()).Right-project.Width)<1.5f,"badge snapping aligns the painted bounds to the canvas edge");
                Up(canvas,new MouseEventArgs(MouseButtons.Left,1,snapFromX,snapFromY,0));
                SelectLayer(badge);w.Value=200m;
                check(Math.Abs(badge.Height-200f*108f/128f)<.01f,"a badge width edit links the height at 128:108");
                h.Value=300m;
                check(Math.Abs(badge.Width-300f*128f/108f)<.01f,"a badge height edit links the width at 128:108");
                check(Math.Abs(badge.Width/badge.Height-128f/108f)<.0001f,"badge property edits keep the 128:108 ratio");
                badge.KeepAspect=false;SelectLayer(badge);
                var handle=SelectionBounds(Targets());
                int handleX=(int)(view.X+handle.Right*badgeFit),handleY=(int)(view.Y+handle.Bottom*badgeFit);
                int badgeUndo=undo.Count;
                Down(canvas,new MouseEventArgs(MouseButtons.Left,1,handleX,handleY,0));
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,handleX+30,handleY+10,0));
                Up(canvas,new MouseEventArgs(MouseButtons.Left,1,handleX+30,handleY+10,0));
                check(Math.Abs(badge.Width/badge.Height-128f/108f)<.0001f,"a single badge resize keeps the 128:108 ratio");
                check(undo.Count==badgeUndo+1,"one badge resize gesture records one undo entry");
                KeysPressed(this,new KeyEventArgs(Keys.Control|Keys.Z));
                var undone=project.Layers.FirstOrDefault(l=>l.Kind=="Badge");
                check(undone!=null&&Math.Abs(undone.Width/undone.Height-128f/108f)<.0001f,"undo restores a canonical badge");
                KeysPressed(this,new KeyEventArgs(Keys.Control|Keys.Y));
                var redone=project.Layers.FirstOrDefault(l=>l.Kind=="Badge");
                check(redone!=null&&Math.Abs(redone.Width/redone.Height-128f/108f)<.0001f,"redo restores a canonical badge");
                SelectLayer(project.Layers.FirstOrDefault(l=>l.Kind=="Shape"));
                check(aspect.Enabled&&aspect.Checked==Current.KeepAspect,"a non-badge layer restores the aspect toggle");
            }
            finally
            {
                try{if(File.Exists(legacyPath))File.Delete(legacyPath);}catch(IOException){}
            }
            // Milestone 10B: component-to-component move snapping. Canvas guides keep their 10A
            // tie priority, stationary layers contribute ordinary Bounds guides (no Kind special
            // cases), and the moving set never snaps to itself.
            string snapPath=Path.Combine(Path.GetTempPath(),"phifree-snap-"+Guid.NewGuid().ToString("N")+".phifree");
            string isolatePath=Path.Combine(Path.GetTempPath(),"phifree-snap-iso-"+Guid.NewGuid().ToString("N")+".phifree");
            string altPath=Path.Combine(Path.GetTempPath(),"phifree-snap-alt-"+Guid.NewGuid().ToString("N")+".phifree");
            try
            {
                var scene=new FreeProject{Width=1000,Height=600,SoftBackground=false};
                scene.Layers.Add(new FreeLayer{Kind="Shape",Name="target",X=600,Y=100,Width=200,Height=100,Slanted=false,Locked=true,ColorArgb=Color.FromArgb(255,60,120,180).ToArgb(),Text=""});
                scene.Layers.Add(new FreeLayer{Kind="Shape",Name="hidden",X=100,Y=300,Width=100,Height=100,Visible=false,Slanted=false,ColorArgb=Color.FromArgb(255,180,120,60).ToArgb(),Text=""});
                scene.Layers.Add(new FreeLayer{Kind="Shape",Name="near",X=508,Y=480,Width=100,Height=60,Slanted=false,ColorArgb=Color.FromArgb(255,90,160,90).ToArgb(),Text=""});
                scene.Layers.Add(new FreeLayer{Kind="Badge",Name="badge target",X=100,Y=500,Width=128,Height=108,Visible=false,Difficulty="IN",Level=15,Text=""});
                scene.Layers.Add(new FreeLayer{Kind="Shape",Name="group A",Group="snapg",X=300,Y=450,Width=100,Height=60,Slanted=false,ColorArgb=Color.FromArgb(255,120,120,200).ToArgb(),Text=""});
                scene.Layers.Add(new FreeLayer{Kind="Shape",Name="group B",Group="snapg",X=430,Y=450,Width=100,Height=60,Slanted=false,ColorArgb=Color.FromArgb(255,120,120,200).ToArgb(),Text=""});
                scene.Layers.Add(new FreeLayer{Kind="Shape",Name="mover",X=300,Y=300,Width=150,Height=100,Slanted=false,ColorArgb=Color.FromArgb(255,225,225,225).ToArgb(),Text=""});
                scene.Save(snapPath);
                var probe=FreeProject.Read(snapPath);using(var checker=new FreePainter())checker.Prepare(probe);
                dirty=false;LoadProject(snapPath);
                using(var bitmap=new Bitmap(Math.Max(1,canvas.Width),Math.Max(1,canvas.Height)))using(var g=Graphics.FromImage(bitmap))PaintCanvas(canvas,new PaintEventArgs(g,canvas.ClientRectangle));
                float f=view.Width/project.Width;
                check(f>.05f,"component snap test needs a usable preview scale (got "+f+")");
                var target=project.Layers.First(l=>l.Name=="target");
                var hiddenLayer=project.Layers.First(l=>l.Name=="hidden");
                var near=project.Layers.First(l=>l.Name=="near");
                var badgeTarget=project.Layers.First(l=>l.Name=="badge target");
                var groupA=project.Layers.First(l=>l.Name=="group A");
                var groupB=project.Layers.First(l=>l.Name=="group B");
                var mover=project.Layers.First(l=>l.Name=="mover");
                float? lastGuideX=null,lastGuideY=null;
                Action<FreeLayer,float,float> dragBy=delegate(FreeLayer layer,float moveX,float moveY)
                {
                    int x=(int)(view.X+(layer.X+layer.Width/2f)*f),y=(int)(view.Y+(layer.Y+layer.Height/2f)*f);
                    Down(canvas,new MouseEventArgs(MouseButtons.Left,1,x,y,0));
                    MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(x+moveX*f),(int)(y+moveY*f),0));
                    // Mouse up clears the guides, so remember what the gesture produced.
                    lastGuideX=activeVerticalGuide;lastGuideY=activeHorizontalGuide;
                    Up(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(x+moveX*f),(int)(y+moveY*f),0));
                };
                near.Visible=false;groupMove.Checked=false;
                // 1 / 2 / 3: left edge, centre and edge-to-edge against a locked stationary layer.
                mover.X=300f;mover.Y=300f;SelectLayer(mover);
                dragBy(mover,target.X-3f-mover.X,0f);
                check(Math.Abs(mover.X-target.X)<.01f,"a single layer snaps its left edge onto a stationary layer");
                check(lastGuideX.HasValue&&Math.Abs(lastGuideX.Value-target.X)<.01f,"the stationary left edge becomes the active guide");
                mover.X=300f;mover.Y=300f;SelectLayer(mover);
                float centreX=target.X+target.Width/2f;
                dragBy(mover,centreX-3f-(mover.X+mover.Width/2f),0f);
                check(Math.Abs(mover.X+mover.Width/2f-centreX)<.01f,"a single layer snaps its centre onto a stationary centre");
                check(lastGuideX.HasValue&&Math.Abs(lastGuideX.Value-centreX)<.01f,"the stationary centre becomes the active guide");
                mover.X=300f;mover.Y=300f;SelectLayer(mover);
                dragBy(mover,target.X-3f-(mover.X+mover.Width),0f);
                check(Math.Abs(mover.X+mover.Width-target.X)<.01f,"an edge-to-edge snap aligns the moving right edge to a stationary left edge");
                check(lastGuideX.HasValue&&lastGuideX.Value==target.X,"the edge-to-edge guide is the stationary left edge");
                // 4 / 5: a group snaps its whole selection bounds and keeps its internal layout.
                groupA.X=300f;groupA.Y=450f;groupB.X=430f;groupB.Y=450f;
                SelectLayer(groupA);groupMove.Checked=true;
                check(Targets().Count==2&&Math.Abs(SelectionBounds(Targets()).Width-230f)<.01f,"the moving set is the whole group");
                float offsetBefore=groupB.X-groupA.X;
                dragBy(groupA,target.X-4f-SelectionBounds(Targets()).Left,0f);
                check(Math.Abs(SelectionBounds(Targets()).Left-target.X)<.01f,"a group snaps its whole selection bounds onto a stationary layer");
                check(Math.Abs((groupB.X-groupA.X)-offsetBefore)<.001f,"the group keeps its internal relative layout while snapping");
                groupMove.Checked=false;
                // 8: hidden stationary layers never contribute guides.
                mover.X=hiddenLayer.X+1f;mover.Y=300f;SelectLayer(mover);
                dragBy(mover,4f,0f);
                check(mover.X>hiddenLayer.X+2.5f&&!lastGuideX.HasValue,"a hidden stationary layer never produces a snap guide");
                // 9: the 10A canvas snapping is untouched.
                mover.X=300f;mover.Y=300f;SelectLayer(mover);
                dragBy(mover,3f-mover.X,0f);
                check(Math.Abs(mover.X)<.01f,"canvas snapping still pulls the layer onto the canvas left edge");
                check(lastGuideX.HasValue&&lastGuideX.Value==0f,"the canvas guide is still x=0");
                // 10 / 11: nearest correction wins; an exact tie keeps the canvas guide.
                near.Visible=true;
                mover.X=300f;mover.Y=300f;SelectLayer(mover);
                dragBy(mover,505f-mover.X,0f);
                check(Math.Abs(mover.X-near.X)<.01f,"the nearest component guide wins over a farther canvas guide");
                check(lastGuideX.HasValue&&Math.Abs(lastGuideX.Value-near.X)<.01f,"the winning component guide becomes the active guide");
                near.X=510f;
                mover.X=300f;mover.Y=300f;SelectLayer(mover);
                dragBy(mover,505f-mover.X,0f);
                check(Math.Abs(mover.X-500f)<.01f,"an exact canvas/component tie keeps the canvas guide");
                check(lastGuideX.HasValue&&lastGuideX.Value==500f,"the tie keeps the canvas guide coordinate");
                near.Visible=false;
                // 12 / 13 / 14: one gesture may switch guides many times but stays one undo entry.
                groupA.X=300f;groupB.X=430f;
                mover.X=300f;mover.Y=300f;SelectLayer(mover);
                int gestureUndo=undo.Count;
                int gx=(int)(view.X+(mover.X+mover.Width/2f)*f),gy=(int)(view.Y+(mover.Y+mover.Height/2f)*f);
                float startPointerX=mover.X+mover.Width/2f;
                Down(canvas,new MouseEventArgs(MouseButtons.Left,1,gx,gy,0));
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(view.X+(startPointerX+200f)*f),gy,0));
                check(Math.Abs(mover.X-500f)<.01f&&activeVerticalGuide.HasValue&&activeVerticalGuide.Value==500f,"the gesture first snaps to the canvas guide");
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(view.X+(startPointerX+300f)*f),gy,0));
                check(Math.Abs(mover.X-target.X)<.01f&&activeVerticalGuide.HasValue&&activeVerticalGuide.Value==target.X,"a later step of the same gesture snaps to a component");
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(view.X+(startPointerX-120f)*f),gy,0));
                check(Math.Abs(mover.X-180f)<1.5f&&!activeVerticalGuide.HasValue,"leaving every guide range keeps the raw position and clears the guide");
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(view.X+(startPointerX+300f)*f),gy,0));
                check(Math.Abs(mover.X-target.X)<.01f&&activeVerticalGuide.HasValue,"returning to the guide range snaps to the component again");
                check(undo.Count==gestureUndo+1,"canvas/component guide switching still records exactly one undo entry");
                Up(canvas,new MouseEventArgs(MouseButtons.Left,1,gx,gy,0));
                check(!activeVerticalGuide.HasValue&&!activeHorizontalGuide.HasValue,"mouse up clears component snap guides");
                KeysPressed(this,new KeyEventArgs(Keys.Control|Keys.Z));
                var undone=project.Layers.First(l=>l.Name=="mover");
                check(Math.Abs(undone.X-300f)<.01f,"undo restores the whole component-snapped gesture");
                KeysPressed(this,new KeyEventArgs(Keys.Control|Keys.Y));
                var redone=project.Layers.First(l=>l.Name=="mover");
                check(Math.Abs(redone.X-target.X)<.01f&&Math.Abs(redone.Y-300f)<.01f,"redo restores the component-snapped result");
                // Undo/redo swaps the project snapshot, so re-bind the layers before touching them.
                mover=project.Layers.First(l=>l.Name=="mover");
                target=project.Layers.First(l=>l.Name=="target");
                badgeTarget=project.Layers.First(l=>l.Name=="badge target");
                // Guides are preview-only UI state and can never reach the export.
                mover.X=300f;mover.Y=300f;SelectLayer(mover);
                int ex=(int)(view.X+(mover.X+mover.Width/2f)*f),ey=(int)(view.Y+(mover.Y+mover.Height/2f)*f);
                Down(canvas,new MouseEventArgs(MouseButtons.Left,1,ex,ey,0));
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(view.X+(startPointerX+200f)*f),ey,0));
                check(activeVerticalGuide.HasValue,"the export isolation check needs an active guide");
                byte[] withGuide;using(var b=painter.Render(project,true))withGuide=Renderer.Encode(b);
                ClearSnapGuides();
                byte[] withoutGuide;using(var b=painter.Render(project,true))withoutGuide=Renderer.Encode(b);
                Up(canvas,new MouseEventArgs(MouseButtons.Left,1,ex,ey,0));
                check(withGuide.SequenceEqual(withoutGuide),"snap guides never reach the FreePainter export");
                // 15: a badge uses the ordinary Bounds path (no Kind special case anywhere).
                badgeTarget.Visible=true;
                mover.X=300f;mover.Y=100f;SelectLayer(mover);
                dragBy(mover,103f-mover.X,0f);
                check(Math.Abs(mover.X-badgeTarget.X)<.01f,"a badge layer snaps through the ordinary Bounds path");
                check(lastGuideX.HasValue&&Math.Abs(lastGuideX.Value-badgeTarget.X)<.01f,"the badge layer's Bounds edge is the active guide");
                // Isolation scene: only the shapes under test are visible, so no other guide interferes.
                var iso=new FreeProject{Width=1000,Height=600,SoftBackground=false};
                iso.Layers.Add(new FreeLayer{Kind="Shape",Name="iso behind",X=900,Y=300,Width=50,Height=50,Slanted=false,ColorArgb=Color.FromArgb(255,90,90,90).ToArgb(),Text=""});
                iso.Layers.Add(new FreeLayer{Kind="Shape",Name="iso mover",X=300,Y=300,Width=150,Height=100,Slanted=false,ColorArgb=Color.FromArgb(255,230,230,230).ToArgb(),Text=""});
                iso.Save(isolatePath);
                var isoProbe=FreeProject.Read(isolatePath);using(var checker=new FreePainter())checker.Prepare(isoProbe);
                dirty=false;LoadProject(isolatePath);
                using(var bitmap=new Bitmap(Math.Max(1,canvas.Width),Math.Max(1,canvas.Height)))using(var g=Graphics.FromImage(bitmap))PaintCanvas(canvas,new PaintEventArgs(g,canvas.ClientRectangle));
                float isoF=view.Width/project.Width;
                var isoMover=project.Layers.First(l=>l.Name=="iso mover");
                SelectLayer(isoMover);groupMove.Checked=false;
                float? isoGuideX=null;
                Action<float,float> isoDrag=delegate(float moveX,float moveY)
                {
                    int x=(int)(view.X+(isoMover.X+isoMover.Width/2f)*isoF),y=(int)(view.Y+(isoMover.Y+isoMover.Height/2f)*isoF);
                    Down(canvas,new MouseEventArgs(MouseButtons.Left,1,x,y,0));
                    MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(x+moveX*isoF),(int)(y+moveY*isoF),0));
                    isoGuideX=activeVerticalGuide;
                    Up(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(x+moveX*isoF),(int)(y+moveY*isoF),0));
                };
                isoMover.X=360f;isoMover.Y=300f;SelectLayer(isoMover);
                isoDrag(4f,0f);
                check(isoMover.X>362f&&!isoGuideX.HasValue,"an isolated move does not snap to the moved layer itself");
                check(isoMover.X<366f,"the isolated move keeps the raw pointer delta");
                // Milestone 10B.1: Alt temporarily bypasses every move guide.
                var alt=new FreeProject{Width=1000,Height=600,SoftBackground=false};
                alt.Layers.Add(new FreeLayer{Kind="Shape",Name="alt behind",X=900,Y=300,Width=50,Height=50,Slanted=false,ColorArgb=Color.FromArgb(255,80,80,120).ToArgb(),Text=""});
                alt.Layers.Add(new FreeLayer{Kind="Shape",Name="alt A",Group="altg",X=400,Y=500,Width=80,Height=40,Slanted=false,ColorArgb=Color.FromArgb(255,120,200,120).ToArgb(),Text=""});
                alt.Layers.Add(new FreeLayer{Kind="Shape",Name="alt B",Group="altg",X=520,Y=500,Width=80,Height=40,Slanted=false,ColorArgb=Color.FromArgb(255,120,200,120).ToArgb(),Text=""});
                alt.Layers.Add(new FreeLayer{Kind="Shape",Name="alt mover",X=300,Y=300,Width=150,Height=100,Slanted=false,ColorArgb=Color.FromArgb(255,235,235,235).ToArgb(),Text=""});
                alt.Save(altPath);
                var altProbe=FreeProject.Read(altPath);using(var checker=new FreePainter())checker.Prepare(altProbe);
                dirty=false;LoadProject(altPath);
                using(var bitmap=new Bitmap(Math.Max(1,canvas.Width),Math.Max(1,canvas.Height)))using(var g=Graphics.FromImage(bitmap))PaintCanvas(canvas,new PaintEventArgs(g,canvas.ClientRectangle));
                float altF=view.Width/project.Width;
                var altBehind=project.Layers.First(l=>l.Name=="alt behind");
                var altA=project.Layers.First(l=>l.Name=="alt A");
                var altB=project.Layers.First(l=>l.Name=="alt B");
                var altMover=project.Layers.First(l=>l.Name=="alt mover");
                float? altGuideX=null,altGuideY=null;
                Action<FreeLayer,float,float> altDrag=delegate(FreeLayer layer,float moveX,float moveY)
                {
                    int x=(int)(view.X+(layer.X+layer.Width/2f)*altF),y=(int)(view.Y+(layer.Y+layer.Height/2f)*altF);
                    Down(canvas,new MouseEventArgs(MouseButtons.Left,1,x,y,0));
                    MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(x+moveX*altF),(int)(y+moveY*altF),0));
                    altGuideX=activeVerticalGuide;altGuideY=activeHorizontalGuide;
                    Up(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(x+moveX*altF),(int)(y+moveY*altF),0));
                };
                groupMove.Checked=false;
                // 1: canvas snapping without Alt.
                altMover.X=300f;altMover.Y=300f;SelectLayer(altMover);
                altDrag(altMover,3f-altMover.X,0f);
                check(Math.Abs(altMover.X)<.01f&&altGuideX.HasValue&&altGuideX.Value==0f,"a move without Alt still snaps to the canvas guide");
                // 2: the same move with Alt keeps the raw delta and shows no guide.
                altMover.X=300f;altMover.Y=300f;SelectLayer(altMover);altHeld=delegate{return true;};
                altDrag(altMover,3f-altMover.X,0f);
                check(Math.Abs(altMover.X-3f)<1.5f,"holding Alt keeps the raw canvas-side delta");
                check(!altGuideX.HasValue&&!altGuideY.HasValue,"holding Alt clears both canvas guides");
                // 3 / 4: the same for a component guide.
                altHeld=delegate{return false;};
                altMover.X=300f;altMover.Y=300f;SelectLayer(altMover);
                altDrag(altMover,altBehind.X-3f-altMover.X,0f);
                check(Math.Abs(altMover.X-altBehind.X)<.01f&&altGuideX.HasValue&&Math.Abs(altGuideX.Value-altBehind.X)<.01f,"a move without Alt still snaps to a component guide");
                altMover.X=300f;altMover.Y=300f;SelectLayer(altMover);altHeld=delegate{return true;};
                altDrag(altMover,altBehind.X-3f-altMover.X,0f);
                check(Math.Abs(altMover.X-(altBehind.X-3f))<1.5f,"holding Alt keeps the raw component-side delta");
                check(!altGuideX.HasValue&&!altGuideY.HasValue,"holding Alt bypasses component guides too");
                // 5 / 6 / 7: Alt pressed and released inside one gesture keeps a single undo entry.
                altHeld=delegate{return false;};
                altMover.X=300f;altMover.Y=300f;SelectLayer(altMover);
                int altUndo=undo.Count;int ax=(int)(view.X+(altMover.X+altMover.Width/2f)*altF),ay=(int)(view.Y+(altMover.Y+altMover.Height/2f)*altF);
                float altStart=altMover.X+altMover.Width/2f;
                Down(canvas,new MouseEventArgs(MouseButtons.Left,1,ax,ay,0));
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(view.X+(altStart+597f)*altF),ay,0));
                check(Math.Abs(altMover.X-altBehind.X)<.01f&&activeVerticalGuide.HasValue,"the gesture first snaps to the component guide");
                altHeld=delegate{return true;};
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(view.X+(altStart+300f)*altF),ay,0));
                check(Math.Abs(altMover.X-600f)<1.5f&&!activeVerticalGuide.HasValue&&!activeHorizontalGuide.HasValue,"pressing Alt mid-gesture switches to raw movement and clears the guides");
                altHeld=delegate{return false;};
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,(int)(view.X+(altStart+597f)*altF),ay,0));
                check(Math.Abs(altMover.X-altBehind.X)<.01f&&activeVerticalGuide.HasValue,"releasing Alt restores snapping on the next move");
                check(undo.Count==altUndo+1,"Alt inside a gesture never adds another undo entry");
                Up(canvas,new MouseEventArgs(MouseButtons.Left,1,ax,ay,0));
                check(!activeVerticalGuide.HasValue&&!activeHorizontalGuide.HasValue,"mouse up after an Alt gesture clears the guides");
                KeysPressed(this,new KeyEventArgs(Keys.Control|Keys.Z));
                var altUndone=project.Layers.First(l=>l.Name=="alt mover");
                check(Math.Abs(altUndone.X-300f)<.01f,"undo restores the whole Alt gesture in one step");
                KeysPressed(this,new KeyEventArgs(Keys.Control|Keys.Y));
                var altRedone=project.Layers.First(l=>l.Name=="alt mover");
                check(Math.Abs(altRedone.X-altBehind.X)<.01f,"redo restores the snapped result in one step");
                // 8: Alt moves a whole group by the raw delta and keeps its relative layout.
                altA=project.Layers.First(l=>l.Name=="alt A");altB=project.Layers.First(l=>l.Name=="alt B");
                SelectLayer(altA);groupMove.Checked=true;altHeld=delegate{return true;};
                altDrag(altA,37f,0f);
                check(Math.Abs((altA.X-400f)-37f)<1.5f&&Math.Abs((altB.X-520f)-37f)<1.5f,"Alt moves the whole group by the raw delta");
                check(Math.Abs((altB.X-altA.X)-120f)<.001f,"Alt keeps the group's internal relative layout");
                check(!altGuideX.HasValue&&!altGuideY.HasValue,"an Alt group move shows no guides either");
                groupMove.Checked=false;altHeld=delegate{return false;};
                // 9: Alt never changes resize behaviour.
                altMover=project.Layers.First(l=>l.Name=="alt mover");
                altMover.X=300f;altMover.Y=300f;altMover.Width=150f;altMover.Height=100f;SelectLayer(altMover);
                var resizeBounds=SelectionBounds(Targets());
                int rrx=(int)(view.X+resizeBounds.Right*altF),rry=(int)(view.Y+resizeBounds.Bottom*altF);
                altHeld=delegate{return true;};
                Down(canvas,new MouseEventArgs(MouseButtons.Left,1,rrx,rry,0));
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,rrx+20,rry+10,0));
                float altWidth=altMover.Width,altHeight=altMover.Height;
                check(!activeVerticalGuide.HasValue&&!activeHorizontalGuide.HasValue,"resizing with Alt held shows no guides");
                Up(canvas,new MouseEventArgs(MouseButtons.Left,1,rrx+20,rry+10,0));
                altMover.Width=150f;altMover.Height=100f;SelectLayer(altMover);
                resizeBounds=SelectionBounds(Targets());
                rrx=(int)(view.X+resizeBounds.Right*altF);rry=(int)(view.Y+resizeBounds.Bottom*altF);
                altHeld=delegate{return false;};
                Down(canvas,new MouseEventArgs(MouseButtons.Left,1,rrx,rry,0));
                MovePointer(canvas,new MouseEventArgs(MouseButtons.Left,1,rrx+20,rry+10,0));
                check(altMover.Width==altWidth&&altMover.Height==altHeight,"resize produces the same result with and without Alt");
                check(altWidth>150f&&altHeight>100f,"the resize actually grew the layer");
                Up(canvas,new MouseEventArgs(MouseButtons.Left,1,rrx+20,rry+10,0));
                project.Validate();
            }
            finally
            {
                try{if(File.Exists(snapPath))File.Delete(snapPath);}catch(IOException){}
                try{if(File.Exists(isolatePath))File.Delete(isolatePath);}catch(IOException){}
                try{if(File.Exists(altPath))File.Delete(altPath);}catch(IOException){}
            }
            project.Validate();return checks;
        }
    }
}
