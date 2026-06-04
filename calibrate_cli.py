"""
Análise de calibração de cores - Termogramas (versão CLI)
Compara original FLIR vs render do Thermix Studio.
Uso: python calibrate_cli.py <original.jpg> <render.jpg>
"""
import cv2
import numpy as np
import matplotlib
matplotlib.use('Agg')  # headless
import matplotlib.pyplot as plt
from scipy.stats import wasserstein_distance
import os, sys
from datetime import datetime


def load_images(path_orig, path_render):
    img_orig = cv2.imread(path_orig)
    img_render = cv2.imread(path_render)
    if img_orig is None or img_render is None:
        raise FileNotFoundError("Não foi possível carregar uma ou ambas as imagens.")
    img_orig_rgb = cv2.cvtColor(img_orig, cv2.COLOR_BGR2RGB)
    img_render_rgb = cv2.cvtColor(img_render, cv2.COLOR_BGR2RGB)
    return img_orig_rgb, img_render_rgb


def compute_histogram_stats(img):
    hsv = cv2.cvtColor(img, cv2.COLOR_RGB2HSV)
    v_channel = hsv[:, :, 2]
    hist_r = cv2.calcHist([img], [0], None, [256], [0, 256])
    hist_g = cv2.calcHist([img], [1], None, [256], [0, 256])
    hist_b = cv2.calcHist([img], [2], None, [256], [0, 256])
    hist_v = cv2.calcHist([v_channel], [0], None, [256], [0, 256])
    hist_r = hist_r / hist_r.sum()
    hist_g = hist_g / hist_g.sum()
    hist_b = hist_b / hist_b.sum()
    hist_v = hist_v / hist_v.sum()
    return hist_r, hist_g, hist_b, hist_v


def compare_histograms(hist_orig, hist_render, method='correlation'):
    methods = {
        'correlation': cv2.HISTCMP_CORREL,
        'chi_square': cv2.HISTCMP_CHISQR,
        'intersection': cv2.HISTCMP_INTERSECT,
        'bhattacharyya': cv2.HISTCMP_BHATTACHARYYA
    }
    return cv2.compareHist(hist_orig, hist_render, methods[method])


def brightness_contrast_stats(img):
    hsv = cv2.cvtColor(img, cv2.COLOR_RGB2HSV)
    v = hsv[:, :, 2]
    return {
        'mean': float(np.mean(v)),
        'std': float(np.std(v)),
        'min': float(np.min(v)),
        'max': float(np.max(v)),
        'median': float(np.median(v))
    }


def analyze_color_distribution(img_orig, img_render, output_dir):
    h_r_orig, h_g_orig, h_b_orig, h_v_orig = compute_histogram_stats(img_orig)
    h_r_ren, h_g_ren, h_b_ren, h_v_ren = compute_histogram_stats(img_render)
    
    metrics = {}
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
    
    # Gráfico de histogramas
    fig, axes = plt.subplots(2, 2, figsize=(12, 9))
    channels = [(h_r_orig, h_r_ren, 'Red'), (h_g_orig, h_g_ren, 'Green'),
                (h_b_orig, h_b_ren, 'Blue'), (h_v_orig, h_v_ren, 'Luminance (V)')]
    for ax, (h_orig, h_ren, title) in zip(axes.flatten(), channels):
        ax.plot(h_orig, color='blue', label='Original FLIR', linewidth=2)
        ax.plot(h_ren, color='red', label='Render Thermix', linewidth=2)
        ax.set_title(title)
        ax.legend()
        ax.set_xlim(0, 255)
    plt.suptitle("Comparação de Histogramas - Original vs Render")
    plt.tight_layout()
    graph_path = os.path.join(output_dir, "histogram_comparison.png")
    plt.savefig(graph_path, dpi=150)
    plt.close()
    print(f"  📊 Gráfico: {graph_path}")
    return metrics


