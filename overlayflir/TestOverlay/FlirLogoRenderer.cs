using SkiaSharp;

namespace FlirStyleOverlay
{
    /// <summary>
    /// Renders the FLIR watermark logo pixel-perfectly, reverse-engineered
    /// from a 320x240 FLIR E8xt thermogram at 1:1 scale.
    ///
    /// Original position in 320x240 image:
    ///   Icon:  x=4..15,  y=219..234  (12w x 16h)
    ///   Text:  x=18..55, y=221..231  (11h)
    ///   Full bounding box: x=4..55, y=219..234 (52w x 16h)
    ///
    /// All measurements are in logical pixels at base resolution 320x240.
    /// When rendering at a different resolution, pass a scale factor.
    /// </summary>
    public static class FlirLogoRenderer
    {
        // --- Binary glyph definitions (row-major, 1 = lit pixel) ---
        // Extracted pixel-by-pixel from FLIR E8xt JPEG thermogram.

        private static readonly int[,] GlyphIcon = 
        {
            // 16 rows x 12 cols — diamond/lozenge shape
            { 0,0,0,0,0,1,1,0,0,0,0,0 },
            { 0,0,0,0,1,1,1,1,0,0,0,0 },
            { 0,0,0,1,1,1,1,1,1,0,0,0 },
            { 0,0,1,1,1,1,1,1,1,1,0,0 },
            { 0,1,1,1,1,1,1,1,1,1,1,0 },
            { 1,1,1,1,1,0,0,1,1,1,1,1 },
            { 0,1,1,1,0,0,0,0,1,1,1,0 },
            { 0,0,1,0,0,0,0,0,0,1,0,0 },
            { 0,0,1,0,0,0,0,0,0,1,0,0 },
            { 0,1,1,1,0,0,0,0,1,1,1,0 },
            { 1,1,1,1,1,0,0,1,1,1,1,1 },
            { 0,1,1,1,1,1,1,1,1,1,1,0 },
            { 0,0,1,1,1,1,1,1,1,1,0,0 },
            { 0,0,0,1,1,1,1,1,1,0,0,0 },
            { 0,0,0,0,1,1,1,1,0,0,0,0 },
            { 0,0,0,0,0,1,1,0,0,0,0,0 },
        };

        private static readonly int[,] GlyphF =
        {
            // 11 rows x 9 cols
            { 1,1,1,1,1,1,1,1,1 },
            { 1,1,1,1,1,1,1,1,1 },
            { 1,1,1,1,1,1,1,1,1 },
            { 1,1,1,1,0,0,0,0,0 },
            { 1,1,1,1,0,0,0,0,0 },
            { 1,1,1,1,1,1,1,0,0 },
            { 1,1,1,1,1,1,1,0,0 },
            { 1,1,1,1,0,0,0,0,0 },
            { 1,1,1,1,0,0,0,0,0 },
            { 1,1,1,1,0,0,0,0,0 },
            { 1,1,1,1,0,0,0,0,0 },
        };

        private static readonly int[,] GlyphL =
        {
            // 11 rows x 8 cols
            { 1,1,1,1,0,0,0,0 },
            { 1,1,1,1,0,0,0,0 },
            { 1,1,1,1,0,0,0,0 },
            { 1,1,1,1,0,0,0,0 },
            { 1,1,1,1,0,0,0,0 },
            { 1,1,1,1,0,0,0,0 },
            { 1,1,1,1,0,0,0,0 },
            { 1,1,1,1,0,0,0,0 },
            { 1,1,1,1,1,1,1,1 },
            { 1,1,1,1,1,1,1,1 },
            { 1,1,1,1,1,1,1,1 },
        };

        private static readonly int[,] GlyphI =
        {
            // 11 rows x 4 cols — solid bar
            { 1,1,1,1 },
            { 1,1,1,1 },
            { 1,1,1,1 },
            { 1,1,1,1 },
            { 1,1,1,1 },
            { 1,1,1,1 },
            { 1,1,1,1 },
            { 1,1,1,1 },
            { 1,1,1,1 },
            { 1,1,1,1 },
            { 1,1,1,1 },
        };

