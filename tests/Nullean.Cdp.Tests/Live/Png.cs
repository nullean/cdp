using System.Buffers.Binary;
using System.IO.Compression;

namespace Nullean.Cdp.Tests.Live;

/// <summary>Just enough PNG decoding to read a screenshot's size and pixels: 8-bit RGB or RGBA, not interlaced.</summary>
internal sealed class Png
{
	private readonly byte[] _pixels;
	private readonly int _channels;

	public Png(byte[] file)
	{
		Width = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(16));
		Height = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(20));
		var colourType = file[25];
		_channels = colourType == 6 ? 4 : colourType == 2 ? 3 : throw new NotSupportedException($"colour type {colourType}");
		using var data = new MemoryStream();
		for (var at = 8; at < file.Length;)
		{
			var length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
			if (file.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8))
				data.Write(file, at + 8, length);
			at += 12 + length;
		}

		data.Position = 0;
		using var inflate = new ZLibStream(data, CompressionMode.Decompress);
		using var raw = new MemoryStream();
		inflate.CopyTo(raw);
		_pixels = Unfilter(raw.ToArray());
	}

	public int Width { get; }

	public int Height { get; }

	public (byte R, byte G, byte B) this[int x, int y]
	{
		get
		{
			var i = ((y * Width) + x) * _channels;
			return (_pixels[i], _pixels[i + 1], _pixels[i + 2]);
		}
	}

	private byte[] Unfilter(byte[] raw)
	{
		var stride = Width * _channels;
		var pixels = new byte[stride * Height];
		for (var y = 0; y < Height; y++)
		{
			var filter = raw[y * (stride + 1)];
			for (var x = 0; x < stride; x++)
			{
				int value = raw[(y * (stride + 1)) + 1 + x];
				int left = x >= _channels ? pixels[(y * stride) + x - _channels] : 0;
				int up = y > 0 ? pixels[((y - 1) * stride) + x] : 0;
				int upLeft = x >= _channels && y > 0 ? pixels[((y - 1) * stride) + x - _channels] : 0;
				pixels[(y * stride) + x] =
					(byte)(value + filter switch
					{
						0 => 0,
						1 => left,
						2 => up,
						3 => (left + up) / 2,
						4 => Paeth(left, up, upLeft),
						_ => throw new NotSupportedException($"filter {filter}"),
					});
			}
		}

		return pixels;
	}

	private static int Paeth(int a, int b, int c)
	{
		var p = a + b - c;
		int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
		return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
	}
}
