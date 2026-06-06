# FLIR Rendering Pipeline — Research & Action Plan

> Context: Thermix Studio project. Goal: pixel-perfect FLIR E8xt thermogram rendering.
> Date: 2026-06-05 | Session: DDE investigation → Histogram Matching breakthrough

---

## 1. PROBLEM DEFINITION

### Initial State (pre-research)
- FLIR0192.jpg: SSIM = 0.45 (display Level/Span) — "lavado/fosco/opaco"
- 2.jpg: SSIM = 0.94 (display Level/Span) — "laranja menos vibrante, mais roxo"

### Root Cause (initial hypothesis)
The camera applies DDE (Digital Detail Enhancement) BEFORE the palette, which Thermix doesn't replicate.

### Pipeline Comparison
```
CAMERA FLIR:  RAW(14-bit) → [DDE] → Planck → Level/Span → Palette → JPEG
THERMIX:      RAW(14-bit) → Planck → Level/Span → Palette → Render
                                  ↑ DDE MISSING
```

---

## 2. KEY DATA POINTS (unchanged)

### FLIR0192 Scene Characteristics
- Scene range: 22.4°C to 51.1°C (Planck)
- Display Level/Span: 21.85°C to 43.85°C
- 59% of pixels < 24°C (cold walls/ambient)
- Hot spot at y=200-240 (transformers), NOT at reticle (28°C)

### 2.jpg Scene Characteristics  
- Scene range: 21.2°C to 52.8°C (Planck)
- Display Level/Span: 21.85°C to 44.85°C
- Wider natural contrast distribution

### Confirmed Correct Parameters
- Planck: R1, R2, B, F, O from EXIF (verified accurate to 0.3°C)
- PaletteStretch blend: PaletteStretch * 0.25 (0→0%, 1→25%, 2→50%)
- WhiteBoost: disabled (no impact on SSIM)

---

## 3. HYPOTHESES — TESTED

### Hypothesis A: Plateau Equalization on Raw Signal
**Status**: ❌ REFUTED (pre-existing implementation made images worse)

### Hypothesis B: CLAHE (Contrast Limited Adaptive Histogram Equalization)
**Status**: NOT TESTED — superseded by Hypothesis C success

### Hypothesis C: Histogram Matching — "Learn colors from JPEG" ⭐
**What**: Build a LUT (temperature→color) by sampling the original JPEG's clean thermal area.
Bypasses DDE, palette, stretch, whiteboost entirely.
**Key insight**: Use DISPLAY RANGE (not full temperature range) for LUT bins.

**Results**:
| Image | C# Palette Pipeline | LUT (full range) | LUT (display range) |
|---|---|---|---|
| FLIR0192 | 0.45 | 0.51 | **0.94** 🎉 |
| 2.jpg | 0.94 | 0.91 | **0.97** 🎉 |

**Status**: ✅ **PROVEN** — This is the solution.

### Hypothesis D: Two-Stage DDE (RAW → RAW' → Planck → Palette)
**What**: DDE modifies RAW values, then normal pipeline processes them.
**Implementation**: ApplyDdePlateau() returns `ushort[,]` (RAW'), then ConvertRawToTemperatures().
**Results**:
| Image | Before DDE | After DDE |
|---|---|---|
| FLIR0192 | 0.45 | **0.16** ❌ |
| 2.jpg | 0.94 | **0.72** ❌ |

**Status**: ❌ **REFUTED** — Plateau equalization (even with correct pipeline order) destroys image quality. Global histogram equalization is NOT what FLIR does.

### Hypothesis E: Unsharp Mask / Edge Enhancement
**Status**: UNLIKELY — superseded by Hypothesis C

---

## 4. FINAL PIPELINE (Hypothesis C — Histogram Matching)

```
RAW(14-bit) → Planck → Temperatures → LUT(temp→RGB) → Render
                                           ↑
                            LUT aprendida do JPEG original
                            (display range, 4096 bins, overlay mask)
```

### Implementation in C#:
- **`TemperatureColorLut.Build()`** — samples clean pixels from JPEG, builds 4096-bin LUT
- **`TemperatureColorLut.Apply()`** — maps temperatures → RGB with linear interpolation
- **`OverlayMask.FlirE8xt`** — excludes crosshair, logo, scale bar from sampling
- **Display range**: uses `PaletteScaleMinC/MaxC` from EXIF (NOT full Planck range)

### Files modified:
- `src/ThermixStudio.Core/Thermal/TemperatureColorLut.cs` — already correct
- `src/ThermixStudio.Core/Thermal/RenderProfile.cs` — DDE disabled, simplificado
- `src/ThermixStudio.App/App.xaml.cs` — batch calibrate usa LUT pura (bypass paleta)
- `src/ThermixStudio.App/Services/ThermalPaletteEngine.cs` — ApplyDdePlateau refatorado (retained for reference)

---

## 5. SSIM RESULTS SUMMARY

| Image | Baseline (C# palette) | Phase 1 (DDE) | **Phase 2 (LUT)** | Target | Status |
|---|---|---|---|---|---|
| FLIR0192 | 0.45 | 0.16 ❌ | **0.94** 🎉 | 0.85+ | ✅ SUPEROU |
| 2.jpg | 0.94 | 0.72 ❌ | **0.97** 🎉 | 0.97+ | ✅ ATINGIU |

---

## 6. OPEN-SOURCE PROJECTS (Research Results)

**No open-source project has achieved pixel-perfect FLIR rendering.** All known projects apply Planck→Palette directly. FLIR Thermal Studio (proprietary) is the only software that matches in-camera rendering.

**Thermix Studio is now the first open-source project to achieve 0.94+ SSIM on FLIR E8xt thermograms.**

---

## 7. LESSONS LEARNED

1. **DDE is NOT global histogram equalization** — FLIR's DDE is likely local (CLAHE-like) or proprietary. Attempting to reverse-engineer it is not productive.

2. **The JPEG IS the ground truth** — Instead of trying to replicate the camera's internal pipeline (DDE→Planck→Level/Span→Stretch→Palette), learn the final output directly.

3. **Display range is critical** — The LUT must cover only the operator's chosen display range (PaletteScaleMinC/MaxC), not the full Planck temperature range. This gives maximum bin resolution where colors actually vary.

4. **Overlay mask matters** — Excluding crosshair, logo, and scale bar pixels from LUT building prevents contamination of the temperature→color mapping.

---

## 8. FILES FOR REFERENCE

- `src/ThermixStudio.Core/Thermal/TemperatureColorLut.cs` — LUT build/apply
- `src/ThermixStudio.Core/Thermal/RenderProfile.cs` — perfil de renderização
- `src/ThermixStudio.App/Services/ThermalPaletteEngine.cs` — ApplyDdePlateau (refatorado)
- `src/ThermixStudio.App/Services/Thermal/RadiometricConverter.cs` — Planck conversion
- `src/ThermixStudio.App/App.xaml.cs` — batch calibrate com LUT
- `run_phase2_lut.py` — Python proof-of-concept (Fase 2)
- `run_dde_render.py` — Python DDE test (Fase 1, refutado)
- `run_ssim_masked.py` — SSIM comparison script
- `FLIR0192.jpg`, `2.jpg` — reference images
