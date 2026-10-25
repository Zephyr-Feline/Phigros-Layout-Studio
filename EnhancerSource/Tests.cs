using System;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace PhigrosEnhancer
{
    static class Tests
    {
        static int count;
        static void Check(bool value, string what)
        { if(!value)throw new Exception("FAIL: "+what);count++; }
        static Rectangle InkBounds(Bitmap image, int top, int bottom, bool white)
        {
            int left=image.Width,right=-1,minY=image.Height,maxY=-1;
            for(int y=top;y<bottom;y++)for(int x=0;x<image.Width;x++)
            {
                Color c=image.GetPixel(x,y);
                bool match=white ? c.A>240&&c.R>245&&c.G>245&&c.B>245 : c.A>240&&c.R==207&&c.G==19&&c.B==19;
                if(match){left=Math.Min(left,x);right=Math.Max(right,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);}
            }
            if(right<0)throw new Exception("Expected rendered ink");
            return Rectangle.FromLTRB(left,minY,right+1,maxY+1);
        }
        // ------------------------------------------------------------------
        // Milestone 2 characterisation tests for the classic coordinate model.
        // The preview viewport lives in private members of MainForm, so the
        // viewport math below mirrors MainForm.PaintPreview / MainForm.Logical
        // and is additionally cross-checked against the real Renderer.Draw.
        // ------------------------------------------------------------------
        static void CheckClose(float actual,float expected,float tolerance,string what)
        {
            if(Math.Abs(actual-expected)>tolerance)
                throw new Exception("FAIL: "+what+" (expected "+expected.ToString("0.####")+", actual "+actual.ToString("0.####")+")");
            count++;
        }
        static Rectangle RenderedInkBounds(Bitmap image,int top,int bottom,bool white,string what)
        {
            int left=image.Width,right=-1,minY=image.Height,maxY=-1;
            int from=Math.Max(0,top),to=Math.Min(image.Height,bottom);
            for(int y=from;y<to;y++)for(int x=0;x<image.Width;x++)
            {
                Color c=image.GetPixel(x,y);
                bool match=white ? c.A>240&&c.R>245&&c.G>245&&c.B>245 : c.A>240&&c.R==207&&c.G==19&&c.B==19;
                if(match){left=Math.Min(left,x);right=Math.Max(right,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);}
            }
            if(right<0)throw new Exception("Expected rendered ink for "+what);
            return Rectangle.FromLTRB(left,minY,right+1,maxY+1);
        }
        // Mirrors the centred letterbox in MainForm.PaintPreview (36px total margin).
        static RectangleF ClassicImageRect(int panelWidth,int panelHeight,int imageWidth,int imageHeight)
        {
            float ratio=Math.Min((panelWidth-36f)/imageWidth,(panelHeight-36f)/imageHeight);
            return new RectangleF((panelWidth-imageWidth*ratio)/2f,(panelHeight-imageHeight*ratio)/2f,imageWidth*ratio,imageHeight*ratio);
        }
        // Mirrors the forward anchor mapping used by MainForm.PaintPreview.
        static PointF ClassicLogicalToScreen(RectangleF imageRect,float x,float y)
        {
            return new PointF(imageRect.X+x*imageRect.Width/1920f,imageRect.Y+y*imageRect.Height/1080f);
        }
        // Mirrors MainForm.Logical.
        static PointF ClassicScreenToLogical(RectangleF imageRect,float x,float y)
        {
            return new PointF((x-imageRect.X)*1920f/imageRect.Width,(y-imageRect.Y)*1080f/imageRect.Height);
        }
        // Runs the real Renderer.Draw through the same transform stack as
        // MainForm.PaintPreview. PaintPreview draws the background bitmap before the
        // labels with the same transform, so omitting it here leaves label geometry
        // identical while making the antialiased badge edges measurable against
        // transparency.
        static Bitmap RenderClassicPreview(Project p,Bitmap background,int panelWidth,int panelHeight,out RectangleF imageRect)
        {
            imageRect=ClassicImageRect(panelWidth,panelHeight,background.Width,background.Height);
            var b=new Bitmap(panelWidth,panelHeight,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                var state=g.Save();
                g.TranslateTransform(imageRect.X,imageRect.Y);
                g.ScaleTransform(imageRect.Width/1920f,imageRect.Height/1080f);
                g.SetClip(new RectangleF(0,0,1920,1080));
                Renderer.Draw(g,p,Renderer.AspectCorrection(background.Width,background.Height));
                g.Restore(state);
            }
            return b;
        }
        // The badge is a slanted polygon, so its leftmost and rightmost pixels fall on
        // single antialiased vertices. Match any strongly red pixel (reduced alpha but
        // unchanged hue) rather than the exact fill colour; the black shadow and the
        // white digits are excluded by the same test.
        static Rectangle RedInkBounds(Bitmap image,int top,int bottom,string what)
        {
            int left=image.Width,right=-1,minY=image.Height,maxY=-1;
            int from=Math.Max(0,top),to=Math.Min(image.Height,bottom);
            for(int y=from;y<to;y++)for(int x=0;x<image.Width;x++)
            {
                Color c=image.GetPixel(x,y);
                if(c.A>0&&c.R>150&&c.G<100&&c.B<100){left=Math.Min(left,x);right=Math.Max(right,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);}
            }
            if(right<0)throw new Exception("Expected rendered ink for "+what);
            return Rectangle.FromLTRB(left,minY,right+1,maxY+1);
        }
        static void VerifyClassicCoordinateCharacterization()
        {
            int[][] ratios={new int[]{1920,1080},new int[]{1440,1080},new int[]{2560,1080}};
            foreach(int[] dim in ratios)
            {
                int bw=dim[0],bh=dim[1];string tag=bw+"x"+bh;
                RectangleF rect=ClassicImageRect(1280,720,bw,bh);
                float correction=Renderer.AspectCorrection(bw,bh);
                PointF[] probes={new PointF(10,10),new PointF(960,540),new PointF(1910,1070),new PointF(1668,246)};
                foreach(PointF q in probes)
                {
                    PointF s=ClassicLogicalToScreen(rect,q.X,q.Y);
                    PointF back=ClassicScreenToLogical(rect,s.X,s.Y);
                    CheckClose(back.X,q.X,.01f,"classic preview round trip X "+tag+" logical "+q.X+","+q.Y);
                    CheckClose(back.Y,q.Y,.01f,"classic preview round trip Y "+tag+" logical "+q.X+","+q.Y);
                }
                PointF origin=ClassicLogicalToScreen(rect,0,0);
                PointF far=ClassicLogicalToScreen(rect,1920,1080);
                CheckClose(origin.X,rect.Left,.01f,"classic preview logical X=0 maps to image left "+tag);
                CheckClose(origin.Y,rect.Top,.01f,"classic preview logical Y=0 maps to image top "+tag);
                CheckClose(far.X,rect.Right,.01f,"classic preview logical X=1920 maps to image right "+tag);
                CheckClose(far.Y,rect.Bottom,.01f,"classic preview logical Y=1080 maps to image bottom "+tag);
                float naiveX=rect.Width/1920f,scaleY=rect.Height/1080f,scaleX=naiveX*correction;
                CheckClose(scaleX,scaleY,1e-4f,"classic effective X and Y scale are equal "+tag);
                if(bw<1920)Check(scaleX>naiveX,"classic correction removes 4:3 horizontal compression "+tag);
                if(bw>1920)Check(scaleX<naiveX,"classic correction removes ultrawide horizontal stretch "+tag);
            }
        }
        static void VerifyClassicRenderedGeometry()
        {
            int[][] ratios={new int[]{1920,1080},new int[]{1440,1080},new int[]{2560,1080}};
            foreach(int[] dim in ratios)
            {
                int bw=dim[0],bh=dim[1];string tag=bw+"x"+bh;
                using(var bg=new Bitmap(bw,bh))
                {
                    using(var g=Graphics.FromImage(bg))g.Clear(Color.FromArgb(50,65,80));
                    float correction=Renderer.AspectCorrection(bw,bh);
                    var label=new ChartLabel{BadgeX=800,BadgeY=500,Scale=1,ShowAuthor=false,Difficulty="IN",Level=15};
                    var p=new Project();p.Labels.Add(label);
                    RectangleF logical=Renderer.BadgeRect(label,correction);
                    RectangleF rect;
                    using(var bmp=RenderClassicPreview(p,bg,1280,720,out rect))
                    {
                        float kx=rect.Width/1920f,ky=rect.Height/1080f;
                        int top=(int)Math.Floor(rect.Y+logical.Top*ky)-25;
                        int bottom=(int)Math.Ceiling(rect.Y+(logical.Top+logical.Height)*ky)+25;
                        Rectangle ink=RedInkBounds(bmp,top,bottom,"classic preview badge "+tag);
                        CheckClose(ink.Left,rect.X+logical.Left*kx,2.5f,"classic rendered badge left matches BadgeRect "+tag);
                        CheckClose(ink.Top,rect.Y+logical.Top*ky,2.5f,"classic rendered badge top matches BadgeRect "+tag);
                        CheckClose(ink.Width,logical.Width*kx,2.5f,"classic rendered badge width matches BadgeRect "+tag);
                        CheckClose(ink.Height,logical.Height*ky,2.5f,"classic rendered badge height matches BadgeRect "+tag);
                        CheckClose((float)ink.Width/ink.Height,128f/108f,.03f,"classic rendered badge keeps 128:108 aspect "+tag);
                    }
                    CheckClose(logical.Width,128f*label.Scale*correction,.001f,"BadgeRect width scales with correction "+tag);
                    CheckClose(logical.Height,108f*label.Scale,.001f,"BadgeRect height ignores correction "+tag);
                    if(bw!=1920)
                        Check(Math.Abs(logical.Width-Renderer.BadgeRect(label,1f).Width)>.5f,"BadgeRect correction changes logical width "+tag);
                    var author=new ChartLabel{BadgeX=200,BadgeY=200,AuthorX=600,AuthorY=700,AuthorWidth=360,Scale=1,ShowBadge=false,ShowAuthor=true,Author="Chart by 测试谱师"};
                    var ap=new Project();ap.Labels.Add(author);
                    RectangleF authorLogical=Renderer.AuthorRect(author,correction);
                    RectangleF arect;
                    using(var abmp=RenderClassicPreview(ap,bg,1280,720,out arect))
                    {
                        float akx=arect.Width/1920f,aky=arect.Height/1080f;
                        int atop=(int)Math.Floor(arect.Y+authorLogical.Top*aky)-25;
                        int abottom=(int)Math.Ceiling(arect.Y+(authorLogical.Top+authorLogical.Height)*aky)+25;
                        Rectangle white=RenderedInkBounds(abmp,atop,abottom,true,"classic preview author text "+tag);
                        float barLeft=arect.X+authorLogical.Left*akx,barTop=arect.Y+authorLogical.Top*aky;
                        float barRight=barLeft+authorLogical.Width*akx,barBottom=barTop+authorLogical.Height*aky;
                        Check(white.Left>=barLeft-2f&&white.Right<=barRight+2f&&white.Top>=barTop-2f&&white.Bottom<=barBottom+2f,
                            "classic author text stays inside AuthorRect projection "+tag);
                    }
                    CheckClose(Renderer.AuthorRect(author,correction).Width,author.AuthorWidth*author.Scale*correction,.001f,"AuthorRect width scales with correction "+tag);
                    if(bw!=1920)
                        Check(Math.Abs(Renderer.AuthorRect(author,correction).Width-Renderer.AuthorRect(author,1f).Width)>.5f,"AuthorRect correction changes logical width "+tag);
                }
            }
        }
        static void VerifyClassicPreviewExportConsistency()
        {
            int[][] ratios={new int[]{1920,1080},new int[]{1440,1080}};
            foreach(int[] dim in ratios)
            {
                int bw=dim[0],bh=dim[1];string tag=bw+"x"+bh;
                using(var bg=new Bitmap(bw,bh))
                {
                    using(var g=Graphics.FromImage(bg))g.Clear(Color.FromArgb(50,65,80));
                    var label=new ChartLabel{BadgeX=800,BadgeY=500,Scale=1,ShowAuthor=false,Difficulty="IN",Level=15};
                    var p=new Project();p.Labels.Add(label);p.Background=Renderer.Encode(bg);
                    float correction=Renderer.AspectCorrection(bw,bh);
                    RectangleF rect;float relLeft,relTop,relWidth,relHeight;
                    using(var bmp=RenderClassicPreview(p,bg,1280,720,out rect))
                    {
                        RectangleF logical=Renderer.BadgeRect(label,correction);
                        float ky=rect.Height/1080f;
                        int top=(int)Math.Floor(rect.Y+logical.Top*ky)-25;
                        int bottom=(int)Math.Ceiling(rect.Y+(logical.Top+logical.Height)*ky)+25;
                        Rectangle ink=RedInkBounds(bmp,top,bottom,"classic preview badge for consistency "+tag);
                        relLeft=(ink.Left-rect.X)/rect.Width;relTop=(ink.Top-rect.Y)/rect.Height;
                        relWidth=ink.Width/rect.Width;relHeight=ink.Height/rect.Height;
                    }
                    using(var b=Renderer.Render(p,bg,bw,bh,true))
                    {
                        int top=(int)Math.Floor(label.BadgeY*bh/1080f)-25;
                        int bottom=(int)Math.Ceiling((label.BadgeY+108f)*bh/1080f)+25;
                        Rectangle ink=RedInkBounds(b,top,bottom,"classic export badge for consistency "+tag);
                        CheckClose(ink.Left/(float)bw,relLeft,.006f,"classic preview/export relative left match "+tag);
                        CheckClose(ink.Top/(float)bh,relTop,.006f,"classic preview/export relative top match "+tag);
                        CheckClose(ink.Width/(float)bw,relWidth,.006f,"classic preview/export relative width match "+tag);
                        CheckClose(ink.Height/(float)bh,relHeight,.006f,"classic preview/export relative height match "+tag);
                        CheckClose((float)ink.Width/ink.Height,128f/108f,.03f,"classic export badge keeps 128:108 aspect "+tag);
                    }
                }
            }
        }
        // Milestone 8 (final item, audited): the classic export height must stay an integer and
        // AspectCorrection must be derived from the FINAL bitmap dimensions. That is what keeps
        // the export drawing scale isotropic (scaleX == scaleY == h / 1080) and the badge at
        // exactly 128:108. Feeding the un-rounded ideal background ratio into the correction
        // would make the drawing anisotropic, so it is not a fix.
        static void VerifyClassicExportRoundingSemantics()
        {
            // 2048 x 1080 exported at width 1920 is the classic half-pixel case: ideal 1012.5.
            int bw=2048,bh=1080,w=1920;
            double idealHeight=bh*(double)w/bw;
            int actualHeight=(int)Math.Round(bh*(double)w/bw);   // same expression as MainForm.Export
            Check(Math.Abs(idealHeight-1012.5)<1e-9,"half-pixel export height is 1012.5 before rounding");
            Check(actualHeight==1012,"ToEven rounding yields the integer export height 1012");
            Check(Math.Abs(actualHeight-idealHeight)==0.5,"the half-pixel case is the worst rounding case (0.5 output pixel)");

            var exportTransform=new LogicalViewportTransform(ClassicGeometry.ReferenceWidth,ClassicGeometry.ReferenceHeight,
                new RectangleF(0,0,w,actualHeight));             // same arguments as Renderer.Render
            float scaleX=exportTransform.HorizontalScale,scaleY=exportTransform.VerticalScale;
            CheckClose(scaleX,(float)w/1920f,1e-6f,"export X scale is width / 1920");
            CheckClose(scaleY,(float)actualHeight/1080f,1e-6f,"export Y scale is the rounded height / 1080");
            Check(scaleX!=scaleY,"the raw export transform is anisotropic before the correction");

            float correction=ClassicGeometry.AspectCorrection(w,actualHeight);
            CheckClose(correction,Renderer.AspectCorrection(w,actualHeight),1e-7f,"the export correction comes from the final bitmap height");
            CheckClose(correction,1920f*actualHeight/(1080f*w),1e-7f,"the export correction is 1920 * actualHeight / (1080 * width)");
            float idealCorrection=ClassicGeometry.AspectCorrection(w,(float)idealHeight);
            Check(Math.Abs(correction-idealCorrection)>0f,"the final-bitmap correction differs from the ideal-ratio correction here");
            CheckClose(correction-idealCorrection,1920f*(actualHeight-(float)idealHeight)/(1080f*w),1e-7f,
                "the correction difference follows 1920 * (actualHeight - idealHeight) / (1080 * width)");
            CheckClose(Math.Abs(correction-idealCorrection),1920f*0.5f/(1080f*w),1e-7f,
                "the correction difference magnitude is 1920 * 0.5 / (1080 * width)");

            // The production combination is isotropic: (width / 1920) * correction == height / 1080.
            CheckClose(scaleX*correction,scaleY,1e-6f,"export X scale * correction equals the export Y scale");
            CheckClose(scaleX*correction,(float)actualHeight/1080f,1e-6f,"export drawing scale is uniformly h / 1080");
            CheckClose(128f*correction*scaleX/(108f*scaleY),128f/108f,1e-6f,"the exported badge keeps exactly 128:108");

            // Counter-example (test maths only): the ideal background ratio would break isotropy.
            float idealCombinedX=scaleX*idealCorrection;
            Check(idealCombinedX!=scaleY,"an ideal-ratio correction would make the export X and Y scales differ");
            CheckClose(idealCombinedX-scaleY,0.5f/1080f,1e-7f,"the ideal-ratio mismatch is exactly 0.5 / 1080");
            CheckClose(128f*idealCorrection*scaleX/(108f*scaleY),128f/108f*(float)(idealHeight/actualHeight),1e-6f,
                "an ideal-ratio correction would drift the badge aspect off 128:108");
            Check(Math.Abs(128f*idealCorrection*scaleX/(108f*scaleY)-128f/108f)>0f,"the drifting ideal-ratio badge aspect differs from 128:108");

            // Integer output contract plus the rendered geometry of the real Renderer path.
            using(var bg=new Bitmap(bw,bh))
            {
                using(var g=Graphics.FromImage(bg))g.Clear(Color.FromArgb(50,65,80));
                var label=new ChartLabel{BadgeX=800,BadgeY=500,Scale=1,ShowAuthor=false,Difficulty="IN",Level=15};
                var p=new Project();p.Labels.Add(label);p.Background=Renderer.Encode(bg);p.Validate();
                using(var b=Renderer.Render(p,bg,w,actualHeight,true))
                {
                    Check(b.Width==w&&b.Height==actualHeight,"classic export honours the integer width x rounded height contract");
                    int top=(int)Math.Floor(label.BadgeY*actualHeight/1080f)-20;
                    int bottom=(int)Math.Ceiling((label.BadgeY+108f)*actualHeight/1080f)+20;
                    Rectangle ink=RedInkBounds(b,top,bottom,"classic export badge at the rounded height");
                    CheckClose((float)ink.Width/ink.Height,128f/108f,.03f,"the rendered badge keeps 128:108 at the rounded export height");
                    // Transparent output at fixed target dimensions must not depend on the
                    // source background ratio. This compares actual pixels, so replacing the
                    // Render call's final-bitmap correction with the ideal ratio must fail.
                    using(var controlBackground=new Bitmap(w,actualHeight))
                    using(var control=Renderer.Render(p,controlBackground,w,actualHeight,true))
                        Check(Renderer.Encode(b).SequenceEqual(Renderer.Encode(control)),
                            "transparent rounding export is independent of the source background ratio");
                }
            }

            // Zero-error control: an exact 16:9 and an exact 4:3 export have no quantization at all.
            foreach(int[] dim in new[]{new int[]{1920,1080,1920},new int[]{1440,1080,1920}})
            {
                int zbw=dim[0],zbh=dim[1],zw=dim[2];string tag=zbw+"x"+zbh+"@"+zw;
                double zIdeal=zbh*(double)zw/zbw;
                int zActual=(int)Math.Round(zbh*(double)zw/zbw);
                Check(Math.Abs(zIdeal-zActual)<1e-9,"an exact-ratio export has no height quantization "+tag);
                Check(ClassicGeometry.AspectCorrection(zw,zActual)==ClassicGeometry.AspectCorrection(zw,(float)zIdeal),
                    "final-bitmap and ideal-ratio corrections are identical when the ratio divides exactly "+tag);
                var t=new LogicalViewportTransform(ClassicGeometry.ReferenceWidth,ClassicGeometry.ReferenceHeight,new RectangleF(0,0,zw,zActual));
                CheckClose(t.HorizontalScale*ClassicGeometry.AspectCorrection(zw,zActual),t.VerticalScale,1e-7f,
                    "an exact-ratio export is already isotropic "+tag);
            }
        }
        static void VerifyDifficultyColorLiterals()
        {
            Color literalIn=Color.FromArgb(207,19,19);
            Color actualIn=Renderer.Colors["IN"];
            Check(actualIn.ToArgb()==literalIn.ToArgb(),"IN difficulty colour equals literal #CF1313");
            Check(actualIn.R==207&&actualIn.G==19&&actualIn.B==19,"IN difficulty colour channels are 207/19/19");
            using(var bg=new Bitmap(1920,1080))
            {
                using(var g=Graphics.FromImage(bg))g.Clear(Color.FromArgb(50,65,80));
                var p=new Project();p.Labels.Add(new ChartLabel{BadgeX=400,BadgeY=300,ShowAuthor=false,Difficulty="IN",Level=15});
                using(var b=Renderer.Render(p,bg,1920,1080,true))
                    Check(b.GetPixel(435,303).ToArgb()==literalIn.ToArgb(),"classic IN badge renders literal #CF1313 pixels");
            }
        }
        // Milestone 3: prove the extracted helper is equivalent to the classic
        // preview/Logical mirror AND to the live Renderer formula, without wiring the
        // helper into any production call site.
        static void VerifyClassicGeometryHelperEquivalence()
        {
            int[][] ratios={new int[]{1920,1080},new int[]{1440,1080},new int[]{2560,1080}};
            foreach(int[] dim in ratios)
            {
                int bw=dim[0],bh=dim[1];string tag=bw+"x"+bh;
                RectangleF view=ClassicImageRect(1280,720,bw,bh);
                var t=new LogicalViewportTransform(ClassicGeometry.ReferenceWidth,ClassicGeometry.ReferenceHeight,view);
                PointF[] probes={new PointF(10,10),new PointF(960,540),new PointF(1910,1070),new PointF(1668,246)};
                foreach(PointF q in probes)
                {
                    PointF expected=ClassicLogicalToScreen(view,q.X,q.Y);
                    PointF actual=t.LogicalToScreen(q);
                    Check(actual.X==expected.X&&actual.Y==expected.Y,
                        "helper logical->screen matches classic preview "+tag+" logical "+q.X+","+q.Y);
                    PointF mirrored=ClassicScreenToLogical(view,expected.X,expected.Y);
                    PointF back=t.ScreenToLogical(expected);
                    Check(back.X==mirrored.X&&back.Y==mirrored.Y,
                        "helper screen->logical matches MainForm.Logical "+tag+" logical "+q.X+","+q.Y);
                }
                Check(t.HorizontalScale==view.Width/1920f&&t.VerticalScale==view.Height/1080f,
                    "helper classic scales match preview mirror "+tag);
                SizeF delta=new SizeF(37f,-23f);
                SizeF screenDelta=t.LogicalDeltaToScreen(delta);
                Check(screenDelta.Width==delta.Width*view.Width/1920f&&screenDelta.Height==delta.Height*view.Height/1080f,
                    "helper logical delta->screen matches classic preview "+tag);
                CheckClose(t.ScreenDeltaToLogical(screenDelta).Width,delta.Width,.001f,
                    "helper screen delta->logical inverts classic delta X "+tag);
                CheckClose(t.ScreenDeltaToLogical(screenDelta).Height,delta.Height,.001f,
                    "helper screen delta->logical inverts classic delta Y "+tag);
            }
            int[][] corrections={new int[]{1920,1080},new int[]{1440,1080},new int[]{2560,1080},new int[]{1920,1500},new int[]{1080,1920}};
            foreach(int[] dim in corrections)
            {
                string tag=dim[0]+"x"+dim[1];
                float live=Renderer.AspectCorrection(dim[0],dim[1]);
                float helper=ClassicGeometry.AspectCorrection(dim[0],dim[1]);
                CheckClose(helper,live,1e-6f,"ClassicGeometry correction matches Renderer.AspectCorrection "+tag);
            }
            Check(ClassicGeometry.ReferenceWidth==1920f&&ClassicGeometry.ReferenceHeight==1080f,
                "ClassicGeometry reference size is 1920x1080");
        }
        // Milestone 3 defensive tests: the helper must reject unusable viewports
        // instead of silently producing NaN/Infinity coordinates later on.
        static void CheckRejectsTransform(string what,float logicalWidth,float logicalHeight,RectangleF view)
        {
            bool rejected=false;
            try { new LogicalViewportTransform(logicalWidth,logicalHeight,view); }
            catch(ArgumentOutOfRangeException) { rejected=true; }
            Check(rejected,"LogicalViewportTransform rejects "+what);
        }
        static void VerifyLogicalViewportTransformGuards()
        {
            RectangleF okView=new RectangleF(10f,20f,100f,200f);
            CheckRejectsTransform("logicalWidth=0",0f,1080f,okView);
            CheckRejectsTransform("logicalWidth<0",-1920f,1080f,okView);
            CheckRejectsTransform("logicalHeight=0",1920f,0f,okView);
            CheckRejectsTransform("logicalHeight<0",1920f,-1080f,okView);
            CheckRejectsTransform("logicalWidth=NaN",float.NaN,1080f,okView);
            CheckRejectsTransform("logicalHeight=Infinity",1920f,float.PositiveInfinity,okView);
            CheckRejectsTransform("view.Width=0",1920f,1080f,new RectangleF(0f,0f,0f,200f));
            CheckRejectsTransform("view.Width<0",1920f,1080f,new RectangleF(0f,0f,-100f,200f));
            CheckRejectsTransform("view.Height=0",1920f,1080f,new RectangleF(0f,0f,100f,0f));
            CheckRejectsTransform("view.Height<0",1920f,1080f,new RectangleF(0f,0f,100f,-200f));
            CheckRejectsTransform("view.Width=NaN",1920f,1080f,new RectangleF(0f,0f,float.NaN,200f));
            CheckRejectsTransform("view.Height=Infinity",1920f,1080f,new RectangleF(0f,0f,100f,float.PositiveInfinity));
            CheckRejectsTransform("view.X=NaN",1920f,1080f,new RectangleF(float.NaN,0f,100f,200f));
            CheckRejectsTransform("view.Y=Infinity",1920f,1080f,new RectangleF(0f,float.PositiveInfinity,100f,200f));
            var shifted=new LogicalViewportTransform(ClassicGeometry.ReferenceWidth,ClassicGeometry.ReferenceHeight,
                new RectangleF(-320f,-180f,1280f,720f));
            Check(shifted.View.X==-320f&&shifted.View.Y==-180f,"transform accepts negative view offsets");
            PointF probe=new PointF(960f,540f);
            PointF roundTrip=shifted.ScreenToLogical(shifted.LogicalToScreen(probe));
            CheckClose(roundTrip.X,probe.X,.01f,"negative-offset transform round trips X");
            CheckClose(roundTrip.Y,probe.Y,.01f,"negative-offset transform round trips Y");
        }
        // ------------------------------------------------------------------
        // Atomic save tests. Locking is created by this process (FileShare.Read denies
        // delete) so the retry path is exercised deterministically on any machine.
        // ------------------------------------------------------------------
        static int CountTempFiles(string directory)
        {
            return Directory.GetFiles(directory,"*.tmp").Length;
        }
        static string DescribeFailure(Exception ex)
        {
            if (ex == null) return "no exception";
            return ex.GetType().Name+" Win32 "+(ex.HResult&0xFFFF);
        }
        static void VerifyAtomicFile(string output)
        {
            string dir=Path.Combine(output,"原子保存测试");
            Directory.CreateDirectory(dir);
            foreach(string stale in Directory.GetFiles(dir))File.Delete(stale);

            // 1. target does not exist -> File.Move commit
            string created=Path.Combine(dir,"新文件.bin");
            AtomicFile.Write(created,delegate(string temp){File.WriteAllBytes(temp,new byte[]{1,2,3,4});});
            byte[] createdBytes=File.ReadAllBytes(created);
            Check(createdBytes.Length==4&&createdBytes[0]==1&&createdBytes[3]==4,
                "atomic write creates a new target with the written content");
            Check(CountTempFiles(dir)==0,"atomic write leaves no temp file after creating a new target");

            // 2. target exists -> File.Replace commit
            string replaced=Path.Combine(dir,"已存在.bin");
            File.WriteAllBytes(replaced,new byte[]{9,9,9,9,9});
            AtomicFile.Write(replaced,delegate(string temp){File.WriteAllBytes(temp,new byte[]{7,7});});
            byte[] replacedBytes=File.ReadAllBytes(replaced);
            Check(replacedBytes.Length==2&&replacedBytes[0]==7&&replacedBytes[1]==7,
                "atomic write replaces the existing target content");
            Check(CountTempFiles(dir)==0,"atomic write leaves no temp file after replacing a target");

            // 3. transient lock released inside the retry budget -> retry then succeed
            string transient=Path.Combine(dir,"短暂占用.bin");
            File.WriteAllBytes(transient,new byte[]{4,4,4,4});
            ManualResetEvent opened1=new ManualResetEvent(false);
            Thread holder1=new Thread(delegate()
            {
                using(var stream=new FileStream(transient,FileMode.Open,FileAccess.Read,FileShare.Read))
                {opened1.Set();Thread.Sleep(50);}
            });
            holder1.Start();opened1.WaitOne();
            Stopwatch retryWatch=Stopwatch.StartNew();
            AtomicFile.Write(transient,delegate(string temp){File.WriteAllBytes(temp,new byte[]{6,6,6});});
            retryWatch.Stop();holder1.Join();
            byte[] transientBytes=File.ReadAllBytes(transient);
            Check(transientBytes.Length==3&&transientBytes[0]==6&&transientBytes[2]==6,
                "atomic write succeeds after a transient lock is released");
            Check(CountTempFiles(dir)==0,"atomic write leaves no temp file after a recovered retry");
            Check(retryWatch.ElapsedMilliseconds>=20,
                "atomic write actually retried while the transient lock was held (elapsed ms "+retryWatch.ElapsedMilliseconds+")");

            // 4. lock outliving the retry budget -> must fail, previous content intact
            string locked=Path.Combine(dir,"超预算占用.bin");
            File.WriteAllBytes(locked,new byte[]{8,8,8});
            ManualResetEvent opened2=new ManualResetEvent(false);
            Thread holder2=new Thread(delegate()
            {
                using(var stream=new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.Read))
                {opened2.Set();Thread.Sleep(300);}
            });
            holder2.Start();opened2.WaitOne();
            Exception lockedFailure=null;
            try{AtomicFile.Write(locked,delegate(string temp){File.WriteAllBytes(temp,new byte[]{3});});}
            catch(Exception ex){lockedFailure=ex;}
            holder2.Join();
            Check(lockedFailure is IOException,
                "atomic write throws IOException when the lock outlives the retry budget (got "+DescribeFailure(lockedFailure)+")");
            byte[] lockedBytes=File.ReadAllBytes(locked);
            Check(lockedBytes.Length==3&&lockedBytes[0]==8,"failed atomic write leaves the previous target intact");
            Check(CountTempFiles(dir)==0,"failed atomic write still removes its temp file");

            // 5a. writeTemp failure is never retried and propagates unchanged
            string writeFail=Path.Combine(dir,"写临时失败.bin");
            bool propagated=false;
            try{AtomicFile.Write(writeFail,delegate(string temp){throw new InvalidOperationException("writeTemp failure");});}
            catch(InvalidOperationException){propagated=true;}
            Check(propagated,"writeTemp exceptions propagate unchanged");
            Check(!File.Exists(writeFail),"a failed writeTemp does not create the target");
            Check(CountTempFiles(dir)==0,"a failed writeTemp leaves no temp file");

            // 5b. non-retryable IOException in the commit phase -> fast failure
            string nonTransient=Path.Combine(dir,"非瞬时错误.bin");
            File.WriteAllBytes(nonTransient,new byte[]{5,5,5});
            Exception fastFailure=null;Stopwatch fastWatch=Stopwatch.StartNew();
            try{AtomicFile.Write(nonTransient,delegate(string temp){File.WriteAllBytes(temp,new byte[]{2});File.Delete(temp);});}
            catch(IOException ex){fastFailure=ex;}
            fastWatch.Stop();
            Check(fastFailure is IOException,
                "commit failure with a non-retryable IOException throws (got "+DescribeFailure(fastFailure)+")");
            Check(fastWatch.ElapsedMilliseconds<70,
                "non-retryable commit failure fails fast instead of burning the retry budget (elapsed ms "+fastWatch.ElapsedMilliseconds+")");
            byte[] nonTransientBytes=File.ReadAllBytes(nonTransient);
            Check(nonTransientBytes.Length==3&&nonTransientBytes[0]==5,"non-retryable commit failure leaves the previous target intact");

            // 6. the production save paths now commit through AtomicFile
            string projectPath=Path.Combine(dir,"原子保存.phipreview");
            var probe=new Project();
            using(var small=new Bitmap(8,8))
            {
                using(var g=Graphics.FromImage(small))g.Clear(Color.FromArgb(12,34,56));
                probe.Background=Renderer.Encode(small);
            }
            probe.Labels.Add(new ChartLabel{Difficulty="IN",Level=15,ShowAuthor=false});
            probe.Save(projectPath);
            var reloaded=Project.Read(projectPath);
            Check(reloaded.Labels.Count==1&&reloaded.Labels[0].Level==15&&reloaded.Labels[0].Difficulty=="IN",
                "Project.Save round trips through the atomic path");
            Check(CountTempFiles(dir)==0,"Project.Save leaves no temp file");

            string pngPath=Path.Combine(dir,"原子保存.png");
            using(var png=new Bitmap(16,12))
            {
                using(var g=Graphics.FromImage(png))g.Clear(Color.Blue);
                Renderer.SavePng(png,pngPath);
            }
            using(var decoded=Renderer.Decode(File.ReadAllBytes(pngPath)))
                Check(decoded.Width==16&&decoded.Height==12,"Renderer.SavePng output decodes through the atomic path");
            Check(CountTempFiles(dir)==0,"Renderer.SavePng leaves no temp file");
        }
        static bool SameBytes(byte[] a,byte[] b)
        {
            if(a.Length!=b.Length)return false;
            for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;
            return true;
        }
        // Milestone 7: .phipreview format versioning and in-memory migration.
        static void VerifyProjectVersioning(string output)
        {
            string dir=Path.Combine(output,"版本兼容测试");
            Directory.CreateDirectory(dir);
            foreach(string stale in Directory.GetFiles(dir))File.Delete(stale);

            var p=new Project();
            using(var bg=new Bitmap(8,8))
            {
                using(var g=Graphics.FromImage(bg))g.Clear(Color.FromArgb(10,20,30));
                p.Background=Renderer.Encode(bg);
            }
            p.Labels.Add(new ChartLabel{Difficulty="IN",Level=15,ShowAuthor=false});
            string v1=Path.Combine(dir,"v1.phipreview");
            p.Save(v1);
            string text=File.ReadAllText(v1);

            Check(Project.Read(v1).Version==Project.CurrentVersion,"phipreview round trip keeps CurrentVersion");
            Check(text.Contains("<Version>"+Project.CurrentVersion+"</Version>"),"phipreview writes the current version element");
            var explicitV1=Project.Read(v1);
            Check(explicitV1.Version==Project.CurrentVersion&&explicitV1.Labels.Count==1,"explicit V1 phipreview loads");

            string futureText=text.Replace("<Version>"+Project.CurrentVersion+"</Version>","<Version>"+(Project.CurrentVersion+1)+"</Version>");
            string future=Path.Combine(dir,"future.phipreview");
            File.WriteAllText(future,futureText);
            bool futureRejected=false;string futureMessage="";
            try{Project.Read(future);}catch(InvalidDataException ex){futureRejected=true;futureMessage=ex.Message;}
            Check(futureRejected,"future phipreview version is rejected");
            Check(futureMessage.Contains("更新版本"),"future phipreview error names the newer-version cause ("+futureMessage+")");

            byte[] before=File.ReadAllBytes(v1);DateTime stampBefore=File.GetLastWriteTimeUtc(v1);
            Project.Read(v1);
            Check(SameBytes(before,File.ReadAllBytes(v1)),"phipreview Read does not rewrite the file on disk");
            Check(stampBefore==File.GetLastWriteTimeUtc(v1),"phipreview Read does not touch LastWriteTime");

            string noVersion=Path.Combine(dir,"noversion.phipreview");
            File.WriteAllText(noVersion,text.Replace("<Version>"+Project.CurrentVersion+"</Version>",""));
            Check(Project.Read(noVersion).Version==Project.CurrentVersion,"phipreview without a Version element defaults to CurrentVersion");

            string invalid=Path.Combine(dir,"invalid.phipreview");
            File.WriteAllText(invalid,text.Replace("<Level>15</Level>","<Level>0</Level>"));
            bool invalidRejected=false;
            try{Project.Read(invalid);}catch(InvalidDataException){invalidRejected=true;}
            Check(invalidRejected,"migrated phipreview is still validated");

            string foreign=Path.Combine(dir,"foreign.phifree");
            File.WriteAllText(foreign,"<?xml version=\"1.0\" encoding=\"utf-8\"?><FreeProject><Version>2</Version></FreeProject>");
            bool foreignRejected=false;
            try{Project.Read(foreign);}catch(Exception){foreignRejected=true;}
            Check(foreignRejected,"phipreview reader does not accept the .phifree format");
        }
        // Independent bugfix: the classic Y bound must use the 1080 height reference.
        static void VerifyClassicValidateYBound(string output)
        {
            string dir=Path.Combine(output,"Y上界测试");
            Directory.CreateDirectory(dir);
            foreach(string stale in Directory.GetFiles(dir))File.Delete(stale);

            var p=new Project();
            using(var bg=new Bitmap(8,8))
            {
                using(var g=Graphics.FromImage(bg))g.Clear(Color.FromArgb(10,20,30));
                p.Background=Renderer.Encode(bg);
            }
            p.Labels.Add(new ChartLabel{Difficulty="IN",Level=15,ShowAuthor=false,
                BadgeX=ClassicGeometry.ReferenceWidth,BadgeY=ClassicGeometry.ReferenceHeight,
                AuthorX=ClassicGeometry.ReferenceWidth,AuthorY=ClassicGeometry.ReferenceHeight});
            bool cornerLegal=true;
            try{p.Validate();}catch(InvalidDataException){cornerLegal=false;}
            Check(cornerLegal,"phipreview accepts coordinates at the 1920 x 1080 logical corner");

            var overBadge=p.Copy();overBadge.Labels[0].BadgeY=1500;
            bool badgeRejected=false;
            try{overBadge.Validate();}catch(InvalidDataException){badgeRejected=true;}
            Check(badgeRejected,"phipreview rejects BadgeY above the 1080 logical height");

            var overAuthor=p.Copy();overAuthor.Labels[0].AuthorY=ClassicGeometry.ReferenceHeight+1;
            bool authorRejected=false;
            try{overAuthor.Validate();}catch(InvalidDataException){authorRejected=true;}
            Check(authorRejected,"phipreview rejects AuthorY above the 1080 logical height");

            var overBadgeX=p.Copy();overBadgeX.Labels[0].BadgeX=ClassicGeometry.ReferenceWidth+1;
            bool badgeXRejected=false;
            try{overBadgeX.Validate();}catch(InvalidDataException){badgeXRejected=true;}
            Check(badgeXRejected,"phipreview still rejects BadgeX above the 1920 logical width");

            bool presetsValid=true;
            for(int i=0;i<3;i++)
            {
                var preset=new Project{Background=p.Background,Labels=Renderer.Preset(i)};
                try{preset.Validate();}catch(InvalidDataException){presetsValid=false;}
            }
            Check(presetsValid,"all classic presets stay inside the validated Y range");

            var illegal=p.Copy();illegal.Labels[0].BadgeY=1500;
            string path=Path.Combine(dir,"invalid_y.phipreview");
            bool saveRejected=false;
            try{illegal.Save(path);}catch(InvalidDataException){saveRejected=true;}
            Check(saveRejected,"saving a project with an out-of-range Y is rejected");
            Check(!File.Exists(path),"a rejected save leaves no project file behind");
        }
        static void VerifyAspectRatios(string output)
        {
            var p=new Project();p.Labels.Add(new ChartLabel{BadgeX=400,BadgeY=200,AuthorX=400,AuthorY=350,Author="Chart by 测试谱师"});
            Rectangle expectedBadge=Rectangle.Empty,expectedDigits=Rectangle.Empty,expectedAuthor=Rectangle.Empty;
            foreach(int width in new[]{1920,1440,1080,2560})
            using(var bg=new Bitmap(width,1080))
            {
                using(var g=Graphics.FromImage(bg))g.Clear(Color.FromArgb(50,65,80));
                p.Background=Renderer.Encode(bg);
                using(var b=Renderer.Render(p,bg,width,1080,true))
                {
                    var badge=InkBounds(b,190,320,false);var digits=InkBounds(b,190,320,true);var author=InkBounds(b,340,400,true);
                    if(width==1920){expectedBadge=badge;expectedDigits=digits;expectedAuthor=author;}
                    Check(Math.Abs(badge.Width-expectedBadge.Width)<=1&&badge.Height==expectedBadge.Height,"undistorted badge at width "+width);
                    Check(Math.Abs(digits.Width-expectedDigits.Width)<=1&&Math.Abs(digits.Height-expectedDigits.Height)<=1,"undistorted digits at width "+width);
                    Check(Math.Abs(author.Width-expectedAuthor.Width)<=1&&Math.Abs(author.Height-expectedAuthor.Height)<=1,"undistorted author at width "+width);
                }
                if(width==1440)
                {
                    Renderer.SavePng(bg,Path.Combine(output,"4比3测试底图.png"));
                    using(var b=Renderer.Render(p,bg,width,1080,false))Renderer.SavePng(b,Path.Combine(output,"4比3比例修复.png"));
                    using(var b=Renderer.Render(p,bg,1920,1440,true))
                    {var bounds=InkBounds(b,250,430,false);Check(Math.Abs((double)bounds.Width/bounds.Height-128.0/108)<.025,"scaled 4:3 export badge ratio");}
                    string saved=Path.Combine(output,"4比3旧格式兼容.phipreview");p.Save(saved);var loaded=Project.Read(saved);
                    Check(loaded.Labels[0].BadgeX==400&&loaded.Labels[0].BadgeY==200,"old project anchor preserved");
                }
                float correction=Renderer.AspectCorrection(width,1080);
                var c=new ChartLabel{BadgeX=1919,BadgeY=1079,AuthorX=1919,AuthorY=1079};Renderer.Clamp(c,correction);
                Check(Renderer.BadgeRect(c,correction).Right<=1920.1f&&Renderer.AuthorRect(c,correction).Right<=1920.1f,"aspect-aware edge clamp "+width);
            }
            var preset=Renderer.Preset(0);Renderer.FitPreset(preset,1440,1080);
            Check(Math.Abs(preset[0].BadgeY-319.5f)<.01f&&Math.Abs(preset[2].BadgeY-855.75f)<.01f&&preset[0].Scale==.75f,"4:3 fitted preset");
            using(var form=new MainForm(null))count+=form.VerifyInteractions(Path.Combine(output,"4比3测试底图.png"));
        }
        public static int Run(string output)
        {
            Directory.CreateDirectory(output);
            VerifyAspectRatios(output);
            VerifyClassicCoordinateCharacterization();
            VerifyClassicRenderedGeometry();
            VerifyClassicPreviewExportConsistency();
            VerifyClassicExportRoundingSemantics();
            VerifyDifficultyColorLiterals();
            VerifyClassicGeometryHelperEquivalence();
            VerifyLogicalViewportTransformGuards();
            VerifyAtomicFile(output);
            VerifyProjectVersioning(output);
            VerifyClassicValidateYBound(output);
            var p=new Project();
            using(var bg=new Bitmap(1920,1080))
            {
                using(var g=Graphics.FromImage(bg))g.Clear(Color.FromArgb(50,65,80));
                p.Background=Renderer.Encode(bg);
                string[] names={"EZ","HD","IN","AT"};
                for(int i=0;i<4;i++)p.Labels.Add(new ChartLabel{Name=names[i]+" 示例",Difficulty=names[i],Level=new[]{8,12,15,16}[i],Author="谱面作者：示例谱师 "+(i+1),BadgeX=250+i*400,BadgeY=200,AuthorX=140+i*400,AuthorY=330,AuthorWidth=350});
                p.Validate();
                using(var b=Renderer.Render(p,bg,1920,1080,false))
                {
                    for(int i=0;i<4;i++)Check(b.GetPixel(285+i*400,203).ToArgb()==Renderer.Colors[names[i]].ToArgb(),"difficulty color "+names[i]);
                    Renderer.SavePng(b,Path.Combine(output,"四色难度与中文谱师.png"));
                }
                using(var b=Renderer.Render(p,bg,960,540,true))
                {
                    Check(b.Width==960&&b.Height==540,"resolution");Check(b.GetPixel(0,0).A==0,"transparent background");
                    Renderer.SavePng(b,Path.Combine(output,"透明标注测试.png"));
                }
                string projectPath=Path.Combine(output,"四色测试.phipreview");p.Save(projectPath);var copy=Project.Read(projectPath);
                Check(copy.Labels.Count==4&&copy.Labels[3].Difficulty=="AT"&&copy.Labels[0].Author.Contains("谱面作者"),"project metadata roundtrip");
                using(var decoded=Renderer.Decode(copy.Background))Check(decoded.Width==1920&&decoded.GetPixel(0,0).ToArgb()==bg.GetPixel(0,0).ToArgb(),"embedded image roundtrip");
                copy.Labels[0].Level=0;bool rejected=false;try{copy.Validate();}catch(InvalidDataException){rejected=true;}Check(rejected,"reject zero");
                copy.Labels[0].Level=-2;rejected=false;try{copy.Validate();}catch(InvalidDataException){rejected=true;}Check(rejected,"reject negative");
                copy.Labels[0].Level=int.MaxValue;copy.Validate();Check(true,"positive integer max");
                copy.Labels[0].Difficulty="SP";rejected=false;try{copy.Validate();}catch(InvalidDataException){rejected=true;}Check(rejected,"reject invalid difficulty");
                copy.Labels[0].Difficulty="EZ";copy.Labels[0].Scale=float.NaN;rejected=false;try{copy.Validate();}catch(InvalidDataException){rejected=true;}Check(rejected,"reject nonfinite scale");
                var c=new ChartLabel{BadgeX=1919,BadgeY=1079,AuthorX=1919,AuthorY=1079};Renderer.Clamp(c);
                Check(Renderer.BadgeRect(c).Right<=1920&&Renderer.BadgeRect(c).Bottom<=1080&&Renderer.AuthorRect(c).Bottom<=1080,"clamp bounds");
                p.Labels[0].Author="很长的中文谱师名称与 English collaborators / αβγ / 記号★1234567890";
                p.Labels[0].Level=int.MaxValue;p.NumberOnTop=false;
                using(var b=Renderer.Render(p,bg,1920,1080,false))Renderer.SavePng(b,Path.Combine(output,"长文本与数字在下.png"));
                foreach(var x in p.Labels){x.ShowBadge=false;x.Author="";}
                using(var b=Renderer.Render(p,bg,1920,1080,true))Check(b.GetPixel(180,350).A==0&&b.GetPixel(285,203).A==0,"hidden and empty labels");
                Check(Renderer.Preset(0).Count==3&&Renderer.Preset(1).Count==6&&Renderer.Preset(2).Count==1,"template counts");
            }
            string reference=Path.Combine(output,"参考底图.png");
            if(File.Exists(reference))
            {
                using(var form=new MainForm(null))count+=form.VerifyInteractions(reference);
                var demo=new Project{SourceName="三曲标注示例",Background=File.ReadAllBytes(reference),Labels=Renderer.Preset(0)};
                string[] ds={"EZ","IN","AT"};int[] ls={8,15,16};
                for(int i=0;i<3;i++){demo.Labels[i].Difficulty=ds[i];demo.Labels[i].Level=ls[i];demo.Labels[i].Author="谱面作者：示例谱师 "+(i+1);}
                demo.Save(Path.Combine(output,"三曲标注示例.phipreview"));
                using(var bg=Renderer.Decode(demo.Background))using(var b=Renderer.Render(demo,bg,bg.Width,bg.Height,false))Renderer.SavePng(b,Path.Combine(output,"三曲标注效果.png"));
            }
            File.WriteAllText(Path.Combine(output,"测试结果.txt"),"PASS: "+count+" checks\r\n"+DateTime.Now.ToString("s"));return 0;
        }
    }
}