def export_full_report(img_orig_rgb, img_render_rgb, metrics, stats_orig, stats_render,
                       path_orig, path_render, output_dir):
    output_txt = os.path.join(output_dir, "relatorio_calibracao.txt")
    with open(output_txt, 'w', encoding='utf-8') as f:
        f.write("=" * 80 + "\n")
        f.write("SUPER RELATÓRIO DE CALIBRAÇÃO DE CORES - TERMOGRAMAS\n")
        f.write(f"Gerado em: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
        f.write("=" * 80 + "\n\n")

        f.write("[ARQUIVOS ANALISADOS]\n")
        f.write(f"Original: {os.path.basename(path_orig)}\n")
        f.write(f"Render:   {os.path.basename(path_render)}\n")
        f.write(f"Caminho original: {path_orig}\n")
        f.write(f"Dimensões original: {img_orig_rgb.shape[1]}x{img_orig_rgb.shape[0]} pixels\n")
        f.write(f"Dimensões render:   {img_render_rgb.shape[1]}x{img_render_rgb.shape[0]} pixels\n\n")

        f.write("[ESTATÍSTICAS DE BRILHO (Luminância - canal V do HSV)]\n")
        f.write(f"{'Métrica':<12} {'Original':>12} {'Render':>12} {'Diferença (R-O)':>18}\n")
        f.write("-" * 60 + "\n")
        for key in ['mean', 'std', 'median', 'min', 'max']:
            orig_val = stats_orig[key]
            ren_val = stats_render[key]
            diff = ren_val - orig_val
            f.write(f"{key.capitalize():<12} {orig_val:>12.2f} {ren_val:>12.2f} {diff:>+18.2f}\n")
        f.write("\n")

        f.write("[COMPARAÇÃO DE HISTOGRAMAS - MÉTRICAS DETALHADAS]\n")
        f.write("(Correlação: 1 = idêntico; Chi²: menor é melhor; Interseção: maior é melhor; Bhattacharyya: 0 = idêntico; Wasserstein: 0 = idêntico)\n\n")
        for ch_name in ['R', 'G', 'B', 'Luminância']:
            m = metrics[ch_name]
            f.write(f"Canal {ch_name}:\n")
            f.write(f"  • Correlação:     {m['correlation']:.6f}\n")
            f.write(f"  • Chi-quadrado:   {m['chi_square']:.4f}\n")
            f.write(f"  • Interseção:     {m['intersection']:.4f}\n")
            f.write(f"  • Bhattacharyya:  {m['bhattacharyya']:.6f}\n")
            f.write(f"  • Wasserstein:    {m['wasserstein']:.6f}\n")
            f.write("\n")

        f.write("[INTERPRETAÇÃO E SUGESTÕES DE COLORAÇÃO]\n")
        delta_mean = stats_render['mean'] - stats_orig['mean']
        if delta_mean > 2.0:
            f.write(f"• O render está MAIS CLARO que o original em média {delta_mean:.2f} níveis de cinza.\n")
            f.write("  → Recomendação: Reduza o brilho geral da sua paleta ou ajuste a curva térmica para escurecer a base.\n")
        elif delta_mean < -2.0:
            f.write(f"• O render está MAIS ESCURO que o original em média {abs(delta_mean):.2f} níveis de cinza.\n")
            f.write("  → Recomendação: Aumente o ganho geral/brilho no software de renderização térmica.\n")
        else:
            f.write("• O brilho global do render está muito bem alinhado com o original.\n")

        avg_corr = np.mean([metrics[ch]['correlation'] for ch in ['R', 'G', 'B']])
        f.write(f"• Correlação média entre canais RGB: {avg_corr:.4f}\n")
        if avg_corr < 0.7:
            f.write("  → Alerta: A paleta de cores (falsas cores) do render está bastante divergente do original.\n")
        elif avg_corr < 0.9:
            f.write("  → Correlação moderada. Requer ajustes pontuais na saturação ou mapa de gradiente (LUT).\n")
        else:
            f.write("  → Excelente correlação de cor. O mapeamento térmico está muito fiel ao equipamento.\n")

        f.write("\n" + "=" * 80 + "\n")
        f.write("FIM DO RELATÓRIO\n")
        f.write("=" * 80 + "\n")
    print(f"  📄 Relatório: {output_txt}")


def main():
    if len(sys.argv) != 3:
        print("Uso: python calibrate_cli.py <original.jpg> <render.jpg>")
        sys.exit(1)
    
    path_orig = sys.argv[1]
    path_render = sys.argv[2]
    
    if not os.path.exists(path_orig):
        print(f"❌ Original não encontrado: {path_orig}")
        sys.exit(1)
    if not os.path.exists(path_render):
        print(f"❌ Render não encontrado: {path_render}")
        sys.exit(1)
    
    output_dir = os.path.dirname(os.path.abspath(path_orig))
    
    print(f"🔬 Analisando...")
    print(f"  Original: {os.path.basename(path_orig)}")
    print(f"  Render:   {os.path.basename(path_render)}")
    
    img_orig_rgb, img_render_rgb = load_images(path_orig, path_render)
    metrics = analyze_color_distribution(img_orig_rgb, img_render_rgb, output_dir)
    stats_orig = brightness_contrast_stats(img_orig_rgb)
    stats_render = brightness_contrast_stats(img_render_rgb)
    export_full_report(img_orig_rgb, img_render_rgb, metrics, stats_orig, stats_render,
                       path_orig, path_render, output_dir)
    
    # Resumo rápido no console
    print(f"\n{'='*60}")
    print(f"RESUMO RÁPIDO")
    print(f"{'='*60}")
    print(f"{'Métrica':<12} {'Original':>12} {'Render':>12} {'Diff':>10}")
    print(f"{'-'*46}")
    for key in ['mean', 'median', 'std']:
        print(f"{key.capitalize():<12} {stats_orig[key]:>12.2f} {stats_render[key]:>12.2f} {stats_render[key]-stats_orig[key]:>+10.2f}")
    
    avg_corr = np.mean([metrics[ch]['correlation'] for ch in ['R','G','B']])
    print(f"\nCorrelação RGB média: {avg_corr:.4f}")
    delta_mean = stats_render['mean'] - stats_orig['mean']
    if abs(delta_mean) <= 2.0:
        print("✅ Brilho ALINHADO com o original!")
    elif delta_mean > 0:
        print(f"⚠️  Render {delta_mean:.1f} níveis MAIS CLARO")
    else:
        print(f"⚠️  Render {abs(delta_mean):.1f} níveis MAIS ESCURO")
    
    print(f"\n✅ Análise concluída! Resultados em: {output_dir}")


if __name__ == "__main__":
    main()
