using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

namespace PhigrosEnhancer
{
    public class ChartLabel
    {
        public string Name = "曲目";
        public string Difficulty = "IN";
        public int Level = 15;
        public string Author = "";
        public bool ShowBadge = true;
        public bool ShowAuthor = true;
        public float BadgeX = 1450, BadgeY = 800;
        public float AuthorX = 1000, AuthorY = 900;
        public float Scale = 1;
        public float AuthorWidth = 360;
        public ChartLabel Copy() { return (ChartLabel)MemberwiseClone(); }
        public override string ToString() { return Name + "  ·  " + Difficulty + " " + Level; }
    }

    public class Project
    {
        // .phipreview format version. Independent from the .phifree version.
        internal const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public string SourceName = "预览";
        public bool NumberOnTop = true;
        public byte[] Background;
        public List<ChartLabel> Labels = new List<ChartLabel>();
        public Project Copy()
        {
            return new Project { Version = Version, SourceName = SourceName, NumberOnTop = NumberOnTop,
                Background = Background, Labels = Labels.Select(x => x.Copy()).ToList() };
        }
        public void Validate()
        {
            if (Version != CurrentVersion) throw new InvalidDataException("工程格式版本无效（V" + Version + "）。");
            if (Background == null || Background.Length == 0) throw new InvalidDataException("工程缺少底图。");
            if (Labels == null || Labels.Count > 100) throw new InvalidDataException("曲目数量无效（最多 100 首）。");
            foreach (ChartLabel x in Labels)
            {
                if (x == null || !Renderer.Colors.ContainsKey(x.Difficulty ?? "") || x.Level < 1)
                    throw new InvalidDataException("难度必须是 EZ / HD / IN / AT，等级必须是正整数。");
                if (!Finite(x.Scale) || x.Scale < .25f || x.Scale > 3 || !Finite(x.AuthorWidth) || x.AuthorWidth < 80 || x.AuthorWidth > 1600)
                    throw new InvalidDataException("标注尺寸无效。");
                // X is bounded by the 1920 width reference, Y by the 1080 height reference.
                if (new[] { x.BadgeX, x.AuthorX }.Any(v => !Finite(v) || v < 0 || v > 1920)
                    || new[] { x.BadgeY, x.AuthorY }.Any(v => !Finite(v) || v < 0 || v > ClassicGeometry.ReferenceHeight))
                    throw new InvalidDataException("标注坐标无效。");
                if ((x.Author ?? "").Length > 500 || (x.Name ?? "").Length > 200)
                    throw new InvalidDataException("文本过长。");
            }
        }
        static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
        // In-memory only: Read must never write the project back to disk. A migrated
        // project is written in the current format only when the user saves it.
        static void Migrate(Project p)
        {
            if (p.Version == CurrentVersion) return;
            if (p.Version > CurrentVersion)
                throw new InvalidDataException("该工程由更新版本的软件创建（工程格式 V" + p.Version +
                    "，本程序支持到 V" + CurrentVersion + "）。请使用支持该工程格式的软件打开。");
            // No pre-V1 .phipreview was ever written; add explicit V1 -> Vn steps here.
            throw new InvalidDataException("不支持的旧工程格式（V" + p.Version + "，本程序支持 V" + CurrentVersion + "）。");
        }
        public static Project Read(string path)
        {
            if (new FileInfo(path).Length > 160 * 1024 * 1024) throw new InvalidDataException("工程文件过大。");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = XmlReader.Create(path, settings))
            {
                var p = (Project)new XmlSerializer(typeof(Project)).Deserialize(reader);
                Migrate(p); p.Validate(); return p;
            }
        }
        public void Save(string path)
        {
            Validate();
            AtomicFile.Write(path, delegate(string temp)
            {
                using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true }))
                    new XmlSerializer(typeof(Project)).Serialize(writer, this);
            });
        }
    }

    public static class Renderer
    {
        // Difficulty colors follow the supplied selected-difficulty swatches.
        // IN red (#CF1313) is sampled from the supplied IN screenshot.
        public static readonly Dictionary<string, Color> Colors = new Dictionary<string, Color> {
            { "EZ", Color.FromArgb(15, 180, 40) }, { "HD", Color.FromArgb(0, 120, 175) },
            { "IN", Color.FromArgb(207, 19, 19) }, { "AT", Color.FromArgb(35, 35, 35) }
        };
        static readonly PrivateFontCollection Fonts = new PrivateFontCollection();
        static FontFamily Family;
        static FontFamily NumberFamily;
        public static void InitFonts()
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FONT");
            if (Directory.Exists(folder)) foreach (string p in Directory.GetFiles(folder))
                try { Fonts.AddFontFile(p); } catch (ArgumentException) { }
            // Use a Windows CJK sans font: the bundled hybrid's CJK fallback can become SimSun in GDI+.
            try { Family = new FontFamily("Microsoft YaHei UI"); }
            catch (ArgumentException) { Family = FontFamily.GenericSansSerif; }
            NumberFamily = Fonts.Families.FirstOrDefault(f => f.Name.Contains("Saira") && !f.Name.Contains("Hybrid")) ?? Family;
        }
        public static Bitmap Decode(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes)) using (var img = Image.FromStream(ms, true, true))
            {
                if ((long)img.Width * img.Height > 80000000) throw new InvalidDataException("图片超过 8000 万像素，请先缩小。");
                return new Bitmap(img);
            }
        }
        public static byte[] Encode(Bitmap b)
        {
            using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }
        // Positions retain the version-1 normalized coordinates. Shape and font sizes
        // use a uniform height-based scale, independently of the background aspect.
        public static float AspectCorrection(float width, float height) { return 1920f * height / (1080f * width); }
        public static RectangleF BadgeRect(ChartLabel c, float correction = 1) { return new RectangleF(c.BadgeX, c.BadgeY, 128 * c.Scale * correction, 108 * c.Scale); }
        public static RectangleF AuthorRect(ChartLabel c, float correction = 1) { return new RectangleF(c.AuthorX, c.AuthorY, c.AuthorWidth * c.Scale * correction, 40 * c.Scale); }
        static PointF[] Shape(RectangleF r)
        {
            float slant = r.Height * .268f;
            return new[] { new PointF(r.Left + slant, r.Top), new PointF(r.Right, r.Top),
                new PointF(r.Right - slant, r.Bottom), new PointF(r.Left, r.Bottom) };
        }
        static void TextFit(Graphics g, string text, RectangleF box, float size, FontFamily family, FontStyle style)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.Alignment = StringAlignment.Center; format.LineAlignment = StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.EllipsisCharacter;
                Font font = new Font(family, size, style, GraphicsUnit.Pixel);
                while (g.MeasureString(text, font, new SizeF(100000, box.Height * 20), format).Width > box.Width && size > 8)
                { font.Dispose(); size = Math.Max(8, size - .5f); font = new Font(family, size, style, GraphicsUnit.Pixel); }
                using (font) g.DrawString(text, font, Brushes.White, box, format);
            }
        }
        public static void Draw(Graphics g, Project p, float correction = 1)
        {
            var transform = g.Save();
            g.ScaleTransform(correction, 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            foreach (ChartLabel source in p.Labels)
            {
                var c = source.Copy();
                Clamp(c, correction);
                c.BadgeX /= correction; c.AuthorX /= correction;
                float s = c.Scale;
                if (c.ShowAuthor && !string.IsNullOrWhiteSpace(c.Author))
                {
                    RectangleF r = AuthorRect(c);
                    using (var brush = new SolidBrush(Color.FromArgb(210, 14, 24, 33))) g.FillPolygon(brush, Shape(r));
                    TextFit(g, c.Author, new RectangleF(r.X + 18 * s, r.Y, r.Width - 36 * s, r.Height), 25 * s, Family, FontStyle.Regular);
                }
                if (c.ShowBadge)
                {
                    RectangleF r = BadgeRect(c);
                    var shadow = r; shadow.Offset(3 * s, 3 * s);
                    using (var brush = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillPolygon(brush, Shape(shadow));
                    using (var brush = new SolidBrush(Colors[c.Difficulty])) g.FillPolygon(brush, Shape(r));
                    if (p.NumberOnTop)
                    {
                        TextFit(g, c.Level.ToString(), new RectangleF(r.X + 20*s, r.Y + 9*s, r.Width - 32*s, 59*s), 49*s, NumberFamily, FontStyle.Regular);
                        TextFit(g, c.Difficulty, new RectangleF(r.X + 10*s, r.Y + 66*s, r.Width - 32*s, 29*s), 27*s, NumberFamily, FontStyle.Regular);
                    }
                    else
                    {
                        TextFit(g, c.Difficulty, new RectangleF(r.X + 20*s, r.Y + 10*s, r.Width - 32*s, 29*s), 27*s, NumberFamily, FontStyle.Regular);
                        TextFit(g, c.Level.ToString(), new RectangleF(r.X + 10*s, r.Y + 39*s, r.Width - 32*s, 61*s), 49*s, NumberFamily, FontStyle.Regular);
                    }
                }
            }
            g.Restore(transform);
        }
        public static Bitmap Render(Project p, Bitmap background, int width, int height, bool transparent)
        {
            var b = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent); g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (!transparent) g.DrawImage(background, new Rectangle(0, 0, width, height));
                var exportTransform = new LogicalViewportTransform(ClassicGeometry.ReferenceWidth, ClassicGeometry.ReferenceHeight, new RectangleF(0, 0, width, height));
                g.ScaleTransform(exportTransform.HorizontalScale, exportTransform.VerticalScale);
                Draw(g, p, AspectCorrection(width, height));
            }
            return b;
        }
        public static void SavePng(Bitmap b, string path)
        {
            AtomicFile.Write(path, delegate(string temp) { b.Save(temp, ImageFormat.Png); });
        }
        public static void Clamp(ChartLabel c, float correction = 1)
        {
            c.AuthorWidth = Math.Min(c.AuthorWidth, 1920 / (c.Scale * correction));
            c.BadgeX = Math.Max(0, Math.Min(1920 - (128*c.Scale + 3*c.Scale)*correction, c.BadgeX));
            c.BadgeY = Math.Max(0, Math.Min(1080 - 108*c.Scale - 3*c.Scale, c.BadgeY));
            c.AuthorX = Math.Max(0, Math.Min(1920 - c.AuthorWidth*c.Scale*correction, c.AuthorX));
            c.AuthorY = Math.Max(0, Math.Min(1080 - 40*c.Scale, c.AuthorY));
        }
        public static List<ChartLabel> Preset(int index)
        {
            var list = new List<ChartLabel>();
            if (index == 0) // Original 3-song template at 1920 x 1080.
            {
                list.Add(new ChartLabel { BadgeX = 1668, BadgeY = 246, AuthorX = 1250, AuthorY = 330 });
                list.Add(new ChartLabel { BadgeX = 1062, BadgeY = 606, AuthorX = 644, AuthorY = 690 });
                list.Add(new ChartLabel { BadgeX = 1472, BadgeY = 961, AuthorX = 1068, AuthorY = 1040 });
            }
            else if (index == 1) // 2 x 3 reference layout; any element remains movable.
            {
                float[] bx = { 615, 1128, 1626, 417, 891, 1395 };
                float[] by = { 342, 342, 342, 842, 842, 842 };
                float[] ax = { 291, 792, 1289, 82, 582, 1082 };
                float[] ay = { 438, 438, 438, 938, 938, 938 };
                for (int i = 0; i < 6; i++) list.Add(new ChartLabel { BadgeX=bx[i], BadgeY=by[i], AuthorX=ax[i], AuthorY=ay[i], Scale=.85f, AuthorWidth=320 });
            }
            else list.Add(new ChartLabel { BadgeX=1600, BadgeY=820, AuthorX=800, AuthorY=925, AuthorWidth=500 });
            for (int i = 0; i < list.Count; i++) list[i].Name = "曲目 " + (i+1);
            return list;
        }
        public static void FitPreset(List<ChartLabel> labels, int width, int height)
        {
            // The original generator fits its 16:9 composition inside other canvas ratios.
            float fit = Math.Min(width / 1920f, height / 1080f);
            float left = (width - 1920 * fit) / 2, top = (height - 1080 * fit) / 2;
            foreach(var c in labels)
            {
                c.BadgeX = (left + c.BadgeX * fit) * 1920 / width;
                c.AuthorX = (left + c.AuthorX * fit) * 1920 / width;
                c.BadgeY = (top + c.BadgeY * fit) * 1080 / height;
                c.AuthorY = (top + c.AuthorY * fit) * 1080 / height;
                c.Scale *= fit * 1080 / height;
                c.Scale = Math.Max(.25f, Math.Min(3, c.Scale));
            }
        }
    }

    class PreviewPanel : Panel
    {
        public PreviewPanel() { DoubleBuffered = true; ResizeRedraw = true; TabStop = true; }
        protected override bool IsInputKey(Keys keyData)
        { return new[] { Keys.Left, Keys.Right, Keys.Up, Keys.Down }.Contains(keyData & Keys.KeyCode) || base.IsInputKey(keyData); }
    }

    class MainForm : Form
    {
        readonly string root = AppDomain.CurrentDomain.BaseDirectory;
        Project project = new Project(); Bitmap background;
        readonly Stack<Project> undo = new Stack<Project>();
        readonly Stack<Project> redo = new Stack<Project>();
        string inputPath = ""; bool dirty, filling;
        readonly PreviewPanel preview = new PreviewPanel();
        readonly ListBox tracks = new ListBox();
        readonly ComboBox difficulty = new ComboBox(), layout = new ComboBox(), textOrder = new ComboBox(), exportSize = new ComboBox();
        readonly TextBox name = new TextBox(), author = new TextBox();
        readonly CheckBox badgeVisible = new CheckBox(), authorVisible = new CheckBox(), watch = new CheckBox();
        readonly NumericUpDown level = new NumericUpDown(), scale = new NumericUpDown(), authorWidth = new NumericUpDown();
        readonly NumericUpDown bx = new NumericUpDown(), by = new NumericUpDown(), ax = new NumericUpDown(), ay = new NumericUpDown();
        readonly Label status = new Label();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Dictionary<string,string> observed = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string pendingPath, pendingStamp; int pendingTicks;
        RectangleF imageRect; LogicalViewportTransform previewTransform; bool dragging, dragChanged; int activePart; PointF startMouse, startPos;
        float Correction { get { return background == null ? 1 : Renderer.AspectCorrection(background.Width, background.Height); } }
        void ClampLabels() { foreach(var c in project.Labels) Renderer.Clamp(c, Correction); }
        ChartLabel Current { get { return tracks.SelectedIndex >= 0 && tracks.SelectedIndex < project.Labels.Count ? project.Labels[tracks.SelectedIndex] : null; } }

        public MainForm(string initial)
        {
            Text = "布局兼容参考工具";
            Size = new Size(1420, 900); MinimumSize = new Size(1060, 740); StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9); BackColor = Color.FromArgb(239,242,247); KeyPreview = true;
            // 96 DPI is the design baseline of the hand-written pixel layout below.
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            var top = new FlowLayoutPanel { Dock=DockStyle.Top, Height=60, Padding=new Padding(10), WrapContents=true, BackColor=Color.White };
            top.Controls.Add(Button("自由排版 / 五点布局", delegate { new FreeLayoutForm().Show(); }, 180));
            top.Controls.Add(Button("导入底图", delegate { OpenImage(); }, 105));
            top.Controls.Add(Button("打开工程", delegate { OpenProject(); }, 105));
            top.Controls.Add(Button("保存工程", delegate { SaveProject(); }, 105));
            top.Controls.Add(Button("导出 PNG", delegate { Export(false); }, 120));
            top.Controls.Add(Button("导出透明标注", delegate { Export(true); }, 140));
            watch.Text="自动载入新图片"; watch.AutoSize=true; watch.Margin=new Padding(12,11,0,0); watch.Checked=true;
            top.Controls.Add(watch);
            var right = new Panel { Dock=DockStyle.Right, Width=318, AutoScroll=true, Padding=new Padding(16), BackColor=Color.White };
            var controls = new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, ColumnCount=1, RowCount=0, Width=280 };
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); right.Controls.Add(controls);
            AddRow(controls, new Label {Text="难度 / 谱师",Font=new Font(Font.FontFamily,16,FontStyle.Bold),Height=40,Dock=DockStyle.Top});
            AddRow(controls, new Label { Text="模板位置", Height=24 });
            layout.Items.AddRange(new object[] { "三曲模板", "六曲模板（2 × 3）", "单曲 / 自定义" }); layout.DropDownStyle=ComboBoxStyle.DropDownList; layout.SelectedIndex=0;
            AddRow(controls, layout); AddRow(controls, Button("应用模板位置", delegate { ApplyPreset(); }, 270));
            tracks.Height=112; tracks.IntegralHeight=false; tracks.SelectedIndexChanged += delegate { FillEditor(); preview.Invalidate(); }; AddRow(controls, tracks);
            var pair = new FlowLayoutPanel {Height=40,WrapContents=false,Dock=DockStyle.Top};
            pair.Controls.Add(Button("＋ 添加曲目", delegate { AddTrack(); }, 130)); pair.Controls.Add(Button("移除当前曲目", delegate { RemoveTrack(); }, 130)); AddRow(controls,pair);
            AddField(controls,"曲目标记（仅供编辑）",name); name.MaxLength=200;
            var diffRow = new FlowLayoutPanel {Height=36,WrapContents=false,Dock=DockStyle.Top};
            difficulty.Items.AddRange(new object[]{"EZ","HD","IN","AT"}); difficulty.DropDownStyle=ComboBoxStyle.DropDownList; difficulty.Width=112;
            level.Minimum=1; level.Maximum=int.MaxValue; level.Width=142; diffRow.Controls.Add(difficulty); diffRow.Controls.Add(level);
            AddRow(controls,new Label{Text="难度类型                       等级（正整数）",Height=25}); AddRow(controls,diffRow);
            AddField(controls,"谱面作者（留空时隐藏底栏）",author); author.MaxLength=500;
            var visibility = new FlowLayoutPanel { Height=32,WrapContents=false,Dock=DockStyle.Top };
            badgeVisible.Text="显示难度牌"; badgeVisible.AutoSize=true; authorVisible.Text="显示谱师栏"; authorVisible.AutoSize=true;
            visibility.Controls.Add(badgeVisible); visibility.Controls.Add(authorVisible); AddRow(controls,visibility);
            scale.Minimum=25;scale.Maximum=300;scale.Value=100;scale.Increment=5;
            authorWidth.Minimum=80;authorWidth.Maximum=1600;authorWidth.Value=360;
            AddField(controls,"当前曲目标注大小（%）",scale); AddField(controls,"谱师栏宽度（基准像素）",authorWidth);
            foreach(var n in new[]{bx,by,ax,ay}) {n.Minimum=0;n.DecimalPlaces=1;n.Increment=1;n.Width=124;}
            bx.Maximum=ax.Maximum=(decimal)ClassicGeometry.ReferenceWidth;   // X logical reference
            by.Maximum=ay.Maximum=(decimal)ClassicGeometry.ReferenceHeight;  // Y logical reference
            AddRow(controls,new Label {Text="位置（1920 × 1080 基准坐标）",Height=27});
            AddCoordinates(controls,"难度牌 X / Y",bx,by); AddCoordinates(controls,"谱师栏 X / Y",ax,ay);
            textOrder.Items.AddRange(new object[]{"数字在上 · 参考图样式","难度在上 · 数字在下"});textOrder.DropDownStyle=ComboBoxStyle.DropDownList;textOrder.SelectedIndex=0;
            AddField(controls,"文字顺序（所有曲目）",textOrder);
            exportSize.Items.AddRange(new object[]{"与底图相同（推荐）","宽 1920（保持底图比例）","宽 3840（保持底图比例）"});exportSize.DropDownStyle=ComboBoxStyle.DropDownList;exportSize.SelectedIndex=0;
            AddField(controls,"导出分辨率",exportSize);
            AddRow(controls,new Label{Text="拖动难度牌或谱师栏可分别调整位置。\n方向键微调，Shift 加速；Ctrl+Z 撤销。\nCtrl+S 导出 PNG；Ctrl+Shift+S 保存工程。",Height=78,ForeColor=Color.DimGray});
            preview.Dock=DockStyle.Fill;preview.BackColor=Color.FromArgb(27,32,42);preview.Paint+=PaintPreview;
            preview.MouseDown+=PreviewDown;preview.MouseMove+=PreviewMove;preview.MouseUp+=PreviewUp;
            preview.MouseCaptureChanged+=delegate {if(dragging){dragging=false;RefreshTrackList(tracks.SelectedIndex);}};
            preview.KeyDown+=PreviewKey;
            status.Dock=DockStyle.Bottom;status.Height=34;status.Padding=new Padding(12,7,0,0);status.BackColor=Color.White;
            Controls.Add(preview);Controls.Add(right);Controls.Add(status);Controls.Add(top);
            AllowDrop=true; DragEnter+=delegate(object s,DragEventArgs e){if(e.Data.GetDataPresent(DataFormats.FileDrop))e.Effect=DragDropEffects.Copy;};
            DragDrop+=delegate(object s,DragEventArgs e){var files=(string[])e.Data.GetData(DataFormats.FileDrop);if(files.Length>0)TryOpen(files[0]);};
            name.TextChanged+=EditorChanged;author.TextChanged+=EditorChanged;difficulty.SelectedIndexChanged+=EditorChanged;
            level.ValueChanged+=EditorChanged;scale.ValueChanged+=EditorChanged;authorWidth.ValueChanged+=EditorChanged;
            badgeVisible.CheckedChanged+=EditorChanged;authorVisible.CheckedChanged+=EditorChanged;
            foreach(var n in new[]{bx,by,ax,ay})n.ValueChanged+=EditorChanged;
            textOrder.SelectedIndexChanged+=delegate{if(!filling){Remember();project.NumberOnTop=textOrder.SelectedIndex==0;Changed();}};
            KeyDown+=Shortcuts;FormClosing+=OnEditorClosing;
            SnapshotFiles();timer.Interval=1000;timer.Tick+=WatchTick;timer.Start();
            project.Labels=Renderer.Preset(0);RefreshTrackList(0);
            SetStatus("请导入图片，或直接将图片拖入此窗口。");
            if(!string.IsNullOrEmpty(initial))Shown+=delegate{TryOpen(initial);};
        }
        static Button Button(string title, Action action, int width)
        {var b=new Button{Text=title,Width=width,Height=38,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(245,247,251),Margin=new Padding(0,1,7,1)};b.FlatAppearance.BorderColor=Color.FromArgb(213,220,230);b.Click+=delegate{action();};return b;}
        static void AddRow(TableLayoutPanel p,Control c)
        {c.Dock=DockStyle.Top;c.Margin=new Padding(0,0,0,7);p.RowCount++;p.RowStyles.Add(new RowStyle(SizeType.AutoSize));p.Controls.Add(c,0,p.RowCount-1);}
        static void AddField(TableLayoutPanel p,string title,Control c)
        {AddRow(p,new Label{Text=title,Height=23});AddRow(p,c);}
        static void AddCoordinates(TableLayoutPanel p,string title,NumericUpDown x,NumericUpDown y)
        {AddRow(p,new Label{Text=title,Height=23});var f=new FlowLayoutPanel{Height=34,WrapContents=false};f.Controls.Add(x);f.Controls.Add(y);AddRow(p,f);}
        void SetStatus(string text){status.Text=text;}
        void Error(Exception ex){MessageBox.Show(this,ex.GetBaseException().Message,"操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        void Remember(){undo.Push(project.Copy());redo.Clear();if(undo.Count>80){var saved=undo.Take(60).Reverse().ToArray();undo.Clear();foreach(var p in saved)undo.Push(p);}}
        void Changed(){dirty=true;preview.Invalidate();Text="布局兼容参考工具 *";}
        void RefreshTrackList(int index)
        {filling=true;tracks.BeginUpdate();tracks.Items.Clear();foreach(var c in project.Labels)tracks.Items.Add(c.ToString());tracks.EndUpdate();tracks.SelectedIndex=tracks.Items.Count==0?-1:Math.Min(Math.Max(index,0),tracks.Items.Count-1);filling=false;FillEditor();preview.Invalidate();}
        void FillEditor()
        {
            if(filling)return;filling=true;var c=Current;
            foreach(Control control in new Control[]{name,author,difficulty,level,scale,authorWidth,bx,by,ax,ay,badgeVisible,authorVisible})control.Enabled=c!=null;
            if(c!=null)
            {name.Text=c.Name;author.Text=c.Author;difficulty.SelectedItem=c.Difficulty;level.Value=c.Level;scale.Value=(decimal)(c.Scale*100);authorWidth.Value=(decimal)c.AuthorWidth;bx.Value=(decimal)c.BadgeX;by.Value=(decimal)c.BadgeY;ax.Value=(decimal)c.AuthorX;ay.Value=(decimal)c.AuthorY;badgeVisible.Checked=c.ShowBadge;authorVisible.Checked=c.ShowAuthor;}
            textOrder.SelectedIndex=project.NumberOnTop?0:1;filling=false;
        }
        void EditorChanged(object sender,EventArgs e)
        {
            if(filling||Current==null)return;Remember();var c=Current;
            c.Name=name.Text;c.Author=author.Text;c.Difficulty=difficulty.SelectedItem as string??"IN";c.Level=(int)level.Value;
            c.Scale=(float)scale.Value/100;c.AuthorWidth=(float)authorWidth.Value;
            c.BadgeX=(float)bx.Value;c.BadgeY=(float)by.Value;c.AuthorX=(float)ax.Value;c.AuthorY=(float)ay.Value;c.ShowBadge=badgeVisible.Checked;c.ShowAuthor=authorVisible.Checked;
            Renderer.Clamp(c, Correction);filling=true;tracks.Items[tracks.SelectedIndex]=c.ToString();filling=false;Changed();
            if(sender is NumericUpDown)FillEditor();
        }
        void ApplyPreset()
        {
            Remember();var replacement=Renderer.Preset(layout.SelectedIndex);
            if(background!=null)Renderer.FitPreset(replacement,background.Width,background.Height);
            for(int i=0;i<Math.Min(replacement.Count,project.Labels.Count);i++)
            {var from=project.Labels[i];var to=replacement[i];to.Name=from.Name;to.Author=from.Author;to.Difficulty=from.Difficulty;to.Level=from.Level;to.ShowBadge=from.ShowBadge;to.ShowAuthor=from.ShowAuthor;}
            project.Labels=replacement;ClampLabels();RefreshTrackList(0);Changed();SetStatus("已应用模板；可继续拖动微调。Ctrl+Z 可还原。");
        }
        void AddTrack(){if(project.Labels.Count>=100)return;Remember();var c=Current==null?new ChartLabel():Current.Copy();c.Name="曲目 "+(project.Labels.Count+1);c.BadgeX+=25;c.BadgeY+=25;c.AuthorX+=25;c.AuthorY+=25;Renderer.Clamp(c, Correction);project.Labels.Add(c);RefreshTrackList(project.Labels.Count-1);Changed();}
        void RemoveTrack(){if(Current==null)return;int i=tracks.SelectedIndex;Remember();project.Labels.RemoveAt(i);RefreshTrackList(i);Changed();}
        void PaintPreview(object sender,PaintEventArgs e)
        {
            if(background==null)
            {using(var font=new Font(Font.FontFamily,18))using(var brush=new SolidBrush(Color.FromArgb(183,195,213)))e.Graphics.DrawString("将图片拖到这里\n\n或点击「导入底图」开始制作",font,brush,new RectangleF(45,preview.Height/2-65,preview.Width-90,160));imageRect=RectangleF.Empty;previewTransform=null;return;}
            float ratio=Math.Min((preview.Width-36f)/background.Width,(preview.Height-36f)/background.Height);
            imageRect=new RectangleF((preview.Width-background.Width*ratio)/2,(preview.Height-background.Height*ratio)/2,background.Width*ratio,background.Height*ratio);
            previewTransform=new LogicalViewportTransform(ClassicGeometry.ReferenceWidth,ClassicGeometry.ReferenceHeight,imageRect);
            Graphics g=e.Graphics;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(background,imageRect);
            var saved=g.Save();g.TranslateTransform(imageRect.X,imageRect.Y);g.ScaleTransform(previewTransform.HorizontalScale,previewTransform.VerticalScale);
            g.SetClip(new RectangleF(0,0,1920,1080));Renderer.Draw(g,project,Correction);
            if(Current!=null)
            {
                using(var pen=new Pen(Color.FromArgb(165,225,255),2*1920/imageRect.Width))
                {pen.DashStyle=DashStyle.Dash;if(Current.ShowBadge)g.DrawPolygon(pen,RectPoints(Renderer.BadgeRect(Current, Correction)));if(Current.ShowAuthor)g.DrawPolygon(pen,RectPoints(Renderer.AuthorRect(Current, Correction)));}
            }
            g.Restore(saved);
        }
        static PointF[] RectPoints(RectangleF r){return new[]{new PointF(r.Left,r.Top),new PointF(r.Right,r.Top),new PointF(r.Right,r.Bottom),new PointF(r.Left,r.Bottom)};}
        PointF Logical(Point p)
        {
            LogicalViewportTransform transform=previewTransform;
            // Fallback only for the "no paint yet" lifecycle: same math as the legacy formula.
            if(transform==null)return new PointF((p.X-imageRect.X)*1920/imageRect.Width,(p.Y-imageRect.Y)*1080/imageRect.Height);
            return transform.ScreenToLogical(new PointF(p.X,p.Y));
        }
        void PreviewDown(object sender,MouseEventArgs e)
        {
            preview.Focus();if(background==null||e.Button!=MouseButtons.Left||!imageRect.Contains(e.Location))return;
            PointF p=Logical(e.Location);
            for(int i=project.Labels.Count-1;i>=0;i--)
            {
                var c=project.Labels[i];int hit=c.ShowBadge&&Renderer.BadgeRect(c, Correction).Contains(p)?1:c.ShowAuthor&&Renderer.AuthorRect(c, Correction).Contains(p)?2:0;
                if(hit==0)continue;tracks.SelectedIndex=i;activePart=hit;dragging=true;dragChanged=false;startMouse=p;startPos=hit==1?new PointF(c.BadgeX,c.BadgeY):new PointF(c.AuthorX,c.AuthorY);preview.Capture=true;return;
            }
        }
        void PreviewMove(object sender,MouseEventArgs e)
        {
            if(!dragging||Current==null)return;PointF p=Logical(e.Location);if(!dragChanged){if(Math.Abs(p.X-startMouse.X)+Math.Abs(p.Y-startMouse.Y)<2)return;Remember();dragChanged=true;}
            if(activePart==1){Current.BadgeX=startPos.X+p.X-startMouse.X;Current.BadgeY=startPos.Y+p.Y-startMouse.Y;}else{Current.AuthorX=startPos.X+p.X-startMouse.X;Current.AuthorY=startPos.Y+p.Y-startMouse.Y;}
            Renderer.Clamp(Current, Correction);Changed();FillEditor();
        }
        void PreviewUp(object sender,MouseEventArgs e){dragging=false;preview.Capture=false;}
        void PreviewKey(object sender,KeyEventArgs e)
        {
            if(Current==null||!new[]{Keys.Left,Keys.Right,Keys.Up,Keys.Down}.Contains(e.KeyCode))return;
            Remember();float step=e.Shift?10:1;float x=e.KeyCode==Keys.Left?-step:e.KeyCode==Keys.Right?step:0;float y=e.KeyCode==Keys.Up?-step:e.KeyCode==Keys.Down?step:0;
            if(activePart==2){Current.AuthorX+=x;Current.AuthorY+=y;}else{Current.BadgeX+=x;Current.BadgeY+=y;}Renderer.Clamp(Current, Correction);FillEditor();Changed();e.Handled=true;e.SuppressKeyPress=true;
        }
        void Shortcuts(object sender,KeyEventArgs e)
        {
            if(e.Control&&e.KeyCode==Keys.S){if(e.Shift)SaveProject();else Export(false);e.Handled=true;e.SuppressKeyPress=true;}
            bool undoShortcut=e.Control&&e.KeyCode==Keys.Z&&!e.Shift;
            bool redoShortcut=e.Control&&(e.KeyCode==Keys.Y||(e.KeyCode==Keys.Z&&e.Shift));
            if(undoShortcut||redoShortcut)
            {
                Stack<Project> from=undoShortcut?undo:redo,to=undoShortcut?redo:undo;
                if(from.Count>0){to.Push(project.Copy());project=from.Pop();ClampLabels();RefreshTrackList(tracks.SelectedIndex);Changed();}
                e.Handled=true;e.SuppressKeyPress=true;
            }
        }
        void OpenImage()
        {using(var d=new OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",InitialDirectory=root})if(d.ShowDialog(this)==DialogResult.OK)TryOpen(d.FileName);}
        void TryOpen(string path)
        {
            try
            {
                if(path.EndsWith(".phipreview",StringComparison.OrdinalIgnoreCase)){LoadProject(path);return;}
                byte[] bytes=File.ReadAllBytes(path);Bitmap next=Renderer.Decode(bytes);
                bool initialLayout = background == null && !dirty;
                project.Background=Renderer.Encode(next);project.SourceName=Path.GetFileNameWithoutExtension(path);inputPath=Path.GetFullPath(path);
                if(background!=null)background.Dispose();background=next;
                if(initialLayout)Renderer.FitPreset(project.Labels,next.Width,next.Height);
                ClampLabels();undo.Clear();redo.Clear();Changed();RefreshTrackList(tracks.SelectedIndex);
                SetStatus("已载入 "+Path.GetFileName(path)+"  ·  "+next.Width+" × "+next.Height+"；标注可拖动，导出时不会显示虚线。");
            }
            catch(Exception ex){Error(ex);}
        }
        void OpenProject(){using(var d=new OpenFileDialog{Filter="标注工程|*.phipreview",InitialDirectory=root})if(d.ShowDialog(this)==DialogResult.OK)TryOpen(d.FileName);}
        bool CanDiscard()
        {
            if(!dirty||background==null)return true;
            var choice=MessageBox.Show(this,"是否先保存当前标注工程？工程包含底图和可编辑标注。","保存修改",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question);
            if(choice==DialogResult.Cancel)return false;if(choice==DialogResult.Yes)return SaveProject();return true;
        }
        void LoadProject(string path)
        {
            var next=Project.Read(path);Bitmap img=Renderer.Decode(next.Background);
            if(!CanDiscard()){img.Dispose();return;}
            if(background!=null)background.Dispose();background=img;project=next;ClampLabels();inputPath="";undo.Clear();redo.Clear();dirty=false;RefreshTrackList(0);
            Text="布局兼容参考工具";SetStatus("已打开工程："+Path.GetFileName(path));
        }
        bool SaveProject()
        {
            if(background==null){SetStatus("请先导入底图。");return false;}
            using(var d=new SaveFileDialog{Filter="标注工程|*.phipreview",FileName=SafeName(project.SourceName)+"_标注.phipreview",InitialDirectory=root})
            {
                if(d.ShowDialog(this)!=DialogResult.OK)return false;
                try{project.Save(d.FileName);dirty=false;Text="布局兼容参考工具";SetStatus("工程已保存："+d.FileName);return true;}catch(Exception ex){Error(ex);return false;}
            }
        }
        static string SafeName(string name){return string.Concat((name??"预览").Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));}
        void Export(bool transparent)
        {
            if(background==null){SetStatus("请先导入底图。");return;}
            string directory=Path.Combine(root,"增强版输出");
            try { Directory.CreateDirectory(directory); } catch(Exception ex) { Error(ex); return; }
            using(var d=new SaveFileDialog{Filter="PNG 图片|*.png",InitialDirectory=directory,FileName=SafeName(project.SourceName)+(transparent?"_透明标注":"_难度谱师")+".png"})
            {
                if(d.ShowDialog(this)!=DialogResult.OK)return;
                if(string.Equals(Path.GetFullPath(d.FileName),inputPath,StringComparison.OrdinalIgnoreCase)){SetStatus("请选择新的文件名，以保留未加标注的底图。");return;}
                try
                {
                    project.Validate();int w=exportSize.SelectedIndex==1?1920:exportSize.SelectedIndex==2?3840:background.Width;
                    int h=(int)Math.Round(background.Height*(double)w/background.Width);
                    if(h<1||(long)w*h>80000000)throw new InvalidDataException("导出尺寸过大，请选择与底图相同。");
                    using(var b=Renderer.Render(project,background,w,h,transparent))Renderer.SavePng(b,d.FileName);
                    observed[Path.GetFullPath(d.FileName)]=FileStamp(d.FileName);
                    SetStatus("已导出 "+w+" × "+h+" PNG："+d.FileName);
                }
                catch(Exception ex){Error(ex);}
            }
        }
        static string FileStamp(string path){var f=new FileInfo(path);return f.Length+":"+f.LastWriteTimeUtc.Ticks;}
        void SnapshotFiles(){foreach(var p in Directory.GetFiles(root,"*.png"))try{observed[p]=FileStamp(p);}catch(IOException){}}
        void WatchTick(object sender,EventArgs e)
        {
            if(!watch.Checked||dragging||!Enabled||OwnedForms.Length>0||Form.ActiveForm!=this&&Form.ActiveForm!=null)return;
            try
            {
                var changed=Directory.GetFiles(root,"*.png").Where(p=>!observed.ContainsKey(p)||observed[p]!=FileStamp(p)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if(changed==null){pendingPath=null;return;}string stamp=FileStamp(changed);
                if(changed!=pendingPath||stamp!=pendingStamp){pendingPath=changed;pendingStamp=stamp;pendingTicks=0;return;}
                if(++pendingTicks<2)return;
                // The original exporter can write slowly. Decode and obtain an exclusive read before accepting it.
                byte[] bytes;using(var stream=new FileStream(changed,FileMode.Open,FileAccess.Read,FileShare.None))using(var ms=new MemoryStream()){stream.CopyTo(ms);bytes=ms.ToArray();}
                using(var image=Renderer.Decode(bytes)){}
                TryOpen(changed);SnapshotFiles();pendingPath=null;
                SetStatus("已自动载入图片："+Path.GetFileName(changed)+"；当前难度和谱师设置已保留。");
            }
            catch(IOException){}catch(ArgumentException){}catch(ExternalException){}catch(InvalidDataException){}
        }
        void OnEditorClosing(object sender,FormClosingEventArgs e){if(!CanDiscard()){e.Cancel=true;return;}timer.Stop();}
        protected override void Dispose(bool disposing)
        {if(disposing){timer.Dispose();if(background!=null){background.Dispose();background=null;}}base.Dispose(disposing);}

        // In-process interaction tests: use the same event handlers as the UI, without desktop input.
        internal int VerifyInteractions(string samplePath)
        {
            timer.Stop();TryOpen(samplePath);int checks=0;
            Action<bool,string> check=delegate(bool ok,string message){if(!ok)throw new Exception("UI test: "+message);checks++;};
            check(background!=null&&Current!=null,"load image and select track");
            // Undo / redo characterisation (self-contained: stacks cleared on entry and exit).
            undo.Clear();redo.Clear();
            int levelA=Current.Level;
            level.Value=levelA+1;
            check(Current.Level==levelA+1,"redo setup: a level edit is applied");
            check(undo.Count==1&&redo.Count==0,"an edit pushes undo and clears redo");
            Shortcuts(this,new KeyEventArgs(Keys.Control|Keys.Z));
            check(Current.Level==levelA&&undo.Count==0&&redo.Count==1,"undo restores the level and moves the state to redo");
            Shortcuts(this,new KeyEventArgs(Keys.Control|Keys.Y));
            check(Current.Level==levelA+1&&undo.Count==1&&redo.Count==0,"redo re-applies the level and moves it back to undo");
            // Ctrl+Shift+Z must behave as redo, not as undo.
            Shortcuts(this,new KeyEventArgs(Keys.Control|Keys.Z));
            check(Current.Level==levelA&&redo.Count==1,"ctrl+Z undoes and leaves one redo entry");
            Shortcuts(this,new KeyEventArgs(Keys.Control|Keys.Shift|Keys.Z));
            check(Current.Level==levelA+1&&undo.Count==1&&redo.Count==0,"ctrl+shift+Z redoes like ctrl+Y");
            Project projectBeforeNoop=project;int levelNoop=Current.Level;
            Shortcuts(this,new KeyEventArgs(Keys.Control|Keys.Shift|Keys.Z));
            check(Current.Level==levelNoop&&ReferenceEquals(project,projectBeforeNoop)&&undo.Count==1&&redo.Count==0,
                "ctrl+shift+Z with an empty redo stack is a safe no-op");
            undo.Clear();redo.Clear();
            int levelBase=Current.Level;
            level.Value=levelBase+1;level.Value=levelBase+2;
            check(undo.Count==2,"two edits create two undo entries");
            Shortcuts(this,new KeyEventArgs(Keys.Control|Keys.Z));
            check(Current.Level==levelBase+1&&redo.Count==1,"undo returns to the middle state");
            level.Value=levelBase+5;
            check(redo.Count==0,"a new edit after undo clears the redo branch");
            Shortcuts(this,new KeyEventArgs(Keys.Control|Keys.Y));
            check(Current.Level==levelBase+5&&redo.Count==0,"redo after invalidation does not restore the abandoned state");
            undo.Clear();redo.Clear();
            float badgeA=Current.BadgeX;
            bx.Value=(decimal)300;
            check(Current.BadgeX!=badgeA,"redo setup: a badge X edit is applied");
            Shortcuts(this,new KeyEventArgs(Keys.Control|Keys.Z));
            check(Math.Abs(Current.BadgeX-badgeA)<.001f,"undo restores badge X");
            Shortcuts(this,new KeyEventArgs(Keys.Control|Keys.Y));
            check(Math.Abs(Current.BadgeX-300)<.001f,"redo re-applies badge X");
            undo.Clear();redo.Clear();
            check(bx.Maximum==(decimal)ClassicGeometry.ReferenceWidth,"badge X input maximum matches the 1920 logical width");
            check(ax.Maximum==(decimal)ClassicGeometry.ReferenceWidth,"author X input maximum matches the 1920 logical width");
            check(by.Maximum==(decimal)ClassicGeometry.ReferenceHeight,"badge Y input maximum matches the 1080 logical height");
            check(ay.Maximum==(decimal)ClassicGeometry.ReferenceHeight,"author Y input maximum matches the 1080 logical height");
            author.Text="Chart by 测试谱师";
            check(Current.Author=="Chart by 测试谱师"&&tracks.SelectedIndex==0,"author edit preserves selection");
            difficulty.SelectedItem="HD";level.Value=12;
            check(Current.Difficulty=="HD"&&Current.Level==12,"difficulty and level");
            AddTrack();check(project.Labels.Count==4&&Current.Difficulty=="HD","add");
            RemoveTrack();check(project.Labels.Count==3,"remove");
            Shortcuts(this,new KeyEventArgs(Keys.Control|Keys.Z));check(project.Labels.Count==4,"undo");
            tracks.SelectedIndex=0;scale.Value=300;authorWidth.Value=1600;
            check(Renderer.AuthorRect(Current, Correction).Right<=1920.1f,"large author bar stays inside canvas");
            scale.Value=100;authorWidth.Value=360;Current.BadgeX=500;Current.BadgeY=300;
            using(var canvas=new Bitmap(Math.Max(1,preview.Width),Math.Max(1,preview.Height)))
            using(var graphics=Graphics.FromImage(canvas))PaintPreview(preview,new PaintEventArgs(graphics,preview.ClientRectangle));
            check(previewTransform!=null,"PaintPreview initializes the preview transform");
            check(previewTransform.HorizontalScale==imageRect.Width/1920f&&previewTransform.VerticalScale==imageRect.Height/1080f,
                "preview transform scales match the image rect exactly");
            PointF probeScreen=new PointF(imageRect.X+imageRect.Width/3f,imageRect.Y+imageRect.Height/4f);
            PointF viaHelper=previewTransform.ScreenToLogical(probeScreen);
            PointF viaLegacy=new PointF((probeScreen.X-imageRect.X)*1920/imageRect.Width,(probeScreen.Y-imageRect.Y)*1080/imageRect.Height);
            check(Math.Abs(viaHelper.X-viaLegacy.X)<.0001f&&Math.Abs(viaHelper.Y-viaLegacy.Y)<.0001f,
                "preview transform inverse matches the legacy formula");
            Point probePoint=new Point((int)probeScreen.X,(int)probeScreen.Y);
            PointF viaLogical=Logical(probePoint);
            PointF legacyLogical=new PointF((probePoint.X-imageRect.X)*1920/imageRect.Width,(probePoint.Y-imageRect.Y)*1080/imageRect.Height);
            check(Math.Abs(viaLogical.X-legacyLogical.X)<.0001f&&Math.Abs(viaLogical.Y-legacyLogical.Y)<.0001f,
                "MainForm.Logical returns the legacy conversion");
            int px=(int)(imageRect.X+(500+128*Correction-10)*imageRect.Width/1920);int py=(int)(imageRect.Y+340*imageRect.Height/1080);
            int dragHistory=undo.Count;
            PreviewDown(preview,new MouseEventArgs(MouseButtons.Left,1,px,py,0));
            PreviewMove(preview,new MouseEventArgs(MouseButtons.Left,1,px+15,py+10,0));
            PreviewMove(preview,new MouseEventArgs(MouseButtons.Left,1,px+25,py+20,0));
            PreviewMove(preview,new MouseEventArgs(MouseButtons.Left,1,px+35,py+30,0));PreviewUp(preview,new MouseEventArgs(MouseButtons.Left,1,px+35,py+30,0));
            check(Current.BadgeX>500&&Current.BadgeY>300,"drag badge");
            check(undo.Count==dragHistory+1,"one continuous drag records exactly one undo entry");
            float moved=Current.BadgeX;PreviewKey(preview,new KeyEventArgs(Keys.Right|Keys.Shift));
            check(Math.Abs(Current.BadgeX-moved-10)<.01f,"keyboard nudge");
            Shortcuts(this,new KeyEventArgs(Keys.Control|Keys.Z));check(Math.Abs(Current.BadgeX-moved)<.01f,"undo nudge");
            Current.AuthorX=300;Current.AuthorY=500;
            px=(int)(imageRect.X+(300+Current.AuthorWidth*Correction-10)*imageRect.Width/1920);
            py=(int)(imageRect.Y+520*imageRect.Height/1080);
            PreviewDown(preview,new MouseEventArgs(MouseButtons.Left,1,px,py,0));
            PreviewMove(preview,new MouseEventArgs(MouseButtons.Left,1,px+20,py+15,0));PreviewUp(preview,new MouseEventArgs(MouseButtons.Left,1,px+20,py+15,0));
            check(Current.AuthorX>300&&Current.AuthorY>500,"drag full-width author bar");
            string incoming=Path.Combine(root,"__enhancer_watch_"+Guid.NewGuid().ToString("N")+".png");
            try
            {
                using(var locked=new FileStream(incoming,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {locked.WriteByte(137);locked.Flush();WatchTick(this,EventArgs.Empty);WatchTick(this,EventArgs.Empty);WatchTick(this,EventArgs.Empty);}
                check(!observed.ContainsKey(incoming),"wait for complete unlocked export");
                File.WriteAllBytes(incoming,File.ReadAllBytes(samplePath));
                string expectedAuthor=Current.Author;WatchTick(this,EventArgs.Empty);WatchTick(this,EventArgs.Empty);WatchTick(this,EventArgs.Empty);
                check(inputPath==incoming&&Current.Author==expectedAuthor&&observed.ContainsKey(incoming),"auto import and retain labels");
                using(var changed=new Bitmap(320,180))Renderer.SavePng(changed,incoming);
                WatchTick(this,EventArgs.Empty);WatchTick(this,EventArgs.Empty);WatchTick(this,EventArgs.Empty);
                check(background.Width==320&&background.Height==180,"receive overwritten export");
            }
            finally { if(File.Exists(incoming))File.Delete(incoming); }
            return checks;
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Renderer.InitFonts();
            try
            {
                if(args.Length>0&&args[0]=="--render")
                {
                    if(args.Length!=3)throw new ArgumentException("--render <工程.phipreview> <输出.png>");
                    var p=Project.Read(args[1]);using(var bg=Renderer.Decode(p.Background))using(var b=Renderer.Render(p,bg,bg.Width,bg.Height,false))Renderer.SavePng(b,args[2]);return 0;
                }
                if(args.Length>0&&args[0]=="--self-test")return Tests.Run(args.Length>1?args[1]:"增强版示例");
                if(args.Length>0&&args[0]=="--free-test")return FreeTests.Run(args.Length>1?args[1]:"增强版示例\\自由排版");
                if(args.Length>0&&args[0]=="--free-render")
                {if(args.Length!=3)throw new ArgumentException("--free-render <工程.phifree> <输出.png>");var free=FreeProject.Read(args[1]);using(var painter=new FreePainter())using(var b=painter.Render(free,false))Renderer.SavePng(b,args[2]);return 0;}
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                if(args.Length>0&&(args[0]=="--free"||args[0].EndsWith(".phifree",StringComparison.OrdinalIgnoreCase)))
                {Application.Run(new FreeLayoutForm(args[0]=="--free"?(args.Length>1?args[1]:null):args[0]));return 0;}
                Application.Run(new MainForm(args.Length>0?args[0]:null));return 0;
            }
            catch(Exception ex)
            {
                if(args.Length>0&&args[0].StartsWith("--")){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"EnhancerSource","last-error.txt"),ex.ToString());return 1;}
                MessageBox.Show(ex.Message,"启动失败");return 1;
            }
        }
    }
}
