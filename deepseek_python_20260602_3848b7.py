import cv2
import numpy as np
import matplotlib.pyplot as plt
from scipy.stats import wasserstein_distance
from skimage.exposure import match_histograms
import os
from datetime import datetime
# Importando as bibliotecas para a interface gráfica de seleção de arquivos
import tkinter as tk
from tkinter import filedialog, messagebox

# ------------------------------------------------------------
# 1. Carregar imagens
# ------------------------------------------------------------
def load_images(path_orig, path_render):
    # Lê as imagens utilizando os caminhos fornecidos pelo usuário
    img_orig = cv2.imread(path_orig)
    img_render = cv2.imread(path_render)
    
    # Verifica se as imagens foram carregadas corretamente
    if img_orig is None or img_render is None:
        raise FileNotFoundError("Não foi possível carregar uma ou ambas as imagens.")
    
    # Converte as imagens do padrão BGR (do OpenCV) para RGB (para visualização e análise corretas)
    img_orig_rgb = cv2.cvtColor(img_orig, cv2.COLOR_BGR2RGB)
    img_render_rgb = cv2.cvtColor(img_render, cv2.COLOR_BGR2RGB)
    
    return img_orig_rgb, img_render_rgb, img_orig, img_render

# ------------------------------------------------------------
# 2. Análise de histogramas e métricas
# ------------------------------------------------------------
def compute_histogram_stats(img, title):
    # Converte para HSV para podermos analisar a Luminância (brilho) no canal V
    hsv = cv2.cvtColor(img, cv2.COLOR_RGB2HSV)
    v_channel = hsv[:, :, 2]
    
    # Calcula os histogramas para os canais R, G, B e V (Luminância)
    hist_r = cv2.calcHist([img], [0], None, [256], [0, 256])
    hist_g = cv2.calcHist([img], [1], None, [256], [0, 256])
    hist_b = cv2.calcHist([img], [2], None, [256], [0, 256])
    hist_v = cv2.calcHist([v_channel], [0], None, [256], [0, 256])
    
    # Normaliza os histogramas para que a soma seja 1 (facilita a comparação)
    hist_r = hist_r / hist_r.sum()
    hist_g = hist_g / hist_g.sum()
    hist_b = hist_b / hist_b.sum()
    hist_v = hist_v / hist_v.sum()
    
    return hist_r, hist_g, hist_b, hist_v

def compare_histograms(hist_orig, hist_render, method='correlation'):
    # Dicionário com os métodos matemáticos de comparação do OpenCV
    methods = {
        'correlation': cv2.HISTCMP_CORREL,
        'chi_square': cv2.HISTCMP_CHISQR,
        'intersection': cv2.HISTCMP_INTERSECT,
        'bhattacharyya': cv2.HISTCMP_BHATTACHARYYA
    }
    # Executa a comparação entre os dois histogramas usando o método escolhido
    return cv2.compareHist(hist_orig, hist_render, methods[method])

def analyze_color_distribution(img_orig, img_render, output_dir):
    # Calcula os histogramas das duas imagens
    h_r_orig, h_g_orig, h_b_orig, h_v_orig = compute_histogram_stats(img_orig, "Original")
    h_r_ren, h_g_ren, h_b_ren, h_v_ren = compute_histogram_stats(img_render, "Render")
    
    metrics = {}
    # Realiza as comparações estatísticas para cada um dos canais
    for ch, (h_orig, h_ren) in enumerate([(h_r_orig, h_r_ren), (h_g_orig, h_g_ren), 
                                          (h_b_orig, h_b_ren), (h_v_orig, h_v_ren)]):
        ch_name = ['R', 'G', 'B', 'Luminância'][ch]
        metrics[ch_name] = {
            'correlation': compare_histograms(h_orig, h_ren, 'correlation'),
            'chi_square': compare_histograms(h_orig, h_ren, 'chi_square'),
            'intersection': compare_histograms(h_orig, h_ren, 'intersection'),
            'bhattacharyya': compare_histograms(h_orig, h_ren, 'bhattacharyya'),
            'wasserstein': wasserstein_distance(h_orig.flatten(), h_ren.flatten())
        }
    
    # Prepara a geração gráfica dos histogramas para o relatório visual
    fig, axes = plt.subplots(2, 2, figsize=(12, 9))
    channels = [(h_r_orig, h_r_ren, 'Red'), (h_g_orig, h_g_ren, 'Green'),
                (h_b_orig, h_b_ren, 'Blue'), (h_v_orig, h_v_ren, 'Luminance (V)')]
    
    # Plota os gráficos sobrepondo a imagem original (azul) e o render (vermelho)
    for ax, (h_orig, h_ren, title) in zip(axes.flatten(), channels):
        ax.plot(h_orig, color='blue', label='Original', linewidth=2)
        ax.plot(h_ren, color='red', label='Render', linewidth=2)
        ax.set_title(title)
        ax.legend()
        ax.set_xlim(0, 255)
        
    plt.suptitle("Comparação de Histogramas - Original vs Render")
    plt.tight_layout()
    
    # Salva o gráfico na mesma pasta da imagem selecionada
    graph_path = os.path.join(output_dir, "histogram_comparison.png")
    plt.savefig(graph_path, dpi=150)
    plt.close()
    
    return metrics

