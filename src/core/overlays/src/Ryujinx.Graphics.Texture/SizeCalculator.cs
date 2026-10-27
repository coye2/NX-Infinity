using System;

namespace Ryujinx.Graphics.Texture;

public static class SizeCalculator
{
	private const int StrideAlignment = 32;

	private static int Calculate3DOffsetCount(int levels, int depth)
	{
		checked
		{
			try
			{
				int num = depth;
				while (--levels > 0)
				{
					depth = Math.Max(1, depth >> 1);
					num += depth;
				}
				return num;
			}
			catch (OverflowException inner)
			{
				throw new InvalidTextureSizeException(inner);
			}
		}
	}

	public static SizeInfo GetBlockLinearTextureSize(int width, int height, int depth, int levels, int layers, int blockWidth, int blockHeight, int bytesPerPixel, int gobBlocksInY, int gobBlocksInZ, int gobBlocksInTileX, int gpuLayerSize = 0)
	{
		ValidateDimensions(width, height, depth, levels, layers, blockWidth, blockHeight, bytesPerPixel, gobBlocksInY, gobBlocksInZ, gobBlocksInTileX);
		if (gpuLayerSize < 0)
		{
			throw new InvalidTextureSizeException();
		}
		checked
		{
			try
			{
				bool flag = depth > 1 || gobBlocksInZ > 1;
				int num = 0;
				int num2 = 0;
				int[] array = new int[flag ? Calculate3DOffsetCount(levels, depth) : (levels * layers * depth)];
				int[] array2 = new int[levels];
				int[] array3 = new int[levels];
				int[] array4 = new int[levels];
				int num3 = gobBlocksInY;
				int num4 = gobBlocksInZ;
				int num5 = unchecked(64 / bytesPerPixel) * gobBlocksInTileX;
				int num6 = gobBlocksInY * 8;
				int num7 = 0;
				for (int i = 0; i < levels; i++)
				{
					int value = Math.Max(1, width >> i);
					int value2 = Math.Max(1, height >> i);
					int num8 = Math.Max(1, depth >> i);
					value = DivideRoundUp(value, blockWidth);
					value2 = DivideRoundUp(value2, blockHeight);
					while (value2 <= (num3 >> 1) * 8 && num3 != 1)
					{
						num3 >>= 1;
					}
					if (i > 0 && num8 <= num4 >> 1 && num4 != 1)
					{
						num4 >>= 1;
					}
					int value3 = DivideRoundUp(value * bytesPerPixel, 64);
					int alignment = gobBlocksInTileX;
					if (num8 < gobBlocksInZ || value <= num5 || value2 <= num6)
					{
						alignment = 1;
					}
					value3 = AlignUp(value3, alignment);
					int num9 = DivideRoundUp(num8, num4);
					int num10 = DivideRoundUp(DivideRoundUp(value2, 8), num3);
					int num11 = value3 * num3 * num4 * 512;
					array2[i] = num;
					array3[i] = num10 * num11;
					array4[i] = num9 * array3[i];
					num2 += array4[i];
					if (flag)
					{
						int num12 = num3 * 512;
						int num13 = num10 * value3 * num12;
						int num14 = num;
						int num15 = gobBlocksInZ - 1;
						for (int j = 0; j < num8; j++)
						{
							int num16 = j & num15;
							int num17 = j & ~num15;
							array[j + num7] = num14 + num16 * num12 + num17 * num13;
						}
						int num18 = unchecked(num8 % num4);
						if (num18 != 0 && i == levels - 1)
						{
							array4[i] -= num12 * (num4 - num18);
							if (array3[i] > array4[i])
							{
								array3[i] = array4[i];
							}
						}
					}
					num += array4[i];
					num7 += num8;
				}
				int totalSize;
				if (layers > 1)
				{
					num2 = AlignLayerSize(num2, height, depth, blockHeight, gobBlocksInY, gobBlocksInZ, gobBlocksInTileX);
					if (num2 < gpuLayerSize)
					{
						try
						{
							totalSize = (layers - 1) * gpuLayerSize + num2;
							num2 = gpuLayerSize;
						}
						catch (OverflowException inner)
						{
							throw new InvalidTextureSizeException(inner);
						}
					}
					else
					{
						totalSize = num2 * layers;
					}
				}
				else
				{
					totalSize = num;
				}
				if (!flag)
				{
					for (int k = 0; k < layers; k++)
					{
						int num19 = k * levels;
						int num20 = k * num2;
						for (int l = 0; l < levels; l++)
						{
							array[num19 + l] = num20 + array2[l];
						}
					}
				}
				return new SizeInfo(array2, array, array3, array4, depth, levels, num2, totalSize, flag);
			}
			catch (OverflowException inner2)
			{
				throw new InvalidTextureSizeException(inner2);
			}
		}
	}

