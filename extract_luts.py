import cv2, json, numpy as np, os

def extract_lut(img_path, lut_name, output_name, x_frac, y_hot_frac, y_cold_frac):
    img = cv2.imread(img_path)
    h, w = img.shape[:2]
    x = int(w * x_frac)
    y_hot  = int(h * y_hot_frac)
    y_cold = int(h * y_cold_frac)
    
    bar = []
    for y in range(y_cold, y_hot - 1, -1):
        b_, g_, r_ = img[y, x]
        bar.append([int(r_), int(g_), int(b_)])
    
    n = len(bar)
    arr = np.array(bar, dtype=np.float32)
    lut = []
    for i in range(256):
        t = i * (n - 1) / 255.0
        lo = int(t); hi = min(lo + 1, n - 1); f = t - lo
        lut.append([
            int(arr[lo,0]*(1-f)+arr[hi,0]*f),
            int(arr[lo,1]*(1-f)+arr[hi,1]*f),
            int(arr[lo,2]*(1-f)+arr[hi,2]*f),
        ])
    
    preview = np.zeros((32, 256, 3), dtype=np.uint8)
    for i, (r,g,b) in enumerate(lut):
        preview[:, i] = [b, g, r]
    
    preview_name = "preview_" + output_name.replace(".json", ".png")
    cv2.imwrite(preview_name, preview)
    
    print(f'{lut_name}: frio[0]={lut[0]}  meio[128]={lut[128]}  quente[255]={lut[255]}')
    
    for base in [r'src\ThermixStudio.App\paletas',
                 r'src\ThermixStudio.App\bin\Debug\net10.0-windows\win-x64\paletas']:
        os.makedirs(base, exist_ok=True)
        path = os.path.join(base, output_name)
        with open(path, 'w') as f:
            json.dump({'name': lut_name, 'rgb': lut}, f, indent=2)
    return lut

extract_lut('2.jpg', 'iron', 'iron_lut.json', 308/320, 30/240, 204/240)
extract_lut(r'Cores paletas\Arco-Iris.jpg', 'rainbow', 'rainbow_lut.json', 308/320, 30/240, 204/240)
extract_lut(r'Cores paletas\Cinza.jpg', 'grayscale', 'grayscale_lut.json', 308/320, 30/240, 204/240)

print("Previews gerados!")
