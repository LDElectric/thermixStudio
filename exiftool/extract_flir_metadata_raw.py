import tkinter as tk
from tkinter import ttk, filedialog, messagebox, scrolledtext
import subprocess
import json
import os
import ctypes
import threading
import csv
from pathlib import Path
from datetime import datetime

# Ativa o DPI Awareness para o Windows 11 (evita texto embaçado)
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(1)
except Exception:
    pass

# Extensões de imagem suportadas para processamento em lote
SUPPORTED_EXTENSIONS = ('.jpg', '.jpeg', '.tiff', '.tif', '.png', '.bmp')

def run_exiftool(file_path):
    """Executa o exiftool para pegar os metadados como JSON."""
    cmd = ['exiftool', '-j', '-a', '-u', '-G1', file_path]
    try:
        creationflags = 0
        if os.name == 'nt':
            creationflags = subprocess.CREATE_NO_WINDOW
        result = subprocess.run(cmd, capture_output=True, text=True, check=True, creationflags=creationflags)
        return json.loads(result.stdout)[0]
    except subprocess.CalledProcessError as e:
        raise Exception(f"Erro ao executar exiftool: {e.stderr}")
    except FileNotFoundError:
        raise Exception("Exiftool não encontrado. Certifique-se de que ele está instalado e no PATH do sistema.")

def extract_radiometric_data(metadata):
    """Extrai especificamente dados radiométricos/FLIR do metadado."""
    radiometric = {}
    
    # Tags específicas FLIR (ajuste conforme seu modelo de câmera)
    flir_tags = [
        'FLIR:AtmosTemperature', 'FLIR:AtmosphericTransAlpha', 'FLIR:AtmosphericTransBeta',
        'FLIR:AtmosphericTransX', 'FLIR:AtmosphericTransY', 'FLIR:AutoMaxMinMaxTemp',
        'FLIR:AutoMaxMinRange', 'FLIR:CameraPartNumber', 'FLIR:CameraSerialNumber',
        'FLIR:Distance', 'FLIR:Emissivity', 'FLIR:EmissivityMode', 'FLIR:Filter',
        'FLIR:FilterSerialNumber', 'FLIR:FocusDistance', 'FLIR:FocusMetric',
        'FLIR:Gain', 'FLIR:GPSAltitude', 'FLIR:GPSDateStamp', 'FLIR:GPSDateTime',
        'FLIR:GPSLatitude', 'FLIR:GPSLongitude', 'FLIR:GPSMapDatum', 'FLIR:GPSStatus',
        'FLIR:GPSTimeStamp', 'FLIR:Humidity', 'FLIR:ImageTemperatureMax',
        'FLIR:ImageTemperatureMean', 'FLIR:ImageTemperatureMin', 'FLIR:Iso',
        'FLIR:ObjectAttitude', 'FLIR:ObjectDistance', 'FLIR:PaletteName',
        'FLIR:PlanckO', 'FLIR:PlanckR1', 'FLIR:PlanckR2', 'FLIR:PlanckB',
        'FLIR:PlanckF', 'FLIR:RawValueRangeMax', 'FLIR:RawValueRangeMin',
        'FLIR:ReflectedApparentTemperature', 'FLIR:ReflectedTemperature',
        'FLIR:RelativeHumidity', 'FLIR:TemperatureMax', 'FLIR:TemperatureMean',
        'FLIR:TemperatureMin', 'FLIR:UnknownTemperature', 'FLIR:WindowNumber',
        'FLIR:WindowTemperature', 'FLIR:WindowTransmission'
    ]
    
    # Busca todas as tags que começam com FLIR ou Thermal
    for key, value in metadata.items():
        if 'FLIR' in key or 'Thermal' in key or 'Temperature' in key:
            radiometric[key] = value
    
    # Adiciona tags específicas mesmo se não estiverem no padrão acima
    for tag in flir_tags:
        if tag in metadata:
            radiometric[tag] = metadata[tag]
    
    return radiometric

def save_radiometric_csv(radiometric_data, output_path):
    """Salva dados radiométricos em CSV."""
    if not radiometric_data:
        return
    
    with open(output_path, 'w', encoding='utf-8', newline='') as f:
        writer = csv.writer(f)
        writer.writerow(['Parâmetro Radiométrico', 'Valor'])
        for key, value in sorted(radiometric_data.items()):
            writer.writerow([key, value])

