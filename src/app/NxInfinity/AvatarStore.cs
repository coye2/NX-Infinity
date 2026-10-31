using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

namespace NxInfinity;

public sealed class AvatarStore
{
    public string Root { get; }

    public AvatarPreferences Preferences { get; private set; }

    public AvatarProfile Active => Preferences.Profiles.First((AvatarProfile p) => p.Id == Preferences.Active);

    public string? LoadWarning { get; private set; }

    public event Action? Changed;

    public AvatarStore(string dataRoot)
    {
        Root = Path.Combine(dataRoot, "frontend-avatars");
        Preferences = new AvatarPreferences(1, "default", new AvatarProfile[1]
        {
            new AvatarProfile("default", "Player")
        }, Array.Empty<string>());
        string text = Path.Combine(Root, "profiles.json");
        try
        {
            if (File.Exists(text))
            {
                if (new FileInfo(text).Length > 262144)
                {
                    throw new InvalidDataException("Avatar preferences are too large.");
                }
                AvatarPreferences avatarPreferences = JsonSerializer.Deserialize<AvatarPreferences>(File.ReadAllText(text));
                if ((object)avatarPreferences == null || !Valid(avatarPreferences))
                {
                    throw new InvalidDataException("Avatar preferences are invalid.");
                }
                Preferences = avatarPreferences;
            }
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
        {
            LoadWarning = "Avatar preferences could not be loaded. Your next change will preserve a backup of the original file. " + ex.Message;
        }
    }

    private static bool Valid(AvatarPreferences p)
    {
        if (p.Version == 1)
        {
            AvatarProfile[] profiles = p.Profiles;
            if (profiles != null)
            {
                int num = profiles.Length;
                if (num > 0 && num <= 32)
                {
                    string[] favorites = p.Favorites;
                    if (favorites != null && favorites.Length <= 2000 && p.Profiles.All((AvatarProfile x) =>
                    {
                        if ((object)x != null)
                        {
                            if (!(x.Id == "default"))
                            {
                                string id = x.Id;
                                if (id == null || id.Length != 32 || !x.Id.All(Uri.IsHexDigit))
                                {
                                    goto IL_00b8;
                                }
                            }
                            if (!string.IsNullOrWhiteSpace(x.Name) && x.Name.Length <= 40)
                            {
                                if (x.Image != null)
                                {
                                    if (x.Image.Length == 64)
                                    {
                                        return x.Image.All(Uri.IsHexDigit);
                                    }
                                    return false;
                                }
                                return true;
                            }
                        }
                        goto IL_00b8;
                    IL_00b8:
                        return false;
                    }) && p.Profiles.Select((AvatarProfile x) => x.Id).Distinct().Count() == p.Profiles.Length && p.Profiles.Any((AvatarProfile x) => x.Id == p.Active))
                    {
                        return p.Favorites.All(AvatarCatalog.ValidImagePath);
                    }
                }
            }
        }
        return false;
    }

    private void Save(AvatarPreferences next)
    {
        if (!Valid(next))
        {
            throw new InvalidDataException("Invalid avatar profile or favorite.");
        }
        string text = Path.Combine(Root, "profiles.json");
        if (LoadWarning != null && File.Exists(text))
        {
            File.Copy(text, text + ".preserved-" + Guid.NewGuid().ToString("N"));
        }
        WriteAtomic(Path.Combine(Root, "profiles.json"), JsonSerializer.SerializeToUtf8Bytes(next));
        Preferences = next;
        LoadWarning = null;
        Changed?.Invoke();
    }

    public void SelectProfile(string id)
    {
        Save(Preferences with
        {
            Active = id
        });
    }

    public void Rename(string name)
    {
        Save(Preferences with
        {
            Profiles = Preferences.Profiles.Select((AvatarProfile p) => (!(p.Id == Active.Id)) ? p : p with
            {
                Name = name.Trim()
            }).ToArray()
        });
    }

    public void AddProfile(string name)
    {
        string text = Guid.NewGuid().ToString("N");
        AvatarPreferences avatarPreferences = Preferences with
        {
            Active = text
        };
        AvatarProfile[] profiles = Preferences.Profiles;
        int num = 0;
        AvatarProfile[] array = new AvatarProfile[1 + profiles.Length];
        ReadOnlySpan<AvatarProfile> readOnlySpan = new ReadOnlySpan<AvatarProfile>(profiles);
        readOnlySpan.CopyTo(new Span<AvatarProfile>(array).Slice(num, readOnlySpan.Length));
        num += readOnlySpan.Length;
        array[num] = new AvatarProfile(text, name.Trim());
        Save(avatarPreferences with
        {
            Profiles = array
        });
    }

    public void Reset()
    {
        Save(Preferences with
        {
            Profiles = Preferences.Profiles.Select((AvatarProfile p) => (!(p.Id == Active.Id)) ? p : p with
            {
                Image = null
            }).ToArray()
        });
    }

    public void Favorite(string path, bool favorite)
    {
        Save(Preferences with
        {
            Favorites = (favorite ? Preferences.Favorites.Append(path).Distinct().ToArray() : Preferences.Favorites.Where((string p) => p != path).ToArray())
        });
    }

    public void SelectImage(byte[] bytes)
    {
        byte[] array = Normalize(bytes);
        string hash = Convert.ToHexStringLower(SHA256.HashData(array));
        WriteAtomic(Path.Combine(Root, "selected", hash + ".png"), array);
        Save(Preferences with
        {
            Profiles = Preferences.Profiles.Select((AvatarProfile p) => (!(p.Id == Active.Id)) ? p : p with
            {
                Image = hash
            }).ToArray()
        });
        HashSet<string> hashSet = Preferences.Profiles.Select((AvatarProfile p) => p.Image).ToHashSet();
        foreach (string item in Directory.EnumerateFiles(Path.Combine(Root, "selected"), "*.png"))
        {
            if (!hashSet.Contains(Path.GetFileNameWithoutExtension(item)))
            {
                try
                {
                    File.Delete(item);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    public byte[]? ReadImage()
    {
        string image = Active.Image;
        if (image == null)
        {
            return null;
        }
        try
        {
            string text = Path.Combine(Root, "selected", image + ".png");
            if (new FileInfo(text).Length > 2097152)
            {
                return null;
            }
            byte[] array = File.ReadAllBytes(text);
            return (Convert.ToHexStringLower(SHA256.HashData(array)) == image) ? array : null;
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
        {
            return null;
        }
    }

    public static byte[] ReadCachedImage(byte[] bytes)
    {
        if (bytes.Length > 2097152)
        {
            throw new InvalidDataException("Cached avatar is too large.");
        }
        using SKData data = SKData.CreateCopy(bytes);
        using SKCodec sKCodec = SKCodec.Create(data);
        if (sKCodec == null || sKCodec.EncodedFormat != SKEncodedImageFormat.Png || sKCodec.Info.Width > 512 || sKCodec.Info.Height > 512)
        {
            throw new InvalidDataException("Invalid cached avatar.");
        }
        using SKBitmap sKBitmap = new SKBitmap(sKCodec.Info);
        if (sKCodec.GetPixels(sKCodec.Info, sKBitmap.GetPixels()) != SKCodecResult.Success)
        {
            throw new InvalidDataException("Incomplete cached avatar.");
        }
        return (sKCodec.Info.Width == 512 && sKCodec.Info.Height == 512) ? bytes : Normalize(bytes);
    }

    public static byte[] Normalize(byte[] bytes)
    {
        int num = bytes.Length;
        if ((num > 8388608 || num == 0) ? true : false)
        {
            throw new InvalidDataException("Choose a PNG or JPEG image smaller than 8 MB.");
        }
        using SKData data = SKData.CreateCopy(bytes);
        using SKCodec sKCodec = SKCodec.Create(data);
        bool flag = sKCodec == null;
        if (!flag)
        {
            SKEncodedImageFormat encodedFormat = sKCodec.EncodedFormat;
            bool flag2 = (uint)(encodedFormat - 3) <= 1u;
            flag = !flag2;
        }
        if (flag || sKCodec.Info.Width < 1 || sKCodec.Info.Height < 1 || (long)sKCodec.Info.Width * (long)sKCodec.Info.Height > 16777216)
        {
            throw new InvalidDataException("Choose a valid PNG or JPEG image up to 16 megapixels.");
        }
        SKImageInfo info = new SKImageInfo(sKCodec.Info.Width, sKCodec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKBitmap sKBitmap = new SKBitmap(info);
        if (sKCodec.GetPixels(info, sKBitmap.GetPixels()) != SKCodecResult.Success)
        {
            throw new InvalidDataException("The image is incomplete or corrupt.");
        }
        int encodedOrigin = (int)sKCodec.EncodedOrigin;
        bool flag3 = encodedOrigin >= 5;
        using SKBitmap sKBitmap2 = new SKBitmap(new SKImageInfo(flag3 ? sKBitmap.Height : sKBitmap.Width, flag3 ? sKBitmap.Width : sKBitmap.Height));
        using (SKCanvas sKCanvas = new SKCanvas(sKBitmap2))
        {
            sKCanvas.Clear(SKColors.Transparent);
            switch (encodedOrigin)
            {
                case 2:
                    sKCanvas.Translate(sKBitmap.Width, 0f);
                    sKCanvas.Scale(-1f, 1f);
                    break;
                case 3:
                    sKCanvas.Translate(sKBitmap.Width, sKBitmap.Height);
                    sKCanvas.RotateDegrees(180f);
                    break;
                case 4:
                    sKCanvas.Translate(0f, sKBitmap.Height);
                    sKCanvas.Scale(1f, -1f);
                    break;
                case 5:
                    sKCanvas.RotateDegrees(90f);
                    sKCanvas.Scale(1f, -1f);
                    break;
                case 6:
                    sKCanvas.Translate(sKBitmap.Height, 0f);
                    sKCanvas.RotateDegrees(90f);
                    break;
                case 7:
                    sKCanvas.Translate(sKBitmap.Height, sKBitmap.Width);
                    sKCanvas.RotateDegrees(90f);
                    sKCanvas.Scale(-1f, 1f);
                    break;
                case 8:
                    sKCanvas.Translate(0f, sKBitmap.Width);
                    sKCanvas.RotateDegrees(-90f);
                    break;
            }
            sKCanvas.DrawBitmap(sKBitmap, 0f, 0f);
        }
        int num2 = sKBitmap2.Width;
        int num3 = sKBitmap2.Height;
        int num4 = -1;
        int num5 = -1;
        SKColor[] pixels = sKBitmap2.Pixels;
        for (int i = 0; i < sKBitmap2.Height; i++)
        {
            for (int j = 0; j < sKBitmap2.Width; j++)
            {
                if (pixels[i * sKBitmap2.Width + j].Alpha >= 12)
                {
                    num2 = Math.Min(num2, j);
                    num3 = Math.Min(num3, i);
                    num4 = Math.Max(num4, j);
                    num5 = Math.Max(num5, i);
                }
            }
        }
        if (num4 < num2)
        {
            throw new InvalidDataException("The image is fully transparent. Choose an image with a visible subject.");
        }
        int num6;
        float num7;
        if (num2 <= 0 && num3 <= 0 && num4 >= sKBitmap2.Width - 1)
        {
            num6 = ((num5 < sKBitmap2.Height - 1) ? 1 : 0);
            if (num6 == 0)
            {
                num7 = Math.Min(sKBitmap2.Width, sKBitmap2.Height);
                goto IL_03b0;
            }
        }
        else
        {
            num6 = 1;
        }
        num7 = (float)Math.Max(num4 - num2 + 1, num5 - num3 + 1) * 1.08f;
        goto IL_03b0;
    IL_03b0:
        float num8 = num7;
        float num9 = ((num6 != 0) ? ((float)(num2 + num4 + 1) / 2f) : ((float)sKBitmap2.Width / 2f));
        float num10 = ((num6 != 0) ? ((float)(num3 + num5 + 1) / 2f) : ((float)sKBitmap2.Height / 2f));
        using SKSurface sKSurface = SKSurface.Create(new SKImageInfo(512, 512));
        sKSurface.Canvas.Clear(SKColors.Transparent);
        using SKPaint paint = new SKPaint
        {
            IsAntialias = true
        };
        using SKImage image = SKImage.FromBitmap(sKBitmap2);
        sKSurface.Canvas.Scale(512f / num8, 512f / num8);
        sKSurface.Canvas.Translate(num8 / 2f - num9, num8 / 2f - num10);
        sKSurface.Canvas.DrawImage(image, new SKRect(0f, 0f, sKBitmap2.Width, sKBitmap2.Height), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        using SKImage sKImage = sKSurface.Snapshot();
        using SKData sKData = sKImage.Encode(SKEncodedImageFormat.Png, 100);
        return sKData.ToArray();
    }

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        if (new FileInfo(path).LinkTarget != null || new DirectoryInfo(Path.GetDirectoryName(path)).LinkTarget != null)
        {
            throw new IOException("Avatar storage must use regular local files and directories.");
        }
        string text = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(text, bytes);
            File.Move(text, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(text))
            {
                File.Delete(text);
            }
        }
    }
}
