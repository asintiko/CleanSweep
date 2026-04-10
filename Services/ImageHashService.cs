using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CleanSweep.Services
{
    public static class ImageHashService
    {
        private const int HashSize = 8;
        private const int DctSize = 32;

        public static ulong ComputePhash(string filePath)
        {
            using var image = Image.Load<Rgba32>(filePath);
            image.Mutate(x => x.Resize(DctSize, DctSize).Grayscale());
            double[,] px = new double[DctSize, DctSize];
            for (int y = 0; y < DctSize; y++)
                for (int x = 0; x < DctSize; x++)
                    px[y, x] = image[x, y].R;
            double[,] dct = ApplyDCT(px);
            double[] lf = new double[HashSize * HashSize - 1];
            int idx = 0;
            for (int y = 0; y < HashSize; y++)
                for (int x = 0; x < HashSize; x++)
                    if (y != 0 || x != 0) lf[idx++] = dct[y, x];
            double[] sorted = (double[])lf.Clone();
            Array.Sort(sorted);
            double median = sorted[sorted.Length / 2];
            ulong hash = 0; idx = 0;
            for (int y = 0; y < HashSize; y++)
                for (int x = 0; x < HashSize; x++)
                {
                    if (y == 0 && x == 0) continue;
                    if (dct[y, x] > median) hash |= (1UL << idx);
                    idx++;
                }
            return hash;
        }

        public static int HammingDistance(ulong a, ulong b)
            => (int)System.Numerics.BitOperations.PopCount(a ^ b);

        private static double[,] ApplyDCT(double[,] input)
        {
            int n = input.GetLength(0);
            double[,] r = new double[n, n];
            double[] cos = new double[n * n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    cos[i * n + j] = Math.Cos(((2.0 * i + 1.0) * j * Math.PI) / (2.0 * n));
            for (int u = 0; u < n; u++)
                for (int v = 0; v < n; v++)
                {
                    double sum = 0;
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                            sum += input[y, x] * cos[y * n + u] * cos[x * n + v];
                    double cu = u == 0 ? 1.0 / Math.Sqrt(2) : 1.0;
                    double cv = v == 0 ? 1.0 / Math.Sqrt(2) : 1.0;
                    r[u, v] = sum * cu * cv * (2.0 / n);
                }
            return r;
        }
    }
}
