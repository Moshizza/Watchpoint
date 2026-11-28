using System.Numerics;

namespace Watchpoint.Rights.Internal.Evaluation;

/// <summary>A fixed-size bitset over device indexes (spec RGT §6: 50,000 devices = 6.25 KB).</summary>
internal sealed class Bits
{
    private readonly ulong[] _words;

    public Bits(int length)
    {
        Length = length;
        _words = new ulong[(length + 63) / 64];
    }

    public int Length { get; }

    public int Bytes => _words.Length * sizeof(ulong);

    public bool this[int index]
    {
        get => (_words[index >> 6] & (1UL << (index & 63))) != 0;
        set
        {
            if (value)
            {
                _words[index >> 6] |= 1UL << (index & 63);
            }
            else
            {
                _words[index >> 6] &= ~(1UL << (index & 63));
            }
        }
    }

    public int Count
    {
        get
        {
            var n = 0;
            foreach (var w in _words)
            {
                n += BitOperations.PopCount(w);
            }

            return n;
        }
    }

    public static Bits Full(int length)
    {
        var b = new Bits(length);
        for (var i = 0; i < length; i++)
        {
            b[i] = true;
        }

        return b;
    }

    public Bits Clone()
    {
        var b = new Bits(Length);
        Array.Copy(_words, b._words, _words.Length);
        return b;
    }

    public void Or(Bits other)
    {
        for (var i = 0; i < _words.Length; i++)
        {
            _words[i] |= other._words[i];
        }
    }

    public void And(Bits other)
    {
        for (var i = 0; i < _words.Length; i++)
        {
            _words[i] &= other._words[i];
        }
    }

    public void AndNot(Bits other)
    {
        for (var i = 0; i < _words.Length; i++)
        {
            _words[i] &= ~other._words[i];
        }
    }

    public IEnumerable<int> Indexes()
    {
        for (var w = 0; w < _words.Length; w++)
        {
            var word = _words[w];
            while (word != 0)
            {
                var bit = BitOperations.TrailingZeroCount(word);
                yield return (w << 6) + bit;
                word &= word - 1;
            }
        }
    }

    public ulong Hash()
    {
        var h = 14695981039346656037UL;
        foreach (var w in _words)
        {
            h = (h ^ w) * 1099511628211UL;
        }

        return h;
    }
}
