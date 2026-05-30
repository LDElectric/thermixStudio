using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Diagnostics; // Adicionado para gerenciar o processo do ExifTool

namespace ThermalPaletteConverter
{
    public partial class MainForm : Form
    {
        private class LutData
        {
            public string Name { get; set; } = string.Empty;
            public List<Color> Rgb { get; set; } = new();
        }

        private Dictionary<string, LutData> _luts = new();
        private Bitmap? _originalImage = null;
        private Bitmap? _currentImage = null;
        private Bitmap? _visibleLightImage = null;
        private string _detectedPalette = "Nenhuma";
        private string _currentFilePath = string.Empty;

        private Button btnOpen = null!;
        private ComboBox cmbPalette = null!;
        private Button btnSave = null!;
        private Button btnHelp = null!;
        private Label lblDetected = null!;
        private PictureBox pictureBox = null!;

        public MainForm()
        {
            InitializeComponent();
            LoadAllLuts();
        }

        private void InitializeComponent()
        {
            this.Text = "Conversor Térmico HD Inteligente - Pixel a Pixel";
            this.Size = new Size(1100, 800);
            this.StartPosition = FormStartPosition.CenterScreen;

            var controlPanel = new Panel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(10), BackColor = Color.FromArgb(30, 30, 30) };

            btnOpen = new Button { Text = "Abrir Termograma", Location = new Point(15, 12), Size = new Size(140, 35), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(60, 60, 60) };
            btnOpen.Click += BtnOpen_Click;

