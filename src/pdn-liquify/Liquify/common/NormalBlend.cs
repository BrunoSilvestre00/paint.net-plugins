using PaintDotNet;

namespace pyrochild.effects.common
{
    /// <summary>
    /// Mistura "Normal" (composição "over" em alfa direto): desenha <c>top</c> sobre <c>bottom</c>.
    ///
    /// Substitui <c>UserBlendOps.NormalBlendOp</c> de PaintDotNet.Data.dll. O Paint.NET 5.2 recusa
    /// plugins de efeito que referenciam essa DLL ("A modern effect plugin DLL may not have a
    /// reference to PaintDotNet.Data.dll"), então a operação é feita aqui.
    /// </summary>
    internal static class NormalBlend
    {
        public static ColorBgra Apply(ColorBgra bottom, ColorBgra top)
        {
            int topA = top.A;
            if (topA == 255) return top;
            if (topA == 0) return bottom;

            int botA = bottom.A;
            if (botA == 0) return top;

            // Parcela do fundo que continua visível por baixo do topo (0..255).
            int botWeight = Div255(botA * (255 - topA));
            int outA = topA + botWeight;
            if (outA == 0) return ColorBgra.FromBgra(0, 0, 0, 0);

            // Cor = média de cada canal ponderada pelas contribuições de alfa.
            byte b = (byte)((top.B * topA + bottom.B * botWeight + outA / 2) / outA);
            byte g = (byte)((top.G * topA + bottom.G * botWeight + outA / 2) / outA);
            byte r = (byte)((top.R * topA + bottom.R * botWeight + outA / 2) / outA);
            return ColorBgra.FromBgra(b, g, r, (byte)outA);
        }

        // Divisão por 255 com arredondamento, sem usar divisão de inteiro.
        private static int Div255(int v)
        {
            v += 0x80;
            return ((v >> 8) + v) >> 8;
        }
    }
}
