using System;
using PaintDotNet;
using pyrochild.effects.common;
using Xunit;

namespace pyrochild.effects.liquify.tests
{
    /// <summary>
    /// NormalBlend substitui UserBlendOps.NormalBlendOp (PaintDotNet.Data.dll), que o Paint.NET 5.2
    /// não deixa um plugin de efeito referenciar. Estes testes comparam as duas, que ainda estão
    /// disponíveis aqui porque o projeto de testes (e só ele) referencia a Data.dll.
    /// </summary>
    public class NormalBlendTests
    {
        private static int MaxChannelDifference(ColorBgra a, ColorBgra b) =>
            Math.Max(Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.G - b.G)),
                     Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.A - b.A)));

        [Fact]
        public void Matches_the_original_normal_blend_op_on_random_pixels()
        {
            var random = new Random(12345);
            int worst = 0;

            for (int i = 0; i < 200_000; ++i)
            {
                ColorBgra bottom = ColorBgra.FromBgra((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                ColorBgra top = ColorBgra.FromBgra((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));

                ColorBgra expected = UserBlendOps.NormalBlendOp.ApplyStatic(bottom, top);
                ColorBgra actual = NormalBlend.Apply(bottom, top);

                // A original usa tabelas de arredondamento próprias: aceita ±1 por canal.
                int diff = MaxChannelDifference(expected, actual);
                if (diff > worst) worst = diff;
                Assert.True(diff <= 1, $"bottom={bottom} top={top} esperado={expected} obtido={actual}");
            }

            Assert.True(worst <= 1);
        }

        [Fact]
        public void Opaque_top_replaces_and_transparent_top_keeps_the_bottom()
        {
            ColorBgra bottom = ColorBgra.FromBgra(10, 20, 30, 200);

            Assert.Equal(ColorBgra.FromBgra(1, 2, 3, 255), NormalBlend.Apply(bottom, ColorBgra.FromBgra(1, 2, 3, 255)));
            Assert.Equal(bottom, NormalBlend.Apply(bottom, ColorBgra.FromBgra(99, 99, 99, 0)));
        }

        [Fact]
        public void Transparent_bottom_gives_the_top()
        {
            ColorBgra top = ColorBgra.FromBgra(7, 8, 9, 100);

            Assert.Equal(top, NormalBlend.Apply(ColorBgra.FromBgra(0, 0, 0, 0), top));
        }
    }
}