            var lblPalette = new Label { Text = "Nova Paleta:", Location = new Point(170, 20), AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 10) };
            cmbPalette = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(260, 17), Size = new Size(160, 30), Font = new Font("Segoe UI", 10) };
            cmbPalette.SelectedIndexChanged += CmbPalette_SelectedIndexChanged;

            btnSave = new Button { Text = "Exportar HD", Location = new Point(440, 12), Size = new Size(120, 35), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(0, 120, 215) };
            btnSave.Click += BtnSave_Click;

            btnHelp = new Button { Text = "Ajuda", Location = new Point(570, 12), Size = new Size(80, 35), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(60, 60, 60) };
            btnHelp.Click += BtnHelp_Click;

            lblDetected = new Label
            {
                Text = "Detectado: ---",
                TextAlign = ContentAlignment.MiddleRight,
                Dock = DockStyle.Right,
                Width = 300,
                ForeColor = Color.FromArgb(0, 190, 255),
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                Padding = new Padding(0, 0, 20, 0)
            };

            controlPanel.Controls.AddRange(new Control[] { btnOpen, lblPalette, cmbPalette, btnSave, btnHelp, lblDetected });
            this.Controls.Add(controlPanel);

            pictureBox = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(20, 20, 20) };
            this.Controls.Add(pictureBox);
        }

        private void LoadAllLuts()
        {
            string lutDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "paletas");
            var lutFiles = new Dictionary<string, string>
            {
                ["Iron"] = "iron_lut.json",
                ["Arco-Íris"] = "rainbow_lut.json",
                ["Grayscale"] = "grayscale_lut.json"
            };

            _luts.Clear();
            foreach (var kv in lutFiles)
            {
                string path = Path.Combine(lutDir, kv.Value);
                if (File.Exists(path))
                {
                    try
                    {
                        string json = File.ReadAllText(path);
                        using JsonDocument doc = JsonDocument.Parse(json);
                        JsonElement rgbArray = doc.RootElement.GetProperty("rgb");
                        var colors = new List<Color>();
                        foreach (JsonElement triplet in rgbArray.EnumerateArray())
                            colors.Add(Color.FromArgb(triplet[0].GetInt32(), triplet[1].GetInt32(), triplet[2].GetInt32()));
                        _luts[kv.Key] = new LutData { Name = kv.Key, Rgb = colors };
                    }
                    catch { }
                }
            }

            UpdatePaletteComboBox();
        }

        private void UpdatePaletteComboBox()
        {
            cmbPalette.Items.Clear();
            foreach (var key in _luts.Keys)
                cmbPalette.Items.Add(key);

            cmbPalette.Items.Add("Luz Visível");
            if (cmbPalette.Items.Count > 0) cmbPalette.SelectedIndex = 0;
        }

        private void BtnOpen_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog { Filter = "Imagens Térmicas|*.jpg;*.jpeg;*.png;*.bmp" };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    _currentFilePath = ofd.FileName;

                    _originalImage?.Dispose();
                    _visibleLightImage?.Dispose();
                    _visibleLightImage = null;

                    _originalImage = new Bitmap(_currentFilePath);

                    _visibleLightImage = ExtractVisibleLightImage(_currentFilePath);

                    _detectedPalette = DetectPalette(_originalImage);
                    lblDetected.Text = $"Detectado: {_detectedPalette}";

                    ApplyConversion();
                }
                catch (Exception ex) { MessageBox.Show("Erro ao abrir imagem: " + ex.Message); }
            }
        }

        private string DetectPalette(Bitmap image)
        {
            int w = image.Width, h = image.Height;
            var rand = new Random();
            var samples = new List<Color>();
            for (int i = 0; i < 500; i++)
                samples.Add(image.GetPixel(rand.Next(w / 4, 3 * w / 4), rand.Next(h / 4, 3 * h / 4)));

            string best = "Iron"; double minDist = double.MaxValue;
            foreach (var lut in _luts.Values)
            {
                double d = samples.Average(s => {
                    double m = double.MaxValue;
                    for (int j = 0; j < lut.Rgb.Count; j += 10)
                    {
                        int dr_c = s.R - lut.Rgb[j].R;
                        int dg_c = s.G - lut.Rgb[j].G;
                        int db_c = s.B - lut.Rgb[j].B;
                        double dist = dr_c * dr_c + dg_c * dg_c + db_c * db_c;
                        if (dist < m) m = dist;
                    }
                    return Math.Sqrt(m);
                });
                if (d < minDist) { minDist = d; best = lut.Name; }
            }
            return minDist > 70 ? "Desconhecida" : best;
        }

        private void CmbPalette_SelectedIndexChanged(object? sender, EventArgs e) => ApplyConversion();

        private void ApplyConversion()
        {
            if (_originalImage == null) return;
            if (cmbPalette.SelectedItem == null) return;

            string target = cmbPalette.SelectedItem.ToString()!;
            _currentImage?.Dispose();

            if (target == "Luz Visível")
            {
                if (_visibleLightImage != null)
                {
                    _currentImage = new Bitmap(_visibleLightImage);
                }
                else
                {
                    MessageBox.Show("Não foi encontrada uma cópia de luz visível (RGB) nativa embutida na estrutura de metadados desse arquivo ou o ExifTool não está disponível.",
                                    "Aviso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    cmbPalette.SelectedIndex = 0;
                    return;
                }
            }
            else
            {
                string src = _luts.ContainsKey(_detectedPalette) ? _detectedPalette : "Iron";
                if (src == target)
                    _currentImage = new Bitmap(_originalImage);
                else
                    _currentImage = ProcessSmartHD(_originalImage, src, target);
            }

            pictureBox.Image?.Dispose();
            pictureBox.Image = new Bitmap(_currentImage);
        }

        private Bitmap ProcessSmartHD(Bitmap srcImg, string srcName, string targetName)
        {
            int w = srcImg.Width, h = srcImg.Height;
            LutData srcLut = _luts[srcName], tgtLut = _luts[targetName];
            Bitmap dstImg = new Bitmap(w, h, PixelFormat.Format32bppArgb);

            BitmapData sData = srcImg.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData dData = dstImg.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            byte[] sBuf = new byte[sData.Stride * h], dBuf = new byte[dData.Stride * h];
            Marshal.Copy(sData.Scan0, sBuf, 0, sBuf.Length);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = (y * sData.Stride) + (x * 4);
                    byte b = sBuf[idx], g = sBuf[idx + 1], r = sBuf[idx + 2], a = sBuf[idx + 3];

                    bool isUI = false;
                    if ((x < w * 0.28 && y < h * 0.15) ||
                        (x > w * 0.85 && y < h * 0.15) ||
                        (x > w * 0.85 && y > h * 0.85) ||
                        (x < w * 0.25 && y > h * 0.85) ||
                        (x > w * 0.94))
                    {
                        int brightness = (r + g + b) / 3;
                        if (brightness > 220 || brightness < 35) isUI = true;
                    }

                    if (isUI)
                    {
                        dBuf[idx] = b; dBuf[idx + 1] = g; dBuf[idx + 2] = r; dBuf[idx + 3] = a;
                    }
                    else
                    {
                        int bestIdx = 0; int minD = int.MaxValue;
                        for (int k = 0; k < srcLut.Rgb.Count; k += 4)
                        {
                            Color c = srcLut.Rgb[k];
                            int dr = r - c.R, dg = g - c.G, db = b - c.B;
                            int dist = dr * dr + dg * dg + db * db;
                            if (dist < minD) { minD = dist; bestIdx = k; }
                        }
                        int start = Math.Max(0, bestIdx - 4), end = Math.Min(srcLut.Rgb.Count, bestIdx + 5);
                        for (int k = start; k < end; k++)
                        {
                            Color c = srcLut.Rgb[k];
                            int dr = r - c.R, dg = g - c.G, db = b - c.B;
                            int dist = dr * dr + dg * dg + db * db;
                            if (dist < minD) { minD = dist; bestIdx = k; }
                        }

                        float ratio = (float)bestIdx / (srcLut.Rgb.Count - 1);
                        Color nc = tgtLut.Rgb[(int)(ratio * (tgtLut.Rgb.Count - 1))];
                        dBuf[idx] = nc.B; dBuf[idx + 1] = nc.G; dBuf[idx + 2] = nc.R; dBuf[idx + 3] = 255;
                    }
                }
            }

            Marshal.Copy(dBuf, 0, dData.Scan0, dBuf.Length);
            srcImg.UnlockBits(sData); dstImg.UnlockBits(dData);
            return dstImg;
        }

        private string? EncontrarExifTool()
        {
            string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "exiftool.exe" : "exiftool";

            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv != null)
            {
                foreach (var p in pathEnv.Split(Path.PathSeparator))
                {
                    string fullPath = Path.Combine(p, exeName);
                    if (File.Exists(fullPath)) return fullPath;
                }
            }

            var possiveisCaminhos = new List<string>
            {
                @"C:\Users\Leonam Dias\AppData\Local\Programs\Python\Python310\lib\site-packages\dji_executables\dji_thermal_sdk_v1.7\exiftool-12.35.exe",
                @"C:\Program Files\exiftool\exiftool.exe",
                @"C:\Windows\exiftool.exe",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "exiftool.exe")
            };

            foreach (var caminho in possiveisCaminhos)
            {
                if (File.Exists(caminho)) return caminho;
            }

            return null;
        }

        private Bitmap? ExtractVisibleLightImage(string filePath)
        {
            string? exifToolPath = EncontrarExifTool();

            if (string.IsNullOrEmpty(exifToolPath))
            {
                MessageBox.Show(
                    "ExifTool não foi encontrado!\n\nCertifique-se de que o exiftool.exe está acessível no sistema para extração em alta definição.",
                    "Dependência Ausente", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            string[] tagsParaTestar = { "FLIR:EmbeddedImage", "EmbeddedImage", "PreviewImage" };

            foreach (var tag in tagsParaTestar)
            {
                try
                {
                    using var processo = new Process();
                    processo.StartInfo.FileName = exifToolPath;
                    processo.StartInfo.Arguments = $"-b -{tag} \"{filePath}\"";
                    processo.StartInfo.UseShellExecute = false;
                    processo.StartInfo.RedirectStandardOutput = true;
                    processo.StartInfo.CreateNoWindow = true;

                    processo.Start();

                    using var ms = new MemoryStream();
                    processo.StandardOutput.BaseStream.CopyTo(ms);
                    processo.WaitForExit();

                    byte[] buffer = ms.ToArray();

                    if (buffer.Length > 5000)
                    {
                        using var imageStream = new MemoryStream(buffer);
                        return new Bitmap(imageStream);
                    }
                }
                catch
                {
                    continue;
                }
            }

            return null;
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            if (_currentImage == null) return;
            using var sfd = new SaveFileDialog { Filter = "PNG HD|*.png|JPEG|*.jpg", FileName = "Termograma_HD.png" };
            if (sfd.ShowDialog() == DialogResult.OK)
            {
                _currentImage.Save(sfd.FileName, ImageFormat.Png);
                MessageBox.Show("Exportação concluída com sucesso!", "Sucesso", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void BtnHelp_Click(object? sender, EventArgs e)
        {
            MessageBox.Show("Algoritmo de Coloração Inteligente HD:\n\n" +
                "1. O sistema separa automaticamente pixels de dados térmicos de elementos de interface (logos, textos e barras).\n" +
                "2. A matriz radiométrica é colorida pixel a pixel em alta definição.\n" +
                "3. Elementos da câmera são preservados para manter a legibilidade das informações de temperatura.\n" +
                "4. Alternância para Luz Visível: O sistema realiza a chamada ao ExifTool em background via pipe binário, garantindo a extração nativa e limpa da foto digital RGB em sua resolução máxima original.");
        }
    }
}