        private static readonly int[,] GlyphR =
        {
            // 11 rows x 12 cols
            { 1,1,1,1,1,1,1,1,1,0,0,0 },
            { 1,1,1,1,1,1,1,1,1,1,0,0 },
            { 1,1,1,1,1,1,1,1,1,1,1,0 },
            { 1,1,1,1,0,0,1,1,1,1,1,0 },
            { 1,1,1,1,0,0,1,1,1,1,1,0 },
            { 1,1,1,1,1,1,1,1,1,1,0,0 },
            { 1,1,1,1,1,1,1,1,0,0,0,0 },
            { 1,1,1,1,1,1,1,1,1,0,0,0 },
            { 1,1,1,1,0,1,1,1,1,1,0,0 },
            { 1,1,1,1,0,0,1,1,1,1,1,0 },
            { 1,1,1,1,0,0,0,1,1,1,1,1 },
        };

        /// <summary>
        /// Layout of glyphs in the 52x16 logical canvas (at base resolution).
        /// Origin is the top-left of the bounding box (x=4, y=219 in a 320x240 image).
        /// </summary>
        private static readonly (int[,] Glyph, int ColOffset, int RowOffset)[] Layout =
        {
            // ColOffset and RowOffset are relative to the logo's own bounding box origin.
            (GlyphIcon, 0,  0),   // icon: starts at col 0, row 0  (12w x 16h)
            (GlyphF,    14, 2),   // F:    starts at col 14, row 2  (gap=2 from icon)
            (GlyphL,    25, 2),   // L:    starts at col 25, row 2  (gap=2 from F)
            (GlyphI,    34, 2),   // I:    starts at col 34, row 2  (gap=1 from L)
            (GlyphR,    40, 2),   // R:    starts at col 40, row 2  (gap=2 from I)
        };

        // Bounding box of the logo in the 320x240 coordinate space
        private const int LogoOriginX = 4;
        private const int LogoOriginY = 219;
        private const int LogoWidth   = 52;   // cols 4..55
        private const int LogoHeight  = 16;   // rows 219..234
        private const int BaseImageWidth  = 320;
        private const int BaseImageHeight = 240;

        /// <summary>
        /// Draws the FLIR logo onto an existing SKCanvas.
        /// </summary>
        /// <param name="canvas">Target SkiaSharp canvas.</param>
        /// <param name="imageWidth">Width of the thermogram being rendered.</param>
        /// <param name="imageHeight">Height of the thermogram being rendered.</param>
        /// <param name="color">Logo color (default: white).</param>
        public static void Draw(SKCanvas canvas, int imageWidth, int imageHeight,
                                SKColor? color = null)
        {
            SKColor logoColor = color ?? SKColors.White;
            using var paint = new SKPaint { Color = logoColor, IsAntialias = false };

            float scaleX = (float)imageWidth  / BaseImageWidth;
            float scaleY = (float)imageHeight / BaseImageHeight;

            // Position of the logo's top-left corner in the target image
            float originX = LogoOriginX * scaleX;
            float originY = LogoOriginY * scaleY;

            foreach (var (glyph, colOff, rowOff) in Layout)
            {
                int rows = glyph.GetLength(0);
                int cols = glyph.GetLength(1);

                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        if (glyph[r, c] == 0) continue;

                        float px = originX + (colOff + c) * scaleX;
                        float py = originY + (rowOff + r) * scaleY;

                        // Each logical pixel becomes a scaleX x scaleY rectangle
                        canvas.DrawRect(px, py, scaleX, scaleY, paint);
                    }
                }
            }
        }

        /// <summary>
        /// Returns the logo bounding rectangle in target-image coordinates.
        /// Useful for hit-testing or layout calculations.
        /// </summary>
        public static SKRect GetBounds(int imageWidth, int imageHeight)
        {
            float scaleX = (float)imageWidth  / BaseImageWidth;
            float scaleY = (float)imageHeight / BaseImageHeight;
            return new SKRect(
                LogoOriginX * scaleX,
                LogoOriginY * scaleY,
                (LogoOriginX + LogoWidth) * scaleX,
                (LogoOriginY + LogoHeight) * scaleY);
        }
    }
}