def flatten_dict(data, parent_key='', sep='.'):
    """Achata dicionários aninhados para facilitar a exibição e exportação."""
    items = []
    for k, v in data.items():
        new_key = f"{parent_key}{sep}{k}" if parent_key else k
        if isinstance(v, dict):
            items.extend(flatten_dict(v, new_key, sep=sep).items())
        elif isinstance(v, list):
            items.append((new_key, ', '.join(str(i) for i in v)))
        else:
            items.append((new_key, v))
    return dict(items)

def save_metadata_to_txt(metadata, output_path):
    """Salva metadados formatados em um arquivo de texto."""
    with open(output_path, 'w', encoding='utf-8') as f:
        f.write("=" * 100 + "\n")
        f.write("METADADOS DO TERMOGRAMA\n")
        f.write("=" * 100 + "\n\n")
        f.write(f"{'Tag / Título':<55} | Valor\n")
        f.write("-" * 100 + "\n")
        for key, value in sorted(metadata.items()):
            f.write(f"{key:<55} | {value}\n")

class FlirExtractorApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Extrator de Metadados FLIR - Com Dados Radiométricos")
        self.root.geometry("1000x750")
        
        style = ttk.Style()
        style.theme_use('clam')
        style.configure('TButton', font=('Segoe UI', 10), padding=5)
        
        self.current_metadata = {}
        self.current_radiometric = {}
        self.batch_running = False
        
        self.setup_ui()

    def setup_ui(self):
        # Frame Superior (Botões)
        top_frame = ttk.Frame(self.root, padding="10")
        top_frame.pack(side=tk.TOP, fill=tk.X)

        self.btn_carregar = ttk.Button(top_frame, text="Selecionar Termograma (Visualizar)", command=self.load_image)
        self.btn_carregar.pack(side=tk.LEFT, padx=5)

        self.btn_batch = ttk.Button(top_frame, text="Processar Pasta (Lote)", command=self.batch_process_folder)
        self.btn_batch.pack(side=tk.LEFT, padx=5)

        self.btn_exportar = ttk.Button(top_frame, text="Exportar Visualizado", command=self.export_txt, state=tk.DISABLED)
        self.btn_exportar.pack(side=tk.LEFT, padx=5)
        
        self.btn_export_radio = ttk.Button(top_frame, text="Exportar Dados Radiométricos", command=self.export_radiometric, state=tk.DISABLED)
        self.btn_export_radio.pack(side=tk.LEFT, padx=5)
        
        self.btn_clear_log = ttk.Button(top_frame, text="Limpar Log", command=self.clear_log)
        self.btn_clear_log.pack(side=tk.LEFT, padx=5)
        
        self.lbl_status = ttk.Label(top_frame, text="Nenhum arquivo carregado", font=('Segoe UI', 10, 'italic'))
        self.lbl_status.pack(side=tk.RIGHT, padx=5)

        # Notebook para abas (metadados completos vs radiométricos)
        notebook = ttk.Notebook(self.root)
        notebook.pack(fill=tk.BOTH, expand=True, padx=10, pady=5)
        
        # Aba de metadados completos
        self.tab_metadata = ttk.Frame(notebook)
        notebook.add(self.tab_metadata, text="Metadados Completos")
        
        self.txt_display = scrolledtext.ScrolledText(self.tab_metadata, wrap=tk.WORD, font=('Consolas', 10))
        self.txt_display.pack(fill=tk.BOTH, expand=True)
        
        # Aba de dados radiométricos
        self.tab_radiometric = ttk.Frame(notebook)
        notebook.add(self.tab_radiometric, text="Dados Radiométricos")
        
        self.txt_radiometric = scrolledtext.ScrolledText(self.tab_radiometric, wrap=tk.WORD, font=('Consolas', 10))
        self.txt_radiometric.pack(fill=tk.BOTH, expand=True)
        
        # Barra de progresso para processamento em lote
        progress_frame = ttk.Frame(self.root, padding="5")
        progress_frame.pack(side=tk.BOTTOM, fill=tk.X)
        self.progress_var = tk.IntVar()
        self.progress_bar = ttk.Progressbar(progress_frame, variable=self.progress_var, maximum=100)
        self.progress_bar.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=5)
        self.lbl_progress = ttk.Label(progress_frame, text="")
        self.lbl_progress.pack(side=tk.RIGHT, padx=5)

    def log_message(self, message, level="INFO"):
        """Adiciona uma mensagem ao log com timestamp."""
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.txt_display.insert(tk.END, f"[{timestamp}] [{level}] {message}\n")
        self.txt_display.see(tk.END)
        self.root.update_idletasks()

    def clear_log(self):
        self.txt_display.delete(1.0, tk.END)
        self.txt_radiometric.delete(1.0, tk.END)

    def load_image(self):
        if self.batch_running:
            messagebox.showwarning("Processamento em andamento", "Aguarde o término do processamento em lote.")
            return
            
        file_path = filedialog.askopenfilename(
            title="Selecione o Termograma",
            filetypes=[("Imagens", "*.jpg *.jpeg *.tiff *.tif *.png"), ("Todos os Arquivos", "*.*")]
        )
        if not file_path:
            return

        self.lbl_status.config(text="Processando...")
        self.root.update()

        try:
            raw_meta = run_exiftool(file_path)
            self.current_metadata = flatten_dict(raw_meta)
            self.current_radiometric = extract_radiometric_data(self.current_metadata)
            
            self.display_metadata_in_text(self.current_metadata)
            self.display_radiometric_in_text(self.current_radiometric)
            
            self.btn_exportar.config(state=tk.NORMAL)
            self.btn_export_radio.config(state=tk.NORMAL if self.current_radiometric else tk.DISABLED)
            self.lbl_status.config(text=f"Visualizando: {os.path.basename(file_path)}")
            self.log_message(f"Metadados carregados de: {file_path}")
            
            if self.current_radiometric:
                self.log_message(f"Encontrados {len(self.current_radiometric)} parâmetros radiométricos")
            else:
                self.log_message("Nenhum dado radiométrico encontrado nesta imagem", "WARNING")
                
        except Exception as e:
            messagebox.showerror("Erro", str(e))
            self.lbl_status.config(text="Erro no processamento")
            self.log_message(str(e), "ERROR")

    def display_metadata_in_text(self, metadata):
        self.txt_display.delete(1.0, tk.END)
        self.txt_display.insert(tk.END, f"{'Tag / Título':<55} | Valor\n")
        self.txt_display.insert(tk.END, "-" * 100 + "\n")
        for key, value in sorted(metadata.items()):
            linha = f"{key:<55} | {value}\n"
            self.txt_display.insert(tk.END, linha)
    
    def display_radiometric_in_text(self, radiometric):
        self.txt_radiometric.delete(1.0, tk.END)
        if not radiometric:
            self.txt_radiometric.insert(tk.END, "Nenhum dado radiométrico encontrado.\n\n")
            self.txt_radiometric.insert(tk.END, "Possíveis causas:\n")
            self.txt_radiometric.insert(tk.END, "1. A imagem não é um termograma FLIR\n")
            self.txt_radiometric.insert(tk.END, "2. O termograma foi salvo como JPEG não-radiométrico\n")
            self.txt_radiometric.insert(tk.END, "3. Use imagens no formato TIFF ou PNG para melhores resultados\n")
            return
        
        self.txt_radiometric.insert(tk.END, "=" * 100 + "\n")
        self.txt_radiometric.insert(tk.END, "DADOS RADIOMÉTRICOS / TEMPERATURA\n")
        self.txt_radiometric.insert(tk.END, "=" * 100 + "\n\n")
        
        for key, value in sorted(radiometric.items()):
            linha = f"{key:<55} | {value}\n"
            self.txt_radiometric.insert(tk.END, linha)

    def export_txt(self):
        if not self.current_metadata:
            return
        file_path = filedialog.asksaveasfilename(
            defaultextension=".txt",
            title="Salvar Metadados Completos",
            filetypes=[("Arquivo de Texto", "*.txt")]
        )
        if not file_path:
            return
        try:
            save_metadata_to_txt(self.current_metadata, file_path)
            messagebox.showinfo("Sucesso", f"Metadados exportados com sucesso para:\n{file_path}")
            self.log_message(f"Arquivo exportado: {file_path}")
        except Exception as e:
            messagebox.showerror("Erro ao salvar", str(e))
            self.log_message(f"Erro exportação: {e}", "ERROR")
    
    def export_radiometric(self):
        if not self.current_radiometric:
            messagebox.showwarning("Sem dados", "Não há dados radiométricos para exportar.")
            return
        
        file_path = filedialog.asksaveasfilename(
            defaultextension=".csv",
            title="Salvar Dados Radiométricos",
            filetypes=[("CSV", "*.csv"), ("Texto", "*.txt")]
        )
        if not file_path:
            return
        
        try:
            save_radiometric_csv(self.current_radiometric, file_path)
            messagebox.showinfo("Sucesso", f"Dados radiométricos exportados para:\n{file_path}")
            self.log_message(f"Dados radiométricos exportados: {file_path}")
        except Exception as e:
            messagebox.showerror("Erro ao salvar", str(e))
            self.log_message(f"Erro exportação radiométrica: {e}", "ERROR")

    def batch_process_folder(self):
        if self.batch_running:
            messagebox.showwarning("Processamento em andamento", "Já existe um processo em lote rodando.")
            return
            
        folder_selected = filedialog.askdirectory(title="Selecione a pasta com os termogramas")
        if not folder_selected:
            return
            
        # Lista todos os arquivos de imagem suportados na pasta
        image_files = []
        for ext in SUPPORTED_EXTENSIONS:
            image_files.extend(Path(folder_selected).glob(f"*{ext}"))
            image_files.extend(Path(folder_selected).glob(f"*{ext.upper()}"))
        image_files = sorted(set(image_files))
        
        if not image_files:
            messagebox.showinfo("Nenhuma imagem", f"Nenhuma imagem com extensões {', '.join(SUPPORTED_EXTENSIONS)} encontrada na pasta.")
            return
        
        # Opções de exportação
        export_radiometric = messagebox.askyesno("Dados Radiométricos", "Deseja também exportar os dados radiométricos em CSV?")
        output_option = messagebox.askyesno("Pasta de saída", "Deseja salvar os arquivos em uma subpasta 'metadados'?\n(Se não, será na mesma pasta das imagens.)")
        
        self.batch_running = True
        self.btn_batch.config(state=tk.DISABLED)
        self.btn_carregar.config(state=tk.DISABLED)
        self.progress_bar["maximum"] = len(image_files)
        self.progress_var.set(0)
        self.lbl_progress.config(text="0 / " + str(len(image_files)))
        self.log_message(f"Iniciando processamento em lote de {len(image_files)} arquivos.")
        
        # Executa em thread separada
        thread = threading.Thread(target=self._process_batch, args=(folder_selected, image_files, output_option, export_radiometric), daemon=True)
        thread.start()

    def _process_batch(self, folder_selected, image_files, output_option, export_radiometric):
        """Processa todos os arquivos em lote (executado em thread)."""
        success_count = 0
        fail_count = 0
        radiometric_count = 0
        
        for idx, img_path in enumerate(image_files, start=1):
            if not self.batch_running:
                break
                
            try:
                # Extrai metadados
                raw_meta = run_exiftool(str(img_path))
                metadata = flatten_dict(raw_meta)
                radiometric = extract_radiometric_data(metadata)
                
                # Define caminho de saída
                if output_option:
                    output_dir = Path(folder_selected) / "metadados"
                    output_dir.mkdir(exist_ok=True)
                else:
                    output_dir = Path(folder_selected)
                
                # Salva metadados completos
                output_file = output_dir / f"{img_path.stem}_metadata.txt"
                save_metadata_to_txt(metadata, output_file)
                
                # Salva dados radiométricos se solicitado
                if export_radiometric and radiometric:
                    radiometric_file = output_dir / f"{img_path.stem}_radiometric.csv"
                    save_radiometric_csv(radiometric, radiometric_file)
                    radiometric_count += 1
                    self.root.after(0, self.log_message, f"RAD: {img_path.name} -> {radiometric_file.name}")
                
                success_count += 1
                self.root.after(0, self.log_message, f"OK: {img_path.name} -> {output_file.name}")
            except Exception as e:
                fail_count += 1
                self.root.after(0, self.log_message, f"FALHA: {img_path.name} - {str(e)}", "ERROR")
            
            # Atualiza barra de progresso
            self.root.after(0, self.update_progress, idx, len(image_files))
        
        # Finalização
        self.root.after(0, self.batch_finished, success_count, fail_count, radiometric_count, export_radiometric)
    
    def update_progress(self, current, total):
        self.progress_var.set(current)
        self.lbl_progress.config(text=f"{current} / {total}")
    
    def batch_finished(self, success, fail, radiometric_count, export_radiometric):
        self.batch_running = False
        self.btn_batch.config(state=tk.NORMAL)
        self.btn_carregar.config(state=tk.NORMAL)
        
        msg = f"Lote concluído: {success} sucesso, {fail} falhas"
        if export_radiometric:
            msg += f"\nArquivos com dados radiométricos: {radiometric_count}"
        
        self.lbl_status.config(text=msg)
        self.log_message(f"Processamento em lote finalizado. {success} arquivos processados, {fail} falhas.")
        
        if export_radiometric:
            self.log_message(f"Dados radiométricos extraídos de {radiometric_count} arquivos.")
        
        messagebox.showinfo("Lote concluído", f"Processamento finalizado.\n\nSucessos: {success}\nFalhas: {fail}\n\nVerifique o log para detalhes.")
        self.progress_var.set(0)
        self.lbl_progress.config(text="")

if __name__ == "__main__":
    root = tk.Tk()
    app = FlirExtractorApp(root)
    root.mainloop()