# ------------------------------------------------------------
# 3. Estatísticas de brilho
# ------------------------------------------------------------
def brightness_contrast_stats(img):
    # Isola o canal V (Value/Brilho) do espaço de cor HSV
    hsv = cv2.cvtColor(img, cv2.COLOR_RGB2HSV)
    v = hsv[:, :, 2]
    
    # Retorna as métricas básicas para sabermos se a imagem está clara/escura no geral
    return {
        'mean': np.mean(v),
        'std': np.std(v),
        'min': np.min(v),
        'max': np.max(v),
        'median': np.median(v)
    }

# ------------------------------------------------------------
# 4. Mapeamento de cores (LUT) 
# ------------------------------------------------------------
def build_color_lut(img_render, img_orig, bins=32):
    # Cria uma tabela de conversão (LUT) para mapear como as cores do render 
    # deveriam se comportar para ficarem idênticas à original
    render_pixels = img_render.reshape(-1, 3).astype(np.float32)
    orig_pixels = img_orig.reshape(-1, 3).astype(np.float32)
    step = 256 // bins
    render_discrete = (render_pixels // step).astype(np.int32)
    
    lut = {}
    for idx, color_idx in enumerate(render_discrete):
        key = tuple(color_idx)
        if key not in lut:
            lut[key] = []
        lut[key].append(orig_pixels[idx])
        
    lut_mean = {}
    for key, color_list in lut.items():
        lut_mean[key] = np.mean(color_list, axis=0).astype(np.uint8)
        
    lut_array = np.zeros((bins, bins, bins, 3), dtype=np.uint8)
    for (r,g,b), color in lut_mean.items():
        if 0 <= r < bins and 0 <= g < bins and 0 <= b < bins:
            lut_array[r, g, b] = color
            
    return lut_mean, lut_array

def apply_lut(img_render, lut_array, bins=32):
    # Aplica a tabela de conversão gerada acima na imagem renderizada
    step = 256 // bins
    h, w, _ = img_render.shape
    result = np.zeros_like(img_render)
    for i in range(h):
        for j in range(w):
            r, g, b = img_render[i, j]
            idx_r = r // step
            idx_g = g // step
            idx_b = b // step
            result[i, j] = lut_array[idx_r, idx_g, idx_b]
    return result

# ------------------------------------------------------------
# 5. Exportação do super relatório em TXT
# ------------------------------------------------------------
def export_full_report_to_txt(img_orig_rgb, img_render_rgb, metrics, stats_orig, stats_render, 
                              path_orig, path_render, output_dir):
    
    # Define o caminho completo de onde o txt será salvo
    output_txt = os.path.join(output_dir, "relatorio_calibracao.txt")
    
    # Abre (ou cria) o arquivo de texto para escrita ('w')
    with open(output_txt, 'w', encoding='utf-8') as f:
        f.write("="*80 + "\n")
        f.write("SUPER RELATÓRIO DE CALIBRAÇÃO DE CORES - TERMOGRAMAS\n")
        f.write(f"Gerado em: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
        f.write("="*80 + "\n\n")
        
        # Bloco 1: Identificação dos Arquivos
        f.write("[ARQUIVOS ANALISADOS]\n")
        f.write(f"Original: {os.path.basename(path_orig)}\n")
        f.write(f"Render:   {os.path.basename(path_render)}\n")
        f.write(f"Caminho original: {path_orig}\n")
        f.write(f"Dimensões original: {img_orig_rgb.shape[1]}x{img_orig_rgb.shape[0]} pixels\n")
        f.write(f"Dimensões render:   {img_render_rgb.shape[1]}x{img_render_rgb.shape[0]} pixels\n\n")
        
        # Bloco 2: Análise de Brilho
        f.write("[ESTATÍSTICAS DE BRILHO (Luminância - canal V do HSV)]\n")
        f.write(f"{'Métrica':<12} {'Original':>12} {'Render':>12} {'Diferença (R-O)':>18}\n")
        f.write("-"*60 + "\n")
        for key in ['mean', 'std', 'median', 'min', 'max']:
            orig_val = stats_orig[key]
            ren_val = stats_render[key]
            diff = ren_val - orig_val
            f.write(f"{key.capitalize():<12} {orig_val:>12.2f} {ren_val:>12.2f} {diff:>+18.2f}\n")
        f.write("\n")
        
        # Bloco 3: Análise dos Histogramas (Cor)
        f.write("[COMPARAÇÃO DE HISTOGRAMAS - MÉTRICAS DETALHADAS]\n")
        f.write("(Correlação: 1 = idêntico, 0 = sem correlação; Chi²: menor é melhor; Interseção: maior é melhor; Bhattacharyya: 0 = idêntico; Wasserstein: 0 = idêntico)\n\n")
        for ch_name in ['R', 'G', 'B', 'Luminância']:
            m = metrics[ch_name]
            f.write(f"Canal {ch_name}:\n")
            f.write(f"  • Correlação:     {m['correlation']:.6f}\n")
            f.write(f"  • Chi-quadrado:   {m['chi_square']:.4f}\n")
            f.write(f"  • Interseção:     {m['intersection']:.4f}\n")
            f.write(f"  • Bhattacharyya:  {m['bhattacharyya']:.6f}\n")
            f.write(f"  • Wasserstein:    {m['wasserstein']:.6f}\n")
            f.write("\n")
        
        # Bloco 4: Inteligência e Sugestões Automáticas
        f.write("[INTERPRETAÇÃO E SUGESTÕES DE COLORAÇÃO]\n")
        delta_mean = stats_render['mean'] - stats_orig['mean']
        
        # Sugestão de Brilho
        if delta_mean > 2.0:
            f.write(f"• O render está MAIS CLARO que o original em média {delta_mean:.2f} níveis de cinza.\n")
            f.write("  → Recomendação: Reduza o brilho geral da sua paleta ou ajuste a curva térmica para escurecer a base.\n")
        elif delta_mean < -2.0:
            f.write(f"• O render está MAIS ESCURO que o original em média {abs(delta_mean):.2f} níveis de cinza.\n")
            f.write("  → Recomendação: Aumente o ganho geral/brilho no software de renderização térmica.\n")
        else:
            f.write("• O brilho global do render está muito bem alinhado com o original.\n")
        
        # Sugestão de Tonalidade (Cor)
        avg_corr = np.mean([metrics[ch]['correlation'] for ch in ['R','G','B']])
        f.write(f"• Correlação média entre canais RGB: {avg_corr:.4f}\n")
        if avg_corr < 0.7:
            f.write("  → Alerta: A paleta de cores (falsas cores) do render está bastante divergente do original.\n")
        elif avg_corr < 0.9:
            f.write("  → Correlação moderada. Requer ajustes pontuais na saturação ou mapa de gradiente (LUT).\n")
        else:
            f.write("  → Excelente correlação de cor. O mapeamento térmico está muito fiel ao equipamento.\n")
        
        r_corr = metrics['R']['correlation']
        g_corr = metrics['G']['correlation']
        if r_corr < g_corr and r_corr < 0.85:
            f.write("• NOTA: O canal de vermelho (R) apresenta discrepância. O render pode estar com excesso de tons quentes (laranja/vermelho intenso) fora do ponto focal térmico.\n")
        
        # Bloco 5: Arquivos exportados
        f.write("\n[ARQUIVOS DE APOIO GERADOS NA MESMA PASTA]\n")
        f.write("• histogram_comparison.png  - Gráfico visual mostrando os desvios de cor\n")
        f.write("• corrected_render.jpg       - Render corrigido usando o padrão matemático da original\n")
        f.write("• lut_corrected.jpg          - Render corrigido via aproximação de LUT de 32 bits\n")
        
        f.write("\n" + "="*80 + "\n")
        f.write("FIM DO RELATÓRIO\n")
        f.write("="*80 + "\n")
    
    print(f"✅ Relatório TXT salvo em: {output_txt}")

# ------------------------------------------------------------
# 6. Função Principal (Automação de Interface e Fluxo)
# ------------------------------------------------------------
def main():
    # 1. Inicia o motor gráfico do Tkinter
    root = tk.Tk()
    root.withdraw() # Oculta a janela principal vazia, deixando só as caixas de diálogo
    
    # Exibe uma mensagem rápida informando o que o usuário deve fazer
    messagebox.showinfo("Iniciando Calibração", "Passo 1: Selecione o termograma ORIGINAL (Referência).")
    
    # 2. Abre a janela para escolher o arquivo Original
    path_orig = filedialog.askopenfilename(
        title="Selecione o termograma de referência (Original)",
        filetypes=[("Imagens", "*.jpg *.jpeg *.png *.bmp"), ("Todos os arquivos", "*.*")]
    )
    
    # Se o usuário cancelar a seleção, encerramos o programa
    if not path_orig:
        print("Operação cancelada. Imagem original não selecionada.")
        return

    messagebox.showinfo("Passo 2", "Passo 2: Selecione agora o seu termograma RENDERIZADO.")
    
    # 3. Abre a janela para escolher o arquivo Renderizado
    path_render = filedialog.askopenfilename(
        title="Selecione o termograma renderizado por você",
        filetypes=[("Imagens", "*.jpg *.jpeg *.png *.bmp"), ("Todos os arquivos", "*.*")]
    )
    
    if not path_render:
        print("Operação cancelada. Imagem renderizada não selecionada.")
        return
        
    print("Processando imagens... Por favor aguarde.")
    
    # 4. Define onde os arquivos de saída serão salvos (na mesma pasta do arquivo original)
    output_dir = os.path.dirname(path_orig)
    
    # 5. Carrega as imagens
    img_orig_rgb, img_render_rgb, _, _ = load_images(path_orig, path_render)
    
    # 6. Calcula a distribuição e gera os gráficos
    metrics = analyze_color_distribution(img_orig_rgb, img_render_rgb, output_dir)
    
    # 7. Levanta as estatísticas de brilho
    stats_orig = brightness_contrast_stats(img_orig_rgb)
    stats_render = brightness_contrast_stats(img_render_rgb)
    
    # 8. Gera o relatório completo
    export_full_report_to_txt(img_orig_rgb, img_render_rgb, metrics, stats_orig, stats_render,
                              path_orig, path_render, output_dir)
    
    # 9. Gera as imagens com correção sugerida (automática)
    matched = match_histograms(img_render_rgb, img_orig_rgb, channel_axis=2)
    matched = np.clip(matched, 0, 255).astype(np.uint8)
    matched_path = os.path.join(output_dir, "corrected_render.jpg")
    cv2.imwrite(matched_path, cv2.cvtColor(matched, cv2.COLOR_RGB2BGR))
    
    lut_mean, lut_array = build_color_lut(img_render_rgb, img_orig_rgb, bins=32)
    corrected_lut = apply_lut(img_render_rgb, lut_array, bins=32)
    lut_path = os.path.join(output_dir, "lut_corrected.jpg")
    cv2.imwrite(lut_path, cv2.cvtColor(corrected_lut, cv2.COLOR_RGB2BGR))
    
    # 10. Mostra as imagens na tela antes de encerrar
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(12, 5))
    ax1.imshow(img_orig_rgb)
    ax1.set_title("Original (Referência)")
    ax1.axis('off')
    ax2.imshow(img_render_rgb)
    ax2.set_title("Render (Sua versão)")
    ax2.axis('off')
    plt.show()
    
    # 11. Finaliza avisando ao usuário que tudo ocorreu bem
    print(f"Processo concluído com sucesso! Verifique a pasta:\n{output_dir}")
    messagebox.showinfo("Sucesso!", f"Análise concluída com sucesso!\n\nOs relatórios, gráficos e sugestões foram salvos na pasta:\n{output_dir}")

if __name__ == "__main__":
    main()