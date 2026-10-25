using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;

namespace PhigrosEnhancer
{
    static class FreeTests
    {
        static int count;
        static void Check(bool ok,string message){if(!ok)throw new Exception("Free test: "+message);count++;}
        static Rectangle TextInk(Bitmap image)
        {
            int left=image.Width,top=image.Height,right=-1,bottom=-1;
            for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++)if(image.GetPixel(x,y).A>128){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
            Check(right>=left,"text pixels are present");return Rectangle.FromLTRB(left,top,right+1,bottom+1);
        }
        // ------------------------------------------------------------------
        // Milestone 2 characterisation tests for the free-layout canvas model.
        // The preview viewport lives in private members of FreeLayoutForm, so the
        // viewport math below mirrors FreeLayoutForm.PaintCanvas / Logical /
        // ResizeCanvas and is additionally cross-checked against the real
        // FreePainter.Draw / FreePainter.Render output.
        // ------------------------------------------------------------------
        static void CheckClose(float actual,float expected,float tolerance,string what)
        {
            if(Math.Abs(actual-expected)>tolerance)
                throw new Exception("Free test: "+what+" (expected "+expected.ToString("0.####")+", actual "+actual.ToString("0.####")+")");
            count++;
        }
        static Rectangle ColorInkBounds(Bitmap image,int top,int bottom,int r,int g,int b,string what)
        {
            int left=image.Width,right=-1,minY=image.Height,maxY=-1;
            int from=Math.Max(0,top),to=Math.Min(image.Height,bottom);
            for(int y=from;y<to;y++)for(int x=0;x<image.Width;x++)
            {Color c=image.GetPixel(x,y);if(c.A>240&&c.R==r&&c.G==g&&c.B==b){left=Math.Min(left,x);right=Math.Max(right,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);}}
            if(right<0)throw new Exception("Free test: expected rendered ink for "+what);
            return Rectangle.FromLTRB(left,minY,right+1,maxY+1);
        }
        // Mirrors the centred uniform fit in FreeLayoutForm.PaintCanvas (32px total margin).
        static RectangleF FreeView(int panelWidth,int panelHeight,int canvasWidth,int canvasHeight)
        {
            float fit=Math.Max(.001f,Math.Min((panelWidth-32f)/canvasWidth,(panelHeight-32f)/canvasHeight));
            return new RectangleF((panelWidth-canvasWidth*fit)/2f,(panelHeight-canvasHeight*fit)/2f,canvasWidth*fit,canvasHeight*fit);
        }
        static PointF FreeCanvasToScreen(RectangleF view,int canvasWidth,float x,float y)
        {
            float fit=view.Width/canvasWidth;
            return new PointF(view.X+x*fit,view.Y+y*fit);
        }
        // Mirrors FreeLayoutForm.Logical.
        static PointF FreeScreenToLogical(RectangleF view,int canvasWidth,float x,float y)
        {
            float fit=view.Width/canvasWidth;
            return new PointF((x-view.X)/fit,(y-view.Y)/fit);
        }
        // Mirrors FreeLayoutForm.ResizeCanvas.
        static void ResizeCanvasMirror(FreeProject p,int width,int height)
        {
            float fit=Math.Min((float)width/p.Width,(float)height/p.Height);
            float left=(width-p.Width*fit)/2f,top=(height-p.Height*fit)/2f;
            foreach(FreeLayer c in p.Layers)
            {c.X=left+c.X*fit;c.Y=top+c.Y*fit;c.Width=Math.Max(1,c.Width*fit);c.Height=Math.Max(1,c.Height*fit);c.FontSize=Math.Max(1,Math.Min(1000,c.FontSize*fit));}
            p.Width=width;p.Height=height;
        }
        static void VerifyFreePreviewTransform()
        {
            int[][] canvases={new int[]{2740,1080},new int[]{1440,1080},new int[]{3840,1080}};
            foreach(int[] c in canvases)
            {
                int cw=c[0],ch=c[1];string tag=cw+"x"+ch;
                RectangleF view=FreeView(1400,860,cw,ch);
                CheckClose(view.Width/view.Height,(float)cw/ch,.001f,"free view keeps canvas aspect "+tag);
                PointF[] probes={new PointF(10,10),new PointF(cw/2f,ch/2f),new PointF(cw-10,ch-10),new PointF(170,190)};
                foreach(PointF q in probes)
                {
                    PointF s=FreeCanvasToScreen(view,cw,q.X,q.Y);
                    PointF back=FreeScreenToLogical(view,cw,s.X,s.Y);
                    CheckClose(back.X,q.X,.01f,"free preview round trip X "+tag+" canvas "+q.X+","+q.Y);
                    CheckClose(back.Y,q.Y,.01f,"free preview round trip Y "+tag+" canvas "+q.X+","+q.Y);
                }
                PointF origin=FreeCanvasToScreen(view,cw,0,0);
                PointF far=FreeCanvasToScreen(view,cw,cw,ch);
                CheckClose(origin.X,view.Left,.01f,"free preview canvas X=0 maps to view left "+tag);
                CheckClose(origin.Y,view.Top,.01f,"free preview canvas Y=0 maps to view top "+tag);
                CheckClose(far.X,view.Right,.02f,"free preview canvas right maps to view right "+tag);
                CheckClose(far.Y,view.Bottom,.02f,"free preview canvas bottom maps to view bottom "+tag);
                Check(view.Width/cw>.001f&&view.Width/cw<10f,"free preview fit is a usable uniform scale "+tag);
            }
        }
        static void VerifyFreePreviewRenderedGeometry()
        {
            int[][] canvases={new int[]{2740,1080},new int[]{1440,1080}};
            foreach(int[] c in canvases)
            {
                int cw=c[0],ch=c[1];string tag=cw+"x"+ch;
                var p=new FreeProject{Width=cw,Height=ch,SoftBackground=false};
                var shape=new FreeLayer{Kind="Shape",Name="probe",X=400,Y=300,Width=300,Height=200,Slanted=false,ColorArgb=Color.FromArgb(255,255,0,255).ToArgb()};
                p.Layers.Add(shape);
                RectangleF view=FreeView(1400,860,cw,ch);
                float fit=view.Width/cw;
                using(var bmp=new Bitmap(1400,860))
                {
                    using(var g=Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        var state=g.Save();
                        g.TranslateTransform(view.X,view.Y);
                        g.ScaleTransform(view.Width/cw,view.Height/ch);
                        g.SetClip(new RectangleF(0,0,cw,ch));
                        using(var painter=new FreePainter())painter.Draw(g,p,true);
                        g.Restore(state);
                    }
                    int top=(int)Math.Floor(view.Y+shape.Y*fit)-20;
                    int bottom=(int)Math.Ceiling(view.Y+(shape.Y+shape.Height)*fit)+20;
                    Rectangle ink=ColorInkBounds(bmp,top,bottom,255,0,255,"free preview shape "+tag);
                    CheckClose(ink.Left,view.X+shape.X*fit,2f,"free preview shape left follows view fit "+tag);
                    CheckClose(ink.Top,view.Y+shape.Y*fit,2f,"free preview shape top follows view fit "+tag);
                    CheckClose(ink.Width,shape.Width*fit,2f,"free preview shape width follows view fit "+tag);
                    CheckClose(ink.Height,shape.Height*fit,2f,"free preview shape height follows view fit "+tag);
                }
            }
        }
        static void VerifyFreeExportIdentity()
        {
            int[][] canvases={new int[]{2740,1080},new int[]{1440,1080},new int[]{3840,1080}};
            foreach(int[] c in canvases)
            {
                int cw=c[0],ch=c[1];string tag=cw+"x"+ch;
                var p=new FreeProject{Width=cw,Height=ch,SoftBackground=false};
                var shape=new FreeLayer{Kind="Shape",Name="probe",X=500,Y=250,Width=320,Height=180,Slanted=false,ColorArgb=Color.FromArgb(255,0,255,0).ToArgb()};
                p.Layers.Add(shape);
                int sx=(int)shape.X,sy=(int)shape.Y,sw=(int)shape.Width,sh=(int)shape.Height;
                using(var painter=new FreePainter())using(var b=painter.Render(p,true))
                {
                    Check(b.Width==cw&&b.Height==ch,"free export bitmap equals canvas size "+tag);
                    Check(b.GetPixel(sx+2,sy+2).ToArgb()==shape.ColorArgb,"free export layer starts at its canvas X/Y "+tag);
                    Check(b.GetPixel(sx+sw-3,sy+sh-3).ToArgb()==shape.ColorArgb,"free export layer fills its canvas size "+tag);
                    Check(b.GetPixel(sx-2,sy-2).A==0,"free export has no ink before layer X/Y "+tag);
                    Check(b.GetPixel(sx+sw+2,sy+sh/2).A==0,"free export has no ink after layer width "+tag);
                    RectangleF view=FreeView(1400,860,cw,ch);
                    float fit=view.Width/cw;
                    if(Math.Abs(fit-1f)>.01f)
                        Check(Math.Abs(view.X+shape.X*fit-shape.X)>1f,"free export ignores preview fit offset "+tag);
                }
            }
        }
        static void VerifyResizeCanvasCharacterization()
        {
            int[][] targets={new int[]{1440,1080},new int[]{3840,1080},new int[]{1080,1920},new int[]{3840,2160}};
            foreach(int[] t in targets)
            {
                int nw=t[0],nh=t[1];string tag="2740x1080->"+nw+"x"+nh;
                var p=new FreeProject();
                var centre=new FreeLayer{Kind="Text",Name="centre",X=1370,Y=540,Width=400,Height=100,FontSize=40};
                var corner=new FreeLayer{Kind="Image",Name="corner",X=100,Y=100,Width=520,Height=250,FontSize=30};
                var huge=new FreeLayer{Kind="Text",Name="huge",X=10,Y=10,Width=100,Height=100,FontSize=900};
                p.Layers.Add(centre);p.Layers.Add(corner);p.Layers.Add(huge);
                float oldRatio=corner.Width/corner.Height;
                ResizeCanvasMirror(p,nw,nh);
                Check(p.Width==nw&&p.Height==nh,"resize canvas updates persisted size "+tag);
                CheckClose(centre.X,nw/2f,.01f,"resize keeps canvas centre X "+tag);
                CheckClose(centre.Y,nh/2f,.01f,"resize keeps canvas centre Y "+tag);
                CheckClose(corner.Width/corner.Height,oldRatio,.0001f,"resize preserves component aspect ratio "+tag);
                float fit=Math.Min((float)nw/2740f,(float)nh/1080f);
                float left=(nw-2740f*fit)/2f,top=(nh-1080f*fit)/2f;
                CheckClose(corner.X,left+100f*fit,.01f,"resize scales component X by canvas fit "+tag);
                CheckClose(corner.Y,top+100f*fit,.01f,"resize scales component Y by canvas fit "+tag);
                Check(huge.FontSize>=1f&&huge.FontSize<=1000f,"resize clamps font size "+tag);
                if(fit>1.1f)CheckClose(huge.FontSize,1000f,.01f,"resize clamps oversized font to 1000 "+tag);
                RectangleF view=FreeView(1400,860,2740,1080);
                float panelFit=view.Width/2740f;
                if(Math.Abs(panelFit-fit)>.01f)
                    Check(Math.Abs(corner.X-(view.X+100f*panelFit))>.5f,"resize ignores preview panel fit "+tag);
            }
        }
        static void VerifyFreeDifficultyColorLiteral()
        {
            Color literalIn=Color.FromArgb(207,19,19);
            var p=new FreeProject{Width=800,Height=400,SoftBackground=false};
            p.Layers.Add(new FreeLayer{Kind="Badge",Name="IN",X=100,Y=100,Width=128,Height=108,Difficulty="IN",Level=15});
            using(var painter=new FreePainter())using(var b=painter.Render(p,true))
                Check(b.GetPixel(135,103).ToArgb()==literalIn.ToArgb(),"free-layout IN badge renders literal #CF1313 pixels");
        }
        // FreeProject.Save now commits through AtomicFile; keep a focused round-trip
        // regression for both the create and the replace branch.
        static void VerifyFreeAtomicSave(string folder)
        {
            string dir=Path.Combine(folder,"原子保存测试");
            Directory.CreateDirectory(dir);
            foreach(string stale in Directory.GetFiles(dir))File.Delete(stale);
            var project=new FreeProject{Width=320,Height=180,SoftBackground=false};
            project.Layers.Add(new FreeLayer{Kind="Text",Name="probe",X=10,Y=20,Width=100,Height=40,Text="atomic"});
            string path=Path.Combine(dir,"原子保存.phifree");
            project.Save(path);
            var reloaded=FreeProject.Read(path);
            Check(reloaded.Layers.Count==1&&reloaded.Layers[0].Text=="atomic"&&reloaded.Width==320,
                "FreeProject.Save round trips through the atomic path");
            Check(Directory.GetFiles(dir,"*.tmp").Length==0,"FreeProject.Save leaves no temp file after creating a project");
            project.Save(path);
            Check(FreeProject.Read(path).Layers[0].Text=="atomic","FreeProject.Save replaces an existing project with valid content");
            Check(Directory.GetFiles(dir,"*.tmp").Length==0,"FreeProject.Save leaves no temp file after replacing a project");
        }
        // Milestone 7: .phifree format versioning and in-memory migration.
        static void VerifyFreeProjectVersioning(string folder)
        {
            string dir=Path.Combine(folder,"版本兼容测试");
            Directory.CreateDirectory(dir);
            foreach(string stale in Directory.GetFiles(dir))File.Delete(stale);

            var p=new FreeProject{Width=320,Height=180,SoftBackground=false};
            p.Layers.Add(new FreeLayer{Kind="Text",Name="probe",X=10,Y=20,Width=100,Height=40,Text="versioned"});
            string v1=Path.Combine(dir,"v1.phifree");
            p.Save(v1);
            string text=File.ReadAllText(v1);

            Check(FreeProject.Read(v1).Version==FreeProject.CurrentVersion,"phifree round trip keeps CurrentVersion");
            Check(text.Contains("<Version>"+FreeProject.CurrentVersion+"</Version>"),"phifree writes the current version element");
            Check(FreeProject.Read(v1).Version==FreeProject.CurrentVersion&&FreeProject.Read(v1).Layers.Count==1,"explicit V1 phifree loads");

            string future=Path.Combine(dir,"future.phifree");
            File.WriteAllText(future,text.Replace("<Version>"+FreeProject.CurrentVersion+"</Version>","<Version>"+(FreeProject.CurrentVersion+1)+"</Version>"));
            bool futureRejected=false;string futureMessage="";
            try{FreeProject.Read(future);}catch(InvalidDataException ex){futureRejected=true;futureMessage=ex.Message;}
            Check(futureRejected,"future phifree version is rejected");
            Check(futureMessage.Contains("更新版本"),"future phifree error names the newer-version cause ("+futureMessage+")");

            byte[] before=File.ReadAllBytes(v1);DateTime stampBefore=File.GetLastWriteTimeUtc(v1);
            FreeProject.Read(v1);
            Check(File.ReadAllBytes(v1).SequenceEqual(before)&&File.GetLastWriteTimeUtc(v1)==stampBefore,"phifree Read does not rewrite the file on disk");

            string noVersion=Path.Combine(dir,"noversion.phifree");
            File.WriteAllText(noVersion,text.Replace("<Version>"+FreeProject.CurrentVersion+"</Version>",""));
            Check(FreeProject.Read(noVersion).Version==FreeProject.CurrentVersion,"phifree without a Version element defaults to CurrentVersion");

            string invalid=Path.Combine(dir,"invalid.phifree");
            File.WriteAllText(invalid,text.Replace("<Width>320</Width>","<Width>100</Width>"));
            bool invalidRejected=false;
            try{FreeProject.Read(invalid);}catch(InvalidDataException){invalidRejected=true;}
            Check(invalidRejected,"migrated phifree is still validated");

            string foreign=Path.Combine(dir,"foreign.phipreview");
            File.WriteAllText(foreign,"<?xml version=\"1.0\" encoding=\"utf-8\"?><Project><Version>2</Version></Project>");
            bool foreignRejected=false;
            try{FreeProject.Read(foreign);}catch(Exception){foreignRejected=true;}
            Check(foreignRejected,"phifree reader does not accept the .phipreview format");
        }
        // Milestone 3: the same LogicalViewportTransform must reproduce the free-layout
        // preview/Logical mirror for arbitrary canvas sizes and must not carry any
        // fixed 1920x1080 assumption.
        static void VerifyFreeGeometryHelperEquivalence()
        {
            int[][] canvases={new int[]{2740,1080},new int[]{1440,1080},new int[]{3840,1080},new int[]{1080,1920}};
            foreach(int[] c in canvases)
            {
                int cw=c[0],ch=c[1];string tag=cw+"x"+ch;
                RectangleF view=FreeView(1400,860,cw,ch);
                var t=new LogicalViewportTransform(cw,ch,view);
                Check(t.LogicalWidth==(float)cw&&t.LogicalHeight==(float)ch,"helper keeps free canvas size "+tag);
                CheckClose(t.HorizontalScale,t.VerticalScale,.0001f,"helper free scale is uniform "+tag);
                PointF[] probes={new PointF(10,10),new PointF(cw/2f,ch/2f),new PointF(cw-10,ch-10),new PointF(170,190)};
                foreach(PointF q in probes)
                {
                    PointF expected=FreeCanvasToScreen(view,cw,q.X,q.Y);
                    PointF actual=t.LogicalToScreen(q);
                    CheckClose(actual.X,expected.X,.01f,"helper logical->screen matches free preview X "+tag+" canvas "+q.X+","+q.Y);
                    CheckClose(actual.Y,expected.Y,.01f,"helper logical->screen matches free preview Y "+tag+" canvas "+q.X+","+q.Y);
                    PointF mirrored=FreeScreenToLogical(view,cw,expected.X,expected.Y);
                    PointF back=t.ScreenToLogical(expected);
                    CheckClose(back.X,mirrored.X,.01f,"helper screen->logical matches FreeLayoutForm.Logical X "+tag+" canvas "+q.X+","+q.Y);
                    CheckClose(back.Y,mirrored.Y,.01f,"helper screen->logical matches FreeLayoutForm.Logical Y "+tag+" canvas "+q.X+","+q.Y);
                }
                SizeF delta=new SizeF(25f,15f);
                SizeF screenDelta=t.LogicalDeltaToScreen(delta);
                CheckClose(screenDelta.Width,delta.Width*t.HorizontalScale,.001f,"helper logical delta uses free logical width "+tag);
                CheckClose(t.ScreenDeltaToLogical(screenDelta).Width,delta.Width,.01f,"helper screen delta inverts free delta "+tag);
                if(cw!=1920||ch!=1080)
                {
                    var classic=new LogicalViewportTransform(ClassicGeometry.ReferenceWidth,ClassicGeometry.ReferenceHeight,view);
                    PointF probe=new PointF(cw/2f,ch/2f);
                    Check(Math.Abs(t.LogicalToScreen(probe).X-classic.LogicalToScreen(probe).X)>.5f,
                        "free helper does not assume 1920x1080 logical width "+tag);
                }
            }
        }
        // Milestone 3 defensive tests in the free-layout context. The full invalid
        // input matrix lives in the classic suite; here we cover representative cases
        // plus the legal negative view offsets a centred fit can produce.
        static void VerifyFreeTransformGuards()
        {
            bool rejected=false;
            try { new LogicalViewportTransform(2740f,1080f,new RectangleF(16f,18f,0f,828f)); }
            catch(ArgumentOutOfRangeException) { rejected=true; }
            Check(rejected,"free transform rejects zero view width");
            rejected=false;
            try { new LogicalViewportTransform(float.NaN,1080f,new RectangleF(16f,18f,1368f,828f)); }
            catch(ArgumentOutOfRangeException) { rejected=true; }
            Check(rejected,"free transform rejects NaN logical width");
            rejected=false;
            try { new LogicalViewportTransform(2740f,1080f,new RectangleF(float.PositiveInfinity,18f,1368f,828f)); }
            catch(ArgumentOutOfRangeException) { rejected=true; }
            Check(rejected,"free transform rejects infinite view offset");
            var t=new LogicalViewportTransform(2740f,1080f,new RectangleF(-500f,-250f,1368f,540f));
            Check(t.View.X==-500f&&t.View.Y==-250f,"free transform accepts negative view offsets");
            PointF origin=t.LogicalToScreen(new PointF(0f,0f));
            Check(origin.X==-500f&&origin.Y==-250f,"free transform maps logical origin to the negative view offset");
            PointF back=t.ScreenToLogical(origin);
            CheckClose(back.X,0f,.01f,"free negative-offset transform round trips X");
            CheckClose(back.Y,0f,.01f,"free negative-offset transform round trips Y");
            PointF probe=new PointF(1370f,540f);
            PointF roundTrip=t.ScreenToLogical(t.LogicalToScreen(probe));
            CheckClose(roundTrip.X,probe.X,.01f,"free negative-offset transform round trips centre X");
            CheckClose(roundTrip.Y,probe.Y,.01f,"free negative-offset transform round trips centre Y");
        }
        // Milestone 10A: pure move-snapping math (canvas guides only).
        static void VerifyFreeSnap()
        {
            const float cw=1000f,ch=600f,tol=10f;
            var b=new RectangleF(100f,80f,200f,100f);

            SnapResult far=FreeSnap.SnapMove(b,0f,0f,cw,ch,tol,tol);
            Check(!far.HasVerticalGuide&&!far.HasHorizontalGuide&&far.DeltaX==0f&&far.DeltaY==0f,
                "snap leaves far-away bounds untouched");

            SnapResult left=FreeSnap.SnapMove(b,-95f,0f,cw,ch,tol,tol);
            Check(left.HasVerticalGuide&&left.VerticalGuideX==0f&&Math.Abs(left.DeltaX-(-100f))<.001f&&!left.HasHorizontalGuide,
                "snap aligns the left edge to x=0");

            SnapResult centre=FreeSnap.SnapMove(b,305f,0f,cw,ch,tol,tol);
            Check(centre.HasVerticalGuide&&centre.VerticalGuideX==cw/2f&&Math.Abs(centre.DeltaX-300f)<.001f,
                "snap centres the bounds on the horizontal canvas centre");

            SnapResult right=FreeSnap.SnapMove(b,695f,0f,cw,ch,tol,tol);
            Check(right.HasVerticalGuide&&right.VerticalGuideX==cw&&Math.Abs(right.DeltaX-700f)<.001f,
                "snap aligns the right edge to x=width");

            SnapResult top=FreeSnap.SnapMove(b,0f,-75f,cw,ch,tol,tol);
            Check(top.HasHorizontalGuide&&top.HorizontalGuideY==0f&&Math.Abs(top.DeltaY-(-80f))<.001f,
                "snap aligns the top edge to y=0");

            SnapResult midY=FreeSnap.SnapMove(b,0f,175f,cw,ch,tol,tol);
            Check(midY.HasHorizontalGuide&&midY.HorizontalGuideY==ch/2f&&Math.Abs(midY.DeltaY-170f)<.001f,
                "snap centres the bounds on the vertical canvas centre");

            SnapResult bottom=FreeSnap.SnapMove(b,0f,415f,cw,ch,tol,tol);
            Check(bottom.HasHorizontalGuide&&bottom.HorizontalGuideY==ch&&Math.Abs(bottom.DeltaY-420f)<.001f,
                "snap aligns the bottom edge to y=height");

            SnapResult outside=FreeSnap.SnapMove(b,-85f,-60f,cw,ch,tol,tol);
            Check(!outside.HasVerticalGuide&&!outside.HasHorizontalGuide&&outside.DeltaX==-85f&&outside.DeltaY==-60f,
                "snap is skipped when every candidate is beyond the screen-pixel threshold");

            var wide=new RectangleF(100f,80f,980f,100f);
            SnapResult nearest=FreeSnap.SnapMove(wide,-90f,0f,cw,ch,tol,tol);
            Check(nearest.HasVerticalGuide&&nearest.VerticalGuideX==cw/2f&&Math.Abs(nearest.DeltaX-(-90f))<.001f,
                "snap prefers the candidate with the smallest correction");

            SnapResult tie=FreeSnap.SnapMove(wide,-95f,0f,cw,ch,tol,tol);
            Check(tie.HasVerticalGuide&&tie.VerticalGuideX==0f&&Math.Abs(tie.DeltaX-(-100f))<.001f,
                "snap resolves equal corrections by the fixed guide order");

            SnapResult both=FreeSnap.SnapMove(b,-95f,-75f,cw,ch,tol,tol);
            Check(both.HasVerticalGuide&&both.HasHorizontalGuide&&both.VerticalGuideX==0f&&both.HorizontalGuideY==0f,
                "snap activates a vertical and a horizontal guide at the same time");
        }
        // Milestone 8B: a badge layer's declared Bounds is its painted area, so the box
        // must be canonicalised to 128:108 instead of keeping an arbitrary container size.
        static Rectangle InkBounds(Bitmap image)
        {
            int left=image.Width,top=image.Height,right=-1,bottom=-1;
            for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++)if(image.GetPixel(x,y).A>0){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
            if(right<0)throw new Exception("Free test: expected rendered badge ink");
            return Rectangle.FromLTRB(left,top,right+1,bottom+1);
        }
        static void VerifyFreeBadgeGeometry()
        {
            Check(FreeBadgeGeometry.ReferenceWidth==128f&&FreeBadgeGeometry.ReferenceHeight==108f,
                "badge geometry exposes the 128x108 reference box");
            // The painter must keep using exactly the pre-8B aspect-fit expression.
            Check(FreeBadgeGeometry.FitScale(256f,108f)==Math.Min(256f/128f,108f/108f)
                &&FreeBadgeGeometry.FitScale(104f,87.75f)==Math.Min(104f/128f,87.75f/108f)
                &&FreeBadgeGeometry.FitScale(300f,100f)==Math.Min(300f/128f,100f/108f),
                "badge painter fit scale equals the pre-8B expression");
            float[][] boxes={new float[]{128f,108f,128f,108f},new float[]{256f,108f,128f,108f},new float[]{128f,216f,128f,108f},new float[]{300f,100f,118.5185f,100f}};
            string[] tags={"canonical 128x108","double width 256x108","double height 128x216","wide box 300x100"};
            for(int i=0;i<boxes.Length;i++)
            {
                float boxW=boxes[i][0],boxH=boxes[i][1];string tag=tags[i];
                SizeF size=FreeBadgeGeometry.CanonicalSize(boxW,boxH);
                CheckClose(size.Width,boxes[i][2],.001f,"badge canonical width "+tag);
                CheckClose(size.Height,boxes[i][3],.001f,"badge canonical height "+tag);
                CheckClose(size.Width/size.Height,128f/108f,.0001f,"badge canonical ratio is 128:108 "+tag);
                Check(size.Width<=boxW+.0001f&&size.Height<=boxH+.0001f,"badge canonical size never grows beyond the box "+tag);
                Check(Math.Abs(size.Width-boxW)<.001f||Math.Abs(size.Height-boxH)<.001f,"badge canonical size keeps one box edge "+tag);
                SizeF again=FreeBadgeGeometry.CanonicalSize(size.Width,size.Height);
                CheckClose(again.Width,size.Width,.0001f,"badge canonicalisation is idempotent in width "+tag);
                CheckClose(again.Height,size.Height,.0001f,"badge canonicalisation is idempotent in height "+tag);
                CheckClose(FreeBadgeGeometry.FitScale(boxW,boxH),size.Width/128f,.0001f,"badge fit scale matches the canonical width "+tag);
            }
            var p=new FreeProject{Width=800,Height=400,SoftBackground=false};
            var badge=new FreeLayer{Kind="Badge",Name="legacy badge",X=140f,Y=90f,Width=256f,Height=108f,KeepAspect=false,Difficulty="IN",Level=15,Text=""};
            var text=new FreeLayer{Kind="Text",Name="text",X=10f,Y=10f,Width=300f,Height=100f,KeepAspect=false};
            p.Layers.Add(badge);p.Layers.Add(text);
            FreeLayoutForm.CanonicalizeBadges(p);
            Check(badge.X==140f&&badge.Y==90f,"badge canonicalisation keeps the top-left anchor");
            CheckClose(badge.Width,128f,.001f,"badge canonicalisation shrinks the width");
            CheckClose(badge.Height,108f,.001f,"badge canonicalisation keeps the height");
            Check(badge.KeepAspect,"badge canonicalisation pins KeepAspect");
            Check(text.Width==300f&&text.Height==100f&&!text.KeepAspect,"badge canonicalisation leaves other layers untouched");
            FreeLayoutForm.CanonicalizeBadges(p);
            CheckClose(badge.Width,128f,.0001f,"badge canonicalisation is idempotent on the model");
        }
        static void VerifyTinyBadgeRoundTrip(string folder)
        {
            string path=Path.Combine(folder,"tiny-badge.phifree");
            var legacy=new FreeProject{Width=640,Height=360,SoftBackground=false};
            legacy.Layers.Add(new FreeLayer{Kind="Badge",X=100,Y=100,Width=1,Height=1});
            legacy.Save(path);byte[] original=File.ReadAllBytes(path);
            var editor=FreeProject.Read(path);FreeLayoutForm.CanonicalizeBadges(editor);editor.Validate();
            Check(editor.Layers[0].Width==1&&editor.Layers[0].Height==.84375f,"tiny legacy badge retains its exact painted box");
            Check(File.ReadAllBytes(path).SequenceEqual(original),"canonicalization never rewrites the legacy file");
            editor.Save(path);
            Check(editor.Layers[0].Height==.84375f,"saving a tiny badge does not change the editor bounds");
            var stored=FreeProject.Read(path);
            Check(stored.Version==1&&stored.Layers[0].Width>=1&&stored.Layers[0].Height>=1,"tiny badge saves an old-reader-compatible V1 container");
            using(var painter=new FreePainter())
            using(var before=painter.Render(editor,true))
            using(var after=painter.Render(stored,true))
                Check(Renderer.Encode(before).SequenceEqual(Renderer.Encode(after)),"tiny badge disk container preserves rendered pixels");
            FreeLayoutForm.CanonicalizeBadges(stored);stored.Validate();
            Check(stored.Layers[0].Width==1&&stored.Layers[0].Height==.84375f,"tiny badge reload restores canonical bounds");
            stored.Layers[0].Kind="Shape";
            bool rejected=false;try{stored.Validate();}catch(InvalidDataException){rejected=true;}
            Check(rejected,"subpixel height remains invalid for ordinary layers");
        }
        // Milestone 8B: canonicalising a legacy badge box must not move a single painted
        // pixel, because the painter already aspect-fits inside the box from its top-left.
        static void VerifyFreeBadgeRenderCompatibility()
        {
            float[][] boxes={new float[]{256f,108f},new float[]{128f,216f},new float[]{300f,100f},new float[]{104f,87.75f}};
            foreach(float[] box in boxes)
            {
                string tag=box[0].ToString("0.###")+"x"+box[1].ToString("0.###");
                var legacy=new FreeProject{Width=640,Height=360,SoftBackground=false};
                legacy.Layers.Add(new FreeLayer{Kind="Badge",Name="legacy "+tag,X=120f,Y=80f,Width=box[0],Height=box[1],KeepAspect=false,Difficulty="IN",Level=15,Text=""});
                float fit=Math.Min(box[0]/128f,box[1]/108f);
                var canonical=legacy.Copy();FreeLayoutForm.CanonicalizeBadges(canonical);
                RectangleF bounds=canonical.Layers[0].Bounds;
                CheckClose(bounds.Width,128f*fit,.001f,"canonical badge bound width equals the painted width "+tag);
                CheckClose(bounds.Height,108f*fit,.001f,"canonical badge bound height equals the painted height "+tag);
                using(var painter=new FreePainter())
                {
                    byte[] beforePng,afterPng;Rectangle beforeInk,afterInk;
                    using(var b=painter.Render(legacy,true)){beforePng=Renderer.Encode(b);beforeInk=InkBounds(b);}
                    using(var b=painter.Render(canonical,true)){afterPng=Renderer.Encode(b);afterInk=InkBounds(b);}
                    Check(beforePng.SequenceEqual(afterPng),"a legacy badge paints byte-identical pixels after canonicalisation "+tag);
                    Check(beforeInk==afterInk,"a legacy badge keeps the same painted ink bounds "+tag);
                    Check(beforeInk.Left>=bounds.X-4f&&beforeInk.Top>=bounds.Y-4f&&beforeInk.Right<=bounds.Right+4f&&beforeInk.Bottom<=bounds.Bottom+4f,
                        "badge ink stays inside the canonical box (3px shadow allowed) "+tag);
                }
            }
        }
        // Milestone 10B: canvas guides and stationary component guides must run through one
        // comparison, with the caller's guide order (canvas first, then project.Layers order)
        // deciding exact ties. Bounds is the only geometry used - no Kind special cases.
        static List<float> Guides(params float[] values){return new List<float>(values);}
        static readonly List<float> NoGuides=new List<float>();
        static SnapResult MoveSnap(RectangleF bounds,float dx,float dy,List<float> guidesX,List<float> guidesY,float tolerance)
        {return FreeSnap.SnapMoveToGuides(bounds,dx,dy,guidesX,guidesY,tolerance,tolerance);}
        static void VerifyFreeComponentSnap()
        {
            Check(FreeSnap.ThresholdScreenPixels==8f,"component snapping keeps the 8 screen pixel threshold");
            const float tol=10f;
            var b=new RectangleF(100f,80f,200f,100f);          // X 100/200/300, Y 80/130/180

            var r=MoveSnap(b,48f,0f,Guides(150f),NoGuides,tol);
            CheckClose(100f+r.DeltaX,150f,.001f,"component snap aligns the moving left edge to a stationary left edge");
            Check(r.HasVerticalGuide&&r.VerticalGuideX==150f,"the stationary left edge becomes the active guide");

            r=MoveSnap(b,195f,0f,Guides(400f),NoGuides,tol);
            CheckClose(200f+r.DeltaX,400f,.001f,"component snap aligns the moving centre to a stationary centre");
            Check(r.HasVerticalGuide&&r.VerticalGuideX==400f,"the stationary centre becomes the active guide");

            r=MoveSnap(b,195f,0f,Guides(500f),NoGuides,tol);
            CheckClose(300f+r.DeltaX,500f,.001f,"component snap aligns the moving right edge to a stationary right edge");

            r=MoveSnap(b,-5f,0f,Guides(300f),NoGuides,tol);
            CheckClose(300f+r.DeltaX,300f,.001f,"component snap supports edge-to-edge (moving right to stationary left)");
            CheckClose(r.DeltaX,0f,.001f,"the edge-to-edge case lands on the raw delta");

            r=MoveSnap(b,0f,215f,NoGuides,Guides(300f),tol);
            CheckClose(80f+r.DeltaY,300f,.001f,"component snap aligns the moving top edge to a stationary bottom edge");
            Check(r.HasHorizontalGuide&&r.HorizontalGuideY==300f,"the stationary bottom edge becomes the active guide");

            r=MoveSnap(b,195f,215f,Guides(400f),Guides(300f),tol);
            Check(r.HasVerticalGuide&&r.HasHorizontalGuide&&Math.Abs(r.DeltaX-200f)<.001f&&Math.Abs(r.DeltaY-220f)<.001f,
                "component snapping works on both axes in one move");
            CheckClose(200f+r.DeltaX,400f,.001f,"the simultaneous snap aligns X and Y to their own guides");

            r=MoveSnap(b,-96f,215f,Guides(0f,1000f),Guides(300f),tol);
            Check(r.HasVerticalGuide&&r.VerticalGuideX==0f&&Math.Abs(100f+r.DeltaX)<.001f,"X can snap to the canvas while Y snaps to a component");
            Check(r.HasHorizontalGuide&&r.HorizontalGuideY==300f,"the mixed canvas/component case keeps the component Y guide");

            r=MoveSnap(b,60f,-60f,Guides(600f),Guides(600f),tol);
            Check(!r.HasVerticalGuide&&!r.HasHorizontalGuide&&r.DeltaX==60f&&r.DeltaY==-60f,
                "component snapping is skipped when every correction exceeds the threshold");

            r=MoveSnap(b,40f,0f,Guides(150f),NoGuides,tol);     // left 140 -> exactly the threshold
            Check(r.HasVerticalGuide&&Math.Abs(100f+r.DeltaX-150f)<.001f,"a correction exactly at the threshold still snaps");

            r=MoveSnap(b,-95f,0f,Guides(0f,1000f,6f),NoGuides,tol);
            Check(r.HasVerticalGuide&&r.VerticalGuideX==6f&&Math.Abs(100f+r.DeltaX-6f)<.001f,
                "the closest component guide wins over the canvas guide");

            r=MoveSnap(b,-95f,0f,Guides(0f,1000f,10f),NoGuides,tol);
            Check(r.HasVerticalGuide&&r.VerticalGuideX==0f&&Math.Abs(100f+r.DeltaX)<.001f,
                "an exact canvas/component tie keeps the canvas guide priority");

            r=MoveSnap(b,50f,0f,Guides(148f,152f),NoGuides,tol);
            Check(r.HasVerticalGuide&&r.VerticalGuideX==148f&&Math.Abs(100f+r.DeltaX-148f)<.001f,
                "an exact component/component tie keeps the first guide in order");
            r=MoveSnap(b,50f,0f,Guides(152f,148f),NoGuides,tol);
            Check(r.HasVerticalGuide&&r.VerticalGuideX==152f&&Math.Abs(100f+r.DeltaX-152f)<.001f,
                "reversing the guide order changes the tie winner deterministically");

            var repeated=MoveSnap(b,50f,0f,Guides(148f,148f,148f),NoGuides,tol);
            var once=MoveSnap(b,50f,0f,Guides(148f),NoGuides,tol);
            Check(repeated.HasVerticalGuide&&repeated.DeltaX==once.DeltaX&&repeated.VerticalGuideX==once.VerticalGuideX,
                "duplicate component guides do not change the result");

            var none=MoveSnap(b,3f,4f,null,null,tol);
            Check(!none.HasVerticalGuide&&!none.HasHorizontalGuide&&none.DeltaX==3f&&none.DeltaY==4f,
                "a null guide list is tolerated and leaves the raw delta");

            // The 10A canvas wrapper must stay exactly the canvas-guide case of the shared helper.
            var legacy=FreeSnap.SnapMove(b,0f,-75f,1000f,600f,tol,tol);
            var shared=MoveSnap(b,0f,-75f,Guides(0f,500f,1000f),Guides(0f,300f,600f),tol);
            Check(legacy.HasVerticalGuide==shared.HasVerticalGuide&&legacy.HasHorizontalGuide==shared.HasHorizontalGuide
                &&legacy.DeltaX==shared.DeltaX&&legacy.DeltaY==shared.DeltaY
                &&legacy.HorizontalGuideY==shared.HorizontalGuideY&&legacy.VerticalGuideX==shared.VerticalGuideX,
                "SnapMove remains the canvas-only wrapper of SnapMoveToGuides");

            // Guide collection: canvas first, visible stationary layers in project order.
            var project=new FreeProject{Width=1000,Height=600,SoftBackground=false};
            var visibleLocked=new FreeLayer{Kind="Shape",Name="visible locked",X=600,Y=100,Width=200,Height=100,Locked=true,Text=""};
            var hidden=new FreeLayer{Kind="Shape",Name="hidden",X=100,Y=300,Width=100,Height=100,Visible=false,Text=""};
            var badge=new FreeLayer{Kind="Badge",Name="badge",X=300,Y=400,Width=128,Height=108,Difficulty="IN",Level=15,Text=""};
            var mover=new FreeLayer{Kind="Shape",Name="mover",X=10,Y=10,Width=50,Height=50,Text=""};
            project.Layers.Add(visibleLocked);project.Layers.Add(hidden);project.Layers.Add(badge);project.Layers.Add(mover);
            var guidesX=new List<float>();var guidesY=new List<float>();
            FreeLayoutForm.CollectSnapGuides(project,new[]{mover},guidesX,guidesY);
            Check(guidesX[0]==0f&&guidesX[1]==500f&&guidesX[2]==1000f&&guidesY[0]==0f&&guidesY[1]==300f&&guidesY[2]==600f,
                "canvas guides stay first in the collected guide list");
            Check(guidesX.Contains(600f)&&guidesX.Contains(700f)&&guidesX.Contains(800f)&&guidesY.Contains(100f)&&guidesY.Contains(150f)&&guidesY.Contains(200f),
                "a visible locked stationary layer contributes guides");
            Check(!guidesX.Contains(100f)&&!guidesX.Contains(150f)&&!guidesX.Contains(200f),
                "a hidden layer contributes no guides");
            Check(!guidesX.Contains(10f)&&!guidesX.Contains(35f)&&!guidesX.Contains(60f),
                "the moving set contributes no guides, so a drag cannot snap to itself");
            Check(guidesX.Contains(300f)&&guidesX.Contains(364f)&&guidesX.Contains(428f),
                "a badge layer contributes the ordinary Bounds guides");
            Check(guidesX.IndexOf(600f)<guidesX.IndexOf(300f),"guide order follows project.Layers order");
            var movedControls=new List<float>();var movedVertical=new List<float>();
            FreeLayoutForm.CollectSnapGuides(project,new[]{mover,visibleLocked},movedControls,movedVertical);
            Check(!movedControls.Contains(600f)&&movedControls.Contains(300f),
                "a multi-layer moving set excludes every one of its own layers");
        }
        static void VerifyTypography(string folder)
        {
            var p=new FreeProject{Width=800,Height=600,SoftBackground=false};
            var t=new FreeLayer{Kind="Text",X=100,Y=100,Width=600,Height=350,Text="Alpha\nBeta",FontName="Arial",FontSize=40,VerticalAlign="Top"};p.Layers.Add(t);
            using(var painter=new FreePainter())
            {
                Rectangle left,center,right,bottom,spaced;
                using(var b=painter.Render(p,true)){left=TextInk(b);Renderer.SavePng(b,Path.Combine(folder,"文字_左对齐.png"));}
                t.Align="Center";using(var b=painter.Render(p,true))center=TextInk(b);
                t.Align="Right";using(var b=painter.Render(p,true))right=TextInk(b);
                Check(left.Left<center.Left&&center.Left<right.Left&&Math.Abs(right.Right-700)<8,"left center right alignment changes actual output");
                t.Align="Left";t.VerticalAlign="Bottom";using(var b=painter.Render(p,true))bottom=TextInk(b);
                Check(bottom.Top>left.Top+150,"vertical alignment");
                t.VerticalAlign="Top";t.LineSpacing=2;using(var b=painter.Render(p,true)){spaced=TextInk(b);Renderer.SavePng(b,Path.Combine(folder,"文字_两倍行距.png"));}
                Check(spaced.Top==left.Top&&spaced.Bottom>left.Bottom+30,"line spacing changes glyph positions");
                t.LineSpacing=1;t.Text="Phigros 2026";byte[] normal;
                using(var b=painter.Render(p,true))normal=Renderer.Encode(b);
                t.FontName="Times New Roman";using(var b=painter.Render(p,true))Check(!normal.SequenceEqual(Renderer.Encode(b)),"selected font changes exported glyphs");
                t.FontName="Arial";t.Bold=true;t.Italic=true;
                using(var b=painter.Render(p,true))Check(!normal.SequenceEqual(Renderer.Encode(b)),"bold and italic change exported glyphs");
                t.FontName="__missing_font__";using(var b=painter.Render(p,true))TextInk(b);
                Check(!FreeFonts.Available(t.FontName),"missing font is identified and renders fallback");
                t.FontName="Arial";t.FontSize=53;t.LineSpacing=1.75f;t.Align="Right";t.VerticalAlign="Bottom";
                string path=Path.Combine(folder,"字体排版_保存测试.phifree");p.Save(path);var loaded=FreeProject.Read(path).Layers[0];
                Check(loaded.FontName==t.FontName&&loaded.Bold&&loaded.Italic&&loaded.LineSpacing==1.75f&&loaded.Align=="Right"&&loaded.VerticalAlign=="Bottom"&&loaded.FontSize==53,"typography project roundtrip");
                string legacy=File.ReadAllText(path).Replace("<FontName>Arial</FontName>","").Replace("<Bold>true</Bold>","").Replace("<Italic>true</Italic>","").Replace("<LineSpacing>1.75</LineSpacing>","").Replace("<VerticalAlign>Bottom</VerticalAlign>","");
                string legacyPath=Path.Combine(folder,"旧工程字体兼容.phifree");File.WriteAllText(legacyPath,legacy);loaded=FreeProject.Read(legacyPath).Layers[0];
                Check(loaded.FontName=="Microsoft YaHei UI"&&loaded.LineSpacing==1&&!loaded.Bold&&loaded.VerticalAlign=="Center","old projects receive typography defaults");
                t.LineSpacing=float.NaN;bool reject=false;try{p.Validate();}catch(InvalidDataException){reject=true;}Check(reject,"reject invalid line spacing");t.LineSpacing=1;
                using(var b=new Bitmap(300,200))using(var g=Graphics.FromImage(b))using(var f=FreeFonts.Create(t))using(var format=new StringFormat(StringFormat.GenericTypographic))
                {
                    string source="中😀e\u0301文";var lines=FreePainter.WrapLines(g,source,f,55,format);Check(string.Concat(lines)==source&&!lines.Any(line=>line.Length==1&&char.IsSurrogate(line[0])),"wrapping preserves Unicode text elements");
                    Check(FreePainter.WrapLines(g,"A\n\nB",f,300,format).SequenceEqual(new[]{"A","","B"}),"blank lines preserved");
                }
            }
            var demo=new FreeProject{Width=1600,Height=900,SoftBackground=false};
            string[] headings={"左对齐 / 1.0 倍行距","居中 / 1.5 倍行距","右对齐 / 2.0 倍行距"};
            for(int i=0;i<3;i++)
            {
                demo.Layers.Add(new FreeLayer{Kind="Shape",X=50+i*520,Y=120,Width=460,Height=620,ColorArgb=Color.FromArgb(255,25,38,52).ToArgb()});
                demo.Layers.Add(new FreeLayer{Kind="Text",X=75+i*520,Y=150,Width=410,Height=80,Text=headings[i],FontSize=29,Bold=true,Align=new[]{"Left","Center","Right"}[i]});
                demo.Layers.Add(new FreeLayer{Kind="Text",X=75+i*520,Y=260,Width=410,Height=400,Text="Phigros Preview\n预选曲组\n谱面作者",FontSize=38,LineSpacing=1+i*.5f,VerticalAlign="Top",Align=new[]{"Left","Center","Right"}[i],FontName="Microsoft YaHei UI",Italic=i==2});
            }
            using(var painter=new FreePainter())using(var b=painter.Render(demo,false))Renderer.SavePng(b,Path.Combine(folder,"字体与排版效果.png"));
        }
        static void VerifyOriginal(string folder)
        {
            var p=new FreeProject{Width=800,Height=400,SoftBackground=false};
            var tag=new FreeLayer{Kind="Tag",Name="Original 标签",Text="Phigros Original",X=100,Y=100,Width=280,Height=42,FontSize=25,ColorArgb=Color.Black.ToArgb(),Align="Center"};p.Layers.Add(tag);
            using(var painter=new FreePainter())
            {
                byte[] original;
                using(var b=painter.Render(p,true))
                {
                    Check(b.GetPixel(120,104).ToArgb()==Color.White.ToArgb(),"original tag has fully opaque white background");
                    Check(b.GetPixel(100,100).A==0,"original tag has slanted transparent corner");
                    bool ink=false;for(int y=110;y<135;y++)for(int x=120;x<360;x++){var c=b.GetPixel(x,y);if(c.A>240&&c.R<40&&c.G<40&&c.B<40)ink=true;}
                    Check(ink,"original tag renders black editable text");original=Renderer.Encode(b);
                }
                tag.Text="Fanmade Chart";using(var b=painter.Render(p,true))Check(!original.SequenceEqual(Renderer.Encode(b)),"changed original text changes export");
                string path=Path.Combine(folder,"Original标签示例.phifree");p.Save(path);var read=FreeProject.Read(path);Check(read.Layers[0].Kind=="Tag"&&read.Layers[0].Text==tag.Text,"original label project roundtrip");
                tag.Visible=false;using(var b=painter.Render(p,true))Check(b.GetPixel(150,120).A==0,"hidden original tag exports neither background nor text");
                tag.Visible=true;tag.Text="Phigros Original";using(var b=painter.Render(p,false))Renderer.SavePng(b,Path.Combine(folder,"Original标签效果.png"));
            }
        }
        public static int Run(string folder)
        {
            Directory.CreateDirectory(folder);var p=FreeProject.FivePoint();p.Validate();
            VerifyTypography(folder);
            VerifyOriginal(folder);
            VerifyFreePreviewTransform();
            VerifyFreePreviewRenderedGeometry();
            VerifyFreeExportIdentity();
            VerifyResizeCanvasCharacterization();
            VerifyFreeDifficultyColorLiteral();
            VerifyFreeGeometryHelperEquivalence();
            VerifyFreeTransformGuards();
            VerifyFreeAtomicSave(folder);
            VerifyFreeProjectVersioning(folder);
            VerifyFreeSnap();
            VerifyFreeBadgeGeometry();
            VerifyTinyBadgeRoundTrip(folder);
            using(var form=new FreeLayoutForm())count+=form.VerifyAuditRegressions();
            VerifyFreeBadgeRenderCompatibility();
            VerifyFreeComponentSnap();
            Check(p.Layers.Where(l=>!string.IsNullOrEmpty(l.Group)).Select(l=>l.Group).Distinct().Count()==5,"five song groups");
            p.Save(Path.Combine(folder,"五点布局_空白模板.phifree"));
            int i=0;Color[] colors={Color.FromArgb(37,126,167),Color.FromArgb(125,72,155),Color.FromArgb(179,64,101),Color.FromArgb(212,158,36),Color.FromArgb(40,158,146)};
            foreach(var art in p.Layers.Where(l=>l.Kind=="Image"&&l.Slanted))
            {
                using(var b=new Bitmap(520,250))using(var g=Graphics.FromImage(b))
                {
                    using(var fill=new LinearGradientBrush(new Rectangle(0,0,520,250),colors[i],Color.FromArgb(13,24,41),35))g.FillRectangle(fill,0,0,520,250);
                    using(var pen=new Pen(Color.FromArgb(55,255,255,255),2))for(int k=0;k<12;k++)g.DrawEllipse(pen,160-k*24,-80+k*8,320+k*20,320+k*20);
                    using(var font=new Font("Microsoft YaHei UI",36,FontStyle.Bold,GraphicsUnit.Pixel))g.DrawString("曲绘示例 "+(i+1),font,Brushes.White,100,93);
                    art.Image=Renderer.Encode(b);
                }
                i++;
            }
            p.Save(Path.Combine(folder,"五点布局_演示工程.phifree"));var read=FreeProject.Read(Path.Combine(folder,"五点布局_演示工程.phifree"));
            Check(read.Layers.Count==p.Layers.Count&&read.Layers.First(l=>l.Slanted&&l.Kind=="Image").Image.Length>0,"portable project roundtrip");
            var copy=p.Copy();copy.Layers[0].X=99;Check(p.Layers[0].X==0,"undo snapshot independence");
            using(var painter=new FreePainter())
            {
                using(var b=painter.Render(p,false)){Check(b.Width==2740&&b.Height==1080,"wide export dimensions");Renderer.SavePng(b,Path.Combine(folder,"五点布局_效果.png"));}
                using(var b=painter.Render(p,true)){Check(b.GetPixel(2735,1075).A==0,"transparent export has no background");}
                // Isolate the blended background to measure seams along every adjacent pixel.
                var background=p.Copy();foreach(var l in background.Layers)if(l.Kind!="Image"||!l.Slanted)l.Visible=false;
                using(var b=painter.Render(background,false))
                {
                    Renderer.SavePng(b,Path.Combine(folder,"背景衔接测试.png"));
                    int maxDelta=0;for(int x=1;x<b.Width;x++){var a=b.GetPixel(x-1,80);var c=b.GetPixel(x,80);maxDelta=Math.Max(maxDelta,Math.Max(Math.Abs(a.R-c.R),Math.Max(Math.Abs(a.G-c.G),Math.Abs(a.B-c.B))));}
                    Check(maxDelta<8,"continuous background has no hard seams (max adjacent RGB delta="+maxDelta+")");
                    Check(b.GetPixel(100,80).ToArgb()!=b.GetPixel(2600,80).ToArgb(),"multiple artwork colors contribute");
                }
                var hidden=p.Copy();foreach(var l in hidden.Layers)l.Visible=false;
                using(var b=painter.Render(hidden,true))Check(b.GetPixel(400,350).A==0,"hidden layers excluded");
                // Ensure rendering order and later layer hiding affect actual output pixels.
                hidden.SoftBackground=false;
                hidden.Layers.Add(new FreeLayer{Kind="Shape",X=100,Y=100,Width=100,Height=100,ColorArgb=Color.Red.ToArgb()});
                hidden.Layers.Add(new FreeLayer{Kind="Shape",X=120,Y=120,Width=100,Height=100,ColorArgb=Color.Blue.ToArgb()});
                using(var b=painter.Render(hidden,true))Check(b.GetPixel(150,150).ToArgb()==Color.Blue.ToArgb(),"layer z-order rendering");
                hidden.Layers.Last().Visible=false;using(var b=painter.Render(hidden,true))Check(b.GetPixel(150,150).ToArgb()==Color.Red.ToArgb(),"hidden front layer");
                // Background images and automatic blending both remain full-canvas.
                hidden.Background=p.Layers.First(l=>l.Slanted&&l.Kind=="Image").Image;hidden.SoftBackground=true;
                using(var b=painter.Render(hidden,false))Check(b.GetPixel(2700,1050).A==255,"custom blurred background fills canvas");
            }
            var invalid=p.Copy();invalid.Layers[0].Width=float.NaN;bool rejected=false;try{invalid.Validate();}catch(InvalidDataException){rejected=true;}Check(rejected,"invalid geometry rejected");
            invalid=p.Copy();invalid.Layers.First(l=>l.Kind=="Badge").Level=0;rejected=false;try{invalid.Validate();}catch(InvalidDataException){rejected=true;}Check(rejected,"zero difficulty rejected");
            using(var form=new FreeLayoutForm())count+=form.VerifyInteractions();
            File.WriteAllText(Path.Combine(folder,"自由排版测试结果.txt"),"PASS: "+count+" checks\r\n"+DateTime.Now.ToString("s"));return 0;
        }
    }
}