	public static SizeInfo GetLinearTextureSize(int stride, int height, int blockHeight)
	{
		if (stride <= 0 || height <= 0 || blockHeight <= 0)
		{
			throw new InvalidTextureSizeException();
		}
		try
		{
			return new SizeInfo(checked(stride * DivideRoundUp(height, blockHeight)));
		}
		catch (OverflowException inner)
		{
			throw new InvalidTextureSizeException(inner);
		}
	}

	private static int AlignLayerSize(int size, int height, int depth, int blockHeight, int gobBlocksInY, int gobBlocksInZ, int gobBlocksInTileX)
	{
		if (gobBlocksInTileX < 2)
		{
			height = DivideRoundUp(height, blockHeight);
			while (height <= (gobBlocksInY >> 1) * 8 && gobBlocksInY != 1)
			{
				gobBlocksInY >>= 1;
			}
			while (depth <= gobBlocksInZ >> 1 && gobBlocksInZ != 1)
			{
				gobBlocksInZ >>= 1;
			}
			int num = gobBlocksInY * gobBlocksInZ * 512;
			int num2 = size / num;
			if (size != num2 * num)
			{
				size = (num2 + 1) * num;
			}
		}
		else
		{
			int alignment = gobBlocksInTileX * 512 * gobBlocksInY * gobBlocksInZ;
			size = AlignUp(size, alignment);
		}
		return size;
	}

	public static Size GetBlockLinearAlignedSize(int width, int height, int depth, int blockWidth, int blockHeight, int bytesPerPixel, int gobBlocksInY, int gobBlocksInZ, int gobBlocksInTileX)
	{
		checked
		{
			try
			{
				width = DivideRoundUp(width, blockWidth);
				height = DivideRoundUp(height, blockHeight);
				int num = unchecked(64 / bytesPerPixel) * gobBlocksInTileX;
				int num2 = gobBlocksInY * 8;
				int alignment = num;
				if (depth < gobBlocksInZ || width <= num || height <= num2)
				{
					alignment = unchecked(64 / bytesPerPixel);
				}
				(int, int) mipGobBlockSizes = GetMipGobBlockSizes(height, depth, 1, gobBlocksInY, gobBlocksInZ);
				gobBlocksInY = mipGobBlockSizes.Item1;
				gobBlocksInZ = mipGobBlockSizes.Item2;
				int alignment2 = gobBlocksInY * 8;
				int alignment3 = gobBlocksInZ;
				width = AlignUp(width, alignment);
				height = AlignUp(height, alignment2);
				depth = AlignUp(depth, alignment3);
				return new Size(width, height, depth);
			}
			catch (OverflowException inner)
			{
				throw new InvalidTextureSizeException(inner);
			}
		}
	}

	public static Size GetLinearAlignedSize(int width, int height, int blockWidth, int blockHeight, int bytesPerPixel)
	{
		try
		{
			width = DivideRoundUp(width, blockWidth);
			height = DivideRoundUp(height, blockHeight);
			int alignment = 32 / bytesPerPixel;
			width = AlignUp(width, alignment);
			return new Size(width, height, 1);
		}
		catch (OverflowException inner)
		{
			throw new InvalidTextureSizeException(inner);
		}
	}

	public static (int, int) GetMipGobBlockSizes(int height, int depth, int blockHeight, int gobBlocksInY, int gobBlocksInZ, int level = int.MaxValue)
	{
		height = DivideRoundUp(height, blockHeight);
		while (height <= (gobBlocksInY >> 1) * 8 && gobBlocksInY != 1)
		{
			gobBlocksInY >>= 1;
		}
		while (level-- > 0 && depth <= gobBlocksInZ >> 1 && gobBlocksInZ != 1)
		{
			gobBlocksInZ >>= 1;
		}
		return (gobBlocksInY, gobBlocksInZ);
	}

	private static int DivideRoundUp(int value, int divisor)
	{
		if (value < 0 || divisor <= 0)
		{
			throw new InvalidTextureSizeException();
		}
		checked
		{
			return (int)unchecked(checked(unchecked((long)value) + unchecked((long)divisor) - 1) / divisor);
		}
	}

	private static int AlignUp(int value, int alignment)
	{
		if (value < 0 || alignment <= 0)
		{
			throw new InvalidTextureSizeException();
		}
		checked
		{
			return (int)(unchecked(checked(unchecked((long)value) + unchecked((long)alignment) - 1) / alignment) * alignment);
		}
	}

	private static void ValidateDimensions(int width, int height, int depth, int levels, int layers, int blockWidth, int blockHeight, int bytesPerPixel, int gobY, int gobZ, int gobX)
	{
		bool flag = width <= 0 || height <= 0 || depth <= 0;
		if (!flag)
		{
			bool flag2 = ((levels < 1 || levels > 32) ? true : false);
			flag = flag2;
		}
		if (flag || layers <= 0 || blockWidth <= 0 || blockHeight <= 0 || bytesPerPixel <= 0 || bytesPerPixel > 64 || gobY <= 0 || gobZ <= 0 || gobX <= 0)
		{
			throw new InvalidTextureSizeException();
		}
	}
}
