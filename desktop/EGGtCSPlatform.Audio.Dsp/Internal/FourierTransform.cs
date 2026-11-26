using System.Numerics;

namespace EGGtCSPlatform.Audio.Dsp.Internal;

internal static class FourierTransform
{
    internal static void Transform(Complex[] data, bool inverse, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (inverse)
            for (var i = 0; i < data.Length; i++)
                data[i] = Complex.Conjugate(data[i]);
        if ((data.Length & (data.Length - 1)) == 0)
            RadixTwo(data, false, token);
        else
            Bluestein(data, token);
        if (inverse)
            for (var i = 0; i < data.Length; i++)
                data[i] = Complex.Conjugate(data[i]) / data.Length;
    }

    // Bluestein uses a padded convolution internally but computes the exact N-point DFT.
    // Padding the speech itself to a power of two would change Hilbert's boundary behavior.
    private static void Bluestein(Complex[] data, CancellationToken token)
    {
        var n = data.Length;
        var length = 1;
        while (length < 2L * n - 1)
            length = checked(length * 2);
        var a = new Complex[length];
        var b = new Complex[length];
        for (var i = 0; i < n; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            var angle = Math.PI * ((long)i * i % (2L * n)) / n;
            var chirp = new Complex(Math.Cos(angle), Math.Sin(angle));
            a[i] = data[i] * Complex.Conjugate(chirp);
            b[i] = chirp;
            if (i > 0)
                b[length - i] = chirp;
        }
        RadixTwo(a, false, token);
        RadixTwo(b, false, token);
        for (var i = 0; i < length; i++)
            a[i] *= b[i];
        RadixTwo(a, true, token);
        for (var i = 0; i < n; i++)
            data[i] = a[i] * Complex.Conjugate(bChirp(i, n));

        static Complex bChirp(int i, int n)
        {
            var angle = Math.PI * ((long)i * i % (2L * n)) / n;
            return new(Math.Cos(angle), Math.Sin(angle));
        }
    }

    private static void RadixTwo(Complex[] data, bool inverse, CancellationToken token)
    {
        var n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (data[i], data[j]) = (data[j], data[i]);
        }
        for (var width = 2; width <= n; width *= 2)
        {
            token.ThrowIfCancellationRequested();
            var angle = (inverse ? 2 : -2) * Math.PI / width;
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < n; start += width)
            {
                if ((start & 4095) == 0)
                    token.ThrowIfCancellationRequested();
                var factor = Complex.One;
                for (var j = 0; j < width / 2; j++)
                {
                    var even = data[start + j];
                    var odd = data[start + j + width / 2] * factor;
                    data[start + j] = even + odd;
                    data[start + j + width / 2] = even - odd;
                    factor *= step;
                }
            }
            if (width == n)
                break;
        }
        if (inverse)
            for (var i = 0; i < n; i++)
                data[i] /= n;
    }
